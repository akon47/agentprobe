using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// The part of Process Monitor that solves most "works everywhere except that PC" problems: which file and registry
/// accesses FAIL. A real-time ETW session on the Kernel-File and Kernel-Registry providers is filtered and counted
/// on the target, so the agent gets "ACCESS DENIED x12 C:\ProgramData\MyApp\settings.json" instead of a trace file
/// with a million rows. Needs administrator rights (ETW kernel providers).
/// </summary>
internal sealed unsafe partial class AccessTracer : IDisposable
{
    private const string SessionName = "agentprobe-access-trace";
    private static readonly Guid FileProvider = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    private static readonly Guid RegistryProvider = new("70EB4F03-C1DE-4F73-A051-33D13D5413BD");
    private static readonly Guid ProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private static AccessTracer? _current; // the ETW callback is a static function

    private readonly ConcurrentDictionary<(string process, string kind, string path, uint status), int> _failures = new();
    private readonly ConcurrentDictionary<ulong, (int pid, string path)> _pendingCreates = new();
    private readonly ConcurrentDictionary<int, (string name, bool watched, long at)> _processes = new();
    private readonly List<string> _filter;
    private readonly Dictionary<string, string> _devices = DeviceMap();
    private readonly string _userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
    private ulong _session;
    private ulong _trace = ulong.MaxValue;
    private Thread? _pump;

    /// <summary>
    /// pid -> image name, from a snapshot when the trace starts and from process-start events after that.
    /// ETW delivers events up to a second late, so a short-lived process (exactly the "crashes at startup" case) is
    /// already gone when its events arrive and cannot be asked for its name; the start event in the same trace can.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _names = new();

    public AccessTracer(List<string> filter, bool files, bool registry)
    {
        _filter = filter;
        Files = files;
        Registry = registry;
    }

    public bool Files { get; }

    public bool Registry { get; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public long Events;

    /// <summary>Internal counters shown by `trace --debug`: which events arrive and where they are filtered out.</summary>
    public readonly ConcurrentDictionary<string, long> Stats = new();

    private void Stat(string key) => Stats.AddOrUpdate(key, 1, (_, n) => n + 1);

    private static uint _lastTdhError;

    public long Lost;

    public string Description => (_filter.Count > 0 ? string.Join(", ", _filter) : "all processes") + (Files && Registry ? "" : Files ? " (files only)" : " (registry only)");

    /// <summary>Starts the session. Returns an error message, or null on success.</summary>
    public string? Start()
    {
        var size = 120 + ((SessionName.Length + 1) * 2);
        var props = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            Fill(props, size);
            var rc = StartTraceW(out _session, SessionName, props);
            if (rc == 183) // a session from an earlier run that never stopped: take it down and start clean
            {
                Fill(props, size);
                ControlTraceW(0, SessionName, props, 1);
                Fill(props, size);
                rc = StartTraceW(out _session, SessionName, props);
            }

            if (rc == 5) return "needs agentprobe elevated on the target (ETW kernel providers are admin-only). Ask the person there to start it with Run as administrator.";
            if (rc != 0) return $"could not start the trace session (error {rc}).";

            // File: FILENAME | OP_END | CREATE. Registry: everything (its volume is modest).
            var file = FileProvider;
            var reg = RegistryProvider;
            foreach (var p in ProcessSnapshot.Take()) _names[p.Pid] = p.Name;
            var proc = ProcessProvider;
            EnableTraceEx2(_session, &proc, 1, 4, 0x10, 0, 0, 0); // WINEVENT_KEYWORD_PROCESS: start/stop events
            if (Files) EnableTraceEx2(_session, &file, 1, 4, 0x10 | 0x40 | 0x80, 0, 0, 0);
            if (Registry) EnableTraceEx2(_session, &reg, 1, 4, 0, 0, 0, 0);

            _current = this;
            var logfile = (byte*)NativeMemory.AllocZeroed(448);
            fixed (char* name = SessionName)
            {
                *(char**)(logfile + 8) = name;                 // LoggerName
                *(uint*)(logfile + 28) = 0x00000100 | 0x10000000; // PROCESS_TRACE_MODE_REAL_TIME | EVENT_RECORD
                *(delegate* unmanaged[Stdcall]<byte*, void>*)(logfile + 424) = &OnEvent;
                _trace = OpenTraceW(logfile);
            }

            NativeMemory.Free(logfile);
            if (_trace == ulong.MaxValue || _trace == 0xFFFFFFFF)
            {
                Stop();
                return $"could not read the trace session (error {Marshal.GetLastPInvokeError()}).";
            }

            _pump = new Thread(() =>
            {
                var handle = _trace;
                ProcessTrace(&handle, 1, 0, 0); // blocks until the session stops
            })
            {
                IsBackground = true,
                Name = "etw",
            };
            _pump.Start();
            return null;
        }
        finally
        {
            NativeMemory.Free(props);
        }
    }

    private static void Fill(byte* props, int size)
    {
        new Span<byte>(props, size).Clear();
        *(uint*)props = (uint)size;        // Wnode.BufferSize
        *(uint*)(props + 40) = 1;           // Wnode.ClientContext: QPC timestamps
        *(uint*)(props + 44) = 0x00020000;  // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        *(uint*)(props + 48) = 256;         // BufferSize (KB)
        *(uint*)(props + 52) = 16;          // MinimumBuffers
        *(uint*)(props + 56) = 128;         // MaximumBuffers
        *(uint*)(props + 64) = 0x00000100;  // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(props + 116) = 120;        // LoggerNameOffset
    }

    private void Stop()
    {
        var size = 120 + ((SessionName.Length + 1) * 2);
        var props = (byte*)NativeMemory.AllocZeroed((nuint)size);
        *(uint*)props = (uint)size;
        *(uint*)(props + 116) = 120;
        ControlTraceW(_session, null, props, 1); // EVENT_TRACE_CONTROL_STOP
        Lost = *(uint*)(props + 88);
        NativeMemory.Free(props);
        if (_trace != ulong.MaxValue) CloseTrace(_trace);
        _trace = ulong.MaxValue;
    }

    public void Dispose()
    {
        if (_session == 0) return;
        Stop();
        _session = 0;
        _pump?.Join(3000);
        if (_current == this) _current = null;
    }

    // ---------------------------------------------------------------- event handling (ETW thread)

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(byte* record)
    {
        try
        {
            _current?.Handle(record);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // never let an exception cross into ETW; count it so `trace --debug` shows that something is wrong
            _current?.Stat("exception " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void Handle(byte* record)
    {
        Events++;
        var pid = *(int*)(record + 12);
        var id = *(ushort*)(record + 40);
        var provider = new ReadOnlySpan<byte>(record + 24, 16);
        var isFile = provider.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Guid>(in FileProvider)));
        if (!isFile && provider.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Guid>(in ProcessProvider))))
        {
            if (*(ushort*)(record + 40) == 1) // ProcessStart
            {
                var started = (int)Number(record, "ProcessID");
                var image = Text(record, "ImageName");
                if (started != 0 && image.Length > 0)
                {
                    _names[started] = Path.GetFileName(image);
                    _processes.TryRemove(started, out _); // the pid may have belonged to another program before
                }
            }

            return;
        }

        var userData = *(byte**)(record + 96);
        var userLength = *(ushort*)(record + 86);
        Stat((isFile ? "file#" : "other#") + id);

        if (isFile)
        {
            if (id == 12) // Create: remember it until its OperationEnd tells us how it went
            {
                if (!Watched(pid)) return;
                Stat("file create from watched pid");
                var path = Text(record, "FileName");
                if (path.Length == 0) Stat("file create without name, tdh error " + _lastTdhError);
                if (path.Length == 0 || userLength < 8) return;
                if (_pendingCreates.Count > 20000) _pendingCreates.Clear();
                _pendingCreates[*(ulong*)userData] = (pid, path);
            }
            else if (id == 24 && userLength >= 8 && _pendingCreates.TryRemove(*(ulong*)userData, out var create)) // OperationEnd; Irp is the first field
            {
                var status = Number(record, "Status");
                Stat("file end matched, status 0x" + status.ToString("X8"));
                if (status >= 0xC0000000) Count(create.pid, "file", FilePath(create.path), status);
            }

            return;
        }

        if (!Watched(pid)) return;
        var kind = id switch
        {
            1 => "reg create",
            2 => "reg open",
            3 => "reg delete",
            5 => "reg set",
            6 => "reg delete value",
            7 => "reg query",
            _ => null,
        };
        if (kind == null) return;
        var regStatus = Number(record, "Status");
        Stat("reg " + kind + " from watched pid, status 0x" + regStatus.ToString("X8"));
        if (regStatus < 0xC0000000) return;
        string key;
        if (id is 1 or 2)
        {
            var baseName = Text(record, "BaseName");
            var relative = Text(record, "RelativeName");
            key = baseName.Length > 0 && !relative.StartsWith('\\') ? baseName.TrimEnd('\\') + "\\" + relative : relative;
        }
        else
        {
            key = Text(record, "KeyName");
            var value = Text(record, "ValueName");
            if (value.Length > 0) key += " : " + value;
        }

        if (key.Length > 0) Count(pid, kind, RegistryPath(key), regStatus);
    }

    private void Count(int pid, string kind, string path, uint status)
    {
        if (_failures.Count > 50000) return; // enough to report; do not grow without bound
        var process = _processes.TryGetValue(pid, out var p) ? p.name : pid.ToString();
        _failures.AddOrUpdate((process, kind, path, status), 1, (_, n) => n + 1);
    }

    /// <summary>
    /// Is this pid one of the processes asked for? The name comes from the trace's own process-start events (exact,
    /// and available even after the process has exited); only for a pid the trace knows nothing about is Windows asked.
    /// </summary>
    private bool Watched(int pid)
    {
        var now = Environment.TickCount64;
        if (_processes.TryGetValue(pid, out var known) && now - known.at < 3000) return known.watched;
        var resolved = _names.TryGetValue(pid, out var name);
        if (!resolved)
        {
            name = ImageName(pid);
            if (name != null)
            {
                _names[pid] = name;
                resolved = true;
            }
        }

        name ??= pid.ToString();
        var watched = pid != Environment.ProcessId
                      && (_filter.Count == 0 || _filter.Any(f => int.TryParse(f, out var n) ? pid == n : name.Contains(f.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase)));

        // A resolved name stays valid until a process-start event replaces it; an unresolved one is retried soon.
        _processes[pid] = (name, watched, resolved ? long.MaxValue / 2 : now);
        return watched;
    }

    private static string? ImageName(int pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var path = Win32.GetProcessImagePath(h);
            return path == null ? null : Path.GetFileName(path);
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    /// <summary>Events the session had to drop because they arrived faster than they could be read.</summary>
    private long QueryLost()
    {
        if (_session == 0) return Lost;
        var size = 120 + ((SessionName.Length + 1) * 2);
        var props = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            *(uint*)props = (uint)size;
            *(uint*)(props + 116) = 120;
            if (ControlTraceW(_session, null, props, 0) == 0) Lost = *(uint*)(props + 88) + (long)*(uint*)(props + 100); // EventsLost + RealTimeBuffersLost
            return Lost;
        }
        finally
        {
            NativeMemory.Free(props);
        }
    }

    private static string Text(byte* record, string property)
    {
        var buffer = stackalloc byte[2048];
        return Property(record, property, buffer, 2048, out var size) && size >= 2 ? new string((char*)buffer).TrimEnd('\0') : "";
    }

    private static uint Number(byte* record, string property)
    {
        var buffer = stackalloc byte[16];
        return Property(record, property, buffer, 16, out var size) && size >= 4 ? *(uint*)buffer : 0;
    }

    private static bool Property(byte* record, string property, byte* buffer, uint capacity, out uint size)
    {
        size = 0;
        fixed (char* name = property)
        {
            var descriptor = stackalloc ulong[2]; // PROPERTY_DATA_DESCRIPTOR { name pointer, array index + reserved }
            descriptor[0] = (ulong)name;
            descriptor[1] = 0xFFFFFFFF; // ArrayIndex = ULONG_MAX: not an array element
            _lastTdhError = TdhGetPropertySize(record, 0, 0, 1, descriptor, out size);
            if (_lastTdhError != 0 || size == 0 || size > capacity - 2) return false;
            new Span<byte>(buffer, (int)capacity).Clear();
            return TdhGetProperty(record, 0, 0, 1, descriptor, size, buffer) == 0;
        }
    }

    // ---------------------------------------------------------------- names

    /// <summary>\Device\HarddiskVolume3 -> C: for every drive letter.</summary>
    private static Dictionary<string, string> DeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var target = stackalloc char[512];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = letter + ":";
            if (QueryDosDeviceW(drive, target, 512) > 0) map[new string(target)] = drive;
        }

        return map;
    }

    private string FilePath(string devicePath)
    {
        foreach (var (device, drive) in _devices)
        {
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + devicePath[device.Length..];
        }

        return devicePath;
    }

    private string RegistryPath(string key)
    {
        if (key.StartsWith(@"\REGISTRY\MACHINE", StringComparison.OrdinalIgnoreCase)) return "HKLM" + key[17..];
        if (_userSid.Length > 0 && key.StartsWith(@"\REGISTRY\USER\" + _userSid + "_Classes", StringComparison.OrdinalIgnoreCase)) return @"HKCU\Software\Classes" + key[(15 + _userSid.Length + 8)..];
        if (_userSid.Length > 0 && key.StartsWith(@"\REGISTRY\USER\" + _userSid, StringComparison.OrdinalIgnoreCase)) return "HKCU" + key[(15 + _userSid.Length)..];
        if (key.StartsWith(@"\REGISTRY\USER", StringComparison.OrdinalIgnoreCase)) return "HKU" + key[14..];
        return key;
    }

    public static string StatusName(uint status) => status switch
    {
        0xC0000034 => "NAME NOT FOUND",
        0xC000003A => "PATH NOT FOUND",
        0xC000000F => "NO SUCH FILE",
        0xC0000022 => "ACCESS DENIED",
        0xC0000043 => "SHARING VIOLATION",
        0xC0000035 => "NAME COLLISION",
        0xC00000BA => "IS A DIRECTORY",
        0xC0000103 => "NOT A DIRECTORY",
        0xC0000056 => "DELETE PENDING",
        0xC0000033 => "NAME INVALID",
        0xC0000061 => "PRIVILEGE NOT HELD",
        0xC0000054 => "LOCK CONFLICT",
        0xC000007F => "DISK FULL",
        0xC00000A2 => "WRITE PROTECTED",
        0xC0000121 => "CANNOT DELETE",
        0xC0000279 => "REPARSE / STOPPED ON SYMLINK",
        0xC000017C => "KEY DELETED",
        0xC0000023 => "BUFFER TOO SMALL",
        _ => $"0x{status:X8}",
    };

    public string Report(string? grep, string? statusFilter, int max, TextWriter err)
    {
        System.Text.RegularExpressions.Regex? re = null;
        if (grep != null)
        {
            try
            {
                re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                err.WriteLine("bad --grep pattern: " + ex.Message);
            }
        }

        var all = _failures.Select(kv => (kv.Key.process, kv.Key.kind, kv.Key.path, kv.Key.status, count: kv.Value)).ToList();
        var sb = new StringBuilder();
        sb.Append($"tracing {Description} for {Format.Duration(DateTime.UtcNow - StartedUtc)}: {Events} events seen, {all.Sum(f => f.count)} failed accesses in {all.Count} distinct places\n");
        var lost = QueryLost();
        var lostNote = lost > 0 ? $"[{lost} events/buffers were dropped under load, so accesses may be missing from this report]\n" : "";
        if (all.Count == 0)
        {
            sb.Append(Events == 0 ? "no events yet: is the process running, and did it do anything since `trace start`?\n" : "no failed file or registry access by the watched processes was seen\n");
            sb.Append(lostNote);
            return sb.ToString();
        }

        sb.Append("by result: " + string.Join(", ", all.GroupBy(f => f.status).OrderByDescending(g => g.Sum(f => f.count)).Select(g => $"{StatusName(g.Key)} x{g.Sum(f => f.count)}")) + "\n");

        IEnumerable<(string process, string kind, string path, uint status, int count)> rows = all;
        if (statusFilter != null) rows = rows.Where(f => StatusName(f.status).Contains(statusFilter.Replace("notfound", "NOT FOUND").Replace("denied", "DENIED"), StringComparison.OrdinalIgnoreCase));
        if (re != null) rows = rows.Where(f => re.IsMatch(f.path) || re.IsMatch(f.process));

        // "Not found" on a probe is usually harmless and there are hundreds of them; real blockers come first.
        static int Rank(uint s) => s is 0xC0000022 or 0xC0000043 or 0xC0000061 or 0xC000007F or 0xC00000A2 or 0xC0000054 ? 0 : s is 0xC0000034 or 0xC000003A or 0xC000000F ? 2 : 1;
        var ordered = rows.OrderBy(f => Rank(f.status)).ThenBy(f => f.kind.StartsWith("reg", StringComparison.Ordinal)).ThenByDescending(f => f.count).ToList();
        var t = new Table("RESULT", ">N", "PROCESS", "WHAT", "PATH");
        foreach (var f in ordered.Take(max)) t.Add(StatusName(f.status), f.count, f.process, f.kind, Format.Clip(f.path, 150));
        sb.Append(t.ToString());
        if (ordered.Count > max) sb.Append($"… {ordered.Count - max} more places (--grep <text>, --status denied|notfound, or --max N)\n");
        sb.Append(lostNote);
        return sb.ToString();
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTraceW(out ulong session, string name, byte* properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTraceW(ulong session, string? name, byte* properties, uint control);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong session, Guid* provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, nint parameters);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial ulong OpenTraceW(byte* logfile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong handle);

    [LibraryImport("tdh.dll")]
    private static partial uint TdhGetPropertySize(byte* record, uint contextCount, nint context, uint count, ulong* descriptor, out uint size);

    [LibraryImport("tdh.dll")]
    private static partial uint TdhGetProperty(byte* record, uint contextCount, nint context, uint count, ulong* descriptor, uint size, byte* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDeviceW(string device, char* target, uint max);
}

internal static class AccessTraceTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("trace", "Capture", "Which file and registry accesses FAIL for a process (Process Monitor, summarized)",
            """
            trace start [process names or pids ...] [--files] [--registry]
            trace [--grep TEXT] [--status denied|notfound] [--max 40]     report so far
            trace stop                                                     final report and stop
              Start it, reproduce the problem, then read the report: every failed open/create/query grouped by result,
              process and path, with counts. ACCESS DENIED and SHARING VIOLATION are listed first; NAME NOT FOUND is
              common and often harmless (programs probe for optional files), so look there for the one path that
              matters: a missing config file, DLL, or registry value.
              Needs agentprobe elevated on the target. The trace session is stopped when the agentprobe session ends.
              For timing/CPU questions use `wpr` instead; this command only reports failures.
            """, TraceAsync),
    ];

    private static Task<int> TraceAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "files", "registry", "debug");
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "report";
        var tracer = ctx.Runtime.Tracer;
        switch (sub)
        {
            case "start":
            {
                tracer?.Dispose();
                var both = !a.Flag("files") && !a.Flag("registry");
                tracer = new AccessTracer(a.Positionals.Skip(1).ToList(), both || a.Flag("files"), both || a.Flag("registry"));
                var error = tracer.Start();
                if (error != null)
                {
                    ctx.Runtime.Tracer = null;
                    return Task.FromResult(ctx.Fail("trace: " + error));
                }

                ctx.Runtime.Tracer = tracer;
                ctx.Runtime.Audit.Write("access trace started: " + tracer.Description);
                ctx.Out.WriteLine($"tracing failed file and registry accesses of {tracer.Description}. Reproduce the problem, then run `agentprobe trace`.");
                return Task.FromResult(0);
            }

            case "stop":
                if (tracer == null) return Task.FromResult(ctx.Fail("trace: not running."));
                tracer.Dispose();
                ctx.Out.Write(tracer.Report(a.Get("grep"), a.Get("status"), a.GetInt("max", 40), ctx.Err));
                ctx.Out.WriteLine("trace stopped");
                ctx.Runtime.Tracer = null;
                return Task.FromResult(0);

            case "report" or "status":
                if (tracer == null) return Task.FromResult(ctx.Fail("trace: not running. Start it with: trace start <process>"));
                if (a.Flag("debug"))
                {
                    foreach (var kv in tracer.Stats.OrderByDescending(kv => kv.Value).Take(40)) ctx.Out.WriteLine($"{kv.Value,8}  {kv.Key}");
                    return Task.FromResult(0);
                }

                ctx.Out.Write(tracer.Report(a.Get("grep"), a.Get("status"), a.GetInt("max", 40), ctx.Err));
                return Task.FromResult(0);

            default:
                return Task.FromResult(ctx.Fail($"trace: unknown subcommand '{sub}'. Use start, stop, or nothing for a report."));
        }
    }
}
