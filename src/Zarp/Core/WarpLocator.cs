using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zarp.Core
{
    /// <summary>
    /// Ищет warp-cli.exe. Раньше проверялись две папки Program Files, и WARP, стоящий в другом месте
    /// (другой диск, свой каталог, Program Files (x86)), оставался незамеченным. Надёжнее всего спросить Windows:
    /// у установленного WARP зарегистрирована служба CloudflareWARP, а рядом с её файлом лежит warp-cli.exe.
    /// </summary>
    public static class WarpLocator
    {
        public sealed class Result
        {
            public string CliPath;
            /// <summary>Что проверялось и что нашлось. Попадает в журнал, чтобы было видно, почему WARP не найден.</summary>
            public readonly List<string> Report = new List<string>();
        }

        const string CliName = "warp-cli.exe";
        const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\CloudflareWARP";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        // Продукт назывался «Cloudflare WARP», с 2026 года в списке программ он «Cloudflare One Client».
        static readonly string[] FolderNames = { "Cloudflare WARP", "Cloudflare One Client", "Cloudflare One" };
        static readonly Regex ProductName = new Regex(@"Cloudflare\s+(WARP|One)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Result Find()
        {
            var r = new Result();
            FromService(r);
            FromRunningService(r);
            FromInstalledPrograms(r);
            FromKnownFolders(r);
            FromPath(r);
            return r;
        }

        static void FromService(Result r)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(ServiceKey))
                {
                    string image = key?.GetValue("ImagePath") as string;
                    if (image == null) { r.Report.Add("service CloudflareWARP: not registered"); return; }
                    string exe = ParseImagePath(image);
                    r.Report.Add("service CloudflareWARP: " + exe);
                    Probe(r, Path.GetDirectoryName(exe));
                }
            }
            catch (Exception e) { r.Report.Add("service lookup failed: " + e.Message); }
        }

        static void FromRunningService(Result r)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("warp-svc"))
                    using (p)
                    {
                        string path = ProcessUtil.GetProcessPath(p);
                        r.Report.Add("process warp-svc: " + (path ?? "path unavailable"));
                        if (path != null) Probe(r, Path.GetDirectoryName(path));
                    }
            }
            catch (Exception e) { r.Report.Add("process lookup failed: " + e.Message); }
        }

        static void FromInstalledPrograms(Result r)
        {
            int entries = 0;
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var uninstall = root.OpenSubKey(UninstallKey))
                        {
                            if (uninstall == null) continue;
                            foreach (string name in uninstall.GetSubKeyNames())
                                using (var app = uninstall.OpenSubKey(name))
                                {
                                    string display = app?.GetValue("DisplayName") as string;
                                    if (display == null || !ProductName.IsMatch(display)) continue;
                                    entries++;
                                    string location = app.GetValue("InstallLocation") as string;
                                    string icon = (app.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                                    r.Report.Add("installed program: " + display + (string.IsNullOrWhiteSpace(location) ? "" : " at " + location));
                                    Probe(r, location);
                                    if (!string.IsNullOrWhiteSpace(icon)) Probe(r, SafeDirectory(icon));
                                }
                        }
                    }
                    catch (Exception e) { r.Report.Add("program list failed: " + e.Message); }
                }
            if (entries == 0) r.Report.Add("installed programs: no Cloudflare WARP entry");
        }

        static void FromKnownFolders(Result r)
        {
            var checkedFolders = new List<string>();
            foreach (string folder in KnownFolders())
            {
                checkedFolders.Add(folder);
                Probe(r, folder);
            }
            r.Report.Add("folders checked: " + checkedFolders.Count);
        }

        /// <summary>Типичные места установки на системном диске и на всех остальных фиксированных дисках.</summary>
        internal static IEnumerable<string> KnownFolders()
        {
            var roots = new List<string>
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            };
            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
                }
            }
            catch { }
            return roots.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.TrimEnd('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(root => FolderNames.Select(name => Path.Combine(root, "Cloudflare", name)))
                .ToList();
        }

        static void FromPath(Result r)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                Probe(r, dir.Trim().Trim('"'));
        }

        static void Probe(Result r, string dir)
        {
            if (r.CliPath != null || string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                string candidate = Path.Combine(dir, CliName);
                if (File.Exists(candidate))
                {
                    r.CliPath = candidate;
                    r.Report.Add("found: " + candidate);
                }
            }
            catch { } // недопустимые символы в пути из PATH или реестра
        }

        static string SafeDirectory(string file)
        {
            try { return Path.GetDirectoryName(file); }
            catch { return null; }
        }

        /// <summary>ImagePath службы: путь в кавычках либо без них, после него могут идти аргументы.</summary>
        internal static string ParseImagePath(string imagePath)
        {
            string s = Environment.ExpandEnvironmentVariables((imagePath ?? "").Trim());
            if (s.StartsWith("\\??\\")) s = s.Substring(4);
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                return end > 0 ? s.Substring(1, end - 1) : s.Substring(1);
            }
            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe >= 0 ? s.Substring(0, exe + 4) : s;
        }
    }
}
