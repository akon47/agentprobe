using System.Diagnostics;
using System.Net.Sockets;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>Memory dumps: the hand-off from "it hangs/leaks on that PC" to a real debugger on the developer PC.</summary>
internal static class DumpTool
{
    public static IEnumerable<Tool> Tools =>
    [
        new("dump", "Processes", "Write a memory dump of a process and bring it to the developer PC",
            """
            dump <pid|name> [--full] [-o folder-or-file] [--keep-remote]
              Default is a small dump (stacks, modules, handles, data segments): enough for "where is it stuck".
              --full includes all memory (heap contents, needed for leak analysis and .NET objects); it can be large.
              Open it with WinDbg / Visual Studio / dotnet-dump on the developer PC. The process keeps running.
              --keep-remote leaves the dump in the session workspace on the target and prints its path instead of
              sending it (a full dump is often gigabytes: analyse it there, or `pull` it later). It goes with the
              workspace at `end`.
            """, DumpAsync),
    ];

    private static async Task<int> DumpAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "full", "keep-remote");
        if (a.Positionals.Count == 0) return ctx.Fail("dump: which process? pid or name.");
        var matches = ProcessTools.Find(ProcessSnapshot.Take(), a.Positionals[0]);
        if (matches.Count == 0) return ctx.Fail($"dump: no process matches '{a.Positionals[0]}'.");
        var p = matches.OrderByDescending(m => m.PrivateBytes).First();
        if (matches.Count > 1) ctx.Err.WriteLine($"[{matches.Count} processes match; dumping the largest, pid {p.Pid}]");

        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ | Win32.PROCESS_DUP_HANDLE, false, p.Pid);
        if (h == 0) return ctx.Fail(ProcessTools.WhyNotOpen("dump", p.Name, p.Pid));
        var full = a.Flag("full");
        var keep = a.Flag("keep-remote");
        var file = keep
            ? Path.Combine(ctx.Runtime.WorkDir, $"{Path.GetFileNameWithoutExtension(p.Name)}-{p.Pid}-{DateTime.Now:HHmmss}.dmp")
            : Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-{Guid.NewGuid():N}.dmp");
        try
        {
            // small: DataSegs | HandleData | UnloadedModules | ProcessThreadData | ThreadInfo
            // full:  FullMemory | HandleData | UnloadedModules | FullMemoryInfo | ThreadInfo
            var type = full ? 0x2u | 0x4 | 0x20 | 0x800 | 0x1000 : 0x1u | 0x4 | 0x20 | 0x100 | 0x1000;
            bool ok;
            int error;
            try
            {
                await using var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                lock (InspectTools.DbgHelpLock) // dbghelp is single-threaded; `stacks` uses it too
                {
                    ok = Win32.MiniDumpWriteDump(h, p.Pid, fs.SafeFileHandle.DangerousGetHandle(), type, 0, 0, 0);
                    error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return ctx.Fail($"dump: could not write {file}: {ex.Message} Nothing was dumped." +
                                (keep ? " --keep-remote writes into the work folder (with -t local that is the current folder): use one you may write to." : ""));
            }

            if (!ok)
            {
                File.Delete(file);
                return ctx.Fail($"dump: MiniDumpWriteDump failed (0x{error:X8}).");
            }

            var size = new FileInfo(file).Length;
            var arch = ProcessDetails.Architecture(h);
            ctx.Runtime.Audit.Write($"dump of {p.Name} ({p.Pid}), {Format.Bytes(size)}");
            ctx.Out.WriteLine($"{(full ? "full" : "small")} dump of {p.Name} (pid {p.Pid}, {arch}), {Format.Bytes(size)}" +
                              (arch == "x86" ? "; 32-bit process dumped by a 64-bit tool: in WinDbg use `.load wow64exts` + `!sw`" : ""));
            if (keep) ctx.Out.WriteLine($"kept on the target: {file}");
            else await ctx.SendFile($"{Path.GetFileNameWithoutExtension(p.Name)}-{p.Pid}-{DateTime.Now:HHmmss}.dmp", file, false, true);
            return 0;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }
}

/// <summary>Block until something happens, instead of polling in a loop (each poll costs a round trip and tokens).</summary>
internal static class WaitTool
{
    public static IEnumerable<Tool> Tools =>
    [
        new("wait", "Run", "Wait for a condition: process exit/start, window, port, file, log line, job, CPU idle",
            """
            wait [--timeout 60s] <condition> [more conditions: all must hold]
              --exit <pid|name>        the process is gone
              --start <name>           a process with that name is running
              --window <title>         a visible window whose title contains the text exists
              --no-window <title>      no such window (dialog closed)
              --port <n>               something is listening on TCP port n on the target (any address, IPv4 or IPv6)
              --file <path>            the file exists
              --file <path> --grep RE  ...and a line matches the pattern
              --job <id>               a background job finished
              --idle <pid|name>        the process uses < 2% of a core for 3s (finished starting up / working)
              --below <percent>        with --idle: that limit instead of 2, in % of one core, for an app that is
                                       never quite idle (a video app at rest may sit at 15%: --idle app --below 25)
              --ui <name|#id>          a control with that name or id is on screen (found as `ui click` finds it)
              --has <regex>            with --ui: ...and its line, as `ui get` prints it without the position,
                                       matches: --ui "#status" --has Saved   --ui "Apply" --has "\(disabled\)"
              --no-ui <name|#id>       no such control is on screen (the progress bar went, the dialog's text is gone)
              --in <window>            the window --ui and --no-ui look in (default: the foreground window)
              Exit code 0 when met (prints how long it took), 124 on timeout (prints what was still missing,
              and for a control what it reads instead).
            """, WaitAsync),
        new("expect", "Run", "Check that something is the case now (or within a few seconds): one line, exit 0 or 1",
            """
            expect [--timeout 5s] <condition> [more conditions: all must hold]
              An assertion with the conditions of `wait` (see `help wait`): --ui <name> [--has <regex>],
              --no-ui <name>, --window <title>, --no-window <title>, --start <name>, --exit <name>, --port <n>,
              --file <path> [--grep RE], --job <id>, --idle <name>. Each has 5 seconds to come true (--timeout).
              Exit 0 and "ok: ..." when they hold. Exit 1 and what is the case instead when they do not:
                  expect --ui "#status" --has Saved
                  FAILED after 5s: control '#status' to read /Saved/ (it reads: Text "Disk full" #status)
              Say what you expect instead of reading the state and judging it: one short line either way.
            """, ExpectAsync),
    ];

    private static Task<int> WaitAsync(ToolContext ctx, string[] args) => CheckAsync(ctx, args, "wait");

    private static Task<int> ExpectAsync(ToolContext ctx, string[] args) => CheckAsync(ctx, args, "expect");

    /// <summary><c>wait</c> and <c>expect</c>: the same conditions; they differ in how long they wait by default and in how they answer.</summary>
    private static async Task<int> CheckAsync(ToolContext ctx, string[] args, string tool)
    {
        var a = ArgParser.Parse(args);
        var expect = tool == "expect";
        var timeout = a.GetDuration("timeout") ?? TimeSpan.FromSeconds(expect ? 5 : 60);
        var checks = new List<(string name, Func<bool> met)>();
        var notes = new Dictionary<string, Func<string>>(); // per condition: what is the case instead, for the answer when it is not met
        Func<string>? idleNote = null; // what --idle last measured, for the timeout message

        if (a.Has("has") && !a.Has("ui")) return ctx.Fail($"{tool}: --has goes with --ui <name|#id>.");
        if (a.Has("in") && !a.Has("ui") && !a.Has("no-ui")) return ctx.Fail($"{tool}: --in <window> goes with --ui or --no-ui.");
        if (a.Get("ui") is { } control)
        {
            System.Text.RegularExpressions.Regex? has = null;
            if (a.Get("has") is { } hasPattern)
            {
                try
                {
                    has = new System.Text.RegularExpressions.Regex(hasPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    return ctx.Fail($"{tool}: bad --has pattern: " + ex.Message);
                }
            }

            var name = has != null ? $"control '{control}' to read /{a.Get("has")}/" : $"control '{control}' on screen";
            var reads = "";
            notes[name] = () => reads;
            checks.Add((name, () =>
            {
                var probe = UiaTools.Probe(a.Get("in"), control);
                if (probe.status != 0)
                {
                    reads = probe.status == 1 ? probe.text : "the window could not be looked in: " + probe.text;
                    return false;
                }

                reads = "it reads: " + probe.text;
                if (probe.offscreen)
                {
                    reads = "it exists but is not visible on screen: " + probe.text;
                    return false;
                }

                return has == null || has.IsMatch(probe.text);
            }));
        }

        if (a.Get("no-ui") is { } absent)
        {
            var name = $"control '{absent}' to be gone";
            var reads = "";
            notes[name] = () => reads;
            checks.Add((name, () =>
            {
                var probe = UiaTools.Probe(a.Get("in"), absent);
                if (probe.status == 1 || (probe.status == 0 && probe.offscreen)) return true;
                if (probe.status == 0)
                {
                    reads = "it is there: " + probe.text;
                    return false;
                }

                // Its window is gone: so is the control. A window that could not be read is not "gone".
                if (probe.text.Contains("no window matches", StringComparison.Ordinal)) return true;
                reads = "the window could not be looked in: " + probe.text;
                return false;
            }));
        }

        if (a.Get("exit") is { } exit) checks.Add(($"exit of {exit}", () => ProcessTools.Find(ProcessSnapshot.Take(), exit).Count == 0));
        if (a.Get("start") is { } start) checks.Add(($"start of {start}", () => ProcessTools.Find(ProcessSnapshot.Take(), start).Count > 0));
        if (a.Get("window") is { } win) checks.Add(($"window \"{win}\"", () => UiTools.EnumerateWindows().Any(w => w.Visible && w.Title.Contains(win, StringComparison.OrdinalIgnoreCase))));
        if (a.Get("no-window") is { } nowin) checks.Add(($"window \"{nowin}\" to close", () => !UiTools.EnumerateWindows().Any(w => w.Visible && w.Title.Contains(nowin, StringComparison.OrdinalIgnoreCase))));
        if (a.Get("port") is { } portText && int.TryParse(portText, out var port)) checks.Add(($"port {port}", () => NetTools.IsListening(port)));
        if (a.Get("job") is { } jobId)
        {
            if (ctx.Runtime.Jobs.Find(jobId) == null) return ctx.Fail($"{tool}: there is no job '{jobId}' (see `jobs`).");
            checks.Add(($"job {jobId}", () => ctx.Runtime.Jobs.Find(jobId) is not { ExitCode: null }));
        }
        if (a.Get("file") is { } file)
        {
            var path = ctx.Runtime.ResolvePath(file);
            if (a.Get("grep") is { } grep)
            {
                System.Text.RegularExpressions.Regex re;
                try
                {
                    re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    return ctx.Fail($"{tool}: bad --grep pattern: " + ex.Message);
                }

                checks.Add(($"/{grep}/ in {Path.GetFileName(path)}", () => FileContains(path, re)));
            }
            else
            {
                checks.Add(($"file {path}", () => File.Exists(path)));
            }
        }

        if (a.Has("below") && !a.Has("idle")) return ctx.Fail($"{tool}: --below goes with --idle <pid|name>.");
        if (a.Get("idle") is { } idle)
        {
            var limit = a.GetDouble("below", 2);
            if (limit is <= 0 or > 6400) return ctx.Fail($"{tool}: --below needs a percentage of one core, for example 25.");
            var quietSince = (DateTime?)null;
            long lastCpu = -1;
            var lastAt = Stopwatch.GetTimestamp();
            var lastBusy = -1.0; // only ever a measured figure
            var running = true;
            idleNote = () => !running ? $" ({idle} is not running)"
                : lastBusy >= 0 ? $" ({idle} last used {Format.Pct(lastBusy)}% of a core; --below sets the limit)" : "";
            checks.Add(($"{idle} to stay below {Format.Pct(limit)}% of a core for 3s", () =>
            {
                var procs = ProcessTools.Find(ProcessSnapshot.Take(), idle);
                running = procs.Count > 0;
                if (!running)
                {
                    (lastCpu, lastBusy, quietSince) = (-1, -1, null); // gone: nothing measured counts any more
                    return false;
                }

                var cpu = procs.Sum(p => p.CpuTime);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(lastAt, now);
                var measured = lastCpu >= 0 && elapsed.Ticks > 0 && cpu >= lastCpu; // the first look has nothing to compare with
                var busy = measured ? 100.0 * (cpu - lastCpu) / elapsed.Ticks : 100;
                lastCpu = cpu;
                lastAt = now;
                if (measured) lastBusy = busy;
                if (busy >= limit) quietSince = null;
                else quietSince ??= DateTime.UtcNow;
                return quietSince != null && DateTime.UtcNow - quietSince >= TimeSpan.FromSeconds(3);
            }));
        }

        if (checks.Count == 0) return ctx.Fail($"{tool}: no condition given. See `agentprobe help {tool}`.");
        string Missing(List<(string name, Func<bool> met)> pending) => string.Join(", ", pending.Select(c => c.name + (notes.TryGetValue(c.name, out var why) && why() is { Length: > 0 } text ? $" ({text})" : "")));

        var sw = Stopwatch.StartNew();
        var pending = checks.ToList();
        while (true)
        {
            pending = pending.Where(c => !Safe(c.met)).ToList();
            if (pending.Count == 0)
            {
                ctx.Out.WriteLine(expect
                    ? $"ok: {string.Join(", ", checks.Select(c => c.name))}{(sw.Elapsed.TotalSeconds >= 1 ? $" (after {Format.Duration(sw.Elapsed)})" : "")}"
                    : $"ok after {Format.Duration(sw.Elapsed)}: {string.Join(", ", checks.Select(c => c.name))}");
                return 0;
            }

            if (sw.Elapsed >= timeout)
            {
                var note = idleNote != null && pending.Any(c => c.name.Contains("% of a core", StringComparison.Ordinal)) ? idleNote() : "";
                ctx.Out.WriteLine(expect
                    ? $"FAILED after {Format.Duration(timeout)}: {Missing(pending)}{note}"
                    : $"timed out after {Format.Duration(timeout)}; still waiting for: {Missing(pending)}{note}");
                return expect ? 1 : 124;
            }

            await Task.Delay(300, ctx.Ct);
        }
    }

    private static bool Safe(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool FileContains(string path, System.Text.RegularExpressions.Regex re)
    {
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
            if (re.IsMatch(line)) return true;
        return false;
    }
}
