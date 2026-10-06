using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>Результат проверки одной стратегии.</summary>
    public sealed class TestResult
    {
        public string StrategyId { get; set; }
        public bool Ok { get; set; }
        public int ConnectMs { get; set; }
        public int PingMs { get; set; }
        /// <summary>Текст ошибки как есть (например, вывод winws2 или запись из старых версий).</summary>
        public string Error { get; set; }
        /// <summary>Ошибка как ключ перевода и аргументы: показывается на текущем языке.</summary>
        public string ErrorKey { get; set; }
        public string[] ErrorArgs { get; set; }
        /// <summary>Ошибка случилась на повторной проверке.</summary>
        public bool Rechecked { get; set; }
        public DateTime When { get; set; }
        /// <summary>Стратегия прошла два теста. Независимость адресов хранится отдельно.</summary>
        public bool Confirmed { get; set; }
        public string Endpoint { get; set; }
        public bool EndpointReused { get; set; }
        public bool Independent { get; set; }

        /// <summary>Чем меньше, тем лучше. Пинг весит больше: он влияет на всю работу, а подключение - один раз.</summary>
        [ScriptIgnore]
        public int Score => Ok ? (int)Math.Min(int.MaxValue - 1L, Math.Max(0L, ConnectMs) + Math.Max(0L, PingMs) * 4) : int.MaxValue;

        public void Fail(Msg error)
        {
            ErrorKey = error.Key;
            ErrorArgs = error.ArgStrings;
        }

        [ScriptIgnore]
        public string ErrorText => ErrorKey != null ? L.T(ErrorKey, ErrorArgs ?? new string[0]) : Error ?? "";

        /// <summary>Ошибка для списка стратегий и журнала.</summary>
        [ScriptIgnore]
        public string DisplayError => Rechecked ? L.T("result.notConfirmed", ErrorText) : ErrorText;
    }

    /// <summary>Настройки приложения. Хранятся в %LOCALAPPDATA%\Zarp\zarp.json.</summary>
    public sealed class AppConfig
    {
        public string SelectedStrategyId { get; set; }
        public Dictionary<string, TestResult> Results { get; set; } = new Dictionary<string, TestResult>();

        /// <summary>Сколько секунд ждать подключения WARP на одной стратегии.</summary>
        public int TestTimeoutSec { get; set; } = 15;
        /// <summary>Сколько рабочих стратегий найти, прежде чем остановить поиск (0 - проверить все).</summary>
        public int StopAfterWorking { get; set; } = 3;
        public bool AutoConnectOnStart { get; set; } = false;
        /// <summary>Спрашивать при закрытии окна, пока пользователь не запомнит действие.</summary>
        public bool AskBeforeClose { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool DisconnectOnExit { get; set; } = true;
        /// <summary>Перехватывать только трафик на адреса WARP (рекомендуется).</summary>
        public bool RestrictToWarpIps { get; set; } = true;
        /// <summary>
        /// При перепроверке выбирать другой эндпоинт WARP. Пул конечен, повторное использование
        /// адресов отмечается в результате и не считается независимой проверкой.
        /// </summary>
        public bool IsolateTests { get; set; } = true;
        /// <summary>Проверять новые релизы zapret2 на GitHub и обновляться в фоне.</summary>
        public bool AutoUpdateZapret { get; set; } = true;
        /// <summary>Код языка интерфейса; null - как в системе.</summary>
        public string Language { get; set; }

        string _path;
        bool _recovered;
        readonly object _saveLock = new object();

        public static AppConfig Load(string path)
        {
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var cfg = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(candidate, Encoding.UTF8));
                    if (cfg == null) throw new InvalidDataException("Empty configuration");
                    cfg._path = path;
                    cfg._recovered = candidate != path;
                    cfg.Normalize();
                    return cfg;
                }
                catch (Exception e)
                {
                    Log.Write(L.T("log.configBroken", e.Message));
                }
            }
            return new AppConfig { _path = path };
        }

        void Normalize()
        {
            TestTimeoutSec = Math.Max(5, Math.Min(60, TestTimeoutSec));
            StopAfterWorking = Math.Max(0, Math.Min(100, StopAfterWorking));
            Results = Results ?? new Dictionary<string, TestResult>();
            foreach (var entry in Results.ToList())
            {
                var r = entry.Value;
                if (string.IsNullOrWhiteSpace(entry.Key) || r == null || r.StrategyId != entry.Key || r.ConnectMs < 0 || r.PingMs < 0)
                    Results.Remove(entry.Key);
                else if (!r.Ok) { r.Confirmed = false; r.Independent = false; }
            }
        }

        /// <summary>
        /// Только язык из настроек - до создания окон и без побочных эффектов Load.
        /// Нужен, чтобы уже первые сообщения (например, о второй копии Zarp) были на нужном языке.
        /// </summary>
        public static string ReadLanguage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                return d != null && d.TryGetValue("Language", out var v) ? v as string : null;
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            lock (_saveLock) SaveCore();
        }

        void SaveCore()
        {
            string tmp = null;
            try
            {
                string json = new JavaScriptSerializer().Serialize(this);
                tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, _recovered ? null : _path + ".bak");
                else File.Move(tmp, _path);
                _recovered = false;
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.configSaveFailed", e.Message));
            }
            finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
        }
    }
}
