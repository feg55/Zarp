using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
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

        public static async Task<bool> IsEnabledAsync() =>
            (await ProcessUtil.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"", 10000)).Ok;

        public static async Task<bool> SetAsync(bool enable, string exePath)
        {
            RunResult r;
            if (enable)
            {
                // XML, потому что через параметры schtasks нельзя снять лимит 72 часа и запрет работы от батареи
                string xml = TaskXml(exePath);
                string tmp = Path.Combine(Path.GetTempPath(), "zarp-task.xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                r = await ProcessUtil.RunAsync("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"", 10000);
                try { File.Delete(tmp); } catch { }
            }
            else
            {
                r = await ProcessUtil.RunAsync("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"", 10000);
            }
            Log.Write(r.Ok
                ? (enable ? "Автозапуск включён." : "Автозапуск выключен.")
                : "Не удалось изменить автозапуск: " + r.Output);
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
