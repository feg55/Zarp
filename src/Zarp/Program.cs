using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Zarp.Core;
using Zarp.UI;

namespace Zarp
{
    static class Program
    {
        const string MutexName = "Global\\Zarp_single_instance";
        const string ShowEventName = "Global\\Zarp_show";

        [STAThread]
        static void Main(string[] args)
        {
            using (var mutex = new Mutex(true, MutexName, out bool first))
            {
                if (!first)
                {
                    // уже запущено — просто показать существующее окно
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                    return;
                }

                // GitHub и Cloudflare требуют TLS 1.2+; явное включение спасает на системах со старыми настройками .NET
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288 /* Tls13 */;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // exe — один файл, лежит где угодно; всё изменяемое (настройки, журнал, zapret2) — в %LOCALAPPDATA%\Zarp
                string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zarp");
                Directory.CreateDirectory(dataDir);
                MigrateFromExeDir(exeDir, dataDir);

                Log.Init(Path.Combine(dataDir, "zarp.log"));
                Log.Line += StartupLog.Add;
                Log.Write("Zarp запущен: " + Application.ExecutablePath);

                var engine = new Engine(dataDir);
                bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
                bool connect = args.Any(a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));
                var form = new MainForm(engine, autostart, connect);

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    var t = new Thread(() =>
                    {
                        try
                        {
                            while (showEvent.WaitOne())
                                try { form.BeginInvoke((Action)form.ShowFromTray); } catch { }
                        }
                        catch (ObjectDisposedException) { } // программа завершается
                    }) { IsBackground = true };
                    t.Start();
                    Application.Run(form);
                }
                Log.Write("Zarp завершён");
            }
        }

        /// <summary>Ранние версии хранили настройки рядом с exe — переносим их в папку данных.</summary>
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
