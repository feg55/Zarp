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
        public string Error { get; set; }
        public DateTime When { get; set; }
        /// <summary>Стратегия прошла вторую, независимую проверку (на другом эндпоинте WARP).</summary>
        public bool Confirmed { get; set; }

        /// <summary>Чем меньше, тем лучше. Пинг весит больше: он влияет на всю работу, а подключение - один раз.</summary>
        [ScriptIgnore]
        public int Score => Ok ? ConnectMs + PingMs * 4 : int.MaxValue;
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
                Log.Write("Настройки повреждены, используются значения по умолчанию: " + e.Message);
            }
            return new AppConfig();
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
                Log.Write("Не удалось сохранить настройки: " + e.Message);
            }
        }
    }
}
