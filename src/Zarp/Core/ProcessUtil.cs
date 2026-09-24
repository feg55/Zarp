using System;
using System.Diagnostics;
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
        public static Task<RunResult> RunAsync(string exe, string args, int timeoutMs = 15000, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    bool exited;
                    using (ct.Register(() => { try { p.Kill(); } catch { } }))
                        exited = p.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        return new RunResult { ExitCode = -1, Output = sb.ToString(), TimedOut = true };
                    }
                    p.WaitForExit(); // дочитать асинхронный вывод
                    lock (sb) return new RunResult { ExitCode = p.ExitCode, Output = sb.ToString().Trim() };
                }
            });
        }
    }
}
