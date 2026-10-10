using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Zarp.Core
{
    /// <summary>
    /// Что нужно знать о запущенном sing-box после перезапуска Zarp, чтобы подхватить подключение:
    /// процесс, порт и данные проверочного входа. Файл лежит в папке данных и не содержит пароля сервера.
    /// </summary>
    public sealed class SingBoxState
    {
        public int Pid { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string ProfileId { get; set; }
    }

    /// <summary>
    /// sing-box (https://github.com/SagerNet/sing-box) - движок собственного сервера: VLESS, Trojan, Hysteria2,
    /// виртуальный адаптер, правила маршрутизации. Вшит в Zarp.exe, как zapret2, и запускается отдельным процессом.
    /// Zarp не линкуется с ним и не меняет его файлы (лицензия GPL-3.0-or-later, см. THIRD_PARTY_NOTICES.md).
    /// </summary>
    public sealed class SingBox
    {
        /// <summary>Имя виртуального адаптера. Сторонним VPN он не считается (см. NetCheck).</summary>
        public const string TunName = "Zarp";

        public string Dir { get; }
        public string Exe => Path.Combine(Dir, "sing-box.exe");
        public string LogFile => Path.Combine(Dir, "sing-box.log");
        string ConfigFile => Path.Combine(Dir, "config.json");
        string StateFile => Path.Combine(Dir, "session.json");

        public SingBox(string dir)
        {
            Dir = dir;
        }

        // ------------------------------------------------------------------ вшитый архив

        static byte[] EmbeddedZip()
        {
            using (var s = typeof(SingBox).Assembly.GetManifestResourceStream("singbox.zip"))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string _embeddedVersion;
        static string _embeddedHash;

        /// <summary>Версия sing-box, вшитая в exe, или null, если exe собран без него.</summary>
        public static string EmbeddedVersion
        {
            get
            {
                if (_embeddedVersion != null) return _embeddedVersion.Length == 0 ? null : _embeddedVersion;
                _embeddedVersion = "";
                var data = EmbeddedZip();
                if (data == null) return null;
                using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                using (var r = new StreamReader(zip.GetEntry("version.txt").Open()))
                    _embeddedVersion = r.ReadToEnd().Trim();
                return _embeddedVersion;
            }
        }

        /// <summary>SHA-256 вшитого sing-box.exe: запускается только файл, совпадающий с ним.</summary>
        static string EmbeddedExeHash()
        {
            if (_embeddedHash != null) return _embeddedHash;
            using (var zip = new ZipArchive(new MemoryStream(EmbeddedZip()), ZipArchiveMode.Read))
            using (var s = zip.GetEntry("sing-box.exe").Open())
            using (var sha = SHA256.Create())
                return _embeddedHash = Hex(sha.ComputeHash(s));
        }

        static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        /// <summary>Версия установленного sing-box (тег релиза) или null.</summary>
        public string Version
        {
            get
            {
                try { return File.ReadAllText(Path.Combine(Dir, "version.txt")).Trim(); }
                catch { return null; }
            }
        }

        public bool Installed => File.Exists(Exe) && Version != null;

        /// <summary>
        /// Положить sing-box.exe из вшитого архива в папку данных. Файл заменяется, если он отличается от вшитого
        /// (в том числе подменён): запускается всегда ровно тот двоичный файл, который лежит в Zarp.exe.
        /// Возвращает true, если файл записан.
        /// </summary>
        public bool ExtractEmbedded()
        {
            if (EmbeddedVersion == null) return false;
            string want = EmbeddedExeHash();
            if (File.Exists(Exe) && Version == EmbeddedVersion && HashOf(Exe) == want) return false;
            Stop(); // запущенный exe не перезаписать
            Directory.CreateDirectory(Dir);
            using (var zip = new ZipArchive(new MemoryStream(EmbeddedZip()), ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries.Where(e => e.Name.Length > 0 && e.Name != "version.txt"))
                {
                    try
                    {
                        e.ExtractToFile(Path.Combine(Dir, e.Name), true);
                    }
                    catch (IOException ex) when (Zapret.IsAntivirusError(ex))
                    {
                        throw new AntivirusBlockedException(L.T("err.avBlockedFile", e.Name), ex);
                    }
                }
            }
            File.WriteAllText(Path.Combine(Dir, "version.txt"), EmbeddedVersion); // последним - признак целостности
            Log.Write(L.T("log.singboxExtracted", EmbeddedVersion));
            return true;
        }

        static string HashOf(string file)
        {
            try
            {
                using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                    return Hex(sha.ComputeHash(s));
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ проверка и запуск

        /// <summary>Проверить конфигурацию командой `sing-box check`: ничего не запускается и не меняется в системе.</summary>
        public async Task<RunResult> CheckAsync(string configJson, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, "check-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, configJson, new UTF8Encoding(false));
            try
            {
                return await ProcessUtil.RunAsync(Exe, "check -c \"" + file + "\"", 30000, ct).ConfigureAwait(false);
            }
            finally { try { File.Delete(file); } catch { } }
        }

        Process _shell;

        public bool Running
        {
            get
            {
                bool found = false;
                foreach (var p in FindOurProcesses()) { found = true; p.Dispose(); }
                return found;
            }
        }

        /// <summary>
        /// Запустить sing-box с конфигурацией и дождаться, пока откроется проверочный порт.
        /// Возвращает null при успехе или описание ошибки (с хвостом журнала sing-box).
        /// Файл конфигурации с паролем удаляется сразу после запуска.
        /// </summary>
        public async Task<Msg> StartAsync(string configJson, int waitPort, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Stop();
            try
            {
                if (!Installed) return new Msg("err.singboxMissing");
                Directory.CreateDirectory(Dir);
                try { File.Delete(LogFile); } catch { }
                File.WriteAllText(ConfigFile, configJson, new UTF8Encoding(false));
                // Пока файл открыт на чтение с запретом записи, его нельзя подменить между проверкой и запуском.
                using (var locked = new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (EmbeddedVersion != null)
                    {
                        using (var sha = SHA256.Create())
                            if (Hex(sha.ComputeHash(locked)) != EmbeddedExeHash())
                                return new Msg("err.singboxChanged");
                        locked.Position = 0;
                    }
                    // cmd нужен только для перенаправления вывода в файл: sing-box не зависит от жизни окна Zarp.
                    var psi = new ProcessStartInfo(ProcessUtil.SystemExe("cmd.exe"),
                        "/d /s /c \"\"" + Path.GetFullPath(Exe) + "\" run -c config.json -D . > sing-box.log 2>&1\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Dir,
                    };
                    try { _shell = Process.Start(psi); }
                    catch (Exception e) { return new Msg("err.singboxStart", e.Message); }

                    for (int i = 0; i < 20 && !Running; i++)
                        await Task.Delay(100, ct).ConfigureAwait(false);
                }

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 25000)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Running) return Failure();
                    if (await PortOpenAsync(waitPort).ConfigureAwait(false))
                    {
                        try { File.Delete(ConfigFile); } catch { }
                        return null;
                    }
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                Stop();
                return new Msg("err.singboxTimeout");
            }
            catch (OperationCanceledException) { Stop(); throw; }
            catch (Exception e) { Stop(); return new Msg("err.singboxStart", e.Message); }
            finally { if (!Running) { try { File.Delete(ConfigFile); } catch { } } }
        }

        /// <summary>Журнал sing-box. Читается с разрешением записи: пока процесс жив, он держит файл открытым.</summary>
        public string ReadLog()
        {
            try
            {
                using (var file = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch { return ""; }
        }

        Msg Failure()
        {
            string log = ReadLog().Trim();
            Stop();
            return log.Length > 0 ? new Msg("err.singboxExited", Tail(log, 6)) : new Msg("err.singboxExitedSilent");
        }

        static string Tail(string text, int lines)
        {
            var l = StripAnsi(text).Split('\n');
            return string.Join("\n", l.Skip(Math.Max(0, l.Length - lines))).Trim();
        }

        // sing-box красит уровни журнала управляющими последовательностями ESC[..m: в файле они только мусор
        static string StripAnsi(string text) => Regex.Replace(text, "\u001b\\[[0-9;]*m", "");

        static async Task<bool> PortOpenAsync(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync("127.0.0.1", port);
                    return await Task.WhenAny(connect, Task.Delay(300)).ConfigureAwait(false) == connect && client.Connected;
                }
            }
            catch { return false; }
        }

        public void Stop()
        {
            foreach (var p in FindOurProcesses())
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                p.Dispose();
            }
            // cmd-обёртка держит журнал открытым: без ожидания следующий запуск не сможет в него писать
            if (_shell != null)
            {
                try { if (!_shell.WaitForExit(3000)) _shell.Kill(); } catch { }
                _shell.Dispose();
                _shell = null;
            }
            try { File.Delete(StateFile); } catch { }
        }

        IEnumerable<Process> FindOurProcesses()
        {
            string exe = Path.GetFullPath(Exe);
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                string path = ProcessUtil.GetProcessPath(p);
                if (path != null && ProcessUtil.SamePath(path, exe))
                    yield return p;
                else
                    p.Dispose();
            }
        }

        /// <summary>Идентификатор нашего работающего sing-box или 0.</summary>
        public int RunningPid
        {
            get
            {
                foreach (var p in FindOurProcesses())
                    using (p) return p.Id;
                return 0;
            }
        }

        // ------------------------------------------------------------------ подхват после перезапуска Zarp

        public void SaveState(SingBoxState state)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                state.Pid = RunningPid;
                File.WriteAllText(StateFile, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>Сведения о работающем sing-box или null, если процесса нет либо файл не его.</summary>
        public SingBoxState LoadState()
        {
            try
            {
                var state = new JavaScriptSerializer().Deserialize<SingBoxState>(File.ReadAllText(StateFile, Encoding.UTF8));
                return state != null && state.Pid != 0 && state.Pid == RunningPid && state.Port > 0 ? state : null;
            }
            catch { return null; }
        }
    }
}
