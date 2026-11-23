using System.Globalization;
using System.Text;
using AgentProbe.Tools;

namespace AgentProbe.Target;

/// <summary>One command the developer PC asked for. Kept so work can be picked up after a dropped connection.</summary>
internal sealed class RunRecord
{
    private const int MaxOutput = 256 * 1024;
    private readonly StringBuilder _output = new();

    public int No { get; init; }

    public string Tool { get; init; } = "";

    public string[] Args { get; init; } = [];

    public string Controller { get; init; } = "";

    public DateTime StartUtc { get; init; } = DateTime.UtcNow;

    public DateTime? EndUtc { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>True when the connection dropped while this was running, so the developer PC never saw the end.</summary>
    public bool Orphaned { get; set; }

    /// <summary>
    /// Carried over from before agentprobe restarted on this PC (a reboot, or an update of agentprobe itself): the
    /// command line and result are known, the output is not.
    /// </summary>
    public bool BeforeReboot { get; init; }

    /// <summary>What the restart was: "reboot" or "update".</summary>
    public string Restart { get; init; } = "reboot";

    public long OutputChars { get; private set; }

    /// <summary>The command as a person would type it (the CLI's internal --script marker is dropped).</summary>
    public string CommandLine => Tool + (Args.Length > 0 ? " " + string.Join(' ', Args.Where(a => a != "--script")) : "");

    public void Append(string text)
    {
        lock (_output)
        {
            OutputChars += text.Length;
            _output.Append(text);
            if (_output.Length > MaxOutput) _output.Remove(0, _output.Length - (MaxOutput * 3 / 4));
        }
    }

    public string Output()
    {
        lock (_output) return _output.ToString();
    }
}

/// <summary>
/// Append-only activity log on the target PC. It belongs to the person who owns that PC:
/// the remote side can add to it but has no command to read around, edit or delete it.
/// </summary>
internal sealed class AuditLog
{
    private readonly object _gate = new();
    private readonly Lazy<string?> _path = new(Locate);

    /// <summary>Next to the exe when that folder is writable, else in the per-user folder. Created on first use.</summary>
    private static string? Locate()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(AppInfo.ExePath)!, Path.Combine(AppInfo.StateDir, "logs") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{AppInfo.Name}-activity.log");
                File.AppendAllText(path, "");
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // exe folder not writable: fall back to the per-user folder
            }
        }

        return null;
    }

    public string? FilePath => _path.Value;

    /// <summary>Raised for every line so the console can mirror it.</summary>
    public event Action<string>? Line;

    public void Write(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {message}";
        lock (_gate)
        {
            if (_path.Value is { } path)
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // disk full or locked: the on-screen log still shows it
                }
            }
        }

        Line?.Invoke(line);
    }
}

/// <summary>State of this target that outlives a single connection: workspace, jobs, captures, history.</summary>
internal sealed class TargetRuntime
{
    private readonly List<RunRecord> _history = [];
    private int _runNo;
    private string? _workDir;

    public TargetRuntime(string? label, string? workDir)
    {
        Label = label;
        _workDir = workDir;
        Jobs = new JobManager(this);
    }

    public AuditLog Audit { get; } = new();

    public JobManager Jobs { get; }

    public DebugCapture? DebugCapture { get; set; }

    public Watcher? Watcher { get; set; }

    public AccessTracer? Tracer { get; set; }

    public string? Label { get; set; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Set by the host: registers a one-shot autostart and writes resume state before a reboot.</summary>
    public Func<string>? PrepareReboot { get; set; }

    /// <summary>
    /// Set by the host: replaces agentprobe's exe with the given one (already checked to run; its version is the
    /// second argument) and restarts sharing from it with the session's approval and workspace. Returns what happens next.
    /// </summary>
    public Func<string, string, string>? UpdateTo { get; set; }

    /// <summary>Set by the host: ends sharing (optionally deleting the exe).</summary>
    public Action<bool>? RequestEnd { get; set; }

    public bool RebootPending { get; set; }

    /// <summary>
    /// The person at this PC has paused remote commands (the space bar in agentprobe's window; Enter resumes): every new command is refused
    /// until they resume. What is already running goes on.
    /// </summary>
    public volatile bool Paused;

    /// <summary>Set while `firewall dismiss --auto` is on: cancelling it stops the watch.</summary>
    public CancellationTokenSource? PromptWatch;

    /// <summary>How many system prompts this session closed (each one can leave firewall rules behind).</summary>
    public int PromptsClosed;

    /// <summary>The names of the firewall rules `firewall allow` made in this session: removed at its end, so never "left over".</summary>
    public HashSet<string> SessionFirewallRules { get; } = [];

    private readonly List<(string what, Action undo)> _undo = [];

    /// <summary>Registers how to revert a change a tool made to this PC; run (newest first) when the session ends.</summary>
    public void AddUndo(string what, Action undo)
    {
        lock (_undo) _undo.Add((what, undo));
    }

    public bool HasWorkDir => _workDir != null;

    /// <summary>
    /// Scratch folder for pushed builds and relative paths. Created on first use and removed when sharing ends,
    /// so a normal session leaves nothing behind.
    /// </summary>
    public string WorkDir
    {
        get
        {
            if (_workDir == null)
            {
                _workDir = Path.Combine(Path.GetTempPath(), AppInfo.Name, "work-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(_workDir);
            }

            return _workDir;
        }
    }

    public string ResolvePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkDir, path));
    }

    public RunRecord NewRun(string tool, string[] args, string controller)
    {
        var r = new RunRecord { No = Interlocked.Increment(ref _runNo), Tool = tool, Args = args, Controller = controller };
        lock (_history)
        {
            _history.Add(r);
            if (_history.Count > 300) _history.RemoveAt(0);
        }

        return r;
    }

    public List<RunRecord> History()
    {
        lock (_history) return [.. _history];
    }

    /// <summary>The most recent runs as tab-separated lines (number, start, end, exit code or empty, command) for the resume state.</summary>
    public List<string> ExportHistory(int max)
    {
        lock (_history)
        {
            return _history.Where(r => r.Tool is not ("history" or "result")).TakeLast(max)
                .Select(r => string.Join('\t', r.No, r.StartUtc.Ticks, (r.EndUtc ?? DateTime.UtcNow).Ticks, r.ExitCode?.ToString() ?? "", r.BeforeReboot ? r.Restart : "", r.CommandLine.Replace('\t', ' ').Replace('\n', ' ')))
                .ToList();
        }
    }

    /// <summary>Restores runs exported before a restart ("reboot" or "update"); numbering continues after them.</summary>
    public void SeedHistory(IEnumerable<string> lines, string restart = "reboot")
    {
        lock (_history)
        {
            foreach (var line in lines)
            {
                // number, start, end, exit code, [the restart an older record was already carried over], command.
                // (Lines written by agentprobe 0.2.4 and older have no restart field.)
                var f = line.Split('\t');
                if (f.Length < 5 || !int.TryParse(f[0], out var no) || !long.TryParse(f[1], out var start) || !long.TryParse(f[2], out var end)) continue;
                var carried = f.Length >= 6 && f[4] is "" or "reboot" or "update";
                var earlier = carried && f[4].Length > 0 ? f[4] : null;
                _history.Add(new RunRecord
                {
                    No = no,
                    Tool = string.Join(' ', f[(carried ? 5 : 4)..]),
                    StartUtc = new DateTime(start, DateTimeKind.Utc),
                    EndUtc = new DateTime(end, DateTimeKind.Utc),
                    ExitCode = int.TryParse(f[3], out var code) ? code : null,
                    BeforeReboot = true,
                    Restart = earlier ?? restart,
                });
                _runNo = Math.Max(_runNo, no);
            }
        }
    }

    /// <summary>The number of the newest run so far.</summary>
    public int LastRunNumber => _runNo;

    /// <summary>Makes the next run number follow <paramref name="number"/> (the session continues after a restart).</summary>
    public void ContinueRunsAfter(int number)
    {
        lock (_history) _runNo = Math.Max(_runNo, number);
    }

    /// <summary>
    /// Stops what tools left running in the background of this process (a watch, a debug-output capture, an access
    /// trace) and returns their names. An ETW session in particular would outlive the process.
    /// </summary>
    public List<string> StopCaptures()
    {
        var stopped = new List<string>();
        foreach (var (name, stop) in new (string, Action)[]
                 {
                     ("watch", () => { var w = Watcher; Watcher = null; w?.Dispose(); if (w != null) stopped.Add("watch"); }),
                     ("debug-output capture", () => { var d = DebugCapture; DebugCapture = null; d?.Dispose(); if (d != null) stopped.Add("debug-output capture"); }),
                     ("access trace", () => { var t = Tracer; Tracer = null; t?.Dispose(); if (t != null) stopped.Add("access trace"); }),
                 })
        {
            try
            {
                stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Audit.Write($"could not stop the {name}: {ex.Message}");
            }
        }

        foreach (var name in stopped) Audit.Write("stopped: " + name);
        return stopped;
    }

    public RunRecord? FindRun(int no)
    {
        lock (_history) return _history.FirstOrDefault(r => r.No == no);
    }

    /// <summary>Stops everything this session started and removes its scratch folder.</summary>
    /// <summary>When a requested reboot should have happened by; after that it is treated as not happening.</summary>
    public DateTime? RebootDeadlineUtc { get; set; }

    /// <summary>Reverts every change tools registered, newest first. Returns what was reverted.</summary>
    public List<string> RunUndo()
    {
        List<(string what, Action undo)> undo;
        lock (_undo)
        {
            undo = [.. _undo];
            _undo.Clear();
        }

        var done = new List<string>();
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                undo[i].undo();
                Audit.Write("reverted: " + undo[i].what);
                done.Add(undo[i].what);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Audit.Write($"could not revert {undo[i].what}: {ex.Message}");
            }
        }

        return done;
    }

    public void Cleanup()
    {
        RunUndo();

        // Each step on its own: one that fails must not keep the others from running.
        foreach (var step in new Action[]
                 {
                     () => Jobs.KillAll(),
                     () => DebugCapture?.Dispose(),
                     () => Watcher?.Dispose(),
                     () => Tracer?.Dispose(), // an ETW session left running would outlive this process
                     () =>
                     {
                         if (_workDir != null && Directory.Exists(_workDir)) Directory.Delete(_workDir, true);
                     },
                 })
        {
            try
            {
                step();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // e.g. a pushed binary still in use: it stays in %TEMP%
            }
        }
    }
}
