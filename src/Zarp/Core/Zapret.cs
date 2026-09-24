using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>Файлы zapret2 заблокированы антивирусом (обычно Защитником Windows - из-за драйвера WinDivert).</summary>
    public sealed class AntivirusBlockedException : Exception
    {
        public AntivirusBlockedException(string msg, Exception inner) : base(msg, inner) { }
    }

    /// <summary>Установка, настройка и запуск winws2 из zapret2.</summary>
    public sealed class Zapret
    {
        public string Dir { get; }
        public string Exe => Path.Combine(Dir, "winws2.exe");
        string CfgFile => Path.Combine(Dir, "zarp.cfg");
        string LogFile => Path.Combine(Dir, "winws2.log");

        static readonly string[] RequiredFiles =
        {
            "winws2.exe", "cygwin1.dll", "WinDivert.dll", "WinDivert64.sys",
            @"lua\zapret-lib.lua", @"lua\zapret-antidpi.lua",
        };

        /// <summary>Блобы, которые подгружаются только если стратегия на них ссылается.</summary>
        static readonly Dictionary<string, string> Blobs = new Dictionary<string, string>
        {
            ["quic_google"] = "@files/fake/quic_initial_www_google_com.bin",
            ["quic_vk"] = "@files/fake/quic_initial_vk_com.bin",
            ["tls_google"] = "@files/fake/tls_clienthello_www_google_com.bin",
            ["tls_vk"] = "@files/fake/tls_clienthello_vk_com.bin",
            ["stun_fake"] = "@files/fake/stun.bin",
            ["zero64"] = "0x" + new string('0', 128),
        };

        // Адреса, к которым подключается WARP (engage/MASQUE/WireGuard эндпоинты Cloudflare).
        static readonly string[][] WarpRanges4 =
        {
            new[] { "162.159.192.0", "162.159.199.255" },
            new[] { "162.159.204.0", "162.159.204.255" },
            new[] { "188.114.96.0", "188.114.99.255" },
        };
        static readonly string[][] WarpRanges6 =
        {
            new[] { "2606:4700:100::", "2606:4700:1ff:ffff:ffff:ffff:ffff:ffff" },
            new[] { "2606:4700:d0::", "2606:4700:df:ffff:ffff:ffff:ffff:ffff" },
        };

        // Первый пакет QUIC v1 (Initial, long header) - сюда входит MASQUE по HTTP/3.
        const string QuicInitialFilter =
            "outbound and udp and udp.PayloadLength>=256 and udp.Payload[0]>=0xC0 and udp.Payload[0]<0xD0 and udp.Payload[1]==0 and udp.Payload16[1]==0 and udp.Payload[4]==1";
        // WireGuard handshake initiation.
        const string WireGuardInitFilter =
            "outbound and udp and udp.PayloadLength==148 and udp.Payload32[0]==0x01000000";

        public Zapret(string dir)
        {
            Dir = dir;
        }

        public bool Installed => RequiredFiles.All(f => File.Exists(Path.Combine(Dir, f)));

        // ------------------------------------------------------------------ установка и обновление

        const string ReleasesApi = "https://api.github.com/repos/bol-van/zapret2/releases/latest";
        string NewDir => Dir + ".new";
        string OldDir => Dir + ".old";
        volatile bool _downloading;

        /// <summary>Версия установленного zapret2 (тег релиза) или null.</summary>
        public string Version => ReadVersion(Dir);

        static string ReadVersion(string dir)
        {
            try { return File.ReadAllText(Path.Combine(dir, "version.txt")).Trim(); }
            catch { return null; }
        }

        static bool Complete(string dir) =>
            ReadVersion(dir) != null && RequiredFiles.All(f => File.Exists(Path.Combine(dir, f)));

        static HttpClient NewHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Zarp/1.0");
            return http;
        }

        /// <summary>Повтор при сетевых сбоях: сеть может «моргнуть», пока WARP меняет состояние.</summary>
        static async Task<T> RetryAsync<T>(Func<Task<T>> f, CancellationToken ct)
        {
            for (int i = 1; ; i++)
            {
                try { return await f(); }
                catch (HttpRequestException) when (i < 3) { }
                catch (TaskCanceledException) when (i < 3 && !ct.IsCancellationRequested) { }
                await Task.Delay(2000 * i, ct);
            }
        }

        const string ReleasesLatestPage = "https://github.com/bol-van/zapret2/releases/latest";

        static async Task<(string Tag, string Url)> LatestAsync(HttpClient http, CancellationToken ct)
        {
            // Основной путь: редирект releases/latest → releases/tag/vX. У GitHub API лимит 60 запросов в час на IP,
            // а через WARP IP общий с тысячами людей, так что API часто отвечает 403.
            try
            {
                using (var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
                {
                    noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("Zarp/1.0");
                    using (var resp = await RetryAsync(() => noRedirect.GetAsync(ReleasesLatestPage, ct), ct))
                    {
                        var m = Regex.Match(resp.Headers.Location?.ToString() ?? "", @"/releases/tag/(v[\d.]+)$");
                        if (m.Success)
                        {
                            string t = m.Groups[1].Value;
                            return (t, $"https://github.com/bol-van/zapret2/releases/download/{t}/zapret2-{t}.zip");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Log.Write("releases/latest недоступен, пробую GitHub API: " + e.Message); }

            string json = await RetryAsync(() => http.GetStringAsync(ReleasesApi), ct);
            var rel = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            string tag = rel["tag_name"] as string;
            string url = ((object[])rel["assets"]).Cast<Dictionary<string, object>>()
                .Select(a => a["browser_download_url"] as string)
                .FirstOrDefault(u => u != null && Regex.IsMatch(u, @"/zapret2-v[\d.]+\.zip$"));
            if (url == null) throw new Exception("В релизе " + tag + " не найден zip-архив.");
            return (tag, url);
        }

        /// <summary>Скачать релиз и распаковать нужные для Windows файлы в target. version.txt пишется последним - это признак целостности.</summary>
        static async Task ExtractReleaseAsync(HttpClient http, string url, string tag, string target, CancellationToken ct)
        {
            // Качаем в память: сам архив на диск не пишем, чтобы антивирус не блокировал его целиком.
            byte[] data = await RetryAsync(() => http.GetByteArrayAsync(url), ct);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(target);
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // каталог
                    string rel = MapEntry(e.FullName);
                    if (rel == null) continue;
                    string dst = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    try
                    {
                        e.ExtractToFile(dst, true);
                    }
                    catch (IOException ex) when (IsAntivirusError(ex))
                    {
                        throw new AntivirusBlockedException("Антивирус заблокировал файл " + rel, ex);
                    }
                }
            }
            File.WriteAllText(Path.Combine(target, "version.txt"), tag);
        }

        // ---- zapret2, вшитый в exe (vendor\zapret2.zip → ресурс "zapret2.zip")

        static byte[] EmbeddedZip()
        {
            using (var s = typeof(Zapret).Assembly.GetManifestResourceStream("zapret2.zip"))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string _embeddedVersion;

        /// <summary>Версия zapret2, вшитая в exe, или null, если exe собран без него.</summary>
        public static string EmbeddedVersion
        {
            get
            {
                if (_embeddedVersion != null) return _embeddedVersion.Length == 0 ? null : _embeddedVersion;
                _embeddedVersion = "";
                var data = EmbeddedZip();
                if (data == null) return null;
                using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                using (var r = new StreamReader(zip.GetEntry("version.txt").Open()))
                    _embeddedVersion = r.ReadToEnd().Trim();
                return _embeddedVersion;
            }
        }

        /// <summary>Нужно распаковать вшитый zapret2: его нет или вшитая версия новее установленной.</summary>
        public bool EmbeddedIsNewer =>
            EmbeddedVersion != null && (!Installed || CompareVersions(EmbeddedVersion, Version) > 0);

        /// <summary>Сравнение тегов вида v1.0.5.2 по числам.</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] Parse(string v) => Regex.Matches(v ?? "", @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();
            var x = Parse(a); var y = Parse(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
                if (c != 0) return c;
            }
            return 0;
        }

        /// <summary>Распаковать вшитый zapret2 в папку данных, если он нужен. Возвращает true, если распаковал.</summary>
        public bool ExtractEmbedded()
        {
            ApplyPendingUpdate(); // скачанное обновление может оказаться новее вшитого
            if (!EmbeddedIsNewer || Running) return false;
            var data = EmbeddedZip();
            Directory.CreateDirectory(Dir);
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries.Where(e => e.Name.Length > 0 && e.Name != "version.txt"))
                {
                    string dst = Path.Combine(Dir, e.FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    try
                    {
                        e.ExtractToFile(dst, true);
                    }
                    catch (IOException ex) when (IsAntivirusError(ex))
                    {
                        throw new AntivirusBlockedException("Антивирус заблокировал файл " + e.FullName, ex);
                    }
                }
            }
            File.WriteAllText(Path.Combine(Dir, "version.txt"), EmbeddedVersion); // последним - признак целостности
            Log.Write("zapret2 " + EmbeddedVersion + " распакован из программы.");
            return true;
        }

        /// <summary>
        /// Установка с GitHub - запасной путь, если exe собран без вшитого zapret2.
        /// </summary>
        public async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
        {
            if (ApplyPendingUpdate()) return; // вдруг обновление уже скачано
            using (var http = NewHttp())
            {
                progress?.Report("Ищу последний релиз zapret2...");
                var (tag, url) = await LatestAsync(http, ct);
                progress?.Report("Скачиваю zapret2 " + tag + "...");
                await ExtractReleaseAsync(http, url, tag, Dir, ct);
            }
            if (!Installed) throw new Exception("После распаковки не хватает файлов zapret2 (возможно, их удалил антивирус).");
            progress?.Report("zapret2 установлен.");
        }

        /// <summary>
        /// Фоновая проверка обновлений: новая версия скачивается в zapret2.new.
        /// Возвращает тег новой версии или null, если обновлять нечего.
        /// </summary>
        public async Task<string> DownloadUpdateAsync(CancellationToken ct)
        {
            if (_downloading) return null;
            _downloading = true;
            try
            {
                using (var http = NewHttp())
                {
                    var (tag, url) = await LatestAsync(http, ct);
                    if (Version != null && CompareVersions(tag, Version) <= 0) return null;
                    if (Complete(NewDir) && ReadVersion(NewDir) == tag) return tag; // уже скачано, ждёт применения
                    if (Directory.Exists(NewDir)) Directory.Delete(NewDir, true);
                    await ExtractReleaseAsync(http, url, tag, NewDir, ct);
                    return tag;
                }
            }
            finally
            {
                _downloading = false;
            }
        }

        public bool UpdatePending => !_downloading && Complete(NewDir);

        /// <summary>
        /// Подменить zapret2 скачанной версией. Возможно только когда winws2 не запущен (файлы не заняты),
        /// поэтому вызывается перед каждым запуском winws2 и сразу после скачивания, если он не работает.
        /// </summary>
        public bool ApplyPendingUpdate()
        {
            if (!UpdatePending || Running) return false;
            string ver = ReadVersion(NewDir);
            try
            {
                if (Directory.Exists(OldDir)) Directory.Delete(OldDir, true);
                if (Directory.Exists(Dir)) Directory.Move(Dir, OldDir);
                Directory.Move(NewDir, Dir);
            }
            catch (Exception e)
            {
                // откат, чтобы не остаться без zapret2
                try { if (!Directory.Exists(Dir) && Directory.Exists(OldDir)) Directory.Move(OldDir, Dir); } catch { }
                Log.Write("Не удалось применить обновление zapret2: " + e.Message);
                return false;
            }
            // старая версия лежит в zapret2.old, пока новая не запустится успешно (см. StartAsync)
            Log.Write("zapret2 обновлён до " + ver + ".");
            return true;
        }

        /// <summary>Вернуть предыдущую версию, если новая не запустилась.</summary>
        bool RollbackUpdate()
        {
            if (!Directory.Exists(OldDir) || !Complete(OldDir)) return false;
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                Directory.Move(OldDir, Dir);
                Log.Write("Новая версия zapret2 не запустилась, возвращена " + Version + ".");
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Не удалось вернуть прежнюю версию zapret2: " + e.Message);
                return false;
            }
        }

        /// <summary>zapret2-vX/binaries/windows-x86_64/* → ./, lua/*.lua → lua/, files/fake/* → files/fake/</summary>
        static string MapEntry(string full)
        {
            var parts = full.Replace('\\', '/').Split('/');
            if (parts.Length < 3) return null;
            string inner = string.Join("/", parts.Skip(1));
            if (inner.StartsWith("binaries/windows-x86_64/")) return parts.Last();
            if (inner.StartsWith("lua/") && inner.EndsWith(".lua")) return Path.Combine("lua", parts.Last());
            if (inner.StartsWith("files/fake/")) return Path.Combine("files", "fake", parts.Last());
            return null;
        }

        public static bool IsAntivirusError(Exception ex)
        {
            // 0x800700E1 ERROR_VIRUS_INFECTED, 0x800700E2 ERROR_VIRUS_DELETED
            int hr = ex.HResult & 0xFFFF;
            return hr == 225 || hr == 226 || (ex.Message ?? "").IndexOf("virus", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Добавить папку zapret2 в исключения Защитника Windows.</summary>
        public async Task<bool> AddDefenderExclusionAsync()
        {
            Directory.CreateDirectory(Dir);
            string cmd = $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{Dir.Replace("'", "''")}'\"";
            var r = await ProcessUtil.RunAsync("powershell.exe", cmd, 30000);
            Log.Write(r.Ok ? "Папка добавлена в исключения Защитника: " + Dir : "Не удалось добавить исключение: " + r.Output);
            return r.Ok;
        }

        // ------------------------------------------------------------------ конфиг winws2

        /// <summary>Собрать полный набор аргументов winws2 для стратегии.</summary>
        public List<string> BuildArgs(Strategy s, bool restrictToWarpIps)
        {
            var a = new List<string>();
            switch (s.Transport)
            {
                case WarpTransport.MasqueH3:
                    a.Add("--wf-raw-part=" + QuicInitialFilter);
                    break;
                case WarpTransport.WireGuard:
                    a.Add("--wf-raw-part=" + WireGuardInitFilter);
                    break;
                case WarpTransport.MasqueH2:
                    // при ограничении по IP можно ловить любые порты - WARP перебирает несколько
                    a.Add("--wf-tcp-out=" + (restrictToWarpIps ? "1-65535" : "443"));
                    break;
            }
            if (restrictToWarpIps) a.Add("--wf-raw-filter=" + WarpIpFilter());
            a.Add("--lua-init=@lua/zapret-lib.lua");
            a.Add("--lua-init=@lua/zapret-antidpi.lua");
            foreach (var b in Blobs)
                if (Regex.IsMatch(s.Args ?? "", @"\b" + b.Key + @"\b"))
                    a.Add($"--blob={b.Key}:{b.Value}");
            return a;
        }

        static string WarpIpFilter()
        {
            string Range(string ver, string lo, string hi) =>
                $"({ver}.DstAddr>={lo} and {ver}.DstAddr<={hi}) or ({ver}.SrcAddr>={lo} and {ver}.SrcAddr<={hi})";
            string v4 = string.Join(" or ", WarpRanges4.Select(r => Range("ip", r[0], r[1])));
            string v6 = string.Join(" or ", WarpRanges6.Select(r => Range("ipv6", r[0], r[1])));
            return $"(ip and ({v4})) or (ipv6 and ({v6}))";
        }

        /// <summary>
        /// Текст файла конфигурации для `winws2 @file`. winws2 разбирает его через wordexp (как shell),
        /// поэтому наши аргументы берём в одинарные кавычки, а аргументы стратегии пишем как есть.
        /// </summary>
        public string BuildConfig(Strategy s, bool restrictToWarpIps)
        {
            var sb = new StringBuilder();
            foreach (var arg in BuildArgs(s, restrictToWarpIps))
                sb.Append('\'').Append(arg.Replace("'", "'\\''")).Append("'\n");
            sb.Append(s.Args.Trim()).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ запуск

        public bool Running => FindOurProcesses().Any();

        /// <summary>Запустить winws2 со стратегией. При ошибке возвращает текст ошибки.</summary>
        public async Task<string> StartAsync(Strategy s, bool restrictToWarpIps)
        {
            Stop();
            bool updated = ApplyPendingUpdate(); // winws2 остановлен - самое время подменить файлы
            if (!s.UsesZapret) return null;
            if (!Installed) return "zapret2 не установлен";

            string err = await StartWithRetryAsync(s, restrictToWarpIps);
            if (err == null)
            {
                if (updated) try { Directory.Delete(OldDir, true); } catch { }
                return null;
            }
            if (updated && RollbackUpdate())
                err = await StartWithRetryAsync(s, restrictToWarpIps);
            return err;
        }

        async Task<string> StartWithRetryAsync(Strategy s, bool restrictToWarpIps)
        {
            File.WriteAllText(CfgFile, BuildConfig(s, restrictToWarpIps), new UTF8Encoding(false));
            string err = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                err = await StartOnceAsync();
                if (err == null) return null;
                Stop();
                await Task.Delay(1000); // даём WinDivert и файлам освободиться
            }
            return err;
        }

        Process _shell;

        async Task<string> StartOnceAsync()
        {
            try { File.Delete(LogFile); } catch { }

            // cmd нужен только для перенаправления вывода в файл: так winws2 не зависит от жизни окна Zarp.
            var psi = new ProcessStartInfo("cmd.exe", "/d /c \"winws2.exe @zarp.cfg > winws2.log 2>&1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Dir,
            };
            try
            {
                _shell = Process.Start(psi);
            }
            catch (Exception e)
            {
                return "Не удалось запустить winws2: " + e.Message;
            }

            // winws2 падает сразу, если фильтр/аргументы неверны или драйвер не загрузился
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(150);
                if (i >= 3 && !Running) break;
            }
            if (Running) return null;
            string log = "";
            try { log = File.ReadAllText(LogFile).Trim(); } catch { }
            return "winws2 завершился: " + (log.Length > 0 ? Tail(log, 6) : "без вывода (возможно, WinDivert заблокирован антивирусом)");
        }

        static string Tail(string text, int lines)
        {
            var l = text.Split('\n');
            return string.Join("\n", l.Skip(Math.Max(0, l.Length - lines))).Trim();
        }

        public void Stop()
        {
            foreach (var p in FindOurProcesses())
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                p.Dispose();
            }
            // cmd-обёртка держит winws2.log открытым - ждём её, иначе следующий запуск не сможет писать в лог
            if (_shell != null)
            {
                try { if (!_shell.WaitForExit(3000)) _shell.Kill(); } catch { }
                _shell.Dispose();
                _shell = null;
            }
        }

        IEnumerable<Process> FindOurProcesses()
        {
            string exe = Path.GetFullPath(Exe);
            foreach (var p in Process.GetProcessesByName("winws2"))
            {
                string path = GetProcessPath(p);
                if (path == null || string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase))
                    yield return p;
                else
                    p.Dispose();
            }
        }

        /// <summary>Другие экземпляры winws/winws2/GoodbyeDPI, которые могут мешать.</summary>
        public IEnumerable<string> ForeignDpiTools()
        {
            string exe = Path.GetFullPath(Exe);
            foreach (var name in new[] { "winws", "winws2", "goodbyedpi" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        string path = GetProcessPath(p);
                        if (path != null && !string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase))
                            yield return path;
                    }
                }
        }

        static string GetProcessPath(Process p) => ProcessUtil.GetProcessPath(p);
    }
}
