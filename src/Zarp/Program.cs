using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Zarp.Core;
using Zarp.UI;

namespace Zarp
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // GitHub и Cloudflare требуют TLS 1.2+; явное включение спасает на системах со старыми настройками .NET
            System.Net.ServicePointManager.SecurityProtocol |=
                System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288 /* Tls13 */;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            bool connect = args.Any(a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));

            using (var mutex = new Mutex(true, Instances.MutexName, out bool owned))
            {
                var all = Instances.Find();
                bool sameExeRunning = all.Any(o => ProcessUtil.SamePath(o.Path, Application.ExecutablePath));
                var others = all.Where(o => !ProcessUtil.SamePath(o.Path, Application.ExecutablePath)).ToList();
                if (!owned && (sameExeRunning || others.Count == 0))
                {
                    // этот же exe уже запущен: просто показать его окно
                    Instances.Signal(Instances.ShowEventName);
                    return;
                }
                if (others.Count > 0 && !TakeOver(mutex, others, ref owned, ref connect))
                    return;

                try { Run(autostart, connect); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>
        /// Запущена другая копия (обычно старая версия из другой папки). Без этого она перехватила бы запуск
        /// и показала своё окно. Возвращает false, если эту копию запускать не нужно.
        /// </summary>
        static bool TakeOver(Mutex mutex, System.Collections.Generic.List<Instances.Other> others, ref bool owned, ref bool connect)
        {
            var paths = others.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool many = paths.Count > 1;
            var answer = MessageBox.Show(
                (many ? "Уже запущены другие копии Zarp:" : "Уже запущена другая копия Zarp:") + "\n\n" + string.Join("\n", paths) + "\n\n" +
                (many ? "Закрыть их" : "Закрыть её") + " и открыть эту? Если WARP подключён, Zarp переподключит его сам.",
                "Zarp", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                if (owned) return true; // та копия не держит блокировку (например, старый ZWARP): работаем рядом
                Instances.Signal(Instances.ShowEventName);
                return false;
            }

            bool wasConnected = WarpStatus() == "Connected";
            Instances.Stop(others);
            if (!owned)
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owned = true; } // старую копию пришлось закрыть принудительно
            }
            if (!owned)
            {
                MessageBox.Show("Не удалось закрыть другую копию Zarp. Закройте её через значок в трее и запустите Zarp снова.",
                    "Zarp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (wasConnected)
            {
                // старая копия могла остаться с подключённым WARP без своего winws2: переподключаем уже здесь
                RunWarp(w => w.DisconnectAsync());
                connect = true;
            }
            return true;
        }

        static string WarpStatus()
        {
            string status = null;
            RunWarp(async w => status = (await w.StatusAsync()).Status);
            return status;
        }

        static void RunWarp(Func<Warp, Task> action)
        {
            try
            {
                var w = new Warp();
                if (w.Installed) Task.Run(() => action(w)).Wait(20000);
            }
            catch { }
        }

        static void Run(bool autostart, bool connect)
        {
            // exe - один файл, лежит где угодно; всё изменяемое (настройки, журнал, zapret2) - в %LOCALAPPDATA%\Zarp
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zarp");
            Directory.CreateDirectory(dataDir);
            MigrateFromExeDir(exeDir, dataDir);

            Log.Init(Path.Combine(dataDir, "zarp.log"));
            Log.Line += StartupLog.Add;
            Log.Write("Zarp запущен: " + Application.ExecutablePath);
            Licenses.Extract(dataDir);

            var engine = new Engine(dataDir);
            var form = new MainForm(engine, autostart, connect);

            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Instances.ShowEventName))
            using (var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Instances.QuitEventName))
            {
                Listen(showEvent, form, () => form.ShowFromTray());
                Listen(quitEvent, form, () => { var _ = form.ExitForHandoverAsync(); });
                Application.Run(form);
            }
            Log.Write("Zarp завершён");
        }

        /// <summary>Ждать сигнала от другого процесса и выполнять действие в потоке окна.</summary>
        static void Listen(EventWaitHandle ev, Control target, Action onSignal)
        {
            var t = new Thread(() =>
            {
                try
                {
                    while (ev.WaitOne())
                        try { target.BeginInvoke(onSignal); } catch { }
                }
                catch (ObjectDisposedException) { } // программа завершается
            }) { IsBackground = true };
            t.Start();
        }

        /// <summary>Ранние версии хранили настройки рядом с exe - переносим их в папку данных.</summary>
        static void MigrateFromExeDir(string exeDir, string dataDir)
        {
            foreach (var (from, to) in new[] { ("zarp.json", "zarp.json"), ("zwarp.json", "zarp.json"), ("strategies.txt", "strategies.txt") })
            {
                try
                {
                    string src = Path.Combine(exeDir, from), dst = Path.Combine(dataDir, to);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
                catch { }
            }
        }
    }
}
