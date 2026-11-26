using System.Diagnostics;
using System.Text;
using AgentProbe.Tools;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>
/// <c>agentprobe run steps.txt</c>: a list of agentprobe commands, run one after the other until one fails.
/// <para>
/// Every command an agent runs is a turn of its own, and each turn carries the whole conversation again. A test
/// that is "push the build, start it, wait for the window, press three buttons, check the status line" is eight
/// turns when typed one by one and one turn as a step file; run again after the next fix it is one more. So the
/// answer is short when all went well (one line, plus the output of the steps marked <c>show</c>), and when a
/// step fails it is that step's output with what is on the target's screen at that moment.
/// </para>
/// </summary>
internal static class Steps
{
    public const string Usage = """
        run <steps-file | ->  [--no-evidence]
          Runs the agentprobe commands in a file (or from stdin with -), one per line, in order, and stops at the
          first that fails (exit code not 0). Lines are what you would type after `agentprobe`:
              push .\bin\Release app
              start --replace MyApp.exe --exe app\MyApp.exe
              wait --window "MyApp" --timeout 30s
              ui click "Open"
              expect --ui "#status" --has Loaded
              show ui get "#result"
          Blank lines and lines starting with # are skipped. In front of a command:
              show   print this step's output (the others print nothing when they succeed)
              try    a failure of this step does not stop the run (it is counted and named at the end)
          `sleep 500ms` pauses. -t before or after `run` names the target for every step; a step's own -t wins.
          All well: one line ("ok: 6 steps in 8.2s") after the output of the `show` steps; notes agentprobe itself
          adds in [brackets] (a crash on the target, a system dialog) are kept. A failed step: its number, command
          and output, then the target's `windows` and a screenshot's path as evidence (--no-evidence leaves those
          out), and how many steps were not run. The exit code is the failed step's.
          Every line is checked before the first runs: a line that is not a command means nothing is run.
        """;

    // One at a time in a process: a run borrows the console's writers to collect what each step prints.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record Step(int No, string Text, string[] Argv, bool Show, bool Try);

    public static async Task<int> RunAsync(string[] args, string? target, TextWriter output)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(Usage.TrimEnd());
            return 0;
        }

        var evidence = !args.Contains("--no-evidence");
        var files = args.Where(a => a != "--no-evidence").ToList();
        if (files.Count != 1)
        {
            Console.Error.WriteLine("usage: agentprobe run <steps-file | ->   (see `agentprobe run --help`)");
            return 2;
        }

        string text;
        if (files[0] == "-")
        {
            text = await Console.In.ReadToEndAsync();
        }
        else if (!File.Exists(files[0]))
        {
            Console.Error.WriteLine($"run: steps file not found: {files[0]}");
            return 2;
        }
        else
        {
            text = await File.ReadAllTextAsync(files[0]);
        }

        return await RunTextAsync(text, target, output, evidence);
    }

    public static async Task<int> RunTextAsync(string text, string? target, TextWriter output, bool evidence = true)
    {
        var steps = new List<Step>();
        var lineNo = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var (step, error) = Parse(line, steps.Count + 1);
            if (step == null)
            {
                output.WriteLine($"run: line {lineNo}: {error} Nothing was run.");
                return 2;
            }

            steps.Add(step);
        }

        if (steps.Count == 0)
        {
            output.WriteLine("run: no steps (the file has only blank lines and comments). Nothing was run.");
            return 2;
        }

        await Gate.WaitAsync();
        try
        {
            return await RunStepsAsync(steps, target, output, evidence);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<int> RunStepsAsync(List<Step> steps, string? target, TextWriter output, bool evidence)
    {
        var total = Stopwatch.StartNew();
        var allowed = new List<string>();
        foreach (var step in steps)
        {
            var (code, all, notes) = await RunOneAsync(step.Argv, target);
            if (code == 0)
            {
                if (step.Show)
                {
                    output.WriteLine($"> {step.Text}");
                    if (all.Length > 0) output.WriteLine(all.TrimEnd('\n'));
                }
                else
                {
                    foreach (var note in notes) output.WriteLine($"step {step.No} ({Format.Clip(step.Text, 50)}): {note}");
                }

                continue;
            }

            if (step.Try)
            {
                allowed.Add($"step {step.No} exit {code}");
                continue;
            }

            output.WriteLine($"step {step.No} of {steps.Count} FAILED (exit {code}): {step.Text}");
            if (all.Length > 0) output.WriteLine(all.TrimEnd('\n'));

            // What the target shows at this moment, while it still shows it. Not when the target could not be
            // reached or its person said "not now": there is nothing to look at, or nothing may be run.
            if (evidence && code is not (3 or 5 or 75))
            {
                var (wCode, windows, _) = await RunOneAsync(["windows"], TargetOf(step.Argv) ?? target);
                if (wCode == 0) output.WriteLine("--- windows on the target now\n" + windows.TrimEnd('\n'));
                var (sCode, shot, _) = await RunOneAsync(["screenshot", "--scale", "0.5"], TargetOf(step.Argv) ?? target);
                var saved = shot.Split('\n').FirstOrDefault(l => l.StartsWith("saved: ", StringComparison.Ordinal));
                if (sCode == 0 && saved != null) output.WriteLine("--- screenshot of the target now: " + saved[7..].Trim() + "  (look at it only if the lines above do not explain the failure)");
            }

            var left = steps.Count - step.No;
            if (left > 0) output.WriteLine($"{left} step{(left == 1 ? " was" : "s were")} not run ({step.No + 1}{(left > 1 ? "-" + steps.Count : "")}).");
            return code;
        }

        output.WriteLine($"ok: {steps.Count} step{(steps.Count == 1 ? "" : "s")} in {Format.Duration(total.Elapsed)}" +
                         (allowed.Count > 0 ? $"; failed but allowed to (try): {string.Join(", ", allowed)}" : ""));
        return 0;
    }

    /// <summary>The target a step names for itself (-t before its command), or null.</summary>
    private static string? TargetOf(string[] argv)
    {
        for (var i = 0; i + 1 < argv.Length; i++)
        {
            if (argv[i] is "-t" or "--target") return argv[i + 1];
            if (!argv[i].StartsWith('-')) break;
        }

        return null;
    }

    /// <summary>Runs one command as the command line would, with what it prints collected instead of shown.</summary>
    private static async Task<(int code, string all, List<string> notes)> RunOneAsync(string[] argv, string? target)
    {
        if (argv.Length > 0 && argv[0] == "sleep")
        {
            await Task.Delay(ArgParser.ParseDuration(argv.Length > 1 ? argv[1] : "1s") ?? TimeSpan.FromSeconds(1));
            return (0, "", []);
        }

        if (target != null && TargetOf(argv) == null) argv = ["-t", target, .. argv];
        var all = new StringBuilder();
        var errors = new StringBuilder();
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        var outWriter = new CallbackWriter(s => all.Append(s));
        var errWriter = new CallbackWriter(s =>
        {
            all.Append(s);
            errors.Append(s);
        });
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        int code;
        try
        {
            code = await Cli.DispatchAsync(argv);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            all.Append(ex.Message).Append('\n');
            code = 1;
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }

        // What agentprobe itself adds to a command's answer is in [brackets] on the error stream: a crash that was
        // logged meanwhile, a system dialog that is waiting. Those are worth a line even when the step succeeded.
        var notes = errors.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.StartsWith('[') && l.EndsWith(']')).ToList();
        return (code, all.ToString(), notes);
    }

    private static (Step? step, string error) Parse(string line, int no)
    {
        var (show, attempt) = (false, false);
        while (true)
        {
            if (line.StartsWith("show ", StringComparison.OrdinalIgnoreCase)) (show, line) = (true, line[5..].TrimStart());
            else if (line.StartsWith("try ", StringComparison.OrdinalIgnoreCase)) (attempt, line) = (true, line[4..].TrimStart());
            else break;
        }

        var tokens = CommandLine.Tokenize(line);
        if (tokens.Count > 1 && tokens[0].text.Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);
        if (tokens.Count == 0) return (null, "no command after show/try.");

        // The command word, after any -t/--full/-o in front of it.
        var at = 0;
        while (at < tokens.Count && tokens[at].text is "-t" or "--target" or "-o" or "--out" or "--full") at += tokens[at].text == "--full" ? 1 : 2;
        if (at >= tokens.Count) return (null, $"'{line}' names no command.");
        var name = tokens[at].text.ToLowerInvariant();
        if (name == "sleep")
        {
            if (tokens.Count != at + 2 || ArgParser.ParseDuration(tokens[at + 1].text) == null) return (null, "sleep wants a time: sleep 500ms.");
            return (new Step(no, line, ["sleep", tokens[at + 1].text], show, attempt), "");
        }

        if (name is not ("push" or "compare" or "list" or "name") && ToolRegistry.Find(name) == null)
            return (null, $"'{name}' is not a command that can be a step (the commands that run on a target, push, compare, list, name and sleep are).");

        var words = tokens.Select(t => t.text).ToList();
        if (name is "exec" or "start")
        {
            // Options first; then the command text, which must reach the target's shell as written. A command
            // given as one quoted word ('Get-Process | Select -First 5', the way it is typed in a shell) is that
            // word; anything else is the rest of the line, quotes included.
            var i = at + 1;
            var whole = false; // -f, --exe, --lnk: what follows are plain arguments
            while (i < tokens.Count)
            {
                var w = tokens[i].text;
                if (w is "-f" or "--file" or "--exe" or "--lnk" or "-")
                {
                    whole = true;
                    break;
                }

                if (w is "--quiet-ok" or "--dismiss-firewall" or "--full") i++;
                else if (w is "--shell" or "--cwd" or "--timeout" or "--name" or "--env" or "--replace" or "--replace-wait" or "-t" or "--target" or "-o" or "--out") i += 2;
                else break;
            }

            if (!whole)
            {
                if (i >= tokens.Count) return (null, $"{name}: no command given.");
                var start = tokens[i].start;
                var rest = line[start..].Trim();
                var oneQuoted = i == tokens.Count - 1 && rest.Length >= 2 && rest[0] is '\'' or '"' && rest[^1] == rest[0];
                words = [.. words.Take(i), oneQuoted ? tokens[i].text : rest];
            }
        }

        return (new Step(no, line, [.. words], show, attempt), "");
    }
}
