using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    /// <summary>Шаг установки для окна: текст и процент загрузки (-1, если процент не определён).</summary>
    public struct WarpStep
    {
        public Msg Message;
        public int Percent;

        public WarpStep(Msg message, int percent = -1)
        {
            Message = message;
            Percent = percent;
        }
    }

    /// <summary>
    /// Скачивает официальный установщик Cloudflare WARP, проверяет подпись Cloudflare и ставит его без окон.
    /// Установщик не вшит в Zarp: клиент WARP проприетарный (Cloudflare лицензирует его, а не продаёт),
    /// и он весит около 60 МБ. Поэтому он скачивается с серверов Cloudflare, как если бы пользователь сделал это сам.
    /// </summary>
    public static class WarpInstaller
    {
        public const string DownloadUrl = "https://downloads.cloudflareclient.com/v1/download/windows/ga";
        public const string InstallerFileName = "Cloudflare_WARP.msi";

        const long MinSize = 5L << 20, MaxSize = 400L << 20;
        static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

        public enum MsiResult { Installed, RestartNeeded, AlreadyInstalled, Failed }

        /// <summary>
        /// Скачать (или взять положенный вручную) установщик, проверить подпись и установить.
        /// WARP по-прежнему нужно найти и запустить после этого: это делает вызывающий.
        /// </summary>
        public static async Task InstallAsync(string dataDir, IProgress<WarpStep> progress, CancellationToken ct)
        {
            string workDir = Path.Combine(dataDir, "warp-installer");
            Directory.CreateDirectory(workDir);
            string msi = Path.Combine(workDir, InstallerFileName);
            string manual = FindManualInstaller(dataDir);
            string file = manual ?? msi;
            string expectedHash = null;

            if (manual != null)
            {
                Log.Write(L.T("log.warpManualFile", manual));
            }
            else
            {
                progress?.Report(new WarpStep(new Msg("progress.warpDownloading", 0), 0));
                var percent = new Progress<int>(p => progress?.Report(new WarpStep(new Msg("progress.warpDownloading", p), p)));
                try
                {
                    expectedHash = await DownloadWithRetryAsync(DownloadUrl, msi, MinSize, MaxSize, StallTimeout, percent, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    throw new Exception(L.T("err.warpDownload", e.Message), e);
                }
                Log.Write(L.T("log.warpDownloaded", (new FileInfo(msi).Length / 1048576.0).ToString("0.0"), expectedHash));
            }

            // Zarp запущен от администратора, а папка данных принадлежит пользователю. Пока файл открыт на чтение
            // с запретом записи, другой процесс не может подменить его между проверкой подписи и запуском.
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (expectedHash != null && Hex(Sha256(locked)) != expectedHash)
                    throw new Exception(L.T("err.warpChanged"));

                progress?.Report(new WarpStep(new Msg("progress.warpVerifying")));
                string signer = await VerifySignatureAsync(file);
                Log.Write(L.T("log.warpSigned", signer));
                ct.ThrowIfCancellationRequested();

                progress?.Report(new WarpStep(new Msg("progress.warpInstalling")));
                string log = Path.Combine(workDir, "install.log");
                // Без ct: прерванная установка MSI хуже незавершённого ожидания.
                var run = await ProcessUtil.RunAsync(ProcessUtil.SystemExe("msiexec.exe"),
                    $"/i \"{file}\" /qn /norestart /L*v \"{log}\"", 15 * 60 * 1000);
                if (run.TimedOut) throw new Exception(L.T("err.warpInstall", "timeout", log));
                switch (InterpretMsiExit(run.ExitCode))
                {
                    case MsiResult.RestartNeeded: Log.Write(L.T("log.warpRestart")); break;
                    case MsiResult.Failed: throw new Exception(L.T("err.warpInstall", run.ExitCode, log));
                }
            }
            if (manual == null)
                try { File.Delete(msi); } catch { }
        }

        /// <summary>Коды возврата msiexec: 0 успех, 3010 нужна перезагрузка, 1638 уже установлена такая или более новая версия.</summary>
        internal static MsiResult InterpretMsiExit(int code)
        {
            switch (code)
            {
                case 0: return MsiResult.Installed;
                case 3010:
                case 1641: return MsiResult.RestartNeeded;
                case 1638: return MsiResult.AlreadyInstalled;
                default: return MsiResult.Failed;
            }
        }

        /// <summary>
        /// Если сайт Cloudflare недоступен, пользователь может скачать установщик любым способом и положить
        /// Cloudflare_WARP*.msi в папку данных Zarp. Подпись у него проверяется так же.
        /// </summary>
        internal static string FindManualInstaller(string dataDir)
        {
            try
            {
                return Directory.GetFiles(dataDir, "Cloudflare_WARP*.msi")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ подпись

        /// <summary>Подпись Authenticode должна быть действительной и принадлежать Cloudflare. Возвращает имя подписавшего.</summary>
        internal static Task<string> VerifySignatureAsync(string file) =>
            Task.Run(() => CheckSigner(Authenticode.Verify(file)));

        /// <summary>
        /// Решение по результату проверки: принимается только действительная подпись Cloudflare.
        /// Подпись без сертификата подписавшего тоже отвергается.
        /// </summary>
        internal static string CheckSigner(Authenticode.Result result)
        {
            if (!result.Trusted)
                throw new Exception(L.T("err.warpSignature", result.Problem ?? "unknown"));
            string subject = result.SignerSubject ?? "";
            if (!IsCloudflareSigner(subject))
                throw new Exception(L.T("err.warpSignature", subject.Trim().Length == 0 ? "no signer certificate" : Shorten(subject)));
            return "Cloudflare, Inc.";
        }

        /// <summary>Организация в сертификате - ровно «Cloudflare, Inc.», а не любое имя, где это встречается.</summary>
        internal static bool IsCloudflareSigner(string subject) =>
            Regex.IsMatch(subject ?? "", "(?:^|,\\s*)O=\"?Cloudflare, Inc\\.\"?(?:,|$)");

        static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 120 ? text.Substring(0, 120) + "..." : text;
        }

        // ------------------------------------------------------------------ загрузка

        internal static async Task<string> DownloadWithRetryAsync(string url, string file, long minSize, long maxSize,
            TimeSpan stall, IProgress<int> percent, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await DownloadAsync(url, file, minSize, maxSize, stall, percent, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                // Повторяется любая ошибка сети и обрыв соединения (тип исключения зависит от того, где оборвалось).
                // Бессмысленно повторять только отказ сервера (404, 403) и неверный размер файла.
                catch (Exception e) when (attempt < 3 && !(e is PermanentDownloadException))
                {
                    Log.Write(L.T("log.warpRetry", attempt, e.Message));
                    await Task.Delay(2000 * attempt, ct);
                }
            }
        }

        sealed class PermanentDownloadException : Exception
        {
            public PermanentDownloadException(string message) : base(message) { }
        }

        /// <summary>
        /// Скачать файл, вернуть SHA-256 (hex). Следит за зависанием (stall без единого байта),
        /// размером и тем, что файл получен целиком.
        /// </summary>
        internal static async Task<string> DownloadAsync(string url, string file, long minSize, long maxSize,
            TimeSpan stall, IProgress<int> percent, CancellationToken ct)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Zarp/1.0");
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string reason = L.T("err.warpHttp", (int)resp.StatusCode);
                        if ((int)resp.StatusCode >= 500) throw new HttpRequestException(reason); // сервер может оправиться
                        throw new PermanentDownloadException(reason);
                    }
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    if (total >= 0 && (total < minSize || total > maxSize))
                        throw new PermanentDownloadException(L.T("err.warpSize", total));

                    using (var watchdog = new CancellationTokenSource())
                    using (watchdog.Token.Register(resp.Dispose)) // зависшее чтение прерывается закрытием ответа
                    using (ct.Register(resp.Dispose))
                    using (var sha = SHA256.Create())
                    {
                        watchdog.CancelAfter(stall);
                        long done = 0;
                        int lastPercent = -1;
                        try
                        {
                            using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[81920];
                                int n;
                                while ((n = await src.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                                {
                                    watchdog.CancelAfter(stall);
                                    dst.Write(buffer, 0, n);
                                    sha.TransformBlock(buffer, 0, n, null, 0);
                                    done += n;
                                    if (total > 0 && percent != null)
                                    {
                                        int p = (int)(done * 100 / total);
                                        if (p != lastPercent) { lastPercent = p; percent.Report(p); }
                                    }
                                }
                                dst.Flush(true);
                            }
                        }
                        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                        catch (Exception) when (watchdog.IsCancellationRequested) { throw new IOException(L.T("err.warpStalled")); }

                        if ((total >= 0 && done != total) || done < minSize)
                            throw new IOException(done < minSize ? L.T("err.warpSize", done) : L.T("err.warpTruncated"));
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        return Hex(sha.Hash);
                    }
                }
            }
        }

        static byte[] Sha256(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create()) return sha.ComputeHash(stream);
        }

        static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
