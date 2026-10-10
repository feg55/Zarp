using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    /// <summary>Итог проверки сервера: время первого ответа, средняя задержка или причина неудачи.</summary>
    public sealed class ProxyMeasure
    {
        public bool Ok;
        public int FirstMs;
        public int PingMs;
        /// <summary>Причина последней неудачи. Заполнена и при успехе, если какой-то из запросов не прошёл.</summary>
        public string Error;
        /// <summary>Сколько запросов не прошло.</summary>
        public int Failed;
    }

    /// <summary>
    /// Всё, что Engine делает с собственным сервером, кроме решений: узнать адрес, запустить sing-box, проверить
    /// соединение, остановить. Отдельный класс, чтобы тесты подменяли его и не запускали ни процессов, ни сети.
    /// </summary>
    public class ProxyRuntime
    {
        public readonly SingBox Box;

        /// <summary>Страница, по которой проверяется соединение. Подменяется только в тестах: настоящая ходила бы в интернет.</summary>
        internal string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

        public ProxyRuntime(SingBox box)
        {
            Box = box;
        }

        public virtual bool Running => Box.Running;

        public virtual void Stop() => Box.Stop();

        /// <summary>Распаковать sing-box, если нужно. false - его нет ни во вшитом архиве, ни в папке данных.</summary>
        public virtual bool Prepare()
        {
            Box.ExtractEmbedded();
            return Box.Installed;
        }

        /// <summary>Заготовка сессии со свободным портом и случайными данными проверочного входа.</summary>
        public virtual ProxySession NewSession(ProxyProfile profile)
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return new ProxySession
            {
                Profile = profile, Port = port, User = "zarp", Password = string.Concat(bytes.Select(b => b.ToString("x2"))),
            };
        }

        /// <summary>
        /// IP-адрес сервера. Имя разрешается заранее системным DNS: после включения адаптера запросы пойдут в туннель,
        /// а он без адреса сервера подняться не может.
        /// </summary>
        public virtual async Task<string> ResolveAsync(string host, CancellationToken ct)
        {
            if (IPAddress.TryParse(host, out _)) return host;
            var lookup = Dns.GetHostAddressesAsync(host);
            var finished = await Task.WhenAny(lookup, Task.Delay(10000, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != lookup) throw new IOException(L.T("err.proxyResolve", host));
            IPAddress[] found;
            try { found = await lookup.ConfigureAwait(false); }
            catch (SocketException) { throw new IOException(L.T("err.proxyResolve", host)); }
            var pick = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault();
            if (pick == null) throw new IOException(L.T("err.proxyResolve", host));
            return pick.ToString();
        }

        /// <summary>Составить конфигурацию, проверить её и запустить sing-box. null - всё в порядке.</summary>
        public virtual async Task<Msg> StartAsync(ProxySession session, CancellationToken ct)
        {
            string config = ProxyConfig.Build(session);
            var check = await Box.CheckAsync(config, ct).ConfigureAwait(false);
            if (!check.Ok)
                return new Msg("err.singboxConfig", Shorten(check.Output));
            var err = await Box.StartAsync(config, session.Port, ct).ConfigureAwait(false);
            if (err == null)
                Box.SaveState(new SingBoxState { Port = session.Port, User = session.User, Password = session.Password, ProfileId = session.Profile.Id });
            return err;
        }

        static string Shorten(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", "\u001b\\[[0-9;]*m", "").Replace("\r", "").Trim();
            return text.Length > 400 ? text.Substring(0, 400) + "..." : text;
        }

        /// <summary>Подхватить sing-box, оставшийся от прошлого запуска Zarp: сессия восстанавливается из файла состояния.</summary>
        public virtual ProxySession Adopt(ProxyProfile profile)
        {
            var state = Box.LoadState();
            if (state == null || state.ProfileId != profile.Id) return null;
            return new ProxySession { Profile = profile, Port = state.Port, User = state.User, Password = state.Password, Tun = true };
        }

        /// <summary>Дождаться, пока виртуальный адаптер Zarp появится и поднимется. false - sing-box упал или адаптера нет.</summary>
        public virtual async Task<bool> WaitAdapterAsync(CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 20)
            {
                ct.ThrowIfCancellationRequested();
                if (!Box.Running) return false;
                try
                {
                    if (NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                            n.Name == SingBox.TunName && n.OperationalStatus == OperationalStatus.Up))
                        return true;
                }
                catch (NetworkInformationException) { } // адаптер как раз создаётся
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>
        /// Проверка уже через виртуальный адаптер: запрос от самого Zarp, без прокси, идёт по маршруту системы - то есть сквозь адаптер.
        /// Проверочный вход этого не доказывает: он ходит к серверу в обход адаптера. false - адаптер поднялся, но трафик не идёт.
        /// </summary>
        public virtual async Task<bool> CheckTunnelAsync(int timeoutMs, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)) })
                    using (var response = await http.GetAsync(TraceUrl + "?" + Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false))
                        if (response.IsSuccessStatusCode) return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>Последняя строка об ошибке в журнале sing-box без метки времени и цветовых кодов (или null).</summary>
        public virtual string LastError()
        {
            try
            {
                string line = Box.ReadLog().Split('\n')
                    .Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\u001b\\[[0-9;]*m", ""))
                    .LastOrDefault(l => l.Contains(" ERROR ") || l.Contains(" FATAL "));
                if (line == null) return null;
                int level = Math.Max(line.IndexOf(" ERROR "), line.IndexOf(" FATAL "));
                line = line.Substring(level + 7).Trim();
                return line.Length > 300 ? line.Substring(0, 300) + "..." : line;
            }
            catch { return null; }
        }

        /// <summary>
        /// Проверка через проверочный вход sing-box: samples+1 запросов к cdn-cgi/trace, каждый на новом соединении
        /// (SOCKS5 с паролем, см. ProbeClient). Первый - прогрев, но его время считается временем подключения.
        /// Ответ должен быть настоящим трассировочным (код 200 и поле ip): страница провайдера с кодом 200 не годится.
        /// timeoutMs - срок на каждый запрос целиком.
        /// </summary>
        public virtual async Task<ProxyMeasure> MeasureAsync(ProxySession session, int samples, int timeoutMs, CancellationToken ct)
        {
            var times = new List<int>();
            int first = 0, failed = 0;
            string last = null;
            for (int i = 0; i < samples + 1; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                ProbeAnswer answer;
                try
                {
                    answer = await ProbeClient.GetAsync(session.Port, session.User, session.Password,
                        new Uri(TraceUrl + "?" + Guid.NewGuid().ToString("N")), Math.Max(1000, timeoutMs), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    last = e is TimeoutException ? "timeout" : (e.InnerException ?? e).Message;
                    failed++;
                    continue;
                }
                sw.Stop();
                if (answer.Status != 200) { last = "HTTP " + answer.Status; failed++; continue; }
                if (!answer.HasIp) { last = "trace"; failed++; continue; }
                int ms = (int)sw.ElapsedMilliseconds;
                if (first == 0) first = Math.Max(1, ms);
                if (i > 0) times.Add(ms);
            }
            if (times.Count == 0) return new ProxyMeasure { Error = last, Failed = failed };
            times.Sort();
            return new ProxyMeasure { Ok = true, FirstMs = first, PingMs = times[times.Count / 2], Error = last, Failed = failed };
        }
    }
}
