using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentProbe.Transport;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Target;

/// <summary>
/// Coming back after a reboot without installing a service.
/// Right before rebooting, the target writes a small DPAPI-protected state file and registers a ONE-SHOT autostart:
/// HKCU RunOnce for a normal user, or a logon scheduled task ("highest privileges") when it was running elevated.
/// The resumed instance removes both again, so nothing is left once the session ends.
/// </summary>
internal static class Resume
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValueName = "agentprobe-resume";
    private const string TaskName = "agentprobe-resume";

    private static string StatePath => Path.Combine(AppInfo.StateDir, "resume.bin");

    public static string Register(ResumeState state)
    {
        var elevated = Environment.IsPrivilegedProcess;
        state.ScheduledTask = elevated;
        Directory.CreateDirectory(AppInfo.StateDir);
        var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonCtx.Default.ResumeState);
        File.WriteAllBytes(StatePath, Dpapi.Protect(json));

        var command = $"\"{AppInfo.ExePath}\" share --resume";
        if (elevated && RegisterTask())
            return "one-shot logon task (keeps administrator rights)";

        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
        key.SetValue(ValueName, command);
        return "one-shot RunOnce entry (runs when this user logs on)";
    }

    /// <summary>Loads and consumes the resume state. Returns null when there is none or it has expired.</summary>
    public static ResumeState? Take()
    {
        ResumeState? state = null;
        try
        {
            if (File.Exists(StatePath))
                state = JsonSerializer.Deserialize(Dpapi.Unprotect(File.ReadAllBytes(StatePath)), JsonCtx.Default.ResumeState);
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            state = null;
        }

        Unregister();
        return state != null && state.ExpiresUtc > DateTime.UtcNow ? state : null;
    }

    /// <summary>Removes the state file and any autostart entry. Safe to call when nothing is registered.</summary>
    public static void Unregister()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
            using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
            if (key?.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // best effort
        }

        if (Environment.IsPrivilegedProcess) RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    private static bool RegisterTask()
    {
        var user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        var exe = SecurityElement.Escape(AppInfo.ExePath);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>agentprobe: resume the debugging session once after a reboot. Removes itself.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>share --resume</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static bool RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
