using System;
using System.IO;

namespace Zarp.Core
{
    /// <summary>Простой лог: в файл рядом с exe и в окно приложения.</summary>
    public static class Log
    {
        static readonly object Sync = new object();
        static string _file;

        public static event Action<string> Line;

        public static void Init(string file)
        {
            _file = file;
            try
            {
                // не даём логу бесконечно расти
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                    File.Delete(file);
            }
            catch { }
        }

        public static void Write(string text)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            lock (Sync)
            {
                try { if (_file != null) File.AppendAllText(_file, line + Environment.NewLine); }
                catch { }
            }
            Line?.Invoke(line);
        }
    }
}
