using System.Diagnostics;
using System.Text;
using AgentProbe.Target;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>Builds and runs shell commands so their output comes back as clean UTF-8 text with a real exit code.</summary>
internal static class Shell
{
    // UTF-8 output, no progress bars, and the exit code of the last native command instead of a flat 0/1.
    private const string PsPrelude =
        "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding $false;$OutputEncoding=[Console]::OutputEncoding\n";

    private const string PsSuffix = "\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }";

    /// <param name="fileArgs">
    /// Not null when <paramref name="command"/> is a whole script file (<c>exec -f</c>): it is then saved and run as a
    /// file with these arguments, so its <c>param(...)</c> block, <c>$PSScriptRoot</c> and line numbers are its own.
    /// </param>
    public static ProcessStartInfo Build(string shell, string command, string cwd, TargetRuntime runtime, IReadOnlyList<string>? fileArgs = null)
    {
        if (fileArgs != null)
        {
            var cmd = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);
            var file = Path.Combine(runtime.WorkDir, $"script-{Guid.NewGuid():N}.{(cmd ? "cmd" : "ps1")}");

            // A PowerShell script ends with the same exit-code rule an inline command gets, so a script whose last
            // step failed does not come back as 0 (it follows the script's own text: the param block stays first and
            // its line numbers stay its own). cmd reads a batch file in the console's OEM code page.
            if (cmd) File.WriteAllText(file, command, LineDecoder.OemEncoding);
            else File.WriteAllText(file, command.TrimEnd() + "\n" + PsSuffix + "\n", new UTF8Encoding(true));
            command = cmd
                ? "\"" + file + "\"" + string.Concat(fileArgs.Select(x => " \"" + x.Replace("\"", "\"\"") + "\""))
                : "& '" + file.Replace("'", "''") + "'" + string.Concat(fileArgs.Select(x => " " + PsArgument(x)));
        }

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        switch (shell.ToLowerInvariant())
        {
            case "cmd":
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + command + "\"";
                break;
            case "powershell" or "ps" or "pwsh" or "ps32" or "powershell32":
                // ps32 = the 32-bit Windows PowerShell: sees SysWOW64 and the WOW6432Node registry like a 32-bit app does.
                psi.FileName = shell.ToLowerInvariant() switch
                {
                    "pwsh" => "pwsh.exe",
                    "ps32" or "powershell32" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe"),
                    _ => "powershell.exe",
                };
                var script = PsPrelude + command + PsSuffix;
                const string common = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text ";
                if (script.Length < 6000)
                {
                    psi.Arguments = common + "-EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                }
                else
                {
                    // Too long for a command line: run it from a script file in the session workspace.
                    var file = Path.Combine(runtime.WorkDir, $"script-{Guid.NewGuid():N}.ps1");
                    File.WriteAllText(file, script, new UTF8Encoding(true));
                    psi.Arguments = common + "-File \"" + file + "\"";
                }

                break;
            default:
                throw new ArgumentException($"Unknown shell '{shell}'. Use powershell, pwsh, ps32 or cmd.");
        }

        return psi;
    }

    /// <summary>
    /// One argument for a script, as PowerShell source: parameter names (-Port), numbers and $true/$false/$null go in
    /// as they are, everything else as a literal string, so spaces and quotes in a value cannot change the command.
    /// </summary>
    private static string PsArgument(string value)
    {
        const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        const string literal = @"(-?\d+(\.\d+)?|\$(true|false|null))"; // \A..\z: "$" would also match before a trailing newline

        // -Name:value and -Switch:$false: the name as it is, the value by the same rule.
        var named = System.Text.RegularExpressions.Regex.Match(value, @"\A(-[A-Za-z_][\w-]*:)(.+)\z", options | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (named.Success) return named.Groups[1].Value + PsArgument(named.Groups[2].Value);
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"\A(-[A-Za-z_][\w-]*:?|" + literal + @")\z", options)
            ? value
            : "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>Runs to completion, streaming decoded lines. Returns the exit code, or 124 on timeout, 130 when cancelled.</summary>
    public static async Task<int> RunAsync(ProcessStartInfo psi, Action<string, bool> onText, TimeSpan? timeout, CancellationToken ct, Action<Process>? onStarted = null)
    {
        using var p = new Process { StartInfo = psi };
        p.Start();
        onStarted?.Invoke(p);
        p.StandardInput.Close(); // anything that prompts fails fast instead of hanging forever

        var outTask = PumpAsync(p.StandardOutput.BaseStream, s => onText(s, false));
        var errTask = PumpAsync(p.StandardError.BaseStream, s => onText(s, true));

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t) timeoutCts.CancelAfter(t);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);
            await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(3));
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1500));
            if (ct.IsCancellationRequested)
            {
                onText("[cancelled]\n", true);
                return 130;
            }

            onText($"[timed out after {Format.Duration(timeout!.Value)}; process tree killed]\n", true);
            return 124;
        }
        catch (TimeoutException)
        {
            // A grandchild still holds the pipes open; the exit code is already known.
            return p.ExitCode;
        }
    }

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // already gone, or some descendants could not be terminated
        }
    }

    public static async Task PumpAsync(Stream stream, Action<string> onText)
    {
        var decoder = new LineDecoder();
        var buf = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf)) > 0)
            {
                var text = decoder.Feed(buf.AsSpan(0, n));
                if (text.Length > 0) onText(text);
            }
        }
        catch (IOException)
        {
            // pipe closed
        }

        var rest = decoder.Flush();
        if (rest.Length > 0) onText(rest + "\n");
    }
}

internal sealed class Job
{
    private const int MaxBuffer = 4 * 1024 * 1024;
    private readonly StringBuilder _buffer = new();
    private long _dropped;
    private long _readCursor;

    public int Id { get; init; }

    public string Command { get; init; } = "";

    public string? Name { get; init; }

    public Process Process { get; init; } = null!;

    public int Pid { get; init; }

    public DateTime StartUtc { get; } = DateTime.UtcNow;

    public DateTime? EndUtc { get; set; }

    public int? ExitCode { get; set; }

    public void Append(string text)
    {
        lock (_buffer)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxBuffer)
            {
                var cut = _buffer.Length - (MaxBuffer * 3 / 4);
                _buffer.Remove(0, cut);
                _dropped += cut;
            }
        }
    }

    /// <summary>Output written since the previous call (or everything still buffered when <paramref name="all"/>).</summary>
    public string Read(bool all)
    {
        lock (_buffer)
        {
            var total = _dropped + _buffer.Length;
            var from = all ? _dropped : Math.Max(_readCursor, _dropped);

            // Output that scrolled out of the buffer before it was read is gone: say so instead of starting mid-stream silently.
            var lost = all ? _dropped : Math.Max(0, _dropped - _readCursor);
            _readCursor = total;
            var text = _buffer.ToString((int)(from - _dropped), (int)(total - from));
            return lost > 0 ? $"[{lost} characters of earlier output were dropped: the job writes more than the {MaxBuffer / (1024 * 1024)} MB buffer keeps]\n" + text : text;
        }
    }
}

/// <summary>Long-running processes started with <c>start</c>. They survive connection drops; output is buffered here.</summary>
internal sealed class JobManager(TargetRuntime runtime)
{
    private readonly List<Job> _jobs = [];
    private int _nextId;

    public Job Start(string shell, string command, string cwd, string? name, IReadOnlyList<string>? fileArgs = null) =>
        Track(Shell.Build(shell, command, cwd, runtime, fileArgs), command, name);

    /// <summary>
    /// Starts a program itself, without a shell in between, so the job IS that program: its pid, its exit code,
    /// and <c>kill</c> ends it. A GUI app started through a shell is not waited for, and the job would end at once.
    /// </summary>
    public Job StartExe(string exe, IReadOnlyList<string> args, string? cwd, string? name)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd ?? Path.GetDirectoryName(exe) ?? runtime.WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true, // no console window for a console program; a GUI program shows its windows as usual
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Track(psi, (exe + " " + string.Join(' ', args)).TrimEnd(), name);
    }

    private Job Track(ProcessStartInfo psi, string command, string? name)
    {
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        p.StandardInput.Close();
        var job = new Job { Id = Interlocked.Increment(ref _nextId), Command = command, Name = name, Process = p, Pid = p.Id };
        lock (_jobs) _jobs.Add(job);

        var pumps = Task.WhenAll(
            Shell.PumpAsync(p.StandardOutput.BaseStream, job.Append),
            Shell.PumpAsync(p.StandardError.BaseStream, job.Append));
        _ = Task.Run(async () =>
        {
            await p.WaitForExitAsync();
            await Task.WhenAny(pumps, Task.Delay(2000));
            job.ExitCode = p.ExitCode;
            job.EndUtc = DateTime.UtcNow;
            runtime.Audit.Write($"job {job.Id} exited: {Native.ProcessDetails.ExitCodeName((uint)p.ExitCode)}  ({Format.Clip(job.Command, 80)})");
        });
        return job;
    }

    public List<Job> List()
    {
        lock (_jobs) return [.. _jobs];
    }

    public Job? Find(string idOrName)
    {
        lock (_jobs)
        {
            return int.TryParse(idOrName, out var id)
                ? _jobs.FirstOrDefault(j => j.Id == id)
                : _jobs.LastOrDefault(j => string.Equals(j.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void KillAll()
    {
        foreach (var j in List()) Shell.KillTree(j.Process);
    }
}

internal static class ExecTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("exec", "Run", "Run a command (PowerShell by default) and wait for it",
            """
            exec [--shell powershell|pwsh|ps32|cmd] [--cwd DIR] [--timeout 60s] <command>
            exec -f local-script.ps1 [args] send a local script file and run it; what follows the file name goes
                                            to the script (its param block works): exec -f t.ps1 -Port 8080 -Fast
                                            Put --shell/--cwd/--timeout before -f; write -- before script arguments
                                            that look like those: exec --timeout 5m -f t.ps1 -- --timeout 3
            exec - [args]                   read the script from stdin
              Output is UTF-8, stdin is closed (prompts fail fast), the exit code is the remote exit code.
              Default folder is the session workspace (where relative `push` paths land).
              Runs as the logged-on user in their desktop session, so GUI apps you launch are visible.
              --shell ps32 is the 32-bit PowerShell (what a 32-bit app sees: SysWOW64, WOW6432Node).
              Trailing spaces and runs of blank lines are removed from the output.
            """, ExecAsync),
        new("start", "Run", "Start a long-running command in the background",
            """
            start [--name NAME] [--shell ..] [--cwd DIR] <command>
            start [--name NAME] [--cwd DIR] --exe <path> [args]
              Returns a job id. The job keeps running if the connection drops. Read it with `logs`, stop it with `kill`.
              --exe starts the program itself, with no shell in between: use it for an app with a window. The job
              is then that app (its pid, its exit code with crash codes named, `kill <job>` closes it, `wait --job`
              waits for it), and it starts in its own folder unless --cwd says otherwise. Started as a <command>
              instead, a GUI app is not waited for and the job ends at once while the app runs on untracked.
            """, StartAsync),
        new("jobs", "Run", "List background jobs", "jobs", JobsAsync) { Quiet = true },
        new("logs", "Run", "Output of a background job (only what is new since the last call)",
            """
            logs <job> [--all] [--tail N] [--grep REGEX]
              Without --all, returns only output produced since the previous `logs` call for that job.
            """, LogsAsync) { Quiet = true },
        new("kill", "Run", "Stop a background job, or any process by pid/name",
            """
            kill <job>                     stop a job started with `start` (its whole process tree)
            kill --pid 1234 [--tree]       terminate one process (--tree: also everything it started)
            kill --name foo.exe [--tree]   terminate every process with that image name
            """, KillAsync),
    ];

    private static async Task<int> ExecAsync(ToolContext ctx, string[] args)
    {
        // The CLI puts the command text in --script. With --as-file it is a whole script file (-f or stdin) and the
        // words after -- are that script's arguments.
        var a = ArgParser.Parse(args, "as-file");
        var command = a.Get("script") ?? string.Join(' ', a.Positionals);
        if (string.IsNullOrWhiteSpace(command)) return ctx.Fail("exec: no command given.");

        var cwd = a.Get("cwd") is { } c ? ctx.Runtime.ResolvePath(c) : ctx.Runtime.WorkDir;
        ProcessStartInfo psi;
        try
        {
            psi = Shell.Build(a.Get("shell") ?? "powershell", command, cwd, ctx.Runtime, a.Flag("as-file") ? a.Positionals : null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ctx.Fail("exec: " + ex.Message);
        }

        try
        {
            var squeeze = new Squeezer();
            var stderr = new StringBuilder();
            var code = await Shell.RunAsync(psi, (text, err) =>
            {
                if (err)
                {
                    lock (stderr)
                    {
                        if (stderr.Length < 2_000_000) stderr.Append(text); // decoded as a whole once the command ends
                    }
                }
                else ctx.Out.Write(squeeze.Apply(text));
            }, a.GetDuration("timeout"), ctx.Ct);
            var errorText = PsErrors.ToPlainText(stderr.ToString());
            if (errorText.Length > 0) ctx.Err.Write(new Squeezer().Apply(errorText));

            // A crash exit code reads better with its meaning next to it.
            if ((uint)code >= 0xC0000000) ctx.Err.WriteLine($"[exit code {Native.ProcessDetails.ExitCodeName((uint)code)}]");
            SystemPrompts.Warn(ctx.Err);
            return code;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ctx.Fail($"exec: cannot start {psi.FileName}: {ex.Message}");
        }
    }

    private static Task<int> StartAsync(ToolContext ctx, string[] args)
    {
        return StartCoreAsync(ctx, args);
    }

    private static async Task<int> StartCoreAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "as-file");
        var cwd = a.Get("cwd") is { } c ? ctx.Runtime.ResolvePath(c) : null;
        try
        {
            Job job;
            if (a.Get("exe") is { } exe)
            {
                var path = ctx.Runtime.ResolvePath(exe);
                if (!File.Exists(path)) return ctx.Fail($"start: {path} does not exist on the target.");
                job = ctx.Runtime.Jobs.StartExe(path, a.Positionals, cwd, a.Get("name"));
                ctx.Out.WriteLine($"job {job.Id} started (pid {job.Pid}): {Path.GetFileName(path)}");
            }
            else
            {
                var command = a.Get("script") ?? string.Join(' ', a.Positionals);
                if (string.IsNullOrWhiteSpace(command)) return ctx.Fail("start: no command given.");
                job = ctx.Runtime.Jobs.Start(a.Get("shell") ?? "powershell", command, cwd ?? ctx.Runtime.WorkDir, a.Get("name"), a.Flag("as-file") ? a.Positionals : null);
                ctx.Out.WriteLine($"job {job.Id} started (pid {job.Pid})");
            }

            // A shell that only launched something is done within moments: say so now, instead of leaving a job
            // that looks like the app but is not.
            // (The process is asked directly: the job's own exit code is set only after its output has been read,
            // and an app the shell launched keeps that output open.)
            if (!a.Has("exe")) await Task.WhenAny(job.Process.WaitForExitAsync(ctx.Ct), Task.Delay(2500, ctx.Ct));
            if (!a.Has("exe") && job.Process.HasExited)
            {
                ctx.Err.WriteLine($"[job {job.Id} already ended with exit {Native.ProcessDetails.ExitCodeName((uint)job.Process.ExitCode)}. If it launched an app with a window, the app is NOT this job: " +
                                  "start it with `start --exe <path> [args]` to track the app itself.]");
            }

            SystemPrompts.Warn(ctx.Err);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return ctx.Fail("start: " + ex.Message);
        }
    }

    private static Task<int> JobsAsync(ToolContext ctx, string[] args)
    {
        var jobs = ctx.Runtime.Jobs.List();
        if (jobs.Count == 0)
        {
            ctx.Out.WriteLine("no jobs");
            return Task.FromResult(0);
        }

        var t = new Table(">JOB", "NAME", ">PID", "STATE", ">AGE", "COMMAND");
        foreach (var j in jobs)
        {
            var state = j.ExitCode is { } code ? "exit " + Native.ProcessDetails.ExitCodeName((uint)code) : "running";
            t.Add(j.Id, j.Name ?? "-", j.Pid, state, Format.Duration((j.EndUtc ?? DateTime.UtcNow) - j.StartUtc), Format.Clip(j.Command, 90));
        }

        ctx.Out.Write(t.ToString());
        return Task.FromResult(0);
    }

    private static Task<int> LogsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all");
        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("logs: which job? (see `jobs`)"));
        var job = ctx.Runtime.Jobs.Find(a.Positionals[0]);
        if (job == null) return Task.FromResult(ctx.Fail($"logs: no job '{a.Positionals[0]}'."));

        var text = job.Read(a.Flag("all") || a.Has("tail"));
        text = TextFilter.Apply(text, a.Get("grep"), null, a.Has("tail") ? a.GetInt("tail", 50) : null, ctx.Err);
        ctx.Out.Write(text);
        if (text.Length > 0 && !text.EndsWith('\n')) ctx.Out.WriteLine();
        var state = job.ExitCode is { } code ? "exited " + Native.ProcessDetails.ExitCodeName((uint)code) : "still running";
        ctx.Out.WriteLine($"[job {job.Id} {state}{(text.Length == 0 ? ", no new output" : "")}]");
        return Task.FromResult(0);
    }

    private static Task<int> KillAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "tree");
        var tree = a.Flag("tree");
        if (a.Get("pid") is { } pidText)
        {
            if (!int.TryParse(pidText, out var pid)) return Task.FromResult(ctx.Fail("kill: --pid needs a number."));
            return Task.FromResult(KillProcess(ctx, pid, tree));
        }

        if (a.Get("name") is { } name)
        {
            var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            var procs = Process.GetProcessesByName(bare);
            if (procs.Length == 0) return Task.FromResult(ctx.Fail($"kill: no process named {name}."));
            var rc = 0;
            foreach (var p in procs) rc |= KillProcess(ctx, p.Id, tree);
            return Task.FromResult(rc);
        }

        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("kill: give a job id, --pid or --name."));
        var job = ctx.Runtime.Jobs.Find(a.Positionals[0]);
        if (job == null) return Task.FromResult(ctx.Fail($"kill: no job '{a.Positionals[0]}'. For a process use --pid."));
        Shell.KillTree(job.Process);
        ctx.Out.WriteLine($"job {job.Id} killed");
        return Task.FromResult(0);
    }

    /// <summary>Terminates exactly one process unless <paramref name="tree"/> is asked for: its children are not ours to take down.</summary>
    private static int KillProcess(ToolContext ctx, int pid, bool tree)
    {
        if (pid == Environment.ProcessId) return ctx.Fail("kill: that is agentprobe itself. Use `end` to close the session.");
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill(entireProcessTree: tree);
            ctx.Out.WriteLine($"killed {name} ({pid}){(tree ? " and the processes it started" : "")}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return ctx.Fail($"kill: pid {pid}: {ex.GetBaseException().Message}");
        }
    }
}

/// <summary>
/// PowerShell pads table rows with spaces and surrounds output with blank lines. None of that carries information,
/// so it is dropped before it costs tokens: trailing whitespace goes, blank-line runs collapse to one, leading blanks go.
/// </summary>
internal sealed class Squeezer
{
    private bool _started;
    private int _blankRun;

    public string Apply(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length; // Split leaves one empty piece after a final newline
        for (var i = 0; i < count; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0)
            {
                _blankRun++;
                continue;
            }

            if (_started && _blankRun > 0) sb.Append('\n');
            _blankRun = 0;
            _started = true;
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Windows PowerShell writes its error stream as "#&lt; CLIXML" + an XML blob when stderr is redirected.
/// This turns it back into the message a person would see, minus the decoration lines that only cost tokens.
/// </summary>
internal static class PsErrors
{
    public static string ToPlainText(string stderr)
    {
        if (!stderr.Contains("#< CLIXML", StringComparison.Ordinal)) return stderr;
        var sb = new StringBuilder();
        var plainEnd = stderr.IndexOf("#< CLIXML", StringComparison.Ordinal);
        sb.Append(stderr, 0, plainEnd);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(stderr, "<S S=\"(\\w+)\">(.*?)</S>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            if (m.Groups[1].Value is "progress" or "Progress") continue;
            var text = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
            text = System.Text.RegularExpressions.Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", x => ((char)Convert.ToInt32(x.Groups[1].Value, 16)).ToString());
            sb.Append(text);
        }

        // Drop the squiggle underline and the CategoryInfo line; keep message, position and error id.
        var lines = sb.ToString().Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("+ ~", StringComparison.Ordinal) && !l.TrimStart().StartsWith("+ CategoryInfo", StringComparison.Ordinal));
        return string.Join('\n', lines).TrimEnd() + "\n";
    }
}

/// <summary>grep / head / tail applied on the target, so only the lines that matter cross the network.</summary>
internal static class TextFilter
{
    public static string Apply(string text, string? grep, int? head, int? tail, TextWriter err)
    {
        if (grep == null && head == null && tail == null) return text;
        IEnumerable<string> lines = text.Split('\n');
        if (text.EndsWith('\n')) lines = lines.SkipLast(1);
        if (grep != null)
        {
            try
            {
                var re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                lines = lines.Where(l => re.IsMatch(l));
            }
            catch (ArgumentException ex)
            {
                err.WriteLine("bad --grep pattern: " + ex.Message);
                return "";
            }
        }

        if (head is { } h) lines = lines.Take(h);
        if (tail is { } t) lines = lines.TakeLast(t);
        var result = string.Join('\n', lines);
        return result.Length > 0 ? result + "\n" : "";
    }
}
