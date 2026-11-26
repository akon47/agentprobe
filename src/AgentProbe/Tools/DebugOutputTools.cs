using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// Captures OutputDebugString text from every process in the session, like Sysinternals DebugView.
/// It listens on the documented DBWIN_BUFFER shared section; lines are kept in a ring buffer on the target and
/// only the new or matching ones are sent, so chatty apps do not flood the agent.
/// </summary>
internal sealed unsafe class DebugCapture : IDisposable
{
    private const int MaxLines = 20000;
    private readonly List<(long seq, DateTime time, int pid, string text)> _lines = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = [];
    private readonly Dictionary<int, (string name, long at)> _names = [];
    private long _seq;

    public long ReadCursor { get; set; }

    public bool Global { get; private set; }

    public string? Warning { get; private set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public void Start(bool global)
    {
        StartListener("");
        if (global)
        {
            if (Win32.EnablePrivilege("SeCreateGlobalPrivilege") || Environment.IsPrivilegedProcess)
            {
                StartListener("Global\\");
                Global = true;
            }
            else
            {
                Warning = "--global (services / other sessions) needs agentprobe elevated on the target; capturing this session only.";
            }
        }
    }

    private void StartListener(string prefix)
    {
        var bufferReady = Win32.CreateEvent(0, false, false, prefix + "DBWIN_BUFFER_READY");
        var dataReady = Win32.CreateEvent(0, false, false, prefix + "DBWIN_DATA_READY");
        var mapping = Win32.CreateFileMapping(-1, 0, 4, 0, 4096, prefix + "DBWIN_BUFFER");
        var existed = System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 183;
        var view = mapping != 0 ? Win32.MapViewOfFile(mapping, 4, 0, 0, 0) : 0;
        if (bufferReady == 0 || dataReady == 0 || view == 0)
        {
            Warning = $"could not open the {(prefix.Length > 0 ? "global " : "")}debug output channel.";
            return;
        }

        if (existed && prefix.Length == 0)
            Warning = "another debug-output viewer (DebugView, an IDE) is already listening on this PC; lines are split between the two. Close it for complete output.";

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding((int)Win32.GetACP());
        var thread = new Thread(() =>
        {
            var data = (byte*)view;
            while (!_stop.IsCancellationRequested)
            {
                Win32.SetEvent(bufferReady);
                if (Win32.WaitForSingleObject(dataReady, 200) != 0) continue;
                var pid = *(int*)data;
                var len = 0;
                while (len < 4091 && data[4 + len] != 0) len++;
                var text = ansi.GetString(data + 4, len).TrimEnd('\r', '\n');
                if (text.Length == 0) continue;
                var owner = ProcessName(pid); // resolved now: the process may be gone, or its pid reused, by the time this is read
                lock (_lines)
                {
                    foreach (var line in text.Split('\n'))
                    {
                        _lines.Add((++_seq, DateTime.UtcNow, pid, owner + Separator + line.TrimEnd('\r')));
                    }

                    if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                }
            }

            Win32.UnmapViewOfFile(view);
            Win32.CloseHandle(mapping);
            Win32.CloseHandle(bufferReady);
            Win32.CloseHandle(dataReady);
        })
        {
            IsBackground = true,
            Name = "dbwin",
        };
        thread.Start();
        _threads.Add(thread);
    }

    public (List<(long seq, DateTime time, int pid, string text)> lines, long lastSeq, long dropped) Read(long after)
    {
        lock (_lines)
        {
            var first = _lines.Count > 0 ? _lines[0].seq : _seq + 1;
            return (_lines.Where(l => l.seq > after).ToList(), _seq, Math.Max(0, first - 1 - after));
        }
    }

    /// <summary>Lines are stored as "process name, separator, text" so the name is the one that was true at capture time.</summary>
    private const char Separator = '\u0001';

    public static string NameOf(string stored) => stored[..Math.Max(0, stored.IndexOf(Separator))];

    public static string TextOf(string stored) => stored[(stored.IndexOf(Separator) + 1)..];

    private string ProcessName(int pid)
    {
        lock (_names)
        {
            // Pids are reused, so a cached name is only trusted for a few seconds.
            var now = Environment.TickCount64;
            if (_names.TryGetValue(pid, out var known) && now - known.at < 3000) return known.name;
            foreach (var p in ProcessSnapshot.Take()) _names[p.Pid] = (p.Name, now);
            return _names.TryGetValue(pid, out known) && known.at == now ? known.name : "?";
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var t in _threads) t.Join(1000);
    }
}

internal static class DebugOutputTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("dbg", "Logs", "Debug output of all apps (OutputDebugString / Debug.WriteLine), like DebugView",
            """
            dbg [--grep REGEX] [--name PROCESS | --pid N] [--tail N] [--all] [--no-fold]
            dbg start [--global]      begin capturing now (plain `dbg` also starts it on first use)
            dbg stop
              Each call returns only lines captured since the previous call, so call it after each test step.
              --all returns everything still buffered (20,000 lines). Start the capture BEFORE reproducing.
              Four or more lines in a row that differ only in their numbers are printed as the first one and
              a line that says how many followed and gives the last; --no-fold prints them all.
              Output of a process that has a debugger attached goes to that debugger instead.
            """, DbgAsync) { Quiet = true },
    ];

    private static Task<int> DbgAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all", "global", "no-fold");
        var sub = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        var capture = ctx.Runtime.DebugCapture;

        if (sub == "stop")
        {
            capture?.Dispose();
            ctx.Runtime.DebugCapture = null;
            ctx.Out.WriteLine(capture == null ? "debug capture was not running" : "debug capture stopped");
            return Task.FromResult(0);
        }

        if (capture == null)
        {
            capture = new DebugCapture();
            capture.Start(a.Flag("global"));
            ctx.Runtime.DebugCapture = capture;
            ctx.Runtime.Audit.Write("debug output capture started");
            ctx.Out.WriteLine("debug capture started: output from now on is recorded. Reproduce the problem, then run `dbg` again.");
            if (capture.Warning != null) ctx.Out.WriteLine("! " + capture.Warning);
            return Task.FromResult(0);
        }

        if (sub == "start")
        {
            ctx.Out.WriteLine($"debug capture already running for {Format.Duration(DateTime.UtcNow - capture.StartedUtc)}");
            return Task.FromResult(0);
        }

        var (lines, last, dropped) = capture.Read(a.Flag("all") ? 0 : capture.ReadCursor);
        if (!a.Flag("all")) capture.ReadCursor = last;

        IEnumerable<(long seq, DateTime time, int pid, string text)> filtered = lines;
        if (a.Get("pid") is { } pidText && int.TryParse(pidText, out var pid)) filtered = filtered.Where(l => l.pid == pid);
        if (a.Get("name") is { } name) filtered = filtered.Where(l => DebugCapture.NameOf(l.text).Contains(name.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase));
        if (a.Get("grep") is { } grep)
        {
            try
            {
                var re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                filtered = filtered.Where(l => re.IsMatch(DebugCapture.TextOf(l.text)));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("dbg: bad --grep pattern: " + ex.Message));
            }
        }

        var result = filtered.ToList();
        var total = result.Count;
        if (a.Has("tail")) result = result.TakeLast(a.GetInt("tail", 50)).ToList();
        var sb = new StringBuilder();
        var printed = result.Select(l => ($"{l.time.ToLocalTime():HH:mm:ss.fff} {DebugCapture.NameOf(l.text)}({l.pid}) {DebugCapture.TextOf(l.text)}", DebugCapture.NameOf(l.text) + " " + DebugCapture.TextOf(l.text))).ToList();
        foreach (var line in a.Flag("no-fold") ? printed.Select(l => l.Item1) : LineFold.Similar(printed)) sb.Append(line).Append('\n');
        ctx.Out.Write(sb.ToString());
        if (dropped > 0 && !a.Flag("all")) ctx.Out.WriteLine($"[{dropped} older lines were dropped from the buffer before being read]");
        if (total == 0) ctx.Out.WriteLine(lines.Count == 0 ? "[no new debug output]" : $"[{lines.Count} new lines, none match the filter]");
        else if (total > result.Count) ctx.Out.WriteLine($"[{total} lines matched; showing the last {result.Count}]");
        if (capture.Warning != null && total == 0) ctx.Out.WriteLine("! " + capture.Warning);
        return Task.FromResult(0);
    }
}
