using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zarp.Core;

// Проверка соединения через проверочный вход (ProbeClient): разбор ответов, сроки, обрывы. Вместо sing-box здесь поддельный
// SOCKS5-сервер на localhost, поэтому не нужны ни интернет, ни sing-box.
static partial class Program
{
    /// <summary>SOCKS5-сервер с паролем. После рукопожатия вызывает Serve: что отдать клиенту, решает случай проверки.</summary>
    sealed class FakeSocks : IDisposable
    {
        public const string User = "zarp", Password = "secret-pass";

        readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        int _connections;

        public byte ConnectReply;           // 0: цель доступна, иначе код отказа SOCKS5
        public bool RefuseMethod;           // ответить 05 FF: вход по паролю не принят
        public bool ResetAfterGreeting;     // сразу после приветствия оборвать соединение сбросом
        public bool ResetAfterServe;        // после ответа, через 300 мс (клиент успевает прочесть ответ), оборвать соединение сбросом
        public Action<Stream> Serve;
        public string LastHost, Request;
        public int LastPort;
        public byte[] FirstBytes;

        public FakeSocks()
        {
            _listener.Start();
            new Thread(Accept) { IsBackground = true }.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Connections => _connections;

        /// <summary>Подождать конца работы сервера: так сервер "молчит" или держит соединение открытым, пока тест не закончится.</summary>
        public void Hold(int ms) => _stop.WaitOne(ms);

        void Accept()
        {
            try
            {
                while (true)
                {
                    var client = _listener.AcceptTcpClient();
                    Interlocked.Increment(ref _connections);
                    new Thread(() => Handle(client)) { IsBackground = true }.Start();
                }
            }
            catch { } // сервер остановлен
        }

        static byte[] Exact(Stream s, int count)
        {
            var buffer = new byte[count];
            for (int done = 0; done < count;)
            {
                int n = s.Read(buffer, done, count - done);
                if (n <= 0) throw new IOException("closed");
                done += n;
            }
            return buffer;
        }

        /// <summary>Читает запрос HTTP до пустой строки и возвращает его текст.</summary>
        public static string ReadRequest(Stream s)
        {
            var text = new StringBuilder();
            var one = new byte[1];
            while (!text.ToString().EndsWith("\r\n\r\n") && s.Read(one, 0, 1) == 1) text.Append((char)one[0]);
            return text.ToString();
        }

        /// <summary>Закрыть соединение сбросом (RST). Закрытие через TcpClient.Dispose сначала шлёт FIN, поэтому нужен жёсткий Close(0).</summary>
        static void Abort(TcpClient client)
        {
            try
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close(0);
            }
            catch { }
        }

        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 10000;
                    var s = client.GetStream();
                    var greeting = Exact(s, 2);
                    var methods = Exact(s, greeting[1]);
                    if (ResetAfterGreeting) { Abort(client); return; }
                    if (RefuseMethod || !methods.Contains((byte)2)) { s.Write(new byte[] { 5, 0xFF }, 0, 2); return; }
                    s.Write(new byte[] { 5, 2 }, 0, 2);
                    var head = Exact(s, 2);
                    string user = Encoding.UTF8.GetString(Exact(s, head[1]));
                    string password = Encoding.UTF8.GetString(Exact(s, Exact(s, 1)[0]));
                    bool ok = user == User && password == Password;
                    s.Write(new byte[] { 1, (byte)(ok ? 0 : 1) }, 0, 2);
                    if (!ok) return;
                    var connect = Exact(s, 5);
                    LastHost = Encoding.ASCII.GetString(Exact(s, connect[4]));
                    var port = Exact(s, 2);
                    LastPort = (port[0] << 8) | port[1];
                    s.Write(new byte[] { 5, ConnectReply, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                    if (ConnectReply != 0) return;
                    Serve?.Invoke(s);
                    if (ResetAfterServe)
                    {
                        Thread.Sleep(300);
                        Abort(client);
                    }
                }
            }
            catch { } // клиент оборвал соединение
        }

        public void Dispose()
        {
            _stop.Set();
            try { _listener.Stop(); } catch { }
        }
    }

    static void TestProbeClient()
    {
        var runtime = new ProxyRuntime(new SingBox(Path.Combine(_data, "probe-box")));
        const string Trace = "fl=1f1\nh=www.example.test\nip=203.0.113.9\nts=1\nvisit_scheme=https\nwarp=off\n";
        const string Url = "http://trace.test:8080/cdn-cgi/trace";

        ProxyMeasure Run(FakeSocks fake, string url = Url, int samples = 1, int timeoutMs = 3000, string password = FakeSocks.Password, CancellationToken ct = default(CancellationToken))
        {
            SetPrivate(runtime, "TraceUrl", url);
            var session = new ProxySession { Port = fake.Port, User = FakeSocks.User, Password = password };
            return Sync(() => runtime.MeasureAsync(session, samples, timeoutMs, ct));
        }
        string Http(string status, string headers, string body) => "HTTP/1.1 " + status + "\r\n" + headers + "\r\n" + body;
        void Send(Stream s, string text) { var bytes = Encoding.ASCII.GetBytes(text); s.Write(bytes, 0, bytes.Length); s.Flush(); }

        // обычный ответ с Content-Length; каждый запрос идёт на новом соединении, имя цели разрешает сервер
        using (var fake = new FakeSocks())
        {
            fake.Serve = s =>
            {
                fake.Request = FakeSocks.ReadRequest(s);
                Send(s, Http("200 OK", "Content-Length: " + Trace.Length + "\r\nConnection: close\r\n", Trace));
            };
            var m = Run(fake, samples: 2);
            Check(m.Ok && m.Failed == 0 && fake.Connections == 3, "An ordinary answer is a success and every request opens a connection, got ok=" + m.Ok + ", " + m.Error + ", connections=" + fake.Connections);
            Check(fake.LastHost == "trace.test" && fake.LastPort == 8080, "The target goes to the server as a name for it to resolve, got " + fake.LastHost + ":" + fake.LastPort);
            Check(fake.Request.StartsWith("GET /cdn-cgi/trace?") && fake.Request.Contains("\r\nHost: trace.test:8080\r\n") && fake.Request.Contains("\r\nConnection: close\r\n"),
                "The request is a plain GET with its own Host header: " + fake.Request.Split('\r')[0]);
        }

        // ответы, после которых сервер не закрывает соединение: проверка должна кончиться сама
        var shapes = new Dictionary<string, string>
        {
            ["chunked answer"] = Http("200 OK", "Transfer-Encoding: chunked\r\n", Trace.Length.ToString("x") + "\r\n" + Trace + "\r\n0\r\n\r\n"),
            ["answer without length, the ip line is complete"] = Http("200 OK", "", Trace),
            ["HTTP/1.0 answer"] = "HTTP/1.0 200 OK\r\n\r\n" + Trace,
        };
        foreach (var shape in shapes)
            using (var fake = new FakeSocks())
            {
                fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, shape.Value); fake.Hold(8000); };
                var clock = Stopwatch.StartNew();
                var m = Run(fake);
                Check(m.Ok && clock.ElapsedMilliseconds < 3000, shape.Key + ": must end without waiting for the server to close, got ok=" + m.Ok + " " + m.Error + " after " + clock.ElapsedMilliseconds + " ms");
            }

        // последняя строка без перевода строки принимается, только когда ответ закончился
        using (var fake = new FakeSocks())
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, Http("200 OK", "", "fl=1f1\nip=203.0.113.9")); };
            Check(Run(fake).Ok, "An unterminated last ip line counts once the server has closed");
        }
        using (var fake = new FakeSocks())
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, Http("200 OK", "", "fl=1f1\nip=203.0.1")); fake.Hold(8000); };
            var m = Run(fake, timeoutMs: 1000);
            Check(!m.Ok && m.Error == "timeout", "A cut-off ip line is not an address while the server may still send more, got " + m.Error);
        }

        // ответ, но не тот: код, заглушка без ip, не HTTP, пустое тело, заголовки без тела
        // (ответ, ожидаемая причина, держит ли сервер соединение открытым после ответа)
        var bad = new Dictionary<string, Tuple<string, string, bool>>
        {
            ["403 with a body"] = Tuple.Create(Http("403 Forbidden", "Content-Length: 4\r\n", "nope"), "HTTP 403", true),
            ["200 stub page without ip"] = Tuple.Create(Http("200 OK", "Content-Length: 22\r\n", "<html>Sign in</html>\n\n"), "trace", true),
            ["200 with an empty body and a length"] = Tuple.Create(Http("200 OK", "Content-Length: 0\r\n", ""), "trace", true),
            ["200 with headers only, then close"] = Tuple.Create(Http("200 OK", "", ""), "trace", false),
            ["200 with an empty ip value"] = Tuple.Create(Http("200 OK", "Content-Length: 4\r\n", "ip=\n"), "trace", true),
            ["not HTTP at all"] = Tuple.Create("garbage\r\n\r\nip=203.0.113.9\n", "not HTTP", true),
        };
        foreach (var pair in bad)
            using (var fake = new FakeSocks())
            {
                fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, pair.Value.Item1); if (pair.Value.Item3) fake.Hold(3000); };
                var clock = Stopwatch.StartNew();
                var m = Run(fake);
                Check(!m.Ok && m.Failed == 2 && m.Error != null && m.Error.Contains(pair.Value.Item2) && clock.ElapsedMilliseconds < 2500,
                    pair.Key + ": must fail with '" + pair.Value.Item2 + "' without waiting, got ok=" + m.Ok + ", '" + m.Error + "' after " + clock.ElapsedMilliseconds + " ms");
            }

        // после ответа сервер обрывает соединение сбросом: прочитанное остаётся ответом, а не превращается в ошибку чтения
        using (var fake = new FakeSocks { ResetAfterServe = true })
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, Http("200 OK", "", "fl=1f1\nh=www.example.test\n")); };
            var m = Run(fake);
            Check(!m.Ok && m.Error == "trace", "A reset after the answer ends it; the answer is judged as it is, got '" + m.Error + "'");
        }
        using (var fake = new FakeSocks { ResetAfterServe = true })
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, Http("200 OK", "", Trace.Substring(0, Trace.IndexOf("ts=", StringComparison.Ordinal)))); };
            Check(Run(fake).Ok, "A reset after the answer does not spoil an answer that has the ip line");
        }

        // ответ приходит по байту: разбор не должен зависеть от того, как нарезаны данные
        using (var fake = new FakeSocks())
        {
            fake.Serve = s =>
            {
                FakeSocks.ReadRequest(s);
                foreach (char c in Http("200 OK", "Content-Length: " + Trace.Length + "\r\n", Trace)) { Send(s, c.ToString()); Thread.Sleep(2); }
            };
            Check(Run(fake, timeoutMs: 10000).Ok, "An answer delivered one byte at a time is parsed");
        }

        // большой ответ без ip: чтение прекращается на пределе, проверка не зависает
        using (var fake = new FakeSocks())
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); Send(s, Http("200 OK", "", new string('x', 200 * 1024))); fake.Hold(3000); };
            var clock = Stopwatch.StartNew();
            var m = Run(fake);
            Check(!m.Ok && m.Error == "trace" && clock.ElapsedMilliseconds < 2500, "A huge answer without ip is read up to a limit and rejected, got '" + m.Error + "' after " + clock.ElapsedMilliseconds + " ms");
        }

        // отказы на уровне SOCKS5: пароль, способ входа, цель
        using (var fake = new FakeSocks())
        {
            var m = Run(fake, password: "wrong");
            Check(!m.Ok && m.Error.Contains("login") && fake.LastHost == null, "A wrong password is refused before any target is requested, got '" + m.Error + "'");
        }
        using (var fake = new FakeSocks { RefuseMethod = true })
        {
            var m = Run(fake);
            Check(!m.Ok && m.Error.Contains("password method"), "A server that does not take a password is a failure, got '" + m.Error + "'");
        }
        using (var fake = new FakeSocks { ConnectReply = 5 })
        {
            var m = Run(fake);
            Check(!m.Ok && m.Error.Contains("code 5") && fake.Request == null, "A refused connection to the target reports the SOCKS5 code, got '" + m.Error + "'");
        }
        using (var fake = new FakeSocks { ResetAfterGreeting = true })
        {
            var clock = Stopwatch.StartNew();
            var m = Run(fake);
            Check(!m.Ok && clock.ElapsedMilliseconds < 2500, "A connection reset during the handshake fails at once, got '" + m.Error + "' after " + clock.ElapsedMilliseconds + " ms");
        }

        // тишина: срок проверки ограничивает запрос целиком (меньше секунды не бывает)
        using (var fake = new FakeSocks())
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); fake.Hold(8000); };
            var clock = Stopwatch.StartNew();
            var m = Run(fake, samples: 1, timeoutMs: 1000);
            Check(!m.Ok && m.Failed == 2 && m.Error == "timeout" && clock.ElapsedMilliseconds >= 1800 && clock.ElapsedMilliseconds < 5000,
                "A server that never answers times out each request, got '" + m.Error + "', failed=" + m.Failed + " after " + clock.ElapsedMilliseconds + " ms");
        }

        // отмена пользователем - это не неудача проверки: исключение, а не результат
        using (var fake = new FakeSocks())
        {
            fake.Serve = s => { FakeSocks.ReadRequest(s); fake.Hold(8000); };
            using (var cts = new CancellationTokenSource(300))
            {
                var clock = Stopwatch.StartNew();
                var failure = Catch(() => Run(fake, samples: 0, timeoutMs: 20000, ct: cts.Token)); // один запрос: следующего круга, который заметил бы отмену, нет
                Check(failure is OperationCanceledException && clock.ElapsedMilliseconds < 3000, "Cancelling stops the check at once with a cancellation, got " + failure?.GetType().Name + " after " + clock.ElapsedMilliseconds + " ms");
            }
        }

        // https: после рукопожатия SOCKS5 клиент начинает TLS; сервер, который отвечает открытым текстом, - неудача
        using (var fake = new FakeSocks())
        {
            fake.Serve = s =>
            {
                var hello = new byte[3];
                int n = s.Read(hello, 0, 3);
                fake.FirstBytes = hello.Take(n).ToArray();
                Send(s, Http("200 OK", "", Trace));
            };
            var clock = Stopwatch.StartNew();
            var m = Run(fake, url: "https://trace.test/cdn-cgi/trace");
            Check(!m.Ok && fake.LastHost == "trace.test" && fake.LastPort == 443 && fake.FirstBytes != null && fake.FirstBytes.Length == 3 && fake.FirstBytes[0] == 0x16 && fake.FirstBytes[1] == 0x03 && clock.ElapsedMilliseconds < 4000,
                "https starts TLS (and port 443) inside the SOCKS5 tunnel, a server answering in plain text fails: '" + m.Error + "'");
        }
    }
}
