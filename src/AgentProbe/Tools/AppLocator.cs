using AgentProbe.Native;
using Microsoft.Win32;

namespace AgentProbe.Tools;

/// <summary>
/// "Where is that app installed?" for an app that is not running, so there is no process to ask: looked up where
/// Windows itself records it (App Paths, the uninstall list) and where the person starts it from (desktop and
/// Start menu shortcuts). Names are compared without spaces and punctuation: "MyApp" finds "My App 2".
/// </summary>
internal static class AppLocator
{
    /// <param name="Shortcut">The shortcut that starts it, when that is how it was found.</param>
    /// <param name="Exact">The program's file name is the name asked for, not merely one that contains it.</param>
    internal sealed record Found(string Exe, string Source, string? Shortcut = null, string? StartIn = null, bool Exact = false);

    private static string Plain(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>An uninstaller, a setup program or an installer's icon file is not the app, whatever its name contains.</summary>
    private static bool NotAnApp(string exe, string? shortcut, string wanted)
    {
        // Unless that is what was asked for: `triage setup` means a program called setup.
        bool Tool(string? file) => file != null && Plain(Path.GetFileNameWithoutExtension(file)) is var n &&
                                    new[] { "unins", "setup", "install", "remove", "제거", "update" }.Any(word => n.Contains(word, StringComparison.Ordinal) && !wanted.Contains(word, StringComparison.Ordinal));
        return Tool(exe) || Tool(shortcut) ||
               exe.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="incomplete">Why the search may have missed something (a part of it failed or was cut short), or null.</param>
    public static List<Found> Find(string name, out string? incomplete, int most = 3)
    {
        incomplete = null;
        var wanted = Plain(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        var found = new List<Found>();
        if (wanted.Length < 2)
        {
            incomplete = "the name is too short to search for";
            return found;
        }

        string? trouble = null;

        void Add(string? exe, string source, string? shortcut = null, string? startIn = null)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            exe = Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || NotAnApp(exe, shortcut, wanted)) return;
            var exact = Plain(Path.GetFileNameWithoutExtension(exe)) == wanted;
            var known = found.FindIndex(f => f.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (known < 0) found.Add(new Found(exe, source, shortcut, startIn, exact));
            else if (shortcut != null && found[known].Shortcut == null) found[known] = found[known] with { Shortcut = shortcut, StartIn = startIn }; // how the person starts it is worth keeping
        }

        try
        {
            // Shortcuts first: they are how the person at the PC starts the app, "Start in" folder included.
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            };
            var read = 0;
            foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
            {
                foreach (var lnk in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (!Plain(Path.GetFileNameWithoutExtension(lnk)).Contains(wanted, StringComparison.Ordinal)) continue;
                    if (read++ >= 12)
                    {
                        trouble ??= "more shortcuts have that name than were read (12)";
                        continue;
                    }

                    if (ShellLink.Read(lnk, out _) is { Target.Length: > 0, Advertised: false } link) Add(link.Target, "shortcut", lnk, link.WorkingDirectory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            trouble ??= "the shortcut folders could not all be read";
        }

        try
        {

            foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using (var paths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                {
                    foreach (var sub in paths?.GetSubKeyNames() ?? [])
                    {
                        if (!Plain(Path.GetFileNameWithoutExtension(sub)).Contains(wanted, StringComparison.Ordinal)) continue;
                        using var k = paths!.OpenSubKey(sub);
                        Add(k?.GetValue(null) as string, "App Paths");
                    }
                }

                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var sub in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var k = uninstall!.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string display || !Plain(display).Contains(wanted, StringComparison.Ordinal)) continue;
                    var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0];
                    Add(icon, $"installed program \"{display}\"");
                    if (k.GetValue("InstallLocation") is string location && location.Length > 2 && Directory.Exists(location.Trim('"')))
                    {
                        foreach (var exe in Directory.EnumerateFiles(location.Trim('"'), "*.exe").Where(e => Plain(Path.GetFileNameWithoutExtension(e)).Contains(wanted, StringComparison.Ordinal)).Take(2))
                            Add(exe, $"installed program \"{display}\"");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            trouble ??= "the list of installed programs could not all be read";
        }

        // A program whose file name IS the name comes before one that merely contains it.
        var ranked = found.OrderByDescending(f => f.Exact).ToList();
        if (ranked.Count > most) trouble ??= $"{ranked.Count} programs match, the first {most} are listed";
        incomplete = trouble;
        return ranked.Take(most).ToList();
    }
}
