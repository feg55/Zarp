using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>Управление клиентом Cloudflare WARP через warp-cli.</summary>
    public sealed class Warp
    {
        public string CliPath { get; }
        public bool Installed => CliPath != null;

        // последний выставленный транспорт — чтобы не дёргать настройки WARP без нужды
        WarpTransport? _transport;

        public Warp()
        {
            CliPath = FindCli();
        }

        static string FindCli()
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string p = Path.Combine(root, "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
                if (File.Exists(p)) return p;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "warp-cli.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        public Task<RunResult> Cli(string args, int timeoutMs = 15000) =>
            ProcessUtil.RunAsync(CliPath, "--accept-tos " + args, timeoutMs);

        /// <summary>Статус из `warp-cli -j status`: Connected, Connecting, Disconnected...</summary>
        public async Task<(string Status, string Reason)> StatusAsync()
        {
            var r = await Cli("-j status", 8000);
            if (!r.Ok) return ("Unknown", r.Output);
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(r.Output) as Dictionary<string, object>;
                string status = d != null && d.TryGetValue("status", out var s) ? s as string : null;
                string reason = d != null && d.TryGetValue("reason", out var re) ? new JavaScriptSerializer().Serialize(re) : "";
                return (status ?? "Unknown", reason);
            }
            catch
            {
                return ("Unknown", r.Output);
            }
        }

        /// <summary>Проверить, что клиент зарегистрирован; если нет — зарегистрировать.</summary>
        public async Task<bool> EnsureRegisteredAsync()
        {
            var r = await Cli("registration show");
            if (r.Ok) return true;
            Log.Write("WARP не зарегистрирован, регистрирую...");
            r = await Cli("registration new", 30000);
            Log.Write(r.Ok ? "Регистрация WARP выполнена." : "Ошибка регистрации WARP: " + r.Output);
            return r.Ok;
        }

        /// <summary>Выставить протокол туннеля под стратегию.</summary>
        public async Task SetTransportAsync(WarpTransport t, CancellationToken ct)
        {
            if (_transport == t) return;
            bool familyChanged = _transport == null || (_transport == WarpTransport.WireGuard) != (t == WarpTransport.WireGuard);
            if (t == WarpTransport.WireGuard)
            {
                await Cli("tunnel protocol set WireGuard");
            }
            else
            {
                await Cli("tunnel protocol set MASQUE");
                await Cli("tunnel masque-options set " + (t == WarpTransport.MasqueH2 ? "h2-only" : "h3-only"));
            }
            _transport = t;
            if (familyChanged) await WaitProtocolAppliedAsync(t, ct);
        }

        // Эндпоинты: WireGuard — 162.159.192-195.x / 2606:4700:d0..d1::, MASQUE — 162.159.197-198.x / 2606:4700:102-103::
        static readonly Regex WireGuardEndpoint = new Regex(@"162\.159\.19[2-5]\.|2606:4700:d[01]:", RegexOptions.Compiled);
        static readonly Regex MasqueEndpoint = new Regex(@"162\.159\.19[78]\.|2606:4700:10[23]:", RegexOptions.Compiled);

        /// <summary>
        /// Демон WARP применяет смену протокола не сразу: первые попытки ещё идут по старому.
        /// Поэтому подключаемся и ждём, пока в статусе не появится эндпоинт нужного протокола.
        /// </summary>
        async Task WaitProtocolAppliedAsync(WarpTransport t, CancellationToken ct)
        {
            var want = t == WarpTransport.WireGuard ? WireGuardEndpoint : MasqueEndpoint;
            await DisconnectAsync();
            await SetEndpointAsync(null); // с жёстким эндпоинтом по статусу не понять, какой протокол применился
            await ConnectAsync();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 40000)
            {
                ct.ThrowIfCancellationRequested();
                var (status, reason) = await StatusAsync();
                if (status == "Connected" || want.IsMatch(reason ?? "")) break;
                await Task.Delay(400, ct);
            }
            await DisconnectAsync();
        }

        public async Task ConnectAsync() => await Cli("connect");

        /// <summary>Жёстко задать эндпоинт туннеля (IP:порт). null — вернуть автоматический выбор.</summary>
        public async Task<bool> SetEndpointAsync(string endpoint)
        {
            if (endpoint == null && !_endpointOverridden) return true; // не трогаем эндпоинт, заданный пользователем
            var r = endpoint == null
                ? await Cli("tunnel endpoint reset")
                : await Cli("tunnel endpoint set " + endpoint);
            if (!r.Ok) Log.Write("  warp-cli tunnel endpoint: " + r.Output);
            else _endpointOverridden = endpoint != null;
            return r.Ok;
        }

        bool _endpointOverridden;

        // Эндпоинты, к которым клиент WARP подключается сам (видно в статусе happy eyeballs).
        static readonly string[] MasqueIps = { "162.159.198.1", "162.159.198.2" };
        static readonly int[] MasquePorts = { 443, 500, 1701, 4500, 4443, 8443 };
        static readonly string[] WireGuardIps =
            Enumerable.Range(1, 10).Select(i => "162.159.192." + i).Concat(Enumerable.Range(1, 10).Select(i => "162.159.193." + i)).ToArray();
        static readonly int[] WireGuardPorts = { 2408, 500, 1701, 4500, 854, 859, 864, 878, 880, 890, 891, 894 };

        int _endpointSeq;

        /// <summary>Следующий ещё не использованный эндпоинт для транспорта — чтобы каждый тест шёл по «чистому» соединению.</summary>
        public string NextEndpoint(WarpTransport t)
        {
            int n = _endpointSeq++;
            switch (t)
            {
                case WarpTransport.WireGuard:
                    return WireGuardIps[n % WireGuardIps.Length] + ":" + WireGuardPorts[n / WireGuardIps.Length % WireGuardPorts.Length];
                case WarpTransport.MasqueH2:
                    return MasqueIps[n % MasqueIps.Length] + ":443"; // HTTP/2 идёт по TCP 443
                default:
                    return MasqueIps[n % MasqueIps.Length] + ":" + MasquePorts[n / MasqueIps.Length % MasquePorts.Length];
            }
        }

        public async Task DisconnectAsync() => await Cli("disconnect");

        /// <summary>Ждать статуса Connected. Возвращает время подключения в мс или -1.</summary>
        public async Task<int> WaitConnectedAsync(int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            string lastReason = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                var (status, reason) = await StatusAsync();
                if (status == "Connected") return (int)sw.ElapsedMilliseconds;
                lastReason = reason;
                await Task.Delay(300, ct);
            }
            if (!string.IsNullOrEmpty(lastReason)) Log.Write("  WARP: " + lastReason);
            return -1;
        }

        static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
            var h = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };
            h.DefaultRequestHeaders.ConnectionClose = true; // каждый замер — новое соединение через туннель
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Zarp/1.0");
            return h;
        }

        /// <summary>
        /// Проверка, что трафик действительно идёт через WARP (cdn-cgi/trace → warp=on|plus),
        /// и замер задержки. Возвращает средний пинг в мс или -1.
        /// </summary>
        public static async Task<int> MeasureAsync(int samples, CancellationToken ct)
        {
            var times = new List<int>();
            for (int i = 0; i < samples + 1; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                string body;
                try
                {
                    body = await Http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace?" + Guid.NewGuid().ToString("N"));
                }
                catch
                {
                    continue;
                }
                sw.Stop();
                bool warp = body.Contains("warp=on") || body.Contains("warp=plus");
                if (!warp) return -1;
                if (i > 0) times.Add((int)sw.ElapsedMilliseconds); // первый запрос — прогрев (DNS и т.п.)
            }
            if (times.Count == 0) return -1;
            times.Sort();
            return times[times.Count / 2];
        }
    }
}
