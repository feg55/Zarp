using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>Состояние одной базы для окна: идёт ли загрузка, сколько скачано, что лежит на диске и чем кончилась прошлая попытка.</summary>
    public sealed class GeoFileState
    {
        public bool Downloading;
        public long Bytes;
        public int Categories;
        public DateTime UpdatedAt;
        public string Error;

        public bool Exists => UpdatedAt != default(DateTime);
    }

    /// <summary>
    /// Базы GeoIP и GeoSite (V2Ray/Xray .dat). Файлы хранятся по адресу источника: у другого адреса свои данные, а неудачная
    /// загрузка никогда не заменяет последнюю исправную базу. Скачанный файл целиком проверяется до замены.
    /// </summary>
    public sealed class GeoData
    {
        public const long MaxBytes = 64L << 20;
        static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);
        const int MaxRedirects = 6;

        public string Dir { get; }
        readonly object _lock = new object();
        readonly object _files = new object();
        readonly Dictionary<string, GeoFileState> _state = new Dictionary<string, GeoFileState>();
        readonly SemaphoreSlim _download = new SemaphoreSlim(1, 1);

        /// <summary>Создаёт обработчик HTTP. Подменяется в тестах, чтобы загрузка не ходила в сеть.</summary>
        internal Func<HttpMessageHandler> HandlerFactory = () => new HttpClientHandler { AllowAutoRedirect = false };

        /// <summary>Состояние любой базы изменилось (из любого потока).</summary>
        public event Action Changed;

        public GeoData(string dir)
        {
            Dir = dir;
        }

        public static string Key(string kind, string url)
        {
            if (kind != "geoip" && kind != "geosite") throw new ArgumentException("kind");
            using (var sha = SHA256.Create())
                return kind + "-" + string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(url)).Take(16).Select(b => b.ToString("x2")));
        }

        public string DataFile(string kind, string url) => Path.Combine(Dir, Key(kind, url) + ".dat");
        string MetaFile(string kind, string url) => Path.Combine(Dir, Key(kind, url) + ".json");

        /// <summary>Что известно о базе: файл на диске плюс ход и итог загрузки в этой сессии.</summary>
        public GeoFileState State(string kind, string url)
        {
            var result = new GeoFileState();
            try
            {
                string file = DataFile(kind, url);
                if (File.Exists(file))
                {
                    var info = new FileInfo(file);
                    result.Bytes = info.Length;
                    result.UpdatedAt = info.LastWriteTime;
                    try
                    {
                        var meta = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(MetaFile(kind, url))) as Dictionary<string, object>;
                        if (meta != null && meta.TryGetValue("categories", out var n)) result.Categories = Convert.ToInt32(n);
                    }
                    catch { }
                }
            }
            catch { }
            lock (_lock)
            {
                if (_state.TryGetValue(Key(kind, url), out var live))
                {
                    result.Downloading = live.Downloading;
                    result.Error = live.Error;
                    if (live.Downloading) result.Bytes = live.Bytes;
                }
            }
            return result;
        }

        public bool AnyDownloading
        {
            get { lock (_lock) return _state.Values.Any(s => s.Downloading); }
        }

        void Update(string key, Action<GeoFileState> change)
        {
            lock (_lock)
            {
                if (!_state.TryGetValue(key, out var s)) _state[key] = s = new GeoFileState();
                change(s);
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Скачать базу с адреса. Ошибки не пробрасываются: итог виден в State(...).Error, прежняя база остаётся на месте.
        /// Только адрес с неверным видом - исключение ArgumentException.
        /// </summary>
        public async Task DownloadAsync(string kind, string url, CancellationToken ct = default)
        {
            if (!RoutingSettings.ValidSource(url)) throw new ArgumentException(L.T("geo.invalidUrl"));
            string key = Key(kind, url);
            await _download.WaitAsync(ct).ConfigureAwait(false);
            string staging = null;
            try
            {
                Directory.CreateDirectory(Dir);
                Update(key, s => { s.Downloading = true; s.Bytes = 0; s.Error = null; });
                staging = Path.Combine(Dir, "download-" + Guid.NewGuid().ToString("N") + ".part");
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(Deadline);
                    await FetchAsync(url, staging, bytes => Update(key, s => s.Bytes = bytes), limit.Token).ConfigureAwait(false);
                    limit.Token.ThrowIfCancellationRequested();
                    int categories = await Task.Run(() => GeoDat.Info(kind, staging).Count, limit.Token).ConfigureAwait(false);
                    limit.Token.ThrowIfCancellationRequested();
                    lock (_files)
                    {
                        string data = DataFile(kind, url);
                        if (File.Exists(data)) File.Replace(staging, data, null);
                        else File.Move(staging, data);
                        File.WriteAllText(MetaFile(kind, url), new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["categories"] = categories }), new UTF8Encoding(false));
                    }
                }
                Update(key, s => { s.Downloading = false; s.Error = null; });
            }
            catch (OperationCanceledException)
            {
                Update(key, s => { s.Downloading = false; s.Error = L.T("geo.cancelled"); });
            }
            catch (Exception)
            {
                // исключения сети могут содержать адрес со служебными параметрами: наружу идёт только общее сообщение
                Update(key, s => { s.Downloading = false; s.Error = L.T("geo.failed"); });
            }
            finally
            {
                if (staging != null) try { File.Delete(staging); } catch { }
                _download.Release();
            }
        }

        /// <summary>Скачивание с ручной обработкой переходов: каждый адрес должен быть HTTPS без имени и пароля.</summary>
        async Task FetchAsync(string source, string target, Action<long> progress, CancellationToken ct)
        {
            string url = source;
            using (var http = new HttpClient(HandlerFactory(), true) { Timeout = Timeout.InfiniteTimeSpan })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Zarp/1.0");
                for (int hop = 0; hop <= MaxRedirects; hop++)
                {
                    if (!RoutingSettings.ValidSource(url)) throw new IOException("unsafe address");
                    using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        int code = (int)response.StatusCode;
                        if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                        {
                            var location = response.Headers.Location;
                            if (location == null) throw new IOException("redirect without a location");
                            url = new Uri(new Uri(url), location).ToString();
                            continue;
                        }
                        long? length = response.Content.Headers.ContentLength;
                        if (code != 200 || (length.HasValue && length.Value > MaxBytes)) throw new IOException("unexpected response");
                        using (ct.Register(response.Dispose)) // зависшее чтение прерывается закрытием ответа
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[64 * 1024];
                            long bytes = 0, reported = 0;
                            int n;
                            try
                            {
                                while ((n = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    bytes += n;
                                    if (bytes > MaxBytes) throw new IOException("file is too large");
                                    output.Write(buffer, 0, n);
                                    if (bytes - reported >= 256 * 1024) { progress(bytes); reported = bytes; }
                                }
                            }
                            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                            output.Flush(true);
                            if (bytes == 0 || (length.HasValue && bytes != length.Value)) throw new IOException("incomplete download");
                            progress(bytes);
                            return;
                        }
                    }
                }
                throw new IOException("too many redirects");
            }
        }

        // ------------------------------------------------------------------ встроенные диапазоны стран

        static Dictionary<string, List<string>> _bundled;

        /// <summary>Встроенные IP-диапазоны России, Ирана и Китая (ipverse/country-ip-blocks, CC0). Нужны до загрузки своей базы GeoIP.</summary>
        public static IDictionary<string, List<string>> BundledRanges()
        {
            lock (typeof(GeoData))
            {
                if (_bundled != null) return _bundled;
                var result = new Dictionary<string, List<string>>();
                var assembly = typeof(GeoData).Assembly;
                foreach (var country in new[] { "ru", "ir", "cn" })
                {
                    var list = new List<string>();
                    using (var stream = assembly.GetManifestResourceStream("routes/" + country + ".txt"))
                    {
                        if (stream != null)
                            using (var reader = new StreamReader(stream, Encoding.ASCII))
                            {
                                string line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    line = line.Trim();
                                    if (line.Length > 0 && !line.StartsWith("#")) list.Add(line);
                                }
                            }
                    }
                    if (list.Count > 0) result[country] = list;
                }
                return _bundled = result;
            }
        }

        /// <summary>
        /// Правила действующего набора в виде правил sing-box. Для адреса по умолчанию, пока своя база GeoIP не скачана,
        /// используются встроенные диапазоны стран; для чужого адреса они не подставляются.
        /// </summary>
        public List<object> Compile(RoutingSettings settings)
        {
            var preset = settings.Active();
            if (preset == null) return new List<object>();
            RouteRules.Validate(preset); // ошибка в самих правилах называет группу и строку и не маскируется под «нет базы»
            lock (_files)
            {
                string ip = DataFile("geoip", settings.GeoipUrl), site = DataFile("geosite", settings.GeositeUrl);
                bool haveIp = File.Exists(ip);
                var bundled = !haveIp && settings.GeoipUrl == RoutingSettings.DefaultGeoipUrl ? BundledRanges() : null;
                try { return RouteRules.Compile(preset, haveIp ? ip : null, File.Exists(site) ? site : null, bundled); }
                catch (RouteRuleException e) { throw new RouteRuleException(L.T("geo.rulesUnavailable") + ": " + e.Message); }
            }
        }
    }
}
