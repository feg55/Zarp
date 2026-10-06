using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    public sealed class RunResult
    {
        public int ExitCode;
        public string Output;
        public bool TimedOut;
        public bool Ok => !TimedOut && ExitCode == 0;
    }

    public static class ProcessUtil
    {
        /// <summary>Запустить консольную программу скрыто и дождаться вывода.</summary>
        public static async Task<RunResult> RunAsync(string exe, string args, int timeoutMs = 15000, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeoutMs);
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                // Windows PowerShell, запущенный из PowerShell 7 (так выполняются шаги на серверах GitHub),
                // наследует его PSModulePath и не может загрузить собственные модули.
                if (Path.GetFileName(exe).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
                    psi.EnvironmentVariables.Remove("PSModulePath");
                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    var stdout = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var stderr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    p.OutputDataReceived += (s, e) => { if (e.Data == null) stdout.TrySetResult(true); else lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data == null) stderr.TrySetResult(true); else lock (sb) sb.AppendLine(e.Data); };
                    p.EnableRaisingEvents = true;
                    p.Exited += (s, e) => exited.TrySetResult(true);
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var complete = Task.WhenAll(exited.Task, stdout.Task, stderr.Task);
                    var expired = Task.Delay(Timeout.Infinite, deadline.Token);
                    if (await Task.WhenAny(complete, expired).ConfigureAwait(false) != complete)
                    {
                        try { p.Kill(); } catch { }
                        try { p.CancelOutputRead(); } catch { }
                        try { p.CancelErrorRead(); } catch { }
                        ct.ThrowIfCancellationRequested();
                        lock (sb) return new RunResult { ExitCode = -1, Output = sb.ToString().Trim(), TimedOut = true };
                    }
                    ct.ThrowIfCancellationRequested();
                    lock (sb) return new RunResult { ExitCode = p.ExitCode, Output = sb.ToString().Trim() };
                }
            }
        }

        public static async Task WaitForExitAsync(Process process)
        {
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (s, e) => exited.TrySetResult(true);
            process.Exited += handler;
            try
            {
                process.EnableRaisingEvents = true;
                if (!process.HasExited) await exited.Task.ConfigureAwait(false);
            }
            finally { process.Exited -= handler; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// Полный путь exe процесса или null. В отличие от Process.MainModule работает
        /// и для 64-битных процессов, и для процессов с правами администратора.
        /// </summary>
        public static string GetProcessPath(Process p)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// Полный путь к системной программе. Голое имя ищется сначала рядом с Zarp.exe,
        /// а Zarp запущен от администратора: подложенный рядом msiexec.exe или powershell.exe получил бы эти права.
        /// </summary>
        public static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

        public static string PowerShellExe => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        public static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
