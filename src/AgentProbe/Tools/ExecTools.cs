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
    /// <param name="onTempFile">Told about a script file this call wrote into the workspace, to be deleted when the command has ended.</param>
    public static ProcessStartInfo Build(string shell, string command, string cwd, TargetRuntime runtime, IReadOnlyList<string>? fileArgs = null, Action<string>? onTempFile = null)
    {
        if (fileArgs != null)
        {
            var cmd = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);
            var file = Path.Combine(runtime.WorkDir, $"script-{Guid.NewGuid():N}.{(cmd ? "cmd" : "ps1")}");
            onTempFile?.Invoke(file);

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
                    onTempFile?.Invoke(file);
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
    /// <param name="onNote">Gets what this method itself has to say (output that was not taken); without it that goes to the error text.</param>
    public static async Task<int> RunAsync(ProcessStartInfo psi, Action<string, bool> onText, TimeSpan? timeout, CancellationToken ct, Action<Process>? onStarted = null, Action<string>? onNote = null)
    {
        using var p = new Process { StartInfo = psi };
        p.Start();
        var startedAt = DateTime.UtcNow.AddSeconds(-2).ToFileTimeUtc(); // what it starts is younger than this
        onStarted?.Invoke(p);
        p.StandardInput.Close(); // anything that prompts fails fast instead of hanging forever

        // Once the command has ended and its own output is in, nothing more is passed on: a program it started
        // and left running holds the same pipes and may write for as long as it lives.
        var closed = 0;
        var forward = onText;
        onText = (text, isError) =>
        {
            if (Volatile.Read(ref closed) == 0) forward(text, isError);
        };
        var outTask = PumpAsync(p.StandardOutput.BaseStream, s => onText(s, false));
        var errTask = PumpAsync(p.StandardError.BaseStream, s => onText(s, true));

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t) timeoutCts.CancelAfter(t);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);

            // What the command itself wrote is in the pipes by now. When it left a program running (an app a
            // script launched), that program keeps the pipes open: its output is taken for a moment longer, so
            // that the command's last lines are not cut, and then no more. Without such a program the pipes
            // close by themselves.
            var pumps = Task.WhenAll(outTask, errTask);
            var left = LeftRunning(p.Id, startedAt);
            if (await Task.WhenAny(pumps, Task.Delay(left.Count > 0 ? 1000 : 3000)) != pumps)
            {
                Volatile.Write(ref closed, 1);
                var who = left.Count > 0
                    ? $"{string.Join(", ", left.Take(4))}{(left.Count > 4 ? $" and {left.Count - 4} more" : "")}, which it started, is still running and writes"
                    : "something it started (not one of its direct child processes) is still running and holds";
                var note = $"[the command ended, but {who} to the same output: that is not shown from here on. Start an app with `start --exe` to keep its output apart.]\n";
                if (onNote != null) onNote(note);
                else forward(note, true);
            }

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
            Volatile.Write(ref closed, 1);
            return p.ExitCode;
        }
    }

    /// <summary>"name.exe(pid)" of the processes that a process which has just ended started and left running (the console host is not one).</summary>
    internal static List<string> LeftRunning(int parentPid, long startedAfter)
    {
        try
        {
            // Younger than the command: a pid is reused, and an older process with that parent pid is nobody's child here.
            return Native.ProcessSnapshot.Take().Where(c => c.ParentPid == parentPid && c.CreateTime >= startedAfter && !c.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name}({c.Pid})").ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Removes the script files a command was run from: the workspace is the agent's, not a pile of these.</summary>
    public static void DeleteScripts(IReadOnlyList<string>? files)
    {
        foreach (var file in files ?? [])
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still held by something the command left running: it goes with the workspace at the end
            }
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

    public Job Start(string shell, string command, string cwd, string? name, IReadOnlyList<string>? fileArgs = null, IReadOnlyList<(string name, string value)>? env = null)
    {
        var scripts = new List<string>();
        try
        {
            var psi = Shell.Build(shell, command, cwd, runtime, fileArgs, scripts.Add);
            Apply(psi, env);
            return Track(psi, command, name, scripts);
        }
        catch
        {
            Shell.DeleteScripts(scripts); // nothing runs from them
            throw;
        }
    }

    /// <summary>
    /// Starts a program itself, without a shell in between, so the job IS that program: its pid, its exit code,
    /// and <c>kill</c> ends it. A GUI app started through a shell is not waited for, and the job would end at once.
    /// </summary>
    /// <param name="rawArguments">Arguments as one command-line string (what a shortcut holds); <paramref name="args"/> follow them.</param>
    public Job StartExe(string exe, IReadOnlyList<string> args, string? cwd, string? name, IReadOnlyList<(string name, string value)>? env = null, string? rawArguments = null)
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
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.Arguments = (rawArguments.Trim() + " " + string.Join(' ', args.Select(QuoteArgument))).TrimEnd();
        }

        Apply(psi, env);
        var shown = string.IsNullOrWhiteSpace(rawArguments) ? string.Join(' ', args) : psi.Arguments;
        return Track(psi, (exe + " " + shown).TrimEnd(), name);
    }

    private static void Apply(ProcessStartInfo psi, IReadOnlyList<(string name, string value)>? env)
    {
        if (env == null) return;
        foreach (var (name, value) in env) psi.Environment[name] = value;
    }

    /// <summary>One argument as Windows programs read their command line (quotes and the backslashes before them escaped).</summary>
    public static string QuoteArgument(string a)
    {
        const char quote = '"';
        const char backslash = '\\';
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', quote]) < 0) return a;
        var sb = new StringBuilder().Append(quote);
        var slashes = 0;
        foreach (var c in a)
        {
            if (c == backslash)
            {
                slashes++;
                continue;
            }

            sb.Append(backslash, c == quote ? (slashes * 2) + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }

        return sb.Append(backslash, slashes * 2).Append(quote).ToString();
    }

    /// <param name="scripts">Script files written for this job, deleted when it has ended.</param>
    private Job Track(ProcessStartInfo psi, string command, string? name, IReadOnlyList<string>? scripts = null)
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
            if (Shell.LeftRunning(p.Id, 0).Count == 0) Shell.DeleteScripts(scripts); // not while something it started may still read them
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
            start [--name NAME] [--shell ..] [--cwd DIR] [--env NAME=VALUE]... <command>
            start [--name NAME] [--cwd DIR] [--env NAME=VALUE]... [--replace IMAGE] [--dismiss-firewall] --exe <path> [arguments for the program]
            start [--name NAME] [--cwd DIR] [--env NAME=VALUE]... [--replace IMAGE] [--dismiss-firewall] --lnk <shortcut.lnk> [more arguments]
              Returns a job id. The job keeps running if the connection drops. Read it with `logs`, stop it with `kill`.
              --exe starts the program itself, with no shell in between: use it for an app with a window. The job
              is then that app (its pid, its exit code with crash codes and .NET host errors named, `kill <job>`
              closes it, `wait --job` waits for it), and it starts in its own folder unless --cwd says otherwise.
              Started as a <command> instead, a GUI app is not waited for and the job ends at once while the app
              runs on untracked.
              EVERYTHING after the --exe path goes to the program as its arguments, words that look like options
              included: --name, --cwd and --env belong BEFORE --exe.
              --lnk starts what a shortcut starts: its target with its arguments in its "Start in" folder, the way
              the person at the PC starts the app from the desktop or the Start menu. `stat <shortcut.lnk>` shows
              those three without starting anything.
              --env sets an environment variable for the job; repeat it for more (--env COREHOST_TRACE=1).
              --replace IMAGE first closes the running instances of that program (MyApp.exe) the way a person
              does, by closing their windows, and waits for them to end (--replace-wait 15s): "swap the installed
              app for the build under test" in one command. When one does not close in time (an unsaved-changes
              question?) nothing is started and nothing is killed; `kill --name` is the forceful way.
              --dismiss-firewall closes a Windows Firewall prompt that appears within a minute of the start, as
              its "Cancel" would (the program stays reachable on 127.0.0.1 only): for a target without
              administrator rights, where `firewall allow` cannot prevent the prompt. `logs <job>` says when it did.
              What it closes is any new prompt of the program Windows shows that question with (PickerHost.exe),
              whichever program it is about; that program also shows the file picker of a Store app.
              The answer names the folder the job started in (cwd=): an app can behave differently from another
              folder. A program started with --exe or --lnk that is gone within moments is reported with its exit code.
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
        var scripts = new List<string>();
        var started = 0; // the shell's pid, once it runs
        try
        {
            psi = Shell.Build(a.Get("shell") ?? "powershell", command, cwd, ctx.Runtime, a.Flag("as-file") ? a.Positionals : null, scripts.Add);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ctx.Fail("exec: " + ex.Message);
        }

        try
        {
            var squeeze = new Squeezer();
            var stderr = new StringBuilder();
            var notes = new StringBuilder();
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
            }, a.GetDuration("timeout"), ctx.Ct, onStarted: process => started = process.Id, onNote: n => notes.Append(n));
            var errorText = PsErrors.ToPlainText(stderr.ToString());
            if (errorText.Length > 0) ctx.Err.Write(new Squeezer().Apply(errorText));
            if (notes.Length > 0) ctx.Err.Write(notes.ToString()); // after the error text: PowerShell's error format would swallow it

            // A crash exit code reads better with its meaning next to it.
            if (Native.ProcessDetails.IsFailureCode((uint)code)) ctx.Err.WriteLine($"[exit code {Native.ProcessDetails.ExitCodeName((uint)code)}]");
            SystemPrompts.Warn(ctx.Err);
            return code;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ctx.Fail($"exec: cannot start {psi.FileName}: {ex.Message}");
        }
        finally
        {
            // Not while a program the script left running may still read it (a script that starts itself again):
            // then the file goes with the workspace when the session ends.
            if (scripts.Count > 0 && (started == 0 || Shell.LeftRunning(started, 0).Count == 0)) Shell.DeleteScripts(scripts);
        }
    }

    private static Task<int> StartAsync(ToolContext ctx, string[] args)
    {
        return StartCoreAsync(ctx, args);
    }

    /// <summary>
    /// <c>start --replace</c>: closes every running instance of a program the way a person would, by closing its
    /// windows, and waits for the processes to end. False when one is still there afterwards: nothing is killed.
    /// </summary>
    private static async Task<(bool ok, string report)> CloseRunningAsync(ToolContext ctx, string image, TimeSpan wait)
    {
        wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 300));
        image = Path.GetFileName(image.Trim('"')); // a path is taken as the program's file name
        var bare = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
        if (bare.Length == 0) return (false, "--replace needs the image name of the program to close first (MyApp.exe).");

        // The instances on this desktop: one in another logon session (a service, another user) has no window
        // here and is not this session's to close.
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var all = Native.ProcessSnapshot.Take().Where(p => p.Name.Equals(bare + ".exe", StringComparison.OrdinalIgnoreCase) && p.Pid != Environment.ProcessId).ToList();
        var running = all.Where(p => p.SessionId == session).ToList();
        var elsewhere = all.Count > running.Count ? $" ({all.Count - running.Count} more run in another logon session and were left alone)" : "";
        if (running.Count == 0) return (true, $"  no {bare}.exe was running here{elsewhere}");

        var windows = UiTools.EnumerateWindows().Where(w => w.Visible && w.Title.Length > 0 && running.Any(p => p.Pid == w.Pid)).ToList();
        if (windows.Any(w => UiTools.IsOwnWindow(w.Hwnd)))
            return (false, $"--replace: {bare}.exe is what shows agentprobe's own window on the target; that is not closed remotely.");
        var windowless = running.Where(p => windows.All(w => w.Pid != p.Pid)).ToList();
        if (windowless.Count > 0)
            return (false, $"--replace: {bare}.exe (pid {string.Join(", ", windowless.Select(p => p.Pid))}) has no window to close, so it cannot be asked to end like a person would; `kill --name {bare}.exe` ends it by force.");

        ctx.Runtime.Audit.Write($"start --replace: closing the windows of {bare}.exe (pid {string.Join(", ", running.Select(p => p.Pid))})");
        var refused = windows.Where(w => !UiTools.Close(w.Hwnd)).Select(w => w.Pid).Distinct().ToList();
        if (refused.Count > 0)
            return (false, $"--replace: Windows did not let agentprobe ask {bare}.exe (pid {string.Join(", ", refused)}) to close: it runs with higher rights than agentprobe on the target (run agentprobe elevated there).");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<int> still;
        do
        {
            await Task.Delay(250, ctx.Ct);
            var now = Native.ProcessSnapshot.Take();
            still = running.Where(p => now.Any(x => x.Pid == p.Pid && x.CreateTime == p.CreateTime)).Select(p => p.Pid).ToList();
        }
        while (still.Count > 0 && sw.Elapsed < wait);

        return still.Count == 0
            ? (true, $"  closed {bare}.exe (pid {string.Join(", ", running.Select(p => p.Pid))}) by closing its window{(running.Count == 1 ? "" : "s")}; it ended after {Format.Duration(sw.Elapsed)}")
            : (false, $"--replace: {bare}.exe (pid {string.Join(", ", still)}) was asked to close and is still running after {Format.Duration(sw.Elapsed)} (a question about unsaved changes? `windows` shows what is open). It was not killed; `kill --name {bare}.exe` does that.");
    }

    /// <summary><c>--env NAME=VALUE</c>, any number of times. Null (after saying why) when one of them is not of that form.</summary>
    public static List<(string name, string value)>? ParseEnv(ToolContext ctx, string tool, ArgParser a)
    {
        var env = new List<(string, string)>();
        foreach (var pair in a.GetAll("env"))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                ctx.Fail($"{tool}: --env wants NAME=VALUE, got '{pair}'.");
                return null;
            }

            env.Add((pair[..eq], pair[(eq + 1)..]));
        }

        return env;
    }

    private static async Task<int> StartCoreAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "as-file", "dismiss-firewall");
        var cwd = a.Get("cwd") is { } c ? ctx.Runtime.ResolvePath(c) : null;
        if (cwd != null && !Directory.Exists(cwd)) return ctx.Fail($"start: the folder {cwd} does not exist on the target.");
        var env = ParseEnv(ctx, "start", a);
        if (env == null) return 2;
        if ((a.Has("replace") || a.Flag("dismiss-firewall")) && !(a.Has("exe") || a.Has("lnk")))
            return ctx.Fail("start: --replace and --dismiss-firewall go with --exe or --lnk (a program that is started itself).");
        if (a.Has("replace") && string.IsNullOrWhiteSpace(a.Get("replace"))) return ctx.Fail("start: --replace needs the image name of the program to close first (MyApp.exe).");
        if (a.Has("replace-wait") && a.GetDuration("replace-wait") == null) return ctx.Fail($"start: --replace-wait wants a time like 15s, got '{a.Get("replace-wait")}'.");

        // Only once the program to start is known to be there: a mistyped path must not leave the app closed.
        async Task<string?> ReplaceFirstAsync()
        {
            if (a.Get("replace") is not { } image) return null;
            var (closedAll, report) = await CloseRunningAsync(ctx, image, a.GetDuration("replace-wait") ?? TimeSpan.FromSeconds(15));
            if (!closedAll) return "start: " + report + " Nothing was started.";
            ctx.Out.WriteLine(report);
            return null;
        }

        var promptsBefore = a.Flag("dismiss-firewall") ? FirewallTools.PromptPids() : null;
        var envNote = env.Count > 0 ? "  env: " + string.Join(' ', env.Select(e => $"{e.name}={Format.Clip(e.value, 60)}")) : "";
        try
        {
            Job job;
            var direct = a.Has("exe") || a.Has("lnk"); // the job is the program itself, not a shell
            if (a.Get("lnk") is { } lnk)
            {
                var shortcut = ctx.Runtime.ResolvePath(lnk);
                if (!File.Exists(shortcut)) return ctx.Fail($"start: {shortcut} does not exist on the target.");
                var link = Native.ShellLink.Read(shortcut, out var why);
                if (link == null) return ctx.Fail($"start: cannot read the shortcut {shortcut}: {why}.");
                if (link.Target.Length == 0)
                    return ctx.Fail($"start: the shortcut {shortcut} does not point to a file (a Store app, a web link or a shell folder): nothing was started.");
                if (link.Advertised)
                    return ctx.Fail($"start: {shortcut} is a shortcut made by Windows Installer: it holds no program path (Windows works it out when the shortcut is used), so nothing was started. Give the program itself: start --exe <path>.");
                if (!File.Exists(link.Target))
                    return ctx.Fail($"start: the shortcut {shortcut} points to {link.Target}, which does not exist on the target: nothing was started.");
                if (!link.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return ctx.Fail($"start: the shortcut {shortcut} points to {link.Target}, which is not a program (.exe): nothing was started.");

                // Explorer starts a shortcut that names no "Start in" folder in a folder of its own choosing; here
                // it is the program's folder, and the answer says so.
                var startIn = cwd ?? (link.WorkingDirectory.Length > 0 ? link.WorkingDirectory : null);
                if (startIn != null && !Directory.Exists(startIn))
                    return ctx.Fail($"start: the shortcut's \"Start in\" folder {startIn} does not exist on the target: nothing was started.");
                if (await ReplaceFirstAsync() is { } notReplaced) return ctx.Fail(notReplaced);
                job = ctx.Runtime.Jobs.StartExe(link.Target, a.Positionals, startIn, a.Get("name"), env, link.Arguments);
                var folderNote = cwd != null ? " (--cwd, not the shortcut's)"
                    : link.WorkingDirectory.Length > 0 ? " (the shortcut's \"Start in\")"
                    : " (the shortcut names no \"Start in\" folder: the program's own folder was used, Explorer may use another)";
                var shownArgs = job.Process.StartInfo.Arguments is { Length: > 0 } raw ? raw : string.Join(' ', a.Positionals.Select(JobManager.QuoteArgument));
                ctx.Runtime.Audit.Write($"start --lnk: {shortcut} starts {link.Target}{(shownArgs.Length > 0 ? " " + Format.Clip(shownArgs, 200) : "")} in {job.Process.StartInfo.WorkingDirectory} (job {job.Id}, pid {job.Pid})");
                ctx.Out.WriteLine($"job {job.Id} started (pid {job.Pid}): {Path.GetFileName(link.Target)}  cwd={job.Process.StartInfo.WorkingDirectory}{folderNote}");
                ctx.Out.WriteLine($"  shortcut target {link.Target}" + (shownArgs.Length > 0 ? "  args: " + Format.Clip(shownArgs, 200) : "") + envNote);
            }
            else if (a.Get("exe") is { } exe)
            {
                var path = ctx.Runtime.ResolvePath(exe);
                if (!File.Exists(path)) return ctx.Fail($"start: {path} does not exist on the target.");
                if (await ReplaceFirstAsync() is { } notReplaced) return ctx.Fail(notReplaced);
                job = ctx.Runtime.Jobs.StartExe(path, a.Positionals, cwd, a.Get("name"), env);
                ctx.Out.WriteLine($"job {job.Id} started (pid {job.Pid}): {Path.GetFileName(path)}  cwd={job.Process.StartInfo.WorkingDirectory}" +
                                  (a.Positionals.Count > 0 ? "  args: " + Format.Clip(string.Join(' ', a.Positionals.Select(JobManager.QuoteArgument)), 200) : "") + envNote);
            }
            else
            {
                var command = a.Get("script") ?? string.Join(' ', a.Positionals);
                if (string.IsNullOrWhiteSpace(command)) return ctx.Fail("start: no command given.");
                job = ctx.Runtime.Jobs.Start(a.Get("shell") ?? "powershell", command, cwd ?? ctx.Runtime.WorkDir, a.Get("name"), a.Flag("as-file") ? a.Positionals : null, env);
                ctx.Out.WriteLine($"job {job.Id} started (pid {job.Pid})  cwd={job.Process.StartInfo.WorkingDirectory}{envNote}");
            }

            // A shell that only launched something is done within moments: say so now, instead of leaving a job
            // that looks like the app but is not. A program that was started itself and is gone within moments
            // did not start: its exit code is the first clue, so it is given right away.
            // (The process is asked directly: the job's own exit code is set only after its output has been read,
            // and an app the shell launched keeps that output open.)
            if (promptsBefore != null)
            {
                FirewallTools.DismissNewPrompts(ctx.Runtime, job, promptsBefore, TimeSpan.FromSeconds(60));
                ctx.Out.WriteLine($"  a new prompt of PickerHost.exe (the Windows Firewall question) within 60s, while the job runs, is closed like its Cancel (127.0.0.1 keeps working, other PCs cannot connect): `logs {job.Id}` says when");
            }

            await Task.WhenAny(job.Process.WaitForExitAsync(ctx.Ct), Task.Delay(direct ? 1500 : 2500, ctx.Ct));
            if (job.Process.HasExited)
            {
                var code = Native.ProcessDetails.ExitCodeName((uint)job.Process.ExitCode);
                ctx.Err.WriteLine(direct
                    ? $"[job {job.Id} already ended with exit {code}. `logs {job.Id}` shows what it wrote" +
                      (job.Process.ExitCode == 0 ? ".]" : "; if that explains nothing: `deps <exe>`, and `deps <exe> --run` for a .NET app.]")
                    : $"[job {job.Id} already ended with exit {code}. If it launched an app with a window, the app is NOT this job: " +
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
