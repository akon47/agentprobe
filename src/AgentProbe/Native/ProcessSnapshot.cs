using System.Runtime.InteropServices;

namespace AgentProbe.Native;

internal sealed class ThreadEntry
{
    public int Tid { get; init; }
    public long KernelTime { get; init; }
    public long UserTime { get; init; }
    public long CreateTime { get; init; }
    public long StartAddress { get; init; }
    public int Priority { get; init; }
    public uint ContextSwitches { get; init; }
    public int State { get; init; }
    public int WaitReason { get; init; }
}

internal sealed class ProcessEntry
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string Name { get; init; } = "";
    public int Threads { get; init; }
    public int Handles { get; init; }
    public int SessionId { get; init; }
    public long CreateTime { get; init; }
    public long KernelTime { get; init; }
    public long UserTime { get; init; }
    public long WorkingSet { get; init; }
    public long PeakWorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public long PeakPrivateBytes { get; init; }
    public long VirtualSize { get; init; }
    public long PagedPool { get; init; }
    public long NonPagedPool { get; init; }
    public long ReadBytes { get; init; }
    public long WriteBytes { get; init; }
    public long OtherBytes { get; init; }
    public uint PageFaults { get; init; }
    public uint HardFaults { get; init; }
    public List<ThreadEntry>? ThreadList { get; init; }

    public long CpuTime => KernelTime + UserTime;

    public DateTime? StartTimeUtc => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime) : null;
}

/// <summary>
/// One NtQuerySystemInformation(SystemProcessInformation) call returns every process with memory, handle,
/// thread and CPU counters without opening a single process handle, so it also sees protected processes.
/// </summary>
internal static unsafe class ProcessSnapshot
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static List<ProcessEntry> Take(bool includeThreads = false)
    {
        var size = 1u << 20;
        while (true)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                var status = Win32.NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (64u << 10));
                    continue;
                }

                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
                return Parse(buffer, includeThreads);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
    }

    private static List<ProcessEntry> Parse(byte* p, bool includeThreads)
    {
        var list = new List<ProcessEntry>(400);
        while (true)
        {
            var next = *(uint*)p;
            var threads = (int)*(uint*)(p + 4);
            var nameLen = *(ushort*)(p + 56);
            var namePtr = *(char**)(p + 64);
            var pid = (int)*(long*)(p + 80);
            var name = namePtr != null ? new string(namePtr, 0, nameLen / 2) : (pid == 0 ? "Idle" : "?");

            List<ThreadEntry>? threadList = null;
            if (includeThreads)
            {
                threadList = new List<ThreadEntry>(threads);
                var t = p + 256;
                for (var i = 0; i < threads; i++, t += 80)
                {
                    threadList.Add(new ThreadEntry
                    {
                        KernelTime = *(long*)t,
                        UserTime = *(long*)(t + 8),
                        CreateTime = *(long*)(t + 16),
                        StartAddress = *(long*)(t + 32),
                        Tid = (int)*(long*)(t + 48),
                        Priority = *(int*)(t + 56),
                        ContextSwitches = *(uint*)(t + 64),
                        State = *(int*)(t + 68),
                        WaitReason = *(int*)(t + 72),
                    });
                }
            }

            list.Add(new ProcessEntry
            {
                Pid = pid,
                Name = name,
                Threads = threads,
                HardFaults = *(uint*)(p + 16),
                CreateTime = *(long*)(p + 32),
                UserTime = *(long*)(p + 40),
                KernelTime = *(long*)(p + 48),
                ParentPid = (int)*(long*)(p + 88),
                Handles = (int)*(uint*)(p + 96),
                SessionId = (int)*(uint*)(p + 100),
                VirtualSize = *(long*)(p + 120),
                PageFaults = *(uint*)(p + 128),
                PeakWorkingSet = *(long*)(p + 136),
                WorkingSet = *(long*)(p + 144),
                PagedPool = *(long*)(p + 160),
                NonPagedPool = *(long*)(p + 176),
                PrivateBytes = *(long*)(p + 184),
                PeakPrivateBytes = *(long*)(p + 192),
                ReadBytes = *(long*)(p + 232),
                WriteBytes = *(long*)(p + 240),
                OtherBytes = *(long*)(p + 248),
                ThreadList = threadList,
            });

            if (next == 0) break;
            p += next;
        }

        return list;
    }

    public static string ThreadStateName(int state) => state switch
    {
        0 => "init",
        1 => "ready",
        2 => "running",
        3 => "standby",
        4 => "terminated",
        5 => "wait",
        6 => "transition",
        7 => "deferred",
        _ => state.ToString(),
    };

    public static string WaitReasonName(int reason) => reason >= 0 && reason < WaitReasons.Length ? WaitReasons[reason] : reason.ToString();

    // KWAIT_REASON in declaration order (Windows 10/11); the index is the value the kernel reports.
    private static readonly string[] WaitReasons =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest",
        "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended", "WrUserRequest",
        "WrEventPair", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous",
        "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack", "WrKernel",
        "WrResource", "WrPushLock", "WrMutex", "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution",
        "WrFastMutex", "WrGuardedMutex", "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault",
        "WrIoRing", "WrMdlCache", "WrRcu",
    ];
}

/// <summary>Per-process details that need a process handle (path, command line, user, integrity, GUI objects).</summary>
internal static unsafe class ProcessDetails
{
    public static nint Open(int pid, bool vmRead = false)
    {
        var access = Win32.PROCESS_QUERY_LIMITED_INFORMATION | (vmRead ? Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ : 0);
        return Win32.OpenProcess(access, false, pid);
    }

    public static string? CommandLine(nint h)
    {
        const int ProcessCommandLineInformation = 60;
        Win32.NtQueryInformationProcess(h, ProcessCommandLineInformation, null, 0, out var needed);
        if (needed == 0) return null;
        var buf = (byte*)NativeMemory.Alloc(needed);
        try
        {
            if (Win32.NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, needed, out _) < 0) return null;
            var len = *(ushort*)buf;
            var str = *(char**)(buf + 8);
            return str == null ? null : new string(str, 0, len / 2);
        }
        finally
        {
            NativeMemory.Free(buf);
        }
    }

    public static (string? user, string? integrity, bool? elevated) TokenInfo(nint h)
    {
        if (!Win32.OpenProcessToken(h, 0x0008, out var token)) return (null, null, null); // TOKEN_QUERY
        try
        {
            string? user = null, integrity = null;
            bool? elevated = null;
            var buf = stackalloc byte[512];
            if (Win32.GetTokenInformation(token, 1, buf, 512, out _)) user = SidToName(*(nint*)buf); // TokenUser
            if (Win32.GetTokenInformation(token, 25, buf, 512, out _)) // TokenIntegrityLevel
            {
                var sid = (byte*)*(nint*)buf;
                var count = sid[1];
                var rid = *(uint*)(sid + 8 + (4 * (count - 1)));
                integrity = rid switch
                {
                    < 0x1000 => "untrusted",
                    < 0x2000 => "low",
                    < 0x3000 => "medium",
                    < 0x4000 => "high",
                    _ => "system",
                };
                elevated = rid >= 0x3000; // high or system integrity = running with administrator rights
            }

            return (user, integrity, elevated);
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }

    private static string? SidToName(nint sid)
    {
        var name = stackalloc char[256];
        var domain = stackalloc char[256];
        uint nl = 256, dl = 256;
        if (!Win32.LookupAccountSid(null, sid, name, ref nl, domain, ref dl, out _)) return null;
        var n = new string(name, 0, (int)nl);
        var d = new string(domain, 0, (int)dl);
        return d.Length > 0 && !d.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? $"{d}\\{n}" : n;
    }

    public static string? Architecture(nint h)
    {
        if (!Win32.IsWow64Process2(h, out var pm, out var nm)) return null;
        var m = pm == 0 ? nm : pm;
        return m switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0xAA64 => "arm64",
            0x01c4 => "arm",
            _ => $"0x{m:X}",
        };
    }

    public static (uint gdi, uint user) GuiObjects(nint h) => (Win32.GetGuiResources(h, 0), Win32.GetGuiResources(h, 1));

    public static List<(nint baseAddr, uint size, string path)> Modules(nint h)
    {
        var result = new List<(nint, uint, string)>();
        var mods = new nint[1024];
        fixed (nint* pm = mods)
        {
            if (!Win32.EnumProcessModulesEx(h, pm, (uint)(mods.Length * sizeof(nint)), out var needed, 3)) return result; // LIST_MODULES_ALL
            var count = Math.Min((int)(needed / sizeof(nint)), mods.Length);
            var name = stackalloc char[1024];
            for (var i = 0; i < count; i++)
            {
                var len = Win32.GetModuleFileNameEx(h, pm[i], name, 1024);
                Win32.GetModuleInformation(h, pm[i], out var info, (uint)sizeof(MODULEINFO));
                result.Add((pm[i], info.SizeOfImage, len > 0 ? new string(name, 0, (int)len) : "?"));
            }
        }

        return result;
    }

    public static string ExitCodeName(uint code) => code switch
    {
        0 => "0",
        0xC0000005 => "0xC0000005 access violation",
        0xC00000FD => "0xC00000FD stack overflow",
        0xC0000409 => "0xC0000409 stack buffer overrun / fail-fast",
        0xC0000374 => "0xC0000374 heap corruption",
        0xC0000135 => "0xC0000135 DLL not found",
        0xC0000139 => "0xC0000139 entry point not found",
        0xC000007B => "0xC000007B bad image (x86/x64 mismatch?)",
        0xC0000142 => "0xC0000142 DLL init failed",
        0xC0000094 => "0xC0000094 integer divide by zero",
        0xC000001D => "0xC000001D illegal instruction",
        0xC0000096 => "0xC0000096 privileged instruction",
        0xC0000006 => "0xC0000006 in-page error",
        0xC0000017 => "0xC0000017 no memory",
        0xE0434352 => "0xE0434352 .NET unhandled exception",
        0x80000003 => "0x80000003 breakpoint",
        0xC000013A => "0xC000013A Ctrl+C",
        0x40010004 => "0x40010004 killed (debugger/terminate)",
        0xCFFFFFFF => "0xCFFFFFFF hung app terminated",
        _ when code > 0x7FFFFFFF => $"0x{code:X8}",
        _ => code.ToString(),
    };
}
