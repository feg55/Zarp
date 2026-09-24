using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Zarp.Core;

namespace Zarp
{
    /// <summary>
    /// Другие запущенные копии Zarp (и старого ZWARP). Нужны, чтобы новая версия не «проваливалась»
    /// в окно старой: раньше второй запуск просто показывал уже работающую копию, даже если это был другой exe.
    /// </summary>
    static class Instances
    {
        public const string MutexName = "Global\\Zarp_single_instance";
        public const string ShowEventName = "Global\\Zarp_show";
        /// <summary>Просьба к работающей копии корректно завершиться (слушают версии начиная с 1.0.1).</summary>
        public const string QuitEventName = "Global\\Zarp_quit";

        public sealed class Other
        {
            public Process Process;
            public string Path;
            public bool Legacy => Process.ProcessName.StartsWith("ZWARP", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Процессы Zarp/ZWARP, кроме текущего, у которых удалось узнать путь к exe.</summary>
        public static List<Other> Find()
        {
            int me = Process.GetCurrentProcess().Id;
            var list = new List<Other>();
            foreach (var p in Process.GetProcesses())
            {
                string n = p.ProcessName;
                bool ours = n.StartsWith("Zarp", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ZWARP", StringComparison.OrdinalIgnoreCase);
                string path = ours && p.Id != me ? ProcessUtil.GetProcessPath(p) : null;
                if (path == null || !IsZarpExe(path)) { p.Dispose(); continue; } // не наш процесс или чужая сессия
                list.Add(new Other { Process = p, Path = path });
            }
            return list;
        }

        /// <summary>По имени процесса не угадать (Zarp (1).exe, чужие программы) - смотрим ProductName в свойствах exe.</summary>
        static bool IsZarpExe(string path)
        {
            try
            {
                string product = FileVersionInfo.GetVersionInfo(path).ProductName ?? "";
                return product.Equals("Zarp", StringComparison.OrdinalIgnoreCase) || product.Equals("ZWARP", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Подать сигнал работающей копии. false, если её событие не найдено.</summary>
        public static bool Signal(string name)
        {
            try
            {
                using (var e = EventWaitHandle.OpenExisting(name))
                    return e.Set();
            }
            catch { return false; }
        }

        /// <summary>
        /// Закрыть другие копии: новые версии просим завершиться сами (они отключают WARP и останавливают winws2),
        /// старые, которые такой просьбы не понимают, закрываем принудительно.
        /// </summary>
        public static void Stop(List<Other> others)
        {
            bool asked = Signal(QuitEventName);
            foreach (var o in others)
            {
                try
                {
                    int wait = asked && !o.Legacy ? 10000 : 0;
                    if (!o.Process.WaitForExit(wait))
                    {
                        o.Process.Kill();
                        o.Process.WaitForExit(3000);
                    }
                }
                catch { }
                // старые версии держали zapret2 рядом с exe: после принудительного закрытия их winws2 остаётся жить
                KillWinws2Under(Path.GetDirectoryName(o.Path));
                o.Process.Dispose();
            }
        }

        static void KillWinws2Under(string dir)
        {
            string prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName("winws2"))
            {
                using (p)
                {
                    string path = ProcessUtil.GetProcessPath(p);
                    if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }
        }
    }
}
