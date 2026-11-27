using System.Diagnostics;
using System.Text;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Tools;

/// <summary>
/// Heavier captures for problems a snapshot cannot explain: automatic dumps at the moment of a crash, and
/// Windows Performance Recorder traces (CPU sampling, disk/file/registry I/O, heap) for analysis on the developer PC.
/// Both change machine state, so both register an undo that runs when the session ends.
/// </summary>
internal static class TraceTools
{
    private const string LocalDumpsKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    private static readonly Dictionary<string, string> WprProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = "GeneralProfile", ["cpu"] = "CPU", ["disk"] = "DiskIO", ["file"] = "FileIO", ["registry"] = "Registry",
        ["heap"] = "Heap", ["memory"] = "VirtualAllocation", ["pool"] = "Pool", ["handle"] = "Handle", ["network"] = "Networking",
        ["dotnet"] = "DotNET", ["gpu"] = "GPU", ["power"] = "Power", ["video"] = "Video", ["audio"] = "Audio", ["minifilter"] = "Minifilter",
    };

    public static IEnumerable<Tool> Tools =>
    [
        new("crashdumps", "Capture", "Make Windows save a dump automatically whenever an app crashes",
            """
            crashdumps on [app.exe] [--full] [--folder DIR]     enable (for one app, or all apps)
            crashdumps off                                       undo
            crashdumps                                           status + dumps collected so far
              Uses Windows Error Reporting "LocalDumps". Turn it on, reproduce the crash, then `pull` the .dmp.
              This is how to catch a crash that happens at startup or too fast for `dump`.
            crashdumps --no-autoreboot
              For the PC as a whole: after a blue screen the PC stays on it instead of restarting (CrashControl
              AutoReboot=0), so the person there can read the stop code. A reset that then leaves no blue screen
              behind was not a bugcheck. Windows may read this only when it starts. If the PC resets before the
              session ends, agentprobe puts it back the next time it starts there.
              Needs agentprobe elevated on the target. Reverted automatically when the session ends.
            """, CrashDumpsAsync),
        new("wpr", "Capture", "Record a Windows Performance Recorder trace (CPU, disk, file, registry, heap...) and fetch the .etl",
            """
            wpr start [cpu] [disk] [file] [registry] [heap] [memory] [network] [dotnet] [gpu] [general] ...
            wpr stop [-o trace.etl]        stop and bring the trace to the developer PC
            wpr status | wpr cancel
              Default profile: general. Open the .etl in Windows Performance Analyzer (WPA) or PerfView.
              `file` + `registry` give Process Monitor-style activity; `cpu` shows where time goes; `heap`/`memory`
              show who allocates. Traces grow fast (hundreds of MB per minute): start right before the repro, stop right after.
              Needs agentprobe elevated on the target. A recording left running is cancelled when the session ends.
            """, WprAsync),
    ];

    // ------------------------------------------------------------------ crashdumps

    private static readonly List<Action> Reverts = [];

    private static int RevertAll()
    {
        List<Action> todo;
        lock (Reverts)
        {
            todo = [.. Reverts];
            Reverts.Clear();
        }

        for (var i = todo.Count - 1; i >= 0; i--) todo[i]();
        return todo.Count;
    }

    private static Task<int> CrashDumpsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "full", "no-autoreboot");
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : a.Flag("no-autoreboot") ? "no-autoreboot" : "status";
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        if (sub == "status")
        {
            using (var cc = hklm.OpenSubKey(CrashControlKey))
            {
                if (cc?.GetValue("AutoReboot") is 0)
                    ctx.Out.WriteLine($"after a blue screen: the PC stays on it (AutoReboot=0{(File.Exists(AutoRebootNote) ? ", set by agentprobe" : "")})");
            }

            using var key = hklm.OpenSubKey(LocalDumpsKey);
            if (key == null) ctx.Out.WriteLine("automatic crash dumps: off (Windows default)");
            else
            {
                var apps = key.GetSubKeyNames();
                var global = key.GetValue("DumpType") != null;
                var mine = Reverts.Count > 0 ? $" ({Reverts.Count} setting(s) made by this session)" : "";
                ctx.Out.WriteLine($"automatic crash dumps: {(global ? "on for all apps" : apps.Length > 0 ? $"on for {apps.Length} app(s): {Format.Clip(string.Join(", ", apps), 160)}" : "key present, no settings")}{mine}");
            }

            var folder = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\CrashDumps");
            if (Directory.Exists(folder))
            {
                foreach (var f in new DirectoryInfo(folder).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).Take(10))
                    ctx.Out.WriteLine($"  {Format.Time(f.LastWriteTimeUtc)}  {Format.Bytes(f.Length)}  {f.FullName}");
            }

            return Task.FromResult(0);
        }

        if (!Environment.IsPrivilegedProcess) return Task.FromResult(ctx.Fail("crashdumps: needs agentprobe elevated on the target (it writes under HKLM). Ask the person there to start it with Run as administrator."));

        if (sub == "off")
        {
            // Only what THIS session set is reverted: other software (GPU drivers, streaming tools...) keeps its own entries here.
            var count = RevertAll();
            if (count == 0)
            {
                ctx.Out.WriteLine("this session did not turn anything on; existing crash dump settings (from other software) were left untouched");
                return Task.FromResult(0);
            }

            ctx.Runtime.Audit.Write("automatic crash dumps: settings made by this session reverted");
            ctx.Out.WriteLine($"reverted {count} setting(s) made by this session");
            return Task.FromResult(0);
        }

        if (sub == "no-autoreboot") return Task.FromResult(NoAutoReboot(ctx, hklm));
        if (sub != "on") return Task.FromResult(ctx.Fail("crashdumps: use on, off, --no-autoreboot, or nothing for status."));

        var app = a.Positionals.Count > 1 ? a.Positionals[1] : null;
        if (app != null && !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
        var dumpFolder = a.Get("folder") ?? @"%LOCALAPPDATA%\CrashDumps";
        var keyPath = app != null ? $@"{LocalDumpsKey}\{app}" : LocalDumpsKey;

        // Remember exactly what was there, so the undo restores it value by value.
        var keyExisted = false;
        var previous = new Dictionary<string, (object value, RegistryValueKind kind)>();
        using (var before = hklm.OpenSubKey(keyPath))
        {
            if (before != null)
            {
                keyExisted = true;
                foreach (var name in new[] { "DumpFolder", "DumpType", "DumpCount" })
                {
                    if (before.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } v) previous[name] = (v, before.GetValueKind(name));
                }
            }
        }

        var rootExisted = hklm.OpenSubKey(LocalDumpsKey) != null;
        using (var key = hklm.CreateSubKey(keyPath))
        {
            key.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);
            key.SetValue("DumpType", a.Flag("full") ? 2 : 1, RegistryValueKind.DWord);
            key.SetValue("DumpCount", 10, RegistryValueKind.DWord);
        }

        lock (Reverts)
        {
            Reverts.Add(() =>
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                if (!keyExisted && app != null)
                {
                    root.DeleteSubKey(keyPath, false); // the per-app key was ours
                }
                else
                {
                    using var key = root.OpenSubKey(keyPath, writable: true);
                    if (key != null)
                    {
                        foreach (var name in new[] { "DumpFolder", "DumpType", "DumpCount" })
                        {
                            if (previous.TryGetValue(name, out var old)) key.SetValue(name, old.value, old.kind);
                            else key.DeleteValue(name, false);
                        }
                    }
                }

                // Remove the LocalDumps key itself only if we created it and nothing else moved in meanwhile.
                if (!rootExisted)
                {
                    using var rootKey = root.OpenSubKey(LocalDumpsKey);
                    if (rootKey != null && rootKey.SubKeyCount == 0 && rootKey.ValueCount == 0) root.DeleteSubKey(LocalDumpsKey, false);
                }
            });
        }

        ctx.Runtime.AddUndo("automatic crash dumps", () => RevertAll());

        ctx.Runtime.Audit.Write($"automatic crash dumps turned on ({app ?? "all apps"})");
        ctx.Out.WriteLine($"automatic crash dumps: on for {app ?? "all apps"} ({(a.Flag("full") ? "full" : "mini")} dumps, up to 10) -> {Environment.ExpandEnvironmentVariables(dumpFolder)}");
        ctx.Out.WriteLine("reproduce the crash, then: agentprobe crashdumps   (lists them)   and   agentprobe pull <dump path>");
        if (app != null && !rootExisted) ctx.Out.WriteLine("note: while this is on, Windows also keeps default mini dumps for other apps that crash (the setting's own behaviour).");
        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ crashdumps --no-autoreboot

    private const string CrashControlKey = @"SYSTEM\CurrentControlSet\Control\CrashControl";

    /// <summary>
    /// Written while AutoReboot=0 is this agentprobe's doing: the value to put back. A reset ends the session without
    /// its undo, so the next agentprobe that starts on this PC puts the value back from here.
    /// </summary>
    private static string AutoRebootNote => Path.Combine(AppInfo.StateDir, "autoreboot-restore.txt");

    private static int NoAutoReboot(ToolContext ctx, RegistryKey hklm)
    {
        using var cc = hklm.OpenSubKey(CrashControlKey, writable: true);
        if (cc == null) return ctx.Fail($@"crashdumps: HKLM\{CrashControlKey} is missing on this PC.");
        var before = cc.GetValue("AutoReboot");
        if (before is 0)
        {
            ctx.Out.WriteLine("after a blue screen this PC already stays on it (AutoReboot=0): nothing changed");
            return 0;
        }

        Directory.CreateDirectory(AppInfo.StateDir);
        File.WriteAllText(AutoRebootNote, before is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent");
        cc.SetValue("AutoReboot", 0, RegistryValueKind.DWord);
        RegisterAutoRebootUndo(ctx.Runtime);
        ctx.Runtime.Audit.Write("after a blue screen this PC now stays on it instead of restarting (AutoReboot=0) until the session ends");
        ctx.Out.WriteLine($"after a blue screen this PC now stays on it instead of restarting (CrashControl AutoReboot=0, was {before ?? "not set (1)"}): the person there can read the stop code.");
        ctx.Out.WriteLine("Windows may read this only when it starts, so it is certain from the next start on. A hard reset or a power loss restarts the PC all the same:");
        ctx.Out.WriteLine("a reset that leaves no blue screen behind while this applies was not a bugcheck. Put back when the session ends, or, after a reset, when agentprobe next starts here.");
        return 0;
    }

    private static void RegisterAutoRebootUndo(Target.TargetRuntime runtime)
    {
        lock (Reverts) Reverts.Add(() => RestoreAutoReboot());
        runtime.AddUndo("staying on a blue screen (AutoReboot)", () => RevertAll());
    }

    /// <summary>Puts AutoReboot back as the note says. True when there was something to put back and it was done.</summary>
    private static bool RestoreAutoReboot()
    {
        if (!File.Exists(AutoRebootNote)) return false;
        var was = File.ReadAllText(AutoRebootNote).Trim();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cc = hklm.OpenSubKey(CrashControlKey, writable: true);
        if (cc == null) return false;
        if (cc.GetValue("AutoReboot") is 0) // someone else may have changed it since: then it is theirs
        {
            if (int.TryParse(was, out var v)) cc.SetValue("AutoReboot", v, RegistryValueKind.DWord);
            else cc.DeleteValue("AutoReboot", false);
        }

        File.Delete(AutoRebootNote);
        return true;
    }

    /// <summary>
    /// At the start of sharing: AutoReboot=0 that an earlier session set and could not put back (the PC reset). Put
    /// back now, or, when this start continues that session after a reboot it asked for, put back when it ends.
    /// </summary>
    public static void TakeOverLeftovers(Target.TargetRuntime runtime, bool resumed)
    {
        try
        {
            if (!File.Exists(AutoRebootNote)) return;
            if (!Environment.IsPrivilegedProcess)
            {
                runtime.Audit.Write("an earlier session made this PC stay on a blue screen (AutoReboot=0): start agentprobe as administrator once to put it back");
                return;
            }

            if (resumed) RegisterAutoRebootUndo(runtime);
            else if (RestoreAutoReboot()) runtime.Audit.Write("reverted: staying on a blue screen (AutoReboot), set by an earlier session that ended with a reset");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            runtime.Audit.Write("could not put back AutoReboot that an earlier session set: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ wpr

    private static (int code, string output) RunWpr(string arguments, int timeoutMs = 120000)
    {
        var psi = new ProcessStartInfo("wpr.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        try
        {
            using var p = Process.Start(psi)!;
            var sb = new StringBuilder();
            var pump = Task.WhenAll(
                Shell.PumpAsync(p.StandardOutput.BaseStream, s => { lock (sb) sb.Append(s); }),
                Shell.PumpAsync(p.StandardError.BaseStream, s => { lock (sb) sb.Append(s); }));
            if (!p.WaitForExit(timeoutMs))
            {
                Shell.KillTree(p);
                return (124, "wpr.exe did not finish in time");
            }

            pump.Wait(3000);
            return (p.ExitCode, new Squeezer().Apply(sb.ToString()));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, "wpr.exe was not found (it ships with Windows 10 and later).");
        }
    }

    private static async Task<int> WprAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "status";
        if (sub == "status")
        {
            var (code, text) = RunWpr("-status");
            ctx.Out.Write(text);
            return code;
        }

        if (!Environment.IsPrivilegedProcess) return ctx.Fail("wpr: needs agentprobe elevated on the target. Ask the person there to start it with Run as administrator.");

        switch (sub)
        {
            case "start":
            {
                var names = a.Positionals.Skip(1).DefaultIfEmpty("general").ToList();
                var profiles = new List<string>();
                foreach (var n in names)
                {
                    if (!WprProfiles.TryGetValue(n, out var profile)) return ctx.Fail($"wpr: unknown profile '{n}'. Known: {string.Join(", ", WprProfiles.Keys)}");
                    profiles.Add(profile);
                }

                var (code, text) = RunWpr(string.Join(' ', profiles.Select(p => "-start " + p)) + " -filemode");
                ctx.Out.Write(text);
                if (code != 0) return ctx.Fail($"wpr: could not start (exit {code}). Is another recording running? Try `wpr cancel`.");
                ctx.Runtime.AddUndo("WPR recording", () => RunWpr("-cancel", 30000));
                ctx.Runtime.Audit.Write("WPR recording started: " + string.Join(", ", profiles));
                ctx.Out.WriteLine($"recording {string.Join(" + ", profiles)}. Reproduce now, then: agentprobe wpr stop");
                return 0;
            }

            case "cancel":
            {
                var (code, text) = RunWpr("-cancel", 30000);
                ctx.Out.Write(text);
                ctx.Out.WriteLine("recording cancelled");
                return code;
            }

            case "stop":
            {
                var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
                ctx.Out.WriteLine("stopping and merging the trace (this can take a minute)...");
                var (code, text) = RunWpr($"-stop \"{file}\" \"agentprobe\"", 600000);
                if (code != 0 || !File.Exists(file))
                {
                    ctx.Out.Write(text);
                    return ctx.Fail($"wpr: stop failed (exit {code}).");
                }

                var size = new FileInfo(file).Length;
                ctx.Runtime.Audit.Write($"WPR recording stopped, {Format.Bytes(size)}");
                ctx.Out.WriteLine($"trace is {Format.Bytes(size)}; open it in Windows Performance Analyzer (WPA) or PerfView");
                await ctx.SendFile(Path.GetFileName(file).Replace(AppInfo.Name + "-", "trace-"), file, false, true);
                try
                {
                    // wpr leaves a folder of generated symbols next to the trace: do not leave it on the target.
                    if (Directory.Exists(file + ".NGENPDB")) Directory.Delete(file + ".NGENPDB", true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }

                return 0;
            }

            default:
                return ctx.Fail("wpr: use start, stop, status or cancel.");
        }
    }
}
