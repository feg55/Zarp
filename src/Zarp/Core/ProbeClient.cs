using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    /// <summary>Ответ страницы проверки: код и наличие поля ip в теле.</summary>
    sealed class ProbeAnswer
    {
        public int Status;
        /// <summary>В теле есть строка ip=...: так отвечает настоящая cdn-cgi/trace, а заглушка провайдера - нет.</summary>
        public bool HasIp;
    }

    /// <summary>
    /// Один запрос GET через проверочный вход sing-box: SOCKS5 с логином и паролем прямо в рукопожатии, затем TLS для https.
    /// Запрос проверки не идёт через HTTP-прокси намеренно: вход HTTP-прокси при неверном или отсутствующем пароле отвечает 407
    /// и закрывает соединение сбросом, а Windows при сбросе выбрасывает непрочитанные данные. Клиент HTTP начинает каждое
    /// соединение без пароля, ждёт этого 407 и иногда его не получает. У SOCKS5 такого обмена нет.
    /// Каждый вызов - новое соединение: время включает подключение к серверу и рукопожатие TLS.
    /// </summary>
    static class ProbeClient
    {
        const int MaxBytes = 64 * 1024;

        /// <summary>
        /// Ответ страницы или исключение: IOException с короткой причиной (без пароля) либо TimeoutException по истечении срока.
        /// Чтение прекращается, как только в теле появилась полная строка ip=, - закрытия соединения сервером ждать не нужно.
        /// </summary>
        public static async Task<ProbeAnswer> GetAsync(int port, string user, string password, Uri url, int timeoutMs, CancellationToken ct)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var tcp = new TcpClient { NoDelay = true })
            {
                limit.CancelAfter(timeoutMs);
                // у сокетов .NET Framework нет отмены: по сигналу соединение закрывается, и ожидающая операция завершается ошибкой
                using (limit.Token.Register(() => { try { tcp.Close(); } catch { } }))
                {
                    try
                    {
                        await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                        Stream stream = tcp.GetStream();
                        await Socks5Async(stream, user, password, url.Host, url.Port).ConfigureAwait(false);
                        if (url.Scheme == Uri.UriSchemeHttps)
                        {
                            // отзыв сертификата не проверяется: запрос к спискам отзыва шёл бы мимо туннеля и мог бы зависнуть
                            var tls = new SslStream(stream, false);
                            await tls.AuthenticateAsClientAsync(url.Host, null, SslProtocols.None, false).ConfigureAwait(false);
                            stream = tls;
                        }
                        return await ExchangeAsync(stream, url).ConfigureAwait(false);
                    }
                    catch (Exception) when (limit.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException("timeout");
                    }
                }
            }
        }

        static async Task WriteAsync(Stream s, byte[] data)
        {
            await s.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }

        static async Task<byte[]> ReadExactAsync(Stream s, int count)
        {
            var buffer = new byte[count];
            for (int done = 0; done < count;)
            {
                int n = await s.ReadAsync(buffer, done, count - done).ConfigureAwait(false);
                if (n <= 0) throw new IOException("connection closed");
                done += n;
            }
            return buffer;
        }

        /// <summary>SOCKS5 (RFC 1928) с входом по логину и паролю (RFC 1929) и адресом цели в виде имени: разрешает его сервер.</summary>
        static async Task Socks5Async(Stream s, string user, string password, string host, int port)
        {
            byte[] u = Encoding.UTF8.GetBytes(user ?? ""), p = Encoding.UTF8.GetBytes(password ?? ""), h = Encoding.ASCII.GetBytes(host);
            if (u.Length == 0 || u.Length > 255 || p.Length == 0 || p.Length > 255 || h.Length == 0 || h.Length > 255)
                throw new IOException("socks5: a field is too long");

            await WriteAsync(s, new byte[] { 5, 1, 2 }).ConfigureAwait(false); // предложен один способ: логин и пароль
            var method = await ReadExactAsync(s, 2).ConfigureAwait(false);
            if (method[0] != 5 || method[1] != 2) throw new IOException("socks5: the password method was not accepted");

            var auth = new byte[3 + u.Length + p.Length];
            auth[0] = 1;
            auth[1] = (byte)u.Length;
            Buffer.BlockCopy(u, 0, auth, 2, u.Length);
            auth[2 + u.Length] = (byte)p.Length;
            Buffer.BlockCopy(p, 0, auth, 3 + u.Length, p.Length);
            await WriteAsync(s, auth).ConfigureAwait(false);
            var verdict = await ReadExactAsync(s, 2).ConfigureAwait(false);
            if (verdict[1] != 0) throw new IOException("socks5: the login was refused");

            var request = new byte[7 + h.Length];
            request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)h.Length;
            Buffer.BlockCopy(h, 0, request, 5, h.Length);
            request[5 + h.Length] = (byte)(port >> 8);
            request[6 + h.Length] = (byte)port;
            await WriteAsync(s, request).ConfigureAwait(false);
            var head = await ReadExactAsync(s, 4).ConfigureAwait(false);
            if (head[0] != 5 || head[1] != 0) throw new IOException("socks5: the server refused the connection, code " + head[1]);
            int address = head[3] == 1 ? 4 : head[3] == 4 ? 16 : head[3] == 3 ? (await ReadExactAsync(s, 1).ConfigureAwait(false))[0] : -1;
            if (address < 0) throw new IOException("socks5: unknown address type");
            await ReadExactAsync(s, address + 2).ConfigureAwait(false); // адрес и порт, с которых сервер вышел к цели
        }

        static async Task<ProbeAnswer> ExchangeAsync(Stream s, Uri url)
        {
            string host = url.IsDefaultPort ? url.Host : url.Host + ":" + url.Port;
            await WriteAsync(s, Encoding.ASCII.GetBytes("GET " + url.PathAndQuery + " HTTP/1.1\r\nHost: " + host +
                "\r\nUser-Agent: Zarp/1.0\r\nAccept: */*\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);

            var received = new MemoryStream();
            var buffer = new byte[4096];
            while (received.Length < MaxBytes)
            {
                int n;
                // сброс соединения после того, как что-то уже пришло, - обычный конец ответа, а не ошибка
                try { n = await s.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false); }
                catch (IOException) when (received.Length > 0) { break; }
                if (n <= 0) break;
                received.Write(buffer, 0, n);
                var partial = Parse(received.ToArray(), false, out bool complete);
                if (partial != null && (partial.HasIp || complete)) return partial;
            }
            var answer = Parse(received.ToArray(), true, out _);
            if (answer == null) throw new IOException("no HTTP answer");
            return answer;
        }

        /// <summary>
        /// Разобрать то, что пришло. null - заголовки ещё не полны. final: данных больше не будет, поэтому последняя строка
        /// считается законченной; иначе ip=1.2.3 из обрезанного куска принял бы за адрес. complete: тело получено целиком
        /// (по Content-Length или по завершающему блоку chunked), ждать закрытия соединения не нужно.
        /// </summary>
        internal static ProbeAnswer Parse(byte[] data, bool final, out bool complete)
        {
            complete = false;
            string text = Encoding.ASCII.GetString(data); // один знак на байт: смещения в тексте и в байтах совпадают
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) return null;
            string head = text.Substring(0, end);
            string[] status = head.Split('\n')[0].TrimEnd('\r').Split(' ');
            if (status.Length < 2 || !status[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(status[1], out int code))
                throw new IOException("the answer is not HTTP");

            string body = text.Substring(end + 4);
            var lines = body.Split('\n');
            bool hasIp = false;
            for (int i = 0; i < (final ? lines.Length : lines.Length - 1) && !hasIp; i++)
                hasIp = lines[i].StartsWith("ip=", StringComparison.Ordinal) && lines[i].TrimEnd('\r').Length > 3;

            foreach (var line in head.Split('\n'))
            {
                string header = line.TrimEnd('\r');
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && long.TryParse(header.Substring(15).Trim(), out long length))
                    complete = body.Length >= length;
                else if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && header.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    complete = body.EndsWith("\r\n0\r\n\r\n", StringComparison.Ordinal) || body.StartsWith("0\r\n\r\n", StringComparison.Ordinal);
            }
            return new ProbeAnswer { Status = code, HasIp = hasIp };
        }
    }
}
