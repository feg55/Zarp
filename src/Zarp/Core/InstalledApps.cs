using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Zarp.Core
{
    /// <summary>Программа для выбора в списке «через сервер идут только эти»: полный путь exe и название для показа.</summary>
    public sealed class AppEntry
    {
        public string Path;
        public string Name;
    }

    /// <summary>
    /// Где искать программы: запущенные сейчас, ярлыки меню «Пуск» и записи App Paths. У программы в Windows нет пакета, как
    /// в Android: она определяется по пути к exe, и именно по нему sing-box узнаёт, чей это трафик.
    /// </summary>
    public static class InstalledApps
    {
        public static AppEntry Describe(string path)
        {
            string name = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                     : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : null;
            }
            catch { }
            return new AppEntry { Path = path, Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path) };
        }

        /// <summary>
        /// Все найденные программы без повторов (путь сравнивается без учёта регистра), по названию. Может занять секунды:
        /// вызывается не из потока окна.
        /// </summary>
        public static List<AppEntry> Enumerate()
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) return;
                    path = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || IsUninstaller(path)) return;
                    if (!paths.ContainsKey(path)) paths[path] = path;
                }
                catch { }
            }
            foreach (var p in RunningExecutables()) Add(p);
            foreach (var p in StartMenuTargets()) Add(p);
            foreach (var p in AppPathsRegistry()) Add(p);
            return paths.Keys.Select(Describe)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool IsUninstaller(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Запущенные программы: свои (вне папки Windows) и любые с окном. Служебные процессы Windows не нужны.</summary>
        static IEnumerable<string> RunningExecutables()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + System.IO.Path.DirectorySeparatorChar;
            foreach (var p in Process.GetProcesses())
            {
                string path = null;
                try
                {
                    using (p)
                    {
                        path = ProcessUtil.GetProcessPath(p);
                        if (path == null) continue;
                        bool system = path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
                        if (system && p.MainWindowHandle == IntPtr.Zero) path = null;
                    }
                }
                catch { path = null; } // процесс мог завершиться, пока его разглядывали
                if (path != null) yield return path;
            }
        }

        /// <summary>Цели ярлыков меню «Пуск» (общего и личного). Ярлык читает WScript.Shell: так не нужен ни COM-интероп, ни ссылки на сборки.</summary>
        static IEnumerable<string> StartMenuTargets()
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) }
                .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count == 0) yield break;
            object shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null) shell = Activator.CreateInstance(type);
            }
            catch { }
            if (shell == null) yield break;
            try
            {
                foreach (var root in roots)
                {
                    IEnumerable<string> links;
                    try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
                    catch { continue; }
                    foreach (var link in links)
                    {
                        string target = null;
                        try
                        {
                            object shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                            target = shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
                            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(target)) yield return target;
                    }
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        /// <summary>Записи App Paths: так Windows запоминает путь к chrome.exe, firefox.exe и десяткам других программ.</summary>
        static IEnumerable<string> AppPathsRegistry()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var found = new List<string>();
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                    {
                        if (key == null) continue;
                        foreach (var name in key.GetSubKeyNames())
                            using (var sub = key.OpenSubKey(name))
                                if (sub?.GetValue(null) is string value) found.Add(Environment.ExpandEnvironmentVariables(value));
                    }
                }
                catch { }
                foreach (var path in found) yield return path;
            }
        }
    }
}
