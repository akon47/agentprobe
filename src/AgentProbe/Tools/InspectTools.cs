using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// Looking inside a live process without pulling a dump first:
/// <c>stacks</c> answers "where is each thread right now" and <c>vmmap</c> answers "what kind of memory is growing".
/// </summary>
internal static partial class InspectTools
{
    internal static readonly object DbgHelpLock = new(); // dbghelp is single-threaded

    public static IEnumerable<Tool> Tools =>
    [
        new("stacks", "Processes", "Call stacks of a live process, identical stacks grouped (where is it stuck / busy)",
            """
            stacks <pid|name> [--tid N] [--frames 14] [--top 12]
              Suspends each thread for an instant, walks its stack and resumes it; the process keeps running.
              Threads with the same stack are printed once. The thread that owns the windows is marked "UI thread".
              Function names come from PDB files when they sit next to the module (or _NT_SYMBOL_PATH is set on the
              target), otherwise from the nearest exported function: exact for Windows API calls, approximate inside a
              module. JIT-compiled (.NET, JavaScript) frames have no module and are shown as "[managed/JIT code]":
              for those take a `dump --full` and use dotnet-dump / WinDbg. 64-bit processes only.
            """, StacksAsync) { Quiet = true },
        new("vmmap", "Processes", "What a process's memory consists of, and which kind grows (heap, stacks, images, mapped files)",
            """
            vmmap <pid|name> [--interval 30s]
              Committed memory by kind: private data (heaps), thread stacks, JIT code, images (EXE/DLL), mapped files,
              plus reserved address space and the largest private allocations. With --interval it measures twice and
              shows what grew: private data = a heap leak, stacks = a thread leak, mapped = files/sections not released.
            """, VmmapAsync) { Quiet = true },
    ];

    // ------------------------------------------------------------------ stacks

    private static Task<int> StacksAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("stacks: which process? pid or name."));
        var matches = ProcessTools.Find(ProcessSnapshot.Take(true), a.Positionals[0]);
        if (matches.Count == 0) return Task.FromResult(ctx.Fail($"stacks: no process matches '{a.Positionals[0]}'."));
        var p = matches.OrderByDescending(m => m.PrivateBytes).First();
        if (matches.Count > 1) ctx.Err.WriteLine($"[{matches.Count} processes match; using the largest, pid {p.Pid}]");
        if (p.Pid == Environment.ProcessId) return Task.FromResult(ctx.Fail("stacks: cannot walk agentprobe's own threads."));

        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, p.Pid);
        if (process == 0) return Task.FromResult(ctx.Fail(ProcessTools.WhyNotOpen("stacks", p.Name, p.Pid)));
        try
        {
            if (ProcessDetails.Architecture(process) is "x86" or "arm")
                return Task.FromResult(ctx.Fail($"stacks: {p.Name} is a 32-bit process, which this command cannot walk yet. Use `threads {p.Pid}` and `dump {p.Pid}`."));

            var modules = ProcessDetails.Modules(process).OrderBy(m => (long)m.baseAddr).ToList();
            var maxFrames = Math.Clamp(a.GetInt("frames", 14), 3, 60);
            var onlyTid = a.Has("tid") ? a.GetInt("tid", 0) : 0;
            var uiThreads = UiTools.EnumerateWindows().Where(w => w.Pid == p.Pid && w.Visible).Select(w => (int)Win32.GetWindowThreadProcessId(w.Hwnd, out _)).ToHashSet();
            var threads = (p.ThreadList ?? []).Where(t => onlyTid == 0 || t.Tid == onlyTid).ToList();
            if (threads.Count == 0) return Task.FromResult(ctx.Fail($"stacks: thread {onlyTid} is not in {p.Name}."));

            var stacks = new List<(ThreadEntry thread, List<string> frames)>();
            var hasPdb = false;
            lock (DbgHelpLock)
            {
                SymSetOptions(0x2 | 0x4 | 0x200 | 0x80000); // UNDNAME | DEFERRED_LOADS | FAIL_CRITICAL_ERRORS | NO_PROMPTS
                if (!SymInitializeW(process, 0, 1)) return Task.FromResult(ctx.Fail($"stacks: could not read the module list of {p.Name} (0x{Marshal.GetLastPInvokeError():X})."));
                try
                {
                    var dbghelp = NativeLibrary.Load("dbghelp.dll");
                    var functionTable = NativeLibrary.GetExport(dbghelp, "SymFunctionTableAccess64");
                    var moduleBase = NativeLibrary.GetExport(dbghelp, "SymGetModuleBase64");
                    foreach (var t in threads)
                    {
                        var frames = WalkThread(process, t.Tid, modules, maxFrames, functionTable, moduleBase, ref hasPdb);
                        if (frames != null) stacks.Add((t, frames));
                    }
                }
                finally
                {
                    SymCleanup(process);
                }
            }

            if (stacks.Count == 0) return Task.FromResult(ctx.Fail($"stacks: could not read any thread of {p.Name} (protected process?)."));

            var groups = stacks.GroupBy(s => string.Join('\n', s.frames)).OrderByDescending(g => g.Any(s => uiThreads.Contains(s.thread.Tid))).ThenByDescending(g => g.Count()).ToList();
            ctx.Out.WriteLine($"{p.Name} ({p.Pid}): {stacks.Count} threads, {groups.Count} distinct stacks. {(hasPdb ? "Names come from PDBs where one was found, otherwise from export tables." : "No PDBs found: names come from export tables only, so internal functions show as module+offset.")}");
            var top = a.GetInt("top", 12);
            foreach (var g in groups.Take(top))
            {
                var list = g.ToList();
                var first = list[0].thread;
                var state = first.State == 5 ? "wait:" + ProcessSnapshot.WaitReasonName(first.WaitReason) : ProcessSnapshot.ThreadStateName(first.State);
                var ui = list.Any(s => uiThreads.Contains(s.thread.Tid)) ? "  UI thread" : "";
                var tids = string.Join(",", list.Take(8).Select(s => s.thread.Tid)) + (list.Count > 8 ? $",+{list.Count - 8}" : "");
                ctx.Out.WriteLine($"== {list.Count} thread{(list.Count == 1 ? "" : "s")} (tid {tids})  {state}{ui}");
                foreach (var f in list[0].frames) ctx.Out.WriteLine("   " + f);
            }

            if (groups.Count > top) ctx.Out.WriteLine($"… {groups.Count - top} more distinct stacks ({groups.Skip(top).Sum(g => g.Count())} threads); raise --top or use --tid");
            return Task.FromResult(0);
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static unsafe List<string>? WalkThread(nint process, int tid, List<(nint baseAddr, uint size, string path)> modules, int maxFrames, nint functionTable, nint moduleBase, ref bool hasPdb)
    {
        var thread = OpenThread(0x0002 | 0x0008 | 0x0040, 0, (uint)tid); // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
        if (thread == 0) return null;
        var context = (byte*)NativeMemory.AlignedAlloc(1232, 16);
        var frame = stackalloc byte[264];
        var symbol = stackalloc byte[88 + 512];
        var suspended = false;
        try
        {
            if (SuspendThread(thread) == uint.MaxValue) return null;
            suspended = true;
            new Span<byte>(context, 1232).Clear();
            *(uint*)(context + 0x30) = 0x100003; // CONTEXT_CONTROL | CONTEXT_INTEGER (AMD64)
            if (!GetThreadContext(thread, context)) return null;

            new Span<byte>(frame, 264).Clear();
            *(ulong*)frame = *(ulong*)(context + 0xF8);        // AddrPC = Rip
            *(int*)(frame + 12) = 3;                            // AddrModeFlat
            *(ulong*)(frame + 32) = *(ulong*)(context + 0xA0);  // AddrFrame = Rbp
            *(int*)(frame + 44) = 3;
            *(ulong*)(frame + 48) = *(ulong*)(context + 0x98);  // AddrStack = Rsp
            *(int*)(frame + 60) = 3;

            var frames = new List<string>();
            var jit = 0;
            for (var i = 0; i < maxFrames + 20 && frames.Count < maxFrames; i++)
            {
                if (!StackWalk64(0x8664, process, thread, frame, context, 0, functionTable, moduleBase, 0)) break;
                var pc = *(ulong*)frame;
                if (pc == 0) break;

                var module = modules.FirstOrDefault(m => pc >= (ulong)m.baseAddr && pc < (ulong)m.baseAddr + m.size);
                if (module.path == null)
                {
                    // JIT-compiled or generated code has no unwind data dbghelp can read from outside the process.
                    // Anything "walked" past this point would be guesswork from stale stack contents: stop here.
                    frames.Add("[managed/JIT or generated code: the frames below it cannot be walked from outside; use `dump --full` + dotnet-dump/WinDbg]");
                    break;
                }

                if (jit > 0)
                {
                    frames.Add($"[managed/JIT code, {jit} frame{(jit == 1 ? "" : "s")}]");
                    jit = 0;
                }

                var name = Path.GetFileNameWithoutExtension(module.path);
                var offset = pc - (ulong)module.baseAddr;
                new Span<byte>(symbol, 88 + 512).Clear();
                *(uint*)symbol = 88;
                *(uint*)(symbol + 80) = 255;
                if (SymFromAddrW(process, pc, out var displacement, symbol))
                {
                    var fn = Format.Clip(new string((char*)(symbol + 84), 0, (int)Math.Min(*(uint*)(symbol + 76), 254u)), 90);
                    if (HasRealSymbols(process, (ulong)module.baseAddr))
                    {
                        hasPdb = true;
                        frames.Add($"{name}!{fn}+0x{displacement:X}");
                    }
                    else if (displacement <= 0x100)
                    {
                        // Without a PDB only exported names are known. Close to the export it is that function.
                        frames.Add($"{name}!{fn}+0x{displacement:X}");
                    }
                    else
                    {
                        // Far past the nearest export: most likely an internal function. Give the exact offset
                        // (resolvable with the module's PDB) and only hint at the neighbourhood.
                        frames.Add($"{name}+0x{offset:X}  (internal; nearest export below: {fn})");
                    }
                }
                else
                {
                    frames.Add($"{name}+0x{offset:X}");
                }
            }

            if (jit > 0) frames.Add($"[managed/JIT code, {jit} frame{(jit == 1 ? "" : "s")}]");
            return frames.Count > 0 ? frames : null;
        }
        finally
        {
            if (suspended) ResumeThread(thread);
            Win32.CloseHandle(thread);
            NativeMemory.AlignedFree(context);
        }
    }

    // ------------------------------------------------------------------ vmmap

    private sealed class MemoryMap
    {
        public long PrivateData, Stacks, JitCode, Images, Mapped, Reserved;
        public readonly Dictionary<long, long> PrivateAllocations = [];

        public long Committed => PrivateData + Stacks + JitCode + Images + Mapped;
    }

    /// <summary>True when dbghelp loaded a PDB (or other full debug info) for the module, false for export-table names.</summary>
    private static unsafe bool HasRealSymbols(nint process, ulong moduleBase)
    {
        var info = (byte*)NativeMemory.AllocZeroed(3264); // IMAGEHLP_MODULEW64
        try
        {
            *(uint*)info = 3264;
            if (!SymGetModuleInfoW64(process, moduleBase, info)) return false;
            return *(int*)(info + 32) is 1 or 2 or 3 or 7; // SymCoff, SymCv, SymPdb, SymDia
        }
        finally
        {
            NativeMemory.Free(info);
        }
    }

    private static unsafe MemoryMap? Map(nint process)
    {
        var map = new MemoryMap();
        var regions = new List<(long allocationBase, long size, uint state, uint protect, uint type)>();
        var info = stackalloc byte[48];
        nuint address = 0;
        while (VirtualQueryEx(process, address, info, 48) == 48)
        {
            var size = *(long*)(info + 24);
            if (size <= 0) break;
            regions.Add((*(long*)(info + 8), size, *(uint*)(info + 32), *(uint*)(info + 36), *(uint*)(info + 40)));
            address = (nuint)(*(long*)info + size);
            if (address >= 0x7FFFFFFF0000) break;
        }

        if (regions.Count == 0) return null;

        // A private allocation that contains a guard page is a thread stack.
        var stackAllocations = regions.Where(r => r.type == 0x20000 && r.state == 0x1000 && (r.protect & 0x100) != 0).Select(r => r.allocationBase).ToHashSet();
        foreach (var r in regions)
        {
            if (r.state == 0x2000)
            {
                map.Reserved += r.size;
                continue;
            }

            if (r.state != 0x1000) continue;
            switch (r.type)
            {
                case 0x1000000:
                    map.Images += r.size;
                    break;
                case 0x40000:
                    map.Mapped += r.size;
                    break;
                default:
                    if (stackAllocations.Contains(r.allocationBase)) map.Stacks += r.size;
                    else if ((r.protect & 0xF0) != 0) map.JitCode += r.size; // executable private memory
                    else
                    {
                        map.PrivateData += r.size;
                        map.PrivateAllocations[r.allocationBase] = map.PrivateAllocations.GetValueOrDefault(r.allocationBase) + r.size;
                    }

                    break;
            }
        }

        return map;
    }

    private static async Task<int> VmmapAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        if (a.Positionals.Count == 0) return ctx.Fail("vmmap: which process? pid or name.");
        var matches = ProcessTools.Find(ProcessSnapshot.Take(), a.Positionals[0]);
        if (matches.Count == 0) return ctx.Fail($"vmmap: no process matches '{a.Positionals[0]}'.");
        var p = matches.OrderByDescending(m => m.PrivateBytes).First();
        if (matches.Count > 1) ctx.Err.WriteLine($"[{matches.Count} processes match; using the largest, pid {p.Pid}]");

        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, p.Pid);
        if (process == 0) return ctx.Fail(ProcessTools.WhyNotOpen("vmmap", p.Name, p.Pid));
        try
        {
            var before = Snapshot(process);
            if (before == null) return ctx.Fail("vmmap: could not read the address space (protected process?).");
            MemoryMap now = before;
            TimeSpan? interval = a.GetDuration("interval");
            if (interval != null)
            {
                await Task.Delay(interval.Value, ctx.Ct);
                var second = Snapshot(process);
                if (second == null) return ctx.Fail($"vmmap: {p.Name} ({p.Pid}) exited during the interval.");
                now = second;
            }

            ctx.Out.WriteLine($"{p.Name} ({p.Pid}): {Format.Bytes(now.Committed)} committed, {Format.Bytes(now.Reserved)} reserved" + (interval != null ? $"; change over {Format.Duration(interval.Value)} in the last column" : ""));
            var t = interval != null ? new Table("KIND", ">COMMITTED", ">CHANGE", "MEANING") : new Table("KIND", ">COMMITTED", "MEANING");
            void Row(string kind, long value, long old, string meaning)
            {
                if (interval != null) t.Add(kind, Format.Bytes(value), value == old ? "" : Format.SignedBytes(value - old), meaning);
                else t.Add(kind, Format.Bytes(value), meaning);
            }

            Row("private data", now.PrivateData, before.PrivateData, "heaps and other allocations; steady growth here is a heap leak");
            Row("thread stacks", now.Stacks, before.Stacks, "grows with the thread count (thread leak)");
            Row("JIT / dynamic code", now.JitCode, before.JitCode, ".NET, JavaScript and other generated code");
            Row("images", now.Images, before.Images, "EXE and DLL files");
            Row("mapped", now.Mapped, before.Mapped, "memory-mapped files and shared sections");
            ctx.Out.Write(t.ToString());

            var largest = now.PrivateAllocations.OrderByDescending(kv => kv.Value).Take(6).ToList();
            ctx.Out.WriteLine($"{now.PrivateAllocations.Count} private allocations; largest: " + string.Join("  ", largest.Select(kv => $"{Format.Bytes(kv.Value)}@0x{kv.Key:X}")));
            if (interval != null)
            {
                var fresh = now.PrivateAllocations.Where(kv => !before.PrivateAllocations.ContainsKey(kv.Key)).ToList();
                var grown = now.PrivateAllocations.Where(kv => before.PrivateAllocations.TryGetValue(kv.Key, out var old) && kv.Value > old).Sum(kv => kv.Value - before.PrivateAllocations[kv.Key]);
                ctx.Out.WriteLine($"during the interval: {fresh.Count} new private allocations ({Format.Bytes(fresh.Sum(kv => kv.Value))}), existing ones grew by {Format.Bytes(grown)}" +
                                  (fresh.Count > 20 && fresh.Sum(kv => kv.Value) > 4 << 20 ? "  -> many new allocations: look for something created repeatedly and never freed" : ""));
            }

            return 0;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static MemoryMap? Snapshot(nint process) => Map(process);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, int inherit, uint tid);

    [LibraryImport("kernel32.dll")]
    private static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetThreadContext(nint thread, byte* context);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial nuint VirtualQueryEx(nint process, nuint address, byte* info, nuint length);

    [LibraryImport("dbghelp.dll")]
    private static partial uint SymSetOptions(uint options);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymInitializeW(nint process, nint searchPath, int invadeProcess);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SymCleanup(nint process);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SymFromAddrW(nint process, ulong address, out ulong displacement, byte* symbol);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SymGetModuleInfoW64(nint process, ulong address, byte* info);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool StackWalk64(uint machine, nint process, nint thread, byte* frame, byte* context, nint readMemory, nint functionTableAccess, nint getModuleBase, nint translateAddress);
}
