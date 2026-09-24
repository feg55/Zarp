using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>Ошибка случилась на второй, независимой проверке.</summary>
        public bool Rechecked { get; set; }
        public DateTime When { get; set; }
        /// <summary>Стратегия прошла вторую, независимую проверку (на другом эндпоинте WARP).</summary>
        public bool Confirmed { get; set; }

        /// <summary>Чем меньше, тем лучше. Пинг весит больше: он влияет на всю работу, а подключение - один раз.</summary>
        [ScriptIgnore]
        public int Score => Ok ? ConnectMs + PingMs * 4 : int.MaxValue;

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
        /// Каждый тест - на новый эндпоинт WARP (IP:порт). Без этого тест «наследует» состояние DPI
        /// от предыдущего удачного подключения, и нерабочая стратегия выглядит рабочей.
        /// </summary>
        public bool IsolateTests { get; set; } = true;
        /// <summary>Проверять новые релизы zapret2 на GitHub и обновляться в фоне.</summary>
        public bool AutoUpdateZapret { get; set; } = true;
        /// <summary>Код языка интерфейса; null - как в системе.</summary>
        public string Language { get; set; }

        static string _path;

        public static AppConfig Load(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    var cfg = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8));
                    if (cfg != null)
                    {
                        cfg.Results = cfg.Results ?? new Dictionary<string, TestResult>();
                        return cfg;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.configBroken", e.Message));
            }
            return new AppConfig();
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
            try
            {
                string json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(_path, json, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Write(L.T("log.configSaveFailed", e.Message));
            }
        }
    }
}
