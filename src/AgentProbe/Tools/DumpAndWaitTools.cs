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
            dump <pid|name> [--full] [-o folder-or-file]
              Default is a small dump (stacks, modules, handles, data segments): enough for "where is it stuck".
              --full includes all memory (heap contents, needed for leak analysis and .NET objects); it can be large.
              Open it with WinDbg / Visual Studio / dotnet-dump on the developer PC. The process keeps running.
            """, DumpAsync),
    ];

    private static async Task<int> DumpAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "full");
        if (a.Positionals.Count == 0) return ctx.Fail("dump: which process? pid or name.");
        var matches = ProcessTools.Find(ProcessSnapshot.Take(), a.Positionals[0]);
        if (matches.Count == 0) return ctx.Fail($"dump: no process matches '{a.Positionals[0]}'.");
        var p = matches.OrderByDescending(m => m.PrivateBytes).First();
        if (matches.Count > 1) ctx.Err.WriteLine($"[{matches.Count} processes match; dumping the largest, pid {p.Pid}]");

        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ | Win32.PROCESS_DUP_HANDLE, false, p.Pid);
        if (h == 0) return ctx.Fail(ProcessTools.WhyNotOpen("dump", p.Name, p.Pid));
        var full = a.Flag("full");
        var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-{Guid.NewGuid():N}.dmp");
        try
        {
            // small: DataSegs | HandleData | UnloadedModules | ProcessThreadData | ThreadInfo
            // full:  FullMemory | HandleData | UnloadedModules | FullMemoryInfo | ThreadInfo
            var type = full ? 0x2u | 0x4 | 0x20 | 0x800 | 0x1000 : 0x1u | 0x4 | 0x20 | 0x100 | 0x1000;
            bool ok;
            int error;
            await using (var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                lock (InspectTools.DbgHelpLock) // dbghelp is single-threaded; `stacks` uses it too
                {
                    ok = Win32.MiniDumpWriteDump(h, p.Pid, fs.SafeFileHandle.DangerousGetHandle(), type, 0, 0, 0);
                    error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                }
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
            await ctx.SendFile($"{Path.GetFileNameWithoutExtension(p.Name)}-{p.Pid}-{DateTime.Now:HHmmss}.dmp", file, false, true);
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
              Exit code 0 when met (prints how long it took), 124 on timeout (prints what was still missing).
            """, WaitAsync),
    ];

    private static async Task<int> WaitAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var timeout = a.GetDuration("timeout") ?? TimeSpan.FromSeconds(60);
        var checks = new List<(string name, Func<bool> met)>();

        if (a.Get("exit") is { } exit) checks.Add(($"exit of {exit}", () => ProcessTools.Find(ProcessSnapshot.Take(), exit).Count == 0));
        if (a.Get("start") is { } start) checks.Add(($"start of {start}", () => ProcessTools.Find(ProcessSnapshot.Take(), start).Count > 0));
        if (a.Get("window") is { } win) checks.Add(($"window \"{win}\"", () => UiTools.EnumerateWindows().Any(w => w.Visible && w.Title.Contains(win, StringComparison.OrdinalIgnoreCase))));
        if (a.Get("no-window") is { } nowin) checks.Add(($"window \"{nowin}\" to close", () => !UiTools.EnumerateWindows().Any(w => w.Visible && w.Title.Contains(nowin, StringComparison.OrdinalIgnoreCase))));
        if (a.Get("port") is { } portText && int.TryParse(portText, out var port)) checks.Add(($"port {port}", () => NetTools.IsListening(port)));
        if (a.Get("job") is { } jobId)
        {
            if (ctx.Runtime.Jobs.Find(jobId) == null) return ctx.Fail($"wait: there is no job '{jobId}' (see `jobs`).");
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
                    return ctx.Fail("wait: bad --grep pattern: " + ex.Message);
                }

                checks.Add(($"/{grep}/ in {Path.GetFileName(path)}", () => FileContains(path, re)));
            }
            else
            {
                checks.Add(($"file {path}", () => File.Exists(path)));
            }
        }

        if (a.Get("idle") is { } idle)
        {
            var quietSince = (DateTime?)null;
            long lastCpu = -1;
            var lastAt = Stopwatch.GetTimestamp();
            checks.Add(($"{idle} to go idle", () =>
            {
                var procs = ProcessTools.Find(ProcessSnapshot.Take(), idle);
                if (procs.Count == 0) return false;
                var cpu = procs.Sum(p => p.CpuTime);
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(lastAt, now);
                var busy = lastCpu >= 0 && elapsed.Ticks > 0 ? 100.0 * (cpu - lastCpu) / elapsed.Ticks : 100;
                lastCpu = cpu;
                lastAt = now;
                if (busy >= 2) quietSince = null;
                else quietSince ??= DateTime.UtcNow;
                return quietSince != null && DateTime.UtcNow - quietSince >= TimeSpan.FromSeconds(3);
            }));
        }

        if (checks.Count == 0) return ctx.Fail("wait: no condition given. See `agentprobe help wait`.");

        var sw = Stopwatch.StartNew();
        var pending = checks.ToList();
        while (true)
        {
            pending = pending.Where(c => !Safe(c.met)).ToList();
            if (pending.Count == 0)
            {
                ctx.Out.WriteLine($"ok after {Format.Duration(sw.Elapsed)}: {string.Join(", ", checks.Select(c => c.name))}");
                return 0;
            }

            if (sw.Elapsed >= timeout)
            {
                ctx.Out.WriteLine($"timed out after {Format.Duration(timeout)}; still waiting for: {string.Join(", ", pending.Select(c => c.name))}");
                return 124;
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
