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
        var a = ArgParser.Parse(args, "full");
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "status";
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        if (sub == "status")
        {
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

        if (sub != "on") return Task.FromResult(ctx.Fail("crashdumps: use on, off, or nothing for status."));

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
