using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zarp.Core
{
    /// <summary>
    /// Автозапуск при входе в Windows. Через Планировщик задач, а не реестр:
    /// только так программа с правами администратора стартует без запроса UAC.
    /// </summary>
    public static class Autostart
    {
        const string TaskName = "Zarp";
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task<bool> IsEnabledAsync() =>
            (await ProcessUtil.RunAsync(ProcessUtil.SystemExe("schtasks.exe"), $"/Query /TN \"{TaskName}\"", 10000)).Ok;

        public static async Task<bool> SetAsync(bool enable, string exePath)
        {
            await Gate.WaitAsync();
            try { return await SetCoreAsync(enable, exePath); }
            finally { Gate.Release(); }
        }

        static async Task<bool> SetCoreAsync(bool enable, string exePath)
        {
            RunResult r;
            if (enable)
            {
                // XML, потому что через параметры schtasks нельзя снять лимит 72 часа и запрет работы от батареи
                string xml = TaskXml(exePath);
                string tmp = Path.Combine(Path.GetTempPath(), "zarp-task-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, Encoding.Unicode)) writer.Write(xml);
                    r = await ProcessUtil.RunAsync(ProcessUtil.SystemExe("schtasks.exe"), $"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"", 10000);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                r = await ProcessUtil.RunAsync(ProcessUtil.SystemExe("schtasks.exe"), $"/Delete /F /TN \"{TaskName}\"", 10000);
            }
            Log.Write(r.Ok
                ? L.T(enable ? "log.autostartOn" : "log.autostartOff")
                : L.T("log.autostartFailed", r.Output));
            return r.Ok;
        }

        static string TaskXml(string exePath)
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(exePath);
            string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath));
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Zarp: WARP over zapret2</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{exe}</Command><Arguments>--autostart</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec>
  </Actions>
</Task>";
        }
    }
}
