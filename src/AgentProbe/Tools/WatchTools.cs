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
        public DateTime? Exited;
    }

    private readonly Dictionary<(int, long), Tracked> _procs = [];
    private readonly List<(DateTime time, string text)> _events = [];
    private readonly Dictionary<nint, DateTime> _hung = [];
    private readonly Series _cpu = new(), _ram = new(), _commit = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _focus;
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _eventCursor;
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
                    _procs[key] = tr;

                    // Keep a handle to focus processes and to anything that starts while we watch, so its exit code is readable later.
                    if ((tr.Focus || !first) && _procs.Values.Count(x => x.Handle != 0) < 512)
                        tr.Handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION | Win32.SYNCHRONIZE, false, p.Pid);
                    if (!first)
                    {
                        var parent = snapshot.FirstOrDefault(x => x.Pid == p.ParentPid);
                        _events.Add((DateTime.UtcNow, $"start {p.Name}({p.Pid}){(parent != null ? $" by {parent.Name}({parent.Pid})" : "")}"));
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

            foreach (var tr in _procs.Values.Where(x => x.Exited == null && !seen.Contains((x.Pid, x.CreateTime))))
            {
                tr.Exited = DateTime.UtcNow;
                var code = "";
                if (tr.Handle != 0)
                {
                    if (Win32.GetExitCodeProcess(tr.Handle, out var exit) && exit != Win32.STILL_ACTIVE) code = " code " + ProcessDetails.ExitCodeName(exit);
                    Win32.CloseHandle(tr.Handle);
                    tr.Handle = 0;
                }

                var lived = tr.CreateTime > 0 ? " after " + Format.Duration(DateTime.UtcNow - DateTime.FromFileTimeUtc(tr.CreateTime)) : "";
                _events.Add((DateTime.UtcNow, $"exit  {tr.Name}({tr.Pid}){code}{lived}"));
            }

            // Windows that stop / resume responding.
            var names = snapshot.ToDictionary(p => p.Pid, p => p.Name);
            var hungNow = windows.Where(w => w.Visible && w.Hung && w.Title.Length > 0).ToDictionary(w => w.Hwnd);
            foreach (var (hwnd, w) in hungNow)
            {
                if (_hung.TryAdd(hwnd, DateTime.UtcNow))
                    _events.Add((DateTime.UtcNow, $"hung  {names.GetValueOrDefault(w.Pid, "?")}({w.Pid}) \"{Format.Clip(w.Title, 50)}\" stopped responding"));
            }

            foreach (var hwnd in _hung.Keys.Where(h => !hungNow.ContainsKey(h)).ToList())
            {
                _events.Add((DateTime.UtcNow, $"alive window 0x{(long)hwnd:X} responds again after {Format.Duration(DateTime.UtcNow - _hung[hwnd])}"));
                _hung.Remove(hwnd);
            }

            if (_events.Count > 5000)
            {
                _events.RemoveRange(0, 1000);
                _eventCursor = Math.Max(0, _eventCursor - 1000); // keep pointing at the same event
            }
            _lastSampleSeconds = t;
        }
    }

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

    public string Report(bool final)
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

            var fresh = _events.Skip(_eventCursor).ToList();
            _eventCursor = _events.Count;
            if (fresh.Count > 0)
            {
                sb.Append($"events since the last report ({fresh.Count}):\n");
                foreach (var (time, text) in fresh.TakeLast(60)) sb.Append($"  {time.ToLocalTime():HH:mm:ss} {text}\n");
                if (fresh.Count > 60) sb.Append($"  … {fresh.Count - 60} earlier events not shown\n");
            }
            else
            {
                sb.Append("no process start/exit or hang events since the last report\n");
            }

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
            watch                    report so far: trends for watched processes + events since the previous report
            watch stop               final report and stop
              Named processes get full tracking: private bytes, handles, GDI/USER objects, threads, CPU, each with
              start->end and growth per minute. Steady growth that continues for 2+ minutes is flagged LIKELY LEAK;
              before that it says "too early to call it a leak", because start-up growth looks the same.
              All processes get start/exit events (exit codes decoded, e.g. 0xC0000005 access violation) and
              "stopped responding" events. Start it BEFORE the test, run the test, then ask for the report:
              one cheap call instead of polling `ps` in a loop.
            """, WatchAsync) { Quiet = true },
    ];

    private static Task<int> WatchAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
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
                ctx.Out.Write(watcher.Report(final: true));
                watcher.Dispose();
                ctx.Runtime.Watcher = null;
                return Task.FromResult(0);
            case "report" or "status":
                if (watcher == null) return Task.FromResult(ctx.Fail("watch: not running. Start it with: watch start [process ...]"));
                ctx.Out.Write(watcher.Report(final: false));
                return Task.FromResult(0);
            default:
                return Task.FromResult(ctx.Fail($"watch: unknown subcommand '{sub}'. Use start, stop, or nothing for a report."));
        }
    }
}
