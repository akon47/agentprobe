using System.Runtime.InteropServices;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>A Windows prompt that is waiting for the person at the target: who shows it and what its window says.</summary>
internal sealed record SystemPrompt(string Process, int Pid, string Title, nint Hwnd, RECT Rect)
{
    public string Describe() => (Title.Length > 0 ? $"\"{Format.Clip(Title, 60)}\" " : "") + $"({Process} pid {Pid})";
}

/// <summary>
/// Windows shows some dialogs in windows of its own: the firewall's "allow this app on the network?", User Account
/// Control, a credential prompt, SmartScreen, and the file picker of a packaged app. They belong to system
/// processes, are often untitled or zero-sized at the top level, and so are missing from an ordinary window list,
/// while whatever caused them sits waiting. An agent that cannot see them stalls without knowing why, so they are
/// found by the process that shows them. Which dialog it is, is not guessed: the window's own title is reported.
/// Known gap: where the firewall question is shown by another host process (older Windows), it is not found.
/// </summary>
internal static partial class SystemPrompts
{
    /// <summary>Programs that exist to show such a prompt. Which prompt it is comes from the window's own title.</summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "PickerHost.exe",          // "Windows Security": the firewall asking about a program that opened a port
        "CredentialUIBroker.exe",  // "Windows Security": user name and password
        "consent.exe",             // User Account Control
        "smartscreen.exe",         // "Windows protected your PC"
    };

    public static List<SystemPrompt> Find() => Find(UiTools.EnumerateWindows(), ProcessSnapshot.Take().ToDictionary(p => p.Pid, p => p.Name));

    public static List<SystemPrompt> Find(List<WindowInfo> windows, Dictionary<int, string> names)
    {
        // Only this logon session: a prompt on another user's screen is not waiting on this one.
        int session;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) session = self.SessionId;
        var here = ProcessSnapshot.Take().Where(p => p.SessionId == session).Select(p => p.Pid).ToHashSet();

        var result = new List<SystemPrompt>();
        foreach (var group in windows.Where(w => w.Visible && names.TryGetValue(w.Pid, out var n) && Hosts.Contains(n)).GroupBy(w => w.Pid))
        {
            // The titled window names the prompt; the largest one is where it is on screen.
            var titled = group.FirstOrDefault(w => w.Title.Length > 0);
            var largest = group.OrderByDescending(w => (long)w.Rect.Width * w.Rect.Height).First();
            if (titled == null && largest.Rect.Width <= 1) continue; // a host process idling with no prompt up
            result.Add(new SystemPrompt(names[group.Key], group.Key, titled?.Title ?? "", (titled ?? largest).Hwnd, largest.Rect));
        }

        // User Account Control normally asks on the secure desktop, where its window cannot be listed at all.
        foreach (var (pid, name) in names)
        {
            if (name.Equals("consent.exe", StringComparison.OrdinalIgnoreCase) && here.Contains(pid) && result.All(r => r.Pid != pid))
                result.Add(new SystemPrompt(name, pid, "User Account Control", 0, default));
        }

        return result;
    }

    /// <summary>
    /// True while Windows shows its secure desktop instead of the user's: a UAC prompt, the lock screen or
    /// Ctrl+Alt+Del. Nothing on the normal desktop can be seen or clicked until that is over.
    /// </summary>
    public static unsafe bool SecureDesktopShowing()
    {
        var desktop = OpenInputDesktop(0, 0, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == 0) return Marshal.GetLastPInvokeError() == 5; // access denied: it belongs to Winlogon
        try
        {
            var name = stackalloc char[64];
            uint needed;
            if (GetUserObjectInformationW(desktop, 2, name, 64 * sizeof(char), &needed) == 0) return false; // UOI_NAME
            return !new string(name).Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>One line per waiting prompt, for a command's error stream; nothing when there is none.</summary>
    public static void Warn(TextWriter err)
    {
        try
        {
            foreach (var p in Find())
                err.WriteLine($"[a system dialog is waiting on the target's screen: {p.Describe()}. The person there has to answer it; `windows` shows it.]");
            if (SecureDesktopShowing())
                err.WriteLine("[the target shows its secure desktop (a UAC prompt, the lock screen or Ctrl+Alt+Del): the normal desktop cannot be seen or clicked until the person there answers.]");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the check is a courtesy: never let it fail the command it follows
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, int inherit, uint access);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetUserObjectInformationW(nint handle, int index, char* info, uint length, uint* needed);

    [LibraryImport("user32.dll")]
    private static partial int CloseDesktop(nint desktop);
}
