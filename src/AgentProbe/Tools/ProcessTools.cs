using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>Process Explorer in text form: list, live CPU, one-process detail, threads, modules, handles.</summary>
internal static partial class ProcessTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("ps", "Processes", "Process list: memory, handles, threads, CPU time",
            """
            ps [name-or-pid] [--sort mem|ws|cpu|handles|threads|start|name] [--top 25] [--all] [--tree] [--cmd] [--user]
              name filters by substring of the image name. Memory is private bytes (what leaks show up in).
              --tree shows parent/child nesting; --cmd adds the command line; --user adds the account.
            """, PsAsync) { Quiet = true },
        new("top", "Processes", "Who is using CPU right now (sampled) and whose memory is moving",
            """
            top [--interval 1s] [--top 15] [name]
              CPU% is of the whole machine (like Task Manager). Also shows memory change and I/O during the interval.
            """, TopAsync) { Quiet = true },
        new("proc", "Processes", "Everything about one process on one screen",
            """
            proc <pid|name> [--modules] [--threads] [--handles]
              Path, command line, user, integrity, bitness, version, memory + peaks, handles, GDI/USER objects,
              windows (and whether they are hung), children. Flags append the matching detail section.
            """, ProcAsync) { Quiet = true },
        new("threads", "Processes", "Threads of a process with CPU use, state and start module (find the busy or stuck thread)",
            """
            threads <pid|name> [--interval 1s] [--top 20] [--grep REGEX] [--by-name]
              CPU% per thread over the interval, plus state/wait reason, when the thread was created, its name and
              the module it started in. A hung UI with ~0% CPU everywhere is a deadlock or blocking wait; one
              thread at 100/cores % is a busy loop. Follow up with `stacks` or `dump`.
              NAME is the thread's description: what .NET (Core) sets from Thread.Name and what native code sets
              with SetThreadDescription ("Stylus Input", ".NET Server GC"). A .NET Framework Thread.Name is not
              visible to other processes and stays empty here.
              --grep keeps the threads whose NAME or START matches (all of them, not only the top ones): "is the
              worker thread there, and since when?". --by-name counts the threads per name, for a process with
              hundreds of them.
            """, ThreadsAsync) { Quiet = true },
        new("modules", "Processes", "DLLs loaded in a process (spot the wrong version or an unexpected path)",
            """
            modules <pid|name> [--grep REGEX] [--all]
              Windows system DLLs are summarized unless --all or --grep is given. Shows version and full path.
            """, ModulesAsync) { Quiet = true },
        new("handles", "Processes", "Open handles of a process by type (which kind is leaking)",
            """
            handles <pid|name> [--interval 10s]
              Counts per object type. With --interval, samples twice and shows what grew.
            """, HandlesAsync) { Quiet = true },
    ];

    /// <summary>Resolves "1234" or "name" / "name.exe" to snapshot entries (several when the name matches more than one).</summary>
    public static List<ProcessEntry> Find(List<ProcessEntry> all, string spec)
    {
        if (int.TryParse(spec, out var pid)) return all.Where(p => p.Pid == pid).ToList();
        var bare = spec.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? spec[..^4] : spec;
        var exact = all.Where(p => p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(bare + ".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : all.Where(p => p.Name.Contains(bare, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Why OpenProcess failed: the process is gone, or it is protected / belongs to someone else. Never guess "access denied" for a process that exited.</summary>
    public static string WhyNotOpen(string tool, string name, int pid)
        => ProcessSnapshot.Take().Any(p => p.Pid == pid)
            ? $"{tool}: cannot open {name} ({pid}): access denied{(Environment.IsPrivilegedProcess ? " (protected process)" : "; run agentprobe elevated on the target")}."
            : $"{tool}: {name} ({pid}) has exited.";

    private static ProcessEntry? Single(ToolContext ctx, string tool, string[] positionals, bool threads = false)
    {
        if (positionals.Length == 0)
        {
            ctx.Fail($"{tool}: which process? Give a pid or a name.");
            return null;
        }

        var matches = Find(ProcessSnapshot.Take(threads), positionals[0]);
        if (matches.Count == 0)
        {
            ctx.Fail($"{tool}: no process matches '{positionals[0]}'.");
            return null;
        }

        if (matches.Count > 1)
        {
            var pick = matches.OrderByDescending(p => p.PrivateBytes).First();
            ctx.Err.WriteLine($"[{matches.Count} processes match; showing the largest, pid {pick.Pid}. Others: {string.Join(", ", matches.Where(m => m != pick).Take(12).Select(m => m.Pid))}]");
            return pick;
        }

        return matches[0];
    }

    private static string Age(ProcessEntry p) => p.StartTimeUtc is { } s ? Format.Duration(DateTime.UtcNow - s) : "-";

    private static string Cpu(long ticks) => Format.Duration(TimeSpan.FromTicks(ticks));

    // ------------------------------------------------------------------ ps

    private static Task<int> PsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all", "tree", "cmd", "user");
        var all = ProcessSnapshot.Take();
        var list = a.Positionals.Count > 0 ? Find(all, a.Positionals[0]) : all;
        if (a.Get("name") is { } n) list = Find(list, n);
        if (list.Count == 0) return Task.FromResult(ctx.Fail("ps: no matching process."));

        ctx.Out.WriteLine($"{all.Count} processes, {all.Sum(p => p.Threads)} threads, {all.Sum(p => (long)p.Handles)} handles" +
                          (list.Count != all.Count ? $"; {list.Count} match" : ""));

        var wantCmd = a.Flag("cmd");
        var wantUser = a.Flag("user");
        var headers = new List<string> { ">PID", ">PPID", "NAME", ">PRIV(M)", ">WS(M)", ">HND", ">THR", ">CPU", ">AGE" };
        if (wantUser) headers.Add("USER");
        if (wantCmd) headers.Add("COMMAND LINE");
        var t = new Table([.. headers]);

        void AddRow(ProcessEntry p, string indent)
        {
            var cells = new List<object?> { p.Pid, p.ParentPid, indent + p.Name, Format.Mb(p.PrivateBytes), Format.Mb(p.WorkingSet), p.Handles, p.Threads, Cpu(p.CpuTime), Age(p) };
            if (wantUser || wantCmd)
            {
                var h = ProcessDetails.Open(p.Pid);
                if (wantUser) cells.Add(h != 0 ? ProcessDetails.TokenInfo(h).user ?? "?" : "?");
                if (wantCmd) cells.Add(h != 0 ? Format.Clip(ProcessDetails.CommandLine(h), 160) : "");
                if (h != 0) Win32.CloseHandle(h);
            }

            t.Add([.. cells]);
        }

        if (a.Flag("tree"))
        {
            var byParent = list.ToLookup(p => p.ParentPid);
            var pids = list.Select(p => p.Pid).ToHashSet();
            var printed = new HashSet<int>();
            void Walk(ProcessEntry p, int depth)
            {
                if (!printed.Add(p.Pid) || depth > 12) return;
                AddRow(p, new string(' ', depth * 2));
                foreach (var c in byParent[p.Pid].Where(c => c.Pid != p.Pid && c.CreateTime >= p.CreateTime).OrderBy(c => c.CreateTime)) Walk(c, depth + 1);
            }

            // Roots: parent no longer exists (or pid was reused by a younger process).
            foreach (var root in list.Where(p => !pids.Contains(p.ParentPid) || p.ParentPid == p.Pid || all.First(x => x.Pid == p.ParentPid).CreateTime > p.CreateTime).OrderBy(p => p.CreateTime))
                Walk(root, 0);
        }
        else
        {
            IEnumerable<ProcessEntry> sorted = (a.Get("sort") ?? "mem").ToLowerInvariant() switch
            {
                "ws" => list.OrderByDescending(p => p.WorkingSet),
                "cpu" => list.OrderByDescending(p => p.CpuTime),
                "handles" or "hnd" => list.OrderByDescending(p => p.Handles),
                "threads" or "thr" => list.OrderByDescending(p => p.Threads),
                "start" or "age" => list.OrderByDescending(p => p.CreateTime),
                "name" => list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
                "pid" => list.OrderBy(p => p.Pid),
                _ => list.OrderByDescending(p => p.PrivateBytes),
            };
            var top = a.Flag("all") || a.Positionals.Count > 0 ? int.MaxValue : a.GetInt("top", 25);
            foreach (var p in sorted.Take(top)) AddRow(p, "");
            ctx.Out.Write(t.ToString());
            if (list.Count > top) ctx.Out.WriteLine($"… {list.Count - top} more (--all, or filter by name)");
            return Task.FromResult(0);
        }

        ctx.Out.Write(t.ToString());
        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ top

    private static async Task<int> TopAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var interval = a.GetDuration("interval") ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(200)) interval = TimeSpan.FromMilliseconds(200);

        var before = ProcessSnapshot.Take().ToDictionary(p => (p.Pid, p.CreateTime));
        Win32.GetSystemTimes(out var idle0, out var kernel0, out var user0);
        var sw = Stopwatch.StartNew();
        await Task.Delay(interval, ctx.Ct);
        var after = ProcessSnapshot.Take();
        Win32.GetSystemTimes(out var idle1, out var kernel1, out var user1);
        var elapsed = sw.Elapsed;

        var cores = Environment.ProcessorCount;
        var capacity = elapsed.Ticks * (double)cores;
        var total = (kernel1 - kernel0) + (user1 - user0);
        var busy = total > 0 ? 100.0 * (total - (idle1 - idle0)) / total : 0;
        var mem = default(MEMORYSTATUSEX);
        mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        Win32.GlobalMemoryStatusEx(ref mem);
        ctx.Out.WriteLine($"cpu {Format.Pct(busy)}% of {cores} cores   ram {mem.dwMemoryLoad}% used ({Format.Bytes((long)(mem.ullTotalPhys - mem.ullAvailPhys))} of {Format.Bytes((long)mem.ullTotalPhys)})   sampled {Format.Duration(elapsed)}");

        var rows = new List<(ProcessEntry p, double cpu, long dPriv, long io)>();
        foreach (var p in after)
        {
            if (p.Pid == 0) continue;
            if (a.Positionals.Count > 0 && !p.Name.Contains(a.Positionals[0].Replace(".exe", ""), StringComparison.OrdinalIgnoreCase) && p.Pid.ToString() != a.Positionals[0]) continue;
            before.TryGetValue((p.Pid, p.CreateTime), out var b);
            var cpu = 100.0 * (p.CpuTime - (b?.CpuTime ?? 0)) / capacity;
            var io = (p.ReadBytes + p.WriteBytes) - ((b?.ReadBytes ?? p.ReadBytes) + (b?.WriteBytes ?? p.WriteBytes));
            rows.Add((p, cpu, p.PrivateBytes - (b?.PrivateBytes ?? p.PrivateBytes), io));
        }

        var t = new Table(">PID", "NAME", ">CPU%", ">PRIV(M)", ">ΔPRIV", ">IO", ">HND", ">THR");
        foreach (var (p, cpu, dPriv, io) in rows.OrderByDescending(r => r.cpu).ThenByDescending(r => r.p.PrivateBytes).Take(a.GetInt("top", 15)))
            t.Add(p.Pid, p.Name, Format.Pct(cpu), Format.Mb(p.PrivateBytes), dPriv == 0 ? "" : Format.SignedBytes(dPriv), io == 0 ? "" : Format.Bytes(io), p.Handles, p.Threads);
        ctx.Out.Write(t.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ proc

    private static Task<int> ProcAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "modules", "threads", "handles");
        var all = ProcessSnapshot.Take(true);
        var p = Single(ctx, "proc", [.. a.Positionals], true);
        if (p == null) return Task.FromResult(2);
        p = all.FirstOrDefault(x => x.Pid == p.Pid) ?? p;

        var o = ctx.Out;
        var parent = all.FirstOrDefault(x => x.Pid == p.ParentPid && x.CreateTime <= p.CreateTime);
        var h = ProcessDetails.Open(p.Pid);
        var arch = h != 0 ? ProcessDetails.Architecture(h) : null;
        o.WriteLine($"{p.Name} (pid {p.Pid})  {arch ?? "?"}  running {Age(p)}  parent {(parent != null ? $"{parent.Name}({parent.Pid})" : $"{p.ParentPid} (exited)")}  session {p.SessionId}");
        if (h == 0)
        {
            o.WriteLine("  (access denied to this process: path, command line and user are unavailable" + (Environment.IsPrivilegedProcess ? "" : "; run agentprobe elevated on the target for more") + ")");
        }
        else
        {
            var path = Win32.GetProcessImagePath(h);
            o.WriteLine($"  path     {path}");
            o.WriteLine($"  cmdline  {Format.Clip(ProcessDetails.CommandLine(h), 600)}");
            var (user, integrity, elevated) = ProcessDetails.TokenInfo(h);
            o.WriteLine($"  user     {user ?? "?"}  integrity {integrity ?? "?"}  elevated {(elevated == true ? "yes" : elevated == false ? "no" : "?")}");
            if (path != null)
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(vi.FileVersion)) o.WriteLine($"  version  {vi.FileVersion}  \"{Format.Clip(vi.FileDescription, 60)}\"  {Format.Clip(vi.CompanyName, 40)}");
                }
                catch (FileNotFoundException)
                {
                    // image deleted while running
                }
            }
        }

        o.WriteLine($"  memory   private {Format.Bytes(p.PrivateBytes)} (peak {Format.Bytes(p.PeakPrivateBytes)})  working set {Format.Bytes(p.WorkingSet)} (peak {Format.Bytes(p.PeakWorkingSet)})");
        o.WriteLine($"  pools    paged {Format.Bytes(p.PagedPool)}  nonpaged {Format.Bytes(p.NonPagedPool)}  page faults {p.PageFaults}  hard faults {p.HardFaults}");
        o.WriteLine($"  cpu      {Cpu(p.CpuTime)} total (user {Cpu(p.UserTime)}, kernel {Cpu(p.KernelTime)})  threads {p.Threads}");
        var gui = h != 0 ? ProcessDetails.GuiObjects(h) : (0u, 0u);
        var warn = new List<string>();
        if (p.Handles > 20000) warn.Add("handle count is very high (leak?)");
        if (gui.Item1 > 8000) warn.Add("GDI objects near the 10,000 per-process limit");
        if (gui.Item2 > 8000) warn.Add("USER objects near the 10,000 per-process limit");
        o.WriteLine($"  handles  {p.Handles}  gdi {gui.Item1}  user {gui.Item2}" + (warn.Count > 0 ? "   ! " + string.Join("; ", warn) : ""));
        o.WriteLine($"  io       read {Format.Bytes(p.ReadBytes)}  write {Format.Bytes(p.WriteBytes)}  other {Format.Bytes(p.OtherBytes)}");

        var windows = UiTools.EnumerateWindows().Where(w => w.Pid == p.Pid && w.Visible && w.Title.Length > 0).ToList();
        if (windows.Count > 0)
            o.WriteLine("  windows  " + string.Join("; ", windows.Take(6).Select(w => $"\"{Format.Clip(w.Title, 50)}\"{(w.Hung ? " NOT RESPONDING" : "")}")));
        var children = all.Where(c => c.ParentPid == p.Pid && c.CreateTime >= p.CreateTime && c.Pid != p.Pid).ToList();
        if (children.Count > 0) o.WriteLine("  children " + string.Join(", ", children.Take(15).Select(c => $"{c.Name}({c.Pid})")));
        if (h != 0) Win32.CloseHandle(h);

        if (a.Flag("threads"))
        {
            o.WriteLine();
            WriteThreads(ctx, p, null, TimeSpan.Zero, 15);
        }

        if (a.Flag("modules"))
        {
            o.WriteLine();
            WriteModules(ctx, p.Pid, null, false);
        }

        if (a.Flag("handles"))
        {
            o.WriteLine();
            WriteHandles(ctx, p.Pid, null);
        }

        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ threads

    private static async Task<int> ThreadsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "by-name");
        System.Text.RegularExpressions.Regex? grep = null;
        if (a.Get("grep") is { } pattern)
        {
            try
            {
                grep = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return ctx.Fail("threads: bad --grep pattern: " + ex.Message);
            }
        }

        var p = Single(ctx, "threads", [.. a.Positionals], true);
        if (p == null) return 2;
        var interval = a.GetDuration("interval") ?? TimeSpan.FromSeconds(1);
        var sw = Stopwatch.StartNew();
        await Task.Delay(interval, ctx.Ct);
        var later = ProcessSnapshot.Take(true).FirstOrDefault(x => x.Pid == p.Pid && x.CreateTime == p.CreateTime);
        if (later == null) return ctx.Fail($"threads: {p.Name} ({p.Pid}) exited during the sample.");
        WriteThreads(ctx, later, p, sw.Elapsed, a.GetInt("top", a.Flag("by-name") ? 40 : 20), grep, a.Flag("by-name"));
        return 0;
    }

    private static unsafe void WriteThreads(ToolContext ctx, ProcessEntry now, ProcessEntry? before, TimeSpan elapsed, int top, System.Text.RegularExpressions.Regex? grep = null, bool byName = false)
    {
        var threads = now.ThreadList ?? [];
        var prev = before?.ThreadList?.ToDictionary(t => t.Tid) ?? [];
        var modules = new List<(nint baseAddr, uint size, string path)>();
        var h = ProcessDetails.Open(now.Pid, vmRead: true);
        if (h != 0)
        {
            modules = ProcessDetails.Modules(h);
            Win32.CloseHandle(h);
        }

        var cores = Environment.ProcessorCount;
        var rows = threads.Select(t =>
        {
            // A thread that was born during the interval did all of its work inside it.
            var delta = prev.TryGetValue(t.Tid, out var b) ? (t.KernelTime + t.UserTime) - (b.KernelTime + b.UserTime) : (before != null ? t.KernelTime + t.UserTime : 0);
            return (t, pct: elapsed.Ticks > 0 ? 100.0 * delta / elapsed.Ticks : 0);
        }).OrderByDescending(r => r.pct).ThenByDescending(r => r.t.KernelTime + r.t.UserTime).ToList();

        var sumPct = rows.Sum(r => r.pct);
        var waiting = threads.Count(t => t.State == 5);
        ctx.Out.WriteLine($"{now.Name} ({now.Pid}): {threads.Count} threads, {waiting} waiting" +
                          (before != null ? $", {Format.Pct(sumPct)}% of one core in use over {Format.Duration(elapsed)} ({cores} cores)" : ""));
        // Process-level CPU also counts threads that came and went inside the interval, which the per-thread rows cannot show.
        var processBusy = before != null && elapsed.Ticks > 0 ? 100.0 * (now.CpuTime - before.CpuTime) / elapsed.Ticks : 0;
        if (before != null && sumPct < 1 && processBusy < 1 && UiTools.EnumerateWindows().Any(w => w.Pid == now.Pid && w.Hung))
            ctx.Out.WriteLine("! a window of this process is not responding while its threads use no CPU: blocked/deadlocked, not busy. Take a `dump`.");

        // Name and start module need a handle to each thread. With --grep or --by-name every thread is asked,
        // otherwise only the ones that are listed.
        var details = new Dictionary<int, (string start, string name, bool nameRead, bool opened)>();
        (string start, string name, bool nameRead, bool opened) Detail(int tid)
        {
            if (!details.TryGetValue(tid, out var d)) details[tid] = d = ThreadDetail(tid, modules);
            return d;
        }

        static string Created(ThreadEntry t) => t.CreateTime > 0 ? Format.Time(DateTime.FromFileTimeUtc(t.CreateTime)) : "";

        if (byName)
        {
            var groups = rows.GroupBy(r => Detail(r.t.Tid) is { nameRead: true } d ? (d.name.Length > 0 ? d.name : "(no name)") : "(name not readable)")
                .Select(g => (name: g.Key, count: g.Count(), pct: g.Sum(r => r.pct), first: g.Min(r => r.t.CreateTime), last: g.Max(r => r.t.CreateTime)))
                .Where(g => grep == null || (g.name is not ("(no name)" or "(name not readable)") && grep.IsMatch(g.name))) // the two placeholders are not names
                .OrderByDescending(g => g.count).ThenBy(g => g.name, StringComparer.Ordinal).ToList();
            if (groups.Count == 0)
            {
                ctx.Out.WriteLine("[no thread name matches]");
                return;
            }

            static string When(long fileTime) => fileTime > 0 ? Format.Time(DateTime.FromFileTimeUtc(fileTime)) : "";
            var names = new Table(">COUNT", ">CPU%", "FIRST CREATED", "LAST CREATED", "NAME");
            foreach (var g in groups.Take(top)) names.Add(g.count, before != null ? Format.Pct(g.pct) : "", When(g.first), g.count > 1 ? When(g.last) : "", g.name);
            ctx.Out.Write(names.ToString());
            if (groups.Count > top) ctx.Out.WriteLine($"… {groups.Count - top} more names, {groups.Skip(top).Sum(g => g.count)} threads (--top)");
            return;
        }

        if (grep != null)
        {
            var total = rows.Count;
            rows = rows.Where(r => Detail(r.t.Tid) is var d && (grep.IsMatch(d.name) || grep.IsMatch(d.start))).ToList();
            var unopened = details.Values.Count(d => !d.opened);
            var unnamed = details.Values.Count(d => d.opened && !d.nameRead);
            ctx.Out.WriteLine($"{rows.Count} of {total} threads match in NAME or START" +
                              (unopened > 0 ? $"; {unopened} threads could not be opened and were NOT looked at (a protected process, or run agentprobe elevated on the target)" : "") +
                              (unnamed > 0 ? $"; the names of {unnamed} threads could not be read: those are judged by START alone" : ""));
            if (rows.Count == 0) return;
        }

        var table = new Table(">TID", ">CPU%", ">CPU TIME", "STATE", ">SWITCHES", "CREATED", "NAME", "START");
        foreach (var (t, pct) in rows.Take(top))
        {
            var state = t.State == 5 ? "wait:" + ProcessSnapshot.WaitReasonName(t.WaitReason) : ProcessSnapshot.ThreadStateName(t.State);
            var d = Detail(t.Tid);
            table.Add(t.Tid, before != null ? Format.Pct(pct) : "", Cpu(t.KernelTime + t.UserTime), state, t.ContextSwitches, Created(t), d.nameRead ? Format.Clip(d.name, 48) : "?", d.start);
        }

        ctx.Out.Write(table.ToString());
        if (rows.Count > top) ctx.Out.WriteLine($"… {rows.Count - top} more threads (--top)");
        if (rows.Take(top).Any(r => !Detail(r.t.Tid).nameRead))
        {
            ctx.Out.WriteLine(_noThreadNames
                ? "NAME ?: this Windows has no thread names (they came with Windows 10 version 1607)."
                : "NAME ?: that thread's name could not be read (a protected process, or run agentprobe elevated on the target).");
        }
    }

    /// <summary>
    /// What needs a handle to the thread: "module.dll+0x1234" for its user-mode start address, and its description
    /// (the name .NET or native code gave it). <c>nameRead</c> is false when the name could not be asked for, which
    /// is not the same as a thread without a name; <c>opened</c> is false when nothing at all could be asked.
    /// </summary>
    private static bool _noThreadNames; // GetThreadDescription does not exist on this Windows

    private static unsafe (string start, string name, bool nameRead, bool opened) ThreadDetail(int tid, List<(nint baseAddr, uint size, string path)> modules)
    {
        var start = "";
        var th = OpenThread(0x0040, 0, (uint)tid); // THREAD_QUERY_INFORMATION
        if (th != 0)
        {
            nint address = 0;
            if (NtQueryInformationThread(th, 9, &address, (uint)sizeof(nint), out _) >= 0 && address != 0) // ThreadQuerySetWin32StartAddress
            {
                start = "0x" + ((long)address).ToString("X");
                foreach (var (baseAddr, size, path) in modules)
                {
                    if (address >= baseAddr && address < baseAddr + (nint)size)
                    {
                        start = $"{Path.GetFileName(path)}+0x{(long)(address - baseAddr):X}";
                        break;
                    }
                }
            }
        }
        else
        {
            th = OpenThread(0x0800, 0, (uint)tid); // THREAD_QUERY_LIMITED_INFORMATION is enough for the name
        }

        if (th == 0) return (start, "", false, false);
        try
        {
            char* description = null;
            if (_noThreadNames || GetThreadDescription(th, &description) < 0) return (start, "", false, true);
            var name = description != null ? new string(description) : "";
            if (description != null) Win32.LocalFree((nint)description);
            return (start, name, true, true);
        }
        catch (EntryPointNotFoundException)
        {
            _noThreadNames = true; // Windows older than 10 version 1607 has no thread descriptions
            return (start, "", false, true);
        }
        finally
        {
            Win32.CloseHandle(th);
        }
    }

    // ------------------------------------------------------------------ modules

    private static Task<int> ModulesAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all");
        var p = Single(ctx, "modules", [.. a.Positionals]);
        if (p == null) return Task.FromResult(2);
        return Task.FromResult(WriteModules(ctx, p.Pid, a.Get("grep"), a.Flag("all")));
    }

    private static int WriteModules(ToolContext ctx, int pid, string? grep, bool all)
    {
        var h = ProcessDetails.Open(pid, vmRead: true);
        if (h == 0) return ctx.Fail(WhyNotOpen("modules", "process", pid));
        List<(nint baseAddr, uint size, string path)> mods;
        try
        {
            mods = ProcessDetails.Modules(h);
        }
        finally
        {
            Win32.CloseHandle(h);
        }

        if (mods.Count == 0) return ctx.Fail("modules: could not enumerate modules (process exiting, or protected).");
        System.Text.RegularExpressions.Regex? re = null;
        if (grep != null)
        {
            try
            {
                re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return ctx.Fail("modules: bad --grep pattern: " + ex.Message);
            }
        }

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var shown = new Table("MODULE", "VERSION", ">SIZE", "PATH");
        var hidden = 0;
        foreach (var (_, size, path) in mods.OrderBy(m => m.path, StringComparer.OrdinalIgnoreCase))
        {
            if (re != null && !re.IsMatch(path)) continue;
            var system = path.StartsWith(windir, StringComparison.OrdinalIgnoreCase);
            if (system && !all && re == null)
            {
                hidden++;
                continue;
            }

            var version = "";
            try
            {
                version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Split(' ')[0] ?? "";
            }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
            {
                version = "(file gone)";
            }

            shown.Add(Path.GetFileName(path), version, Format.Bytes(size), path);
        }

        ctx.Out.WriteLine($"{mods.Count} modules loaded in pid {pid}" + (hidden > 0 ? $"; {hidden} under {windir} hidden (--all shows them)" : ""));
        if (shown.Count > 0) ctx.Out.Write(shown.ToString());
        else if (re != null) ctx.Out.WriteLine("[no module matches]");
        return 0;
    }

    // ------------------------------------------------------------------ handles

    private static async Task<int> HandlesAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var p = Single(ctx, "handles", [.. a.Positionals]);
        if (p == null) return 2;
        Dictionary<string, int>? before = null;
        if (a.GetDuration("interval") is { } interval)
        {
            before = HandleSnapshot.CountByType(p.Pid).GetValueOrDefault(p.Pid) ?? [];
            await Task.Delay(interval, ctx.Ct);
            if (!ProcessSnapshot.Take().Any(x => x.Pid == p.Pid && x.CreateTime == p.CreateTime)) return ctx.Fail($"handles: {p.Name} ({p.Pid}) exited during the interval.");
        }

        return WriteHandles(ctx, p.Pid, before);
    }

    private static int WriteHandles(ToolContext ctx, int pid, Dictionary<string, int>? before)
    {
        Dictionary<string, int> now;
        try
        {
            now = HandleSnapshot.CountByType(pid).GetValueOrDefault(pid) ?? [];
        }
        catch (InvalidOperationException ex)
        {
            return ctx.Fail("handles: " + ex.Message);
        }

        var sb = new StringBuilder($"pid {pid}: {now.Values.Sum()} handles");
        if (before != null) sb.Append($" ({now.Values.Sum() - before.Values.Sum():+0;-0;+0} during the interval)");
        ctx.Out.WriteLine(sb.ToString());
        var t = before != null ? new Table("TYPE", ">COUNT", ">CHANGE") : new Table("TYPE", ">COUNT");
        foreach (var (type, count) in now.OrderByDescending(kv => kv.Value))
        {
            if (before != null)
            {
                var d = count - before.GetValueOrDefault(type);
                t.Add(type, count, d == 0 ? "" : d.ToString("+0;-0"));
            }
            else
            {
                t.Add(type, count);
            }
        }

        ctx.Out.Write(t.ToString());
        return 0;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, int inherit, uint tid);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetThreadDescription(nint thread, char** description);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationThread(nint thread, int infoClass, void* buffer, uint length, out uint returnLength);
}
