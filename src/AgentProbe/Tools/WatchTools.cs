using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// Background monitor on the target. It samples every process and the system, remembers process starts/exits (with
/// exit codes) and windows that stop responding, and answers with trends instead of raw samples:
/// "private bytes 210M -> 480M, +22M/min, steady" is one line where polling would have cost hundreds.
/// </summary>
internal sealed class Watcher : IDisposable
{
    private sealed class Series
    {
        public readonly List<(double t, double v)> Points = [];
        private double _first, _max, _sum;
        private long _count;

        public void Add(double t, double v)
        {
            if (_count == 0)
            {
                _first = v;
                _max = v;
            }

            _max = Math.Max(_max, v);
            _sum += v;
            _count++;
            Points.Add((t, v));

            // A long watch must still describe ALL of itself: instead of forgetting old samples, halve the
            // resolution of the older half. The first sample always stays.
            if (Points.Count > 3600)
            {
                var thinned = new List<(double, double)>(2700);
                for (var i = 0; i < 1800; i += 2) thinned.Add(Points[i]);
                thinned.AddRange(Points.GetRange(1800, Points.Count - 1800));
                Points.Clear();
                Points.AddRange(thinned);
            }
        }

        public double First => _first;

        public double Last => Points.Count > 0 ? Points[^1].v : 0;

        public double Max => _max;

        public double Avg => _count > 0 ? _sum / _count : 0;

        /// <summary>How long this series has been recorded, in seconds.</summary>
        public double Seconds => Points.Count > 1 ? Points[^1].t - Points[0].t : 0;

        /// <summary>The value half way through the recording.</summary>
        public double Middle
        {
            get
            {
                if (Points.Count == 0) return 0;
                var half = Points[0].t + (Seconds / 2);
                foreach (var (t, v) in Points)
                    if (t >= half) return v;
                return Last;
            }
        }

        /// <summary>Least-squares slope per minute and R² (how straight the growth is).</summary>
        public (double perMinute, double r2) Trend()
        {
            if (Points.Count < 4) return (0, 0);
            double n = Points.Count, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            foreach (var (t, v) in Points)
            {
                sx += t;
                sy += v;
                sxx += t * t;
                sxy += t * v;
                syy += v * v;
            }

            var den = (n * sxx) - (sx * sx);
            if (den == 0) return (0, 0);
            var slope = ((n * sxy) - (sx * sy)) / den;
            var varY = (n * syy) - (sy * sy);
            var r2 = varY <= 0 ? 0 : Math.Pow((n * sxy) - (sx * sy), 2) / (den * varY);
            return (slope * 60, r2);
        }
    }

    private sealed class Tracked
    {
        public int Pid;
        public long CreateTime;
        public string Name = "";
        public nint Handle;
        public long LastCpu;
        public ProcessEntry Latest = null!;
        public readonly Series Private = new(), Handles = new(), Threads = new(), Gdi = new(), User = new(), Cpu = new();
        public long FirstPrivate, FirstHandles;
        public bool Focus;

        /// <summary>A watched process, or one that a watched process (or one of its descendants) started.</summary>
        public bool Related;
        public DateTime? Exited;
    }

    private readonly Dictionary<(int, long), Tracked> _procs = [];
    /// <summary>
    /// own: about a watched process, something it started, a crash anywhere, or a window that stopped responding.
    /// The rest is Windows starting and ending its own helpers, which would bury the events that matter.
    /// </summary>
    /// <remarks>noCode: an exit whose exit code could not be read, so nobody can say whether it was a crash.</remarks>
    private readonly List<(DateTime time, string text, bool own, bool noCode)> _events = [];
    private readonly Dictionary<nint, DateTime> _hung = [];
    private readonly Series _cpu = new(), _ram = new(), _commit = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _focus;
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _eventCursor;
    private int _leftOutFrom = int.MaxValue; // the first event of the reports that left unrelated events out
    private int _handles;                    // process handles held open for exit codes
    private int _droppedUnreported, _droppedLeftOut; // events thrown away before any report showed them
    private bool _trimmed;
    private const int MaxEvents = 20000, TrimEvents = 5000, MaxHandles = 2048;
    private int _samples;
    private long _idle, _kernel, _user;
    private double _lastSampleSeconds;

    public Watcher(List<string> focus, TimeSpan interval)
    {
        _focus = focus;
        _interval = interval;
        Win32.GetSystemTimes(out _idle, out _kernel, out _user);
        Sample(first: true);
        new Thread(Loop) { IsBackground = true, Name = "watch" }.Start();
    }

    public string Description => (_focus.Count > 0 ? string.Join(", ", _focus) : "all processes") + $", every {Format.Duration(_interval)}";

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_interval))
        {
            try
            {
                Sample(first: false);
            }
            catch (InvalidOperationException)
            {
                // a failed snapshot is skipped; the next one usually works
            }
        }
    }

    private bool IsFocus(ProcessEntry p)
        => _focus.Any(f => int.TryParse(f, out var pid) ? p.Pid == pid : p.Name.Contains(f.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase));

    private void Sample(bool first)
    {
        var snapshot = ProcessSnapshot.Take();
        var windows = UiTools.EnumerateWindows();
        var t = _clock.Elapsed.TotalSeconds;
        var dt = Math.Max(0.001, t - _lastSampleSeconds);
        Win32.GetSystemTimes(out var idle, out var kernel, out var user);
        var mem = SystemTools.Memory();
        var perf = SystemTools.Performance();

        lock (_gate)
        {
            _samples++;
            var total = (kernel - _kernel) + (user - _user);
            if (!first && total > 0) _cpu.Add(t, 100.0 * (total - (idle - _idle)) / total);
            (_idle, _kernel, _user) = (idle, kernel, user);
            _ram.Add(t, mem.dwMemoryLoad);
            _commit.Add(t, perf.CommitLimit > 0 ? 100.0 * perf.CommitTotal / perf.CommitLimit : 0);

            var seen = new HashSet<(int, long)>();
            foreach (var p in snapshot)
            {
                if (p.Pid is 0 or 4) continue;
                var key = (p.Pid, p.CreateTime);
                seen.Add(key);
                if (!_procs.TryGetValue(key, out var tr))
                {
                    tr = new Tracked { Pid = p.Pid, CreateTime = p.CreateTime, Name = p.Name, FirstPrivate = p.PrivateBytes, FirstHandles = p.Handles, LastCpu = p.CpuTime, Focus = IsFocus(p) };
                    tr.Related = tr.Focus || (!first && StartedByRelated(p));
                    _procs[key] = tr;

                    // Keep a handle to every process Windows lets us open, so its exit code is readable when it ends:
                    // a crash is told from an ordinary exit by that code. Where no handle could be had (protected
                    // system processes, and most others' on a target that is not elevated) the exit is recorded
                    // without a code, and the report says how many such exits there were.
                    if (_handles < MaxHandles)
                    {
                        tr.Handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.SYNCHRONIZE, false, p.Pid);
                        if (tr.Handle != 0) _handles++;
                    }

                    if (!first)
                    {
                        var parent = snapshot.FirstOrDefault(x => x.Pid == p.ParentPid);
                        _events.Add((DateTime.UtcNow, $"start {p.Name}({p.Pid}){(parent != null ? $" by {parent.Name}({parent.Pid})" : "")}", tr.Related, false));
                    }
                }

                tr.Latest = p;
                if (tr.Focus)
                {
                    tr.Private.Add(t, p.PrivateBytes);
                    tr.Handles.Add(t, p.Handles);
                    tr.Threads.Add(t, p.Threads);
                    if (!first) tr.Cpu.Add(t, 100.0 * (p.CpuTime - tr.LastCpu) / (dt * TimeSpan.TicksPerSecond));
                    if (tr.Handle != 0)
                    {
                        var (gdi, usr) = ProcessDetails.GuiObjects(tr.Handle);
                        tr.Gdi.Add(t, gdi);
                        tr.User.Add(t, usr);
                    }
                }

                tr.LastCpu = p.CpuTime;
            }

            if (first)
            {
                // What the watched processes had already started when the watch began: follow the parents until
                // nothing changes (the snapshot is in no particular order).
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var p in snapshot)
                    {
                        if (!_procs.TryGetValue((p.Pid, p.CreateTime), out var tr) || tr.Related || !StartedByRelated(p)) continue;
                        tr.Related = true;
                        changed = true;
                    }
                }
            }

            foreach (var tr in _procs.Values.Where(x => x.Exited == null && !seen.Contains((x.Pid, x.CreateTime))))
            {
                tr.Exited = DateTime.UtcNow;
                var code = "";
                var crashed = false;
                if (tr.Handle != 0)
                {
                    if (Win32.GetExitCodeProcess(tr.Handle, out var exit) && exit != Win32.STILL_ACTIVE)
                    {
                        code = " code " + ProcessDetails.ExitCodeName(exit);
                        crashed = IsCrashCode(exit);
                    }

                    Win32.CloseHandle(tr.Handle);
                    tr.Handle = 0;
                    _handles--;
                }

                var lived = tr.CreateTime > 0 ? " after " + Format.Duration(DateTime.UtcNow - DateTime.FromFileTimeUtc(tr.CreateTime)) : "";
                _events.Add((DateTime.UtcNow, $"exit  {tr.Name}({tr.Pid}){code}{lived}", tr.Related || crashed, code.Length == 0));
            }

            // Windows that stop / resume responding.
            var names = snapshot.ToDictionary(p => p.Pid, p => p.Name);
            var hungNow = windows.Where(w => w.Visible && w.Hung && w.Title.Length > 0).ToDictionary(w => w.Hwnd);
            foreach (var (hwnd, w) in hungNow)
            {
                if (_hung.TryAdd(hwnd, DateTime.UtcNow))
                    _events.Add((DateTime.UtcNow, $"hung  {names.GetValueOrDefault(w.Pid, "?")}({w.Pid}) \"{Format.Clip(w.Title, 50)}\" stopped responding", true, false));
            }

            foreach (var hwnd in _hung.Keys.Where(h => !hungNow.ContainsKey(h)).ToList())
            {
                _events.Add((DateTime.UtcNow, $"alive window 0x{(long)hwnd:X} responds again after {Format.Duration(DateTime.UtcNow - _hung[hwnd])}", true, false));
                _hung.Remove(hwnd);
            }

            if (_events.Count > MaxEvents)
            {
                // Throwing events away must not go unsaid: count the ones no report has shown yet.
                _droppedUnreported += Math.Max(0, TrimEvents - _eventCursor);
                _droppedLeftOut += Math.Max(0, TrimEvents - Math.Min(_leftOutFrom, _eventCursor));
                _events.RemoveRange(0, TrimEvents);
                _eventCursor = Math.Max(0, _eventCursor - TrimEvents); // keep pointing at the same event
                if (_leftOutFrom != int.MaxValue) _leftOutFrom = Math.Max(0, _leftOutFrom - TrimEvents);
                _trimmed = true;
            }

            _lastSampleSeconds = t;
        }
    }

    /// <summary>
    /// Was this process started by a watched process or by something a watched process started? The parent must
    /// be older than the child: Windows reuses process ids, and a new process may carry the pid of a parent that
    /// is long gone.
    /// </summary>
    private bool StartedByRelated(ProcessEntry p)
    {
        foreach (var parent in _procs.Values)
        {
            if (parent.Pid == p.ParentPid && parent.Exited == null && parent.Related && parent.CreateTime <= p.CreateTime) return true;
        }

        return false;
    }

    /// <summary>
    /// An exit code that means the process did not end by itself in the ordinary way: an exception or a fail-fast
    /// (0xC0000005, 0xC0000409, 0xE0434352 ...). Closing a console (0xC000013A) is not a crash.
    /// </summary>
    private static bool IsCrashCode(uint code) => code >= 0x80000000 && code != 0xC000013A;

    private static string Range(Series s, Func<double, string> f) => s.Points.Count == 0 ? "?" : Math.Abs(s.First - s.Last) < 0.5 ? f(s.Last) : $"{f(s.First)}->{f(s.Last)}";

    /// <summary>Text for a growing counter, with a verdict when the growth is straight enough to look like a leak.</summary>
    /// <summary>A leak is only called after this long: start-up growth is a straight line too.</summary>
    private const double MinSecondsForVerdict = 120;

    /// <summary>Text for a growing counter, with a verdict once there is enough evidence for one.</summary>
    private static string Growth(string label, Series s, Func<double, string> f, double minDelta)
    {
        if (s.Points.Count == 0) return "";
        var text = $"{label} {Range(s, f)}";
        var delta = s.Last - s.First;
        if (Math.Abs(delta) < minDelta || s.Points.Count < 8) return text;
        var (perMin, r2) = s.Trend();
        text += $" ({(perMin >= 0 ? "+" : "-")}{f(Math.Abs(perMin))}/min";
        if (delta > 0 && s.Last > s.First * 1.05)
        {
            var secondHalf = s.Last - s.Middle; // growth that is still going on, not just a jump at the beginning
            if (s.Seconds < MinSecondsForVerdict) text += ", growing; too early to call it a leak: keep watching for 2+ minutes";
            else if (r2 >= 0.8 && secondHalf >= delta * 0.25) text += ", steady growth: LIKELY LEAK";
            else if (secondHalf <= delta * 0.1) text += ", grew earlier and has levelled off";
            else text += ", fluctuating";
        }

        return text + ")";
    }

    /// <param name="allEvents">List every event, also the starts and exits of unrelated processes that earlier reports left out.</param>
    public string Report(bool final, bool allEvents)
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            var elapsed = DateTime.UtcNow - _started;
            sb.Append($"watching {Description} for {Format.Duration(elapsed)} ({_samples} samples)\n");
            sb.Append($"system   cpu avg {Format.Pct(_cpu.Avg)}% max {Format.Pct(_cpu.Max)}%   ram {Range(_ram, v => v.ToString("0") + "%")}   commit {Range(_commit, v => v.ToString("0") + "%")}\n");

            foreach (var tr in _procs.Values.Where(p => p.Focus).OrderByDescending(p => p.Private.Last))
            {
                var state = tr.Exited != null ? " [exited]" : "";
                var parts = new List<string>
                {
                    Growth("private", tr.Private, v => Format.Bytes((long)v), 1 << 20),
                    Growth("handles", tr.Handles, v => v.ToString("0"), 20),
                    Growth("gdi", tr.Gdi, v => v.ToString("0"), 20),
                    Growth("user", tr.User, v => v.ToString("0"), 20),
                    Growth("threads", tr.Threads, v => v.ToString("0"), 5),
                    tr.Cpu.Points.Count > 0 ? $"cpu avg {Format.Pct(tr.Cpu.Avg)}% max {Format.Pct(tr.Cpu.Max)}% of a core" : "",
                };
                sb.Append($"{tr.Name}({tr.Pid}){state}  {string.Join("  ", parts.Where(p => p.Length > 0))}\n");
            }

            if (_focus.Count > 0 && !_procs.Values.Any(p => p.Focus)) sb.Append($"(no process matching {string.Join(", ", _focus)} has been seen yet)\n");

            // Everything else: only what moved noticeably.
            var movers = _procs.Values.Where(p => !p.Focus && p.Exited == null && p.Latest != null)
                .Select(p => (p, dPriv: p.Latest.PrivateBytes - p.FirstPrivate, dHnd: p.Latest.Handles - p.FirstHandles))
                .Where(x => Math.Abs(x.dPriv) > 50L << 20 || Math.Abs(x.dHnd) > 1000)
                .OrderByDescending(x => Math.Abs(x.dPriv)).Take(6).ToList();
            if (movers.Count > 0)
                sb.Append("others   " + string.Join("  ", movers.Select(m => $"{m.p.Name}({m.p.Pid}) private {Format.SignedBytes(m.dPriv)}{(Math.Abs(m.dHnd) > 1000 ? $" handles {m.dHnd:+0;-0}" : "")}")) + "\n");

            // Events. With named processes, only what concerns them is listed: the processes themselves, what they
            // started, any crash, any window that stopped responding. Windows starts and ends dozens of its own
            // helpers per hour, and a report that lists those buries the one line that matters.
            var from = allEvents ? Math.Min(_leftOutFrom, _eventCursor) : _eventCursor;
            var fresh = _events.Skip(from).ToList();
            var listed = allEvents || _focus.Count == 0 ? fresh : fresh.Where(e => e.own).ToList();
            var leftOut = fresh.Count - listed.Count;
            var blind = fresh.Count(e => e.noCode && !(allEvents || _focus.Count == 0 || e.own)); // left out, and nobody knows how they ended
            var dropped = allEvents ? _droppedLeftOut : _droppedUnreported;
            _leftOutFrom = allEvents ? int.MaxValue : leftOut > 0 ? Math.Min(_leftOutFrom, _eventCursor) : _leftOutFrom;
            _eventCursor = _events.Count;
            _droppedUnreported = 0;
            if (_leftOutFrom == int.MaxValue) _droppedLeftOut = 0;
            var since = !allEvents ? "since the last report"
                : from == 0 && !_trimmed ? "since the watch started"
                : from < _events.Count ? $"since {_events[from].time.ToLocalTime():HH:mm:ss}"
                : "since the last report";
            var note = leftOut == 0 ? ""
                : $"{leftOut} start/exit event{(leftOut == 1 ? "" : "s")} of unrelated processes left out: " +
                  (final ? "`watch stop --all-events` would have listed them" : "`watch --all-events` lists them") +
                  (blind > 0 ? $"; {blind} of those exits had no readable exit code (processes this target may not open), so a crash among them would not show" : "");
            if (listed.Count > 0)
            {
                sb.Append($"{(allEvents ? "all events" : "events")} {since} ({listed.Count}{(note.Length > 0 ? "; " + note : "")}):\n");
                var most = allEvents ? 1000 : 60; // asked for all of them: only a runaway list is cut
                if (listed.Count > most) sb.Append($"  … {listed.Count - most} earlier events not shown\n");
                foreach (var (time, text, _, _) in listed.TakeLast(most)) sb.Append($"  {time.ToLocalTime():HH:mm:ss} {text}\n");
            }
            else if (leftOut > 0)
            {
                sb.Append($"no events for {string.Join(", ", _focus)} or anything they started, and no crash or hang was seen {since} ({note})\n");
            }
            else
            {
                sb.Append($"no process start/exit or hang events {since}\n");
            }

            if (dropped > 0)
                sb.Append($"({dropped:N0} older events were thrown away before any report showed them: more than {MaxEvents:N0} piled up. Ask for reports more often.)\n");

            if (final) sb.Append("watch stopped\n");
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            foreach (var tr in _procs.Values.Where(t => t.Handle != 0))
            {
                Win32.CloseHandle(tr.Handle);
                tr.Handle = 0; // a sample still in flight must not use or close it again
            }
        }
    }
}

internal static class WatchTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("watch", "Monitor", "Monitor in the background; report trends, leaks, crashes and hangs on demand",
            """
            watch start [process names or pids ...] [--interval 2s]
            watch [--all-events]     report so far: trends for watched processes + events since the previous report
            watch stop [--all-events]  final report and stop
              Named processes get full tracking: private bytes, handles, GDI/USER objects, threads, CPU, each with
              start->end and growth per minute. Steady growth that continues for 2+ minutes is flagged LIKELY LEAK;
              before that it says "too early to call it a leak", because start-up growth looks the same.
              Events: starts and exits (exit codes decoded, e.g. 0xC0000005 access violation) and windows that
              stop responding. With named processes the report lists the events of those processes and of what
              they started, plus any other process that ended with a crash code (an exception or fail-fast exit
              code) and any window that stopped responding; the starts and exits of unrelated processes are
              counted, not listed (--all-events lists them too, including those left out of earlier reports).
              An exit code can only be read for a process the target may open: where it could not (system
              processes, most of them when the target is not elevated) the report says how many such exits it
              left out. A child whose parent lived shorter than the interval is not recognised as started by a
              watched process. Without names every event is listed.
              Start it BEFORE the test, run the test, then ask for the report: one cheap call instead of polling
              `ps` in a loop.
            """, WatchAsync) { Quiet = true },
    ];

    private static Task<int> WatchAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all-events");
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "report";
        var watcher = ctx.Runtime.Watcher;
        switch (sub)
        {
            case "start":
                watcher?.Dispose();
                var interval = a.GetDuration("interval") ?? TimeSpan.FromSeconds(2);
                if (interval < TimeSpan.FromMilliseconds(250)) interval = TimeSpan.FromMilliseconds(250);
                watcher = new Watcher(a.Positionals.Skip(1).ToList(), interval);
                ctx.Runtime.Watcher = watcher;
                ctx.Runtime.Audit.Write("watch started: " + watcher.Description);
                ctx.Out.WriteLine($"watching {watcher.Description}. Run the scenario, then `agentprobe watch` for the report.");
                return Task.FromResult(0);
            case "stop":
                if (watcher == null) return Task.FromResult(ctx.Fail("watch: not running."));
                ctx.Out.Write(watcher.Report(final: true, a.Flag("all-events")));
                watcher.Dispose();
                ctx.Runtime.Watcher = null;
                return Task.FromResult(0);
            case "report" or "status":
                if (watcher == null) return Task.FromResult(ctx.Fail("watch: not running. Start it with: watch start [process ...]"));
                ctx.Out.Write(watcher.Report(final: false, a.Flag("all-events")));
                return Task.FromResult(0);
            default:
                return Task.FromResult(ctx.Fail($"watch: unknown subcommand '{sub}'. Use start, stop, or nothing for a report."));
        }
    }
}
