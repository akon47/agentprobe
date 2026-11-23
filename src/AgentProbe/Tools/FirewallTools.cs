using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Target;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// The Windows Firewall prompt ("allow this app on the network?") stops a test until the person at the PC answers,
/// and Windows shields that dialog from automation: its buttons cannot be read or pressed, even by an elevated
/// program. So the two answers are produced directly instead. "Allow" is an inbound rule for the program, added
/// for the length of the session (best added before the program starts, then no prompt appears at all). "Cancel"
/// is closing the prompt, which leaves the program reachable on 127.0.0.1 only.
/// </summary>
internal static class FirewallTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("firewall", "System", "Answer the Windows Firewall prompt: allow a program in, or close the prompt",
            """
            firewall allow <exe>     (elevated target) let other PCs connect to that program, as "Allow" in the prompt
                                     would. Run it BEFORE you start a server you pushed and the prompt never appears.
                                     A block that an earlier dismissed prompt left for that path is switched off too.
                                     Both are put back when the session ends.
            firewall dismiss [pid]   close the prompt that `windows` lists as SYSTEM PROMPT with that pid, as its
                                     "Cancel" would: nothing is allowed. Windows does not let its buttons be pressed
                                     remotely, so the prompt's process is ended instead. The pid is the PROMPT's
                                     (PickerHost.exe), not the app's; without a pid every such prompt on the screen
                                     is closed. `start --dismiss-firewall --exe ...` does it by itself.
            firewall show <exe>      the inbound rules that exist for that program, and what they do
              Without an allow rule a program still answers on 127.0.0.1; other PCs cannot reach it.
            """, FirewallAsync),
    ];

    private static async Task<int> FirewallAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        if (verb is not ("allow" or "dismiss" or "show") || (a.Positionals.Count < 2 && verb != "dismiss"))
            return ctx.Fail("firewall: usage: firewall allow <exe> | firewall dismiss [pid] | firewall show <exe>   (see `agentprobe help firewall`)");

        if (verb == "dismiss") return a.Positionals.Count > 1 ? Dismiss(ctx, a.Positionals[1]) : DismissAll(ctx);

        var path = ctx.Runtime.ResolvePath(a.Positionals[1]);
        if (!File.Exists(path)) return ctx.Fail($"firewall {verb}: {path} does not exist on the target.");
        var (found, rules) = await RulesAsync(ctx, path);
        if (!found) return ctx.Fail($"firewall {verb}: could not read the firewall rules: {rules.FirstOrDefault().name}");

        if (verb == "show")
        {
            if (rules.Count == 0)
            {
                ctx.Out.WriteLine($"no inbound rule for {path}: other PCs cannot connect to it (127.0.0.1 works), and Windows asks the person at the PC the first time it listens.");
                return 0;
            }

            var t = new Table("ACTION", "ENABLED", "RULE");
            foreach (var r in rules) t.Add(r.block ? "block" : "allow", r.enabled ? "yes" : "no", Format.Clip(r.display, 90));
            ctx.Out.Write(t.ToString());
            if (rules.Any(r => r.block && r.enabled)) ctx.Out.WriteLine("An enabled block rule wins over any allow rule for the same program.");
            return 0;
        }

        // allow
        if (!Environment.IsPrivilegedProcess)
            return ctx.Fail("firewall allow: changing firewall rules needs an elevated target (restart agentprobe there with Run as administrator). Nothing was changed.");

        var name = RuleName(path);
        var (code, text) = await RunAsync(ctx, "netsh.exe", $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{path}\" enable=yes profile=any");
        if (code != 0) return ctx.Fail($"firewall allow: the rule could not be added (netsh exit {code}): {text.Trim()}");
        ctx.Runtime.AddUndo($"firewall rule for {Path.GetFileName(path)}", () => RunBlocking("netsh.exe", $"advfirewall firewall delete rule name=\"{name}\""));
        ctx.Runtime.Audit.Write($"firewall: allowed inbound connections to {path} for this session");
        ctx.Out.WriteLine($"allowed inbound connections to {path} (rule \"{name}\"); removed again when the session ends");

        // A block for the same program beats the new rule: switch it off for the session.
        foreach (var r in rules.Where(r => r.block && r.enabled))
        {
            var id = r.name.Replace("'", "''");
            var (off, why) = await RunAsync(ctx, "powershell.exe", $"-NoProfile -NonInteractive -Command \"Disable-NetFirewallRule -Name '{id}'\"");
            if (off != 0)
            {
                ctx.Err.WriteLine($"[a block rule for this program (\"{r.display}\") could not be switched off: {why.Trim()} It still wins: other PCs cannot connect.]");
                continue;
            }

            ctx.Runtime.AddUndo($"block rule \"{r.display}\" (switched off)", () => RunBlocking("powershell.exe", $"-NoProfile -NonInteractive -Command \"Enable-NetFirewallRule -Name '{id}'\""));
            ctx.Runtime.Audit.Write($"firewall: switched off the block rule \"{r.display}\" for this session");
            ctx.Out.WriteLine($"switched off the block rule \"{r.display}\" that an earlier prompt left; switched on again when the session ends");
        }

        foreach (var p in SystemPrompts.Find().Where(p => p.Process.Equals("PickerHost.exe", StringComparison.OrdinalIgnoreCase)))
            ctx.Out.WriteLine($"A prompt is still on the screen: {p.Describe()}. If it is the firewall's, close it with: firewall dismiss {p.Pid}");
        return 0;
    }

    /// <summary>The prompts on the screen that the firewall's host program shows (it also shows a packaged app's file picker: the title tells).</summary>
    private static List<SystemPrompt> Prompts() => SystemPrompts.Find().Where(p => p.Process.Equals("PickerHost.exe", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The pids of those prompts, to tell later which ones are new.</summary>
    public static HashSet<int> PromptPids() => Prompts().Select(p => p.Pid).ToHashSet();

    /// <summary>Ends the prompt's process, which is what closes it. Null when it is gone, else why not.</summary>
    private static string? Close(TargetRuntime runtime, SystemPrompt prompt)
    {
        try
        {
            using var p = Process.GetProcessById(prompt.Pid);
            p.Kill();
            p.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Windows did not let agentprobe close {prompt.Describe()}: {ex.Message}";
        }

        runtime.Audit.Write($"firewall: closed the prompt {prompt.Describe()}");
        return SystemPrompts.Find().Any(p => p.Pid == prompt.Pid) ? $"{prompt.Describe()} is still there." : null;
    }

    private const string AfterClosing = "Nothing was allowed and no rule was made, so Windows may ask again when that program listens the next time (`firewall allow <exe>` first prevents that on an elevated target; `start --dismiss-firewall` closes the next one by itself).";

    private static int Dismiss(ToolContext ctx, string pidText)
    {
        if (!int.TryParse(pidText, out var pid)) return ctx.Fail("firewall dismiss: give the pid that `windows` shows for the SYSTEM PROMPT, or no pid to close every firewall prompt.");
        var prompt = SystemPrompts.Find().FirstOrDefault(p => p.Pid == pid);
        if (prompt == null)
        {
            // The usual slip is to give the pid of the app the prompt is about: say which pids the prompts have.
            var showing = Prompts();
            return ctx.Fail($"firewall dismiss: pid {pid} shows no system dialog right now. Nothing was closed. " + (showing.Count == 0
                ? "No prompt of the firewall's host program is on the screen (see `windows`)."
                : $"The pid to give is the prompt's own, not the app's: {string.Join("; ", showing.Select(p => p.Describe()))}. `firewall dismiss {showing[0].Pid}`, or `firewall dismiss` alone for all of them."));
        }

        if (!prompt.Process.Equals("PickerHost.exe", StringComparison.OrdinalIgnoreCase))
            return ctx.Fail($"firewall dismiss: {prompt.Describe()} is not shown by the program that shows the firewall prompt. Nothing was closed.");
        if (Close(ctx.Runtime, prompt) is { } problem) return ctx.Fail("firewall dismiss: " + problem);
        ctx.Out.WriteLine($"closed the prompt {prompt.Describe()}. {AfterClosing}");
        return 0;
    }

    private static int DismissAll(ToolContext ctx)
    {
        var showing = Prompts();
        if (showing.Count == 0)
        {
            ctx.Out.WriteLine("no prompt of the firewall's host program (PickerHost.exe) is on the screen; nothing to close");
            return 0;
        }

        var failed = 0;
        foreach (var prompt in showing)
        {
            if (Close(ctx.Runtime, prompt) is { } problem)
            {
                ctx.Err.WriteLine("firewall dismiss: " + problem);
                failed++;
            }
            else ctx.Out.WriteLine($"closed the prompt {prompt.Describe()}");
        }

        if (failed < showing.Count) ctx.Out.WriteLine(AfterClosing);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>start --dismiss-firewall</c>: for a while after a program was started, closes every prompt of the
    /// firewall's host program that was not on the screen before. It runs in the background of the session; what
    /// it did goes to the activity log and into the job's own output, where `logs` shows it.
    /// </summary>
    public static void DismissNewPrompts(TargetRuntime runtime, Job job, HashSet<int> before, TimeSpan watch)
    {
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var closed = 0;
            while (sw.Elapsed < watch && !job.Process.HasExited)
            {
                await Task.Delay(500);
                if (runtime.Paused) continue; // the person at the PC said "not now": nothing is closed for them
                try
                {
                    foreach (var prompt in Prompts().Where(p => !before.Contains(p.Pid)))
                    {
                        var problem = Close(runtime, prompt);
                        if (problem == null) closed++;
                        job.Append(problem == null
                            ? $"[agentprobe: closed the prompt {prompt.Describe()}, taken to be the Windows Firewall question, {Format.Duration(sw.Elapsed)} after the start, as its Cancel would: 127.0.0.1 works, other PCs cannot connect]\n"
                            : $"[agentprobe: a prompt of PickerHost.exe (the Windows Firewall question?) appeared and could not be closed: {problem}]\n");
                        before.Add(prompt.Pid); // tried once
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // the next round looks again
                }
            }

            // The answer promised a minute: say so when the watch ended sooner, having closed nothing.
            if (closed == 0 && sw.Elapsed < watch)
                job.Append($"[agentprobe: the watch for a Windows Firewall prompt ended after {Format.Duration(sw.Elapsed)} because the job's process ended; no prompt had appeared]\n");
        });
    }

    /// <summary>One name per program path, stable so that asking twice does not add a second rule under another name.</summary>
    private static string RuleName(string path) =>
        $"agentprobe session {Path.GetFileName(path)} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())), 0, 4)}";

    /// <summary>The inbound rules for one program. Read through the firewall cmdlets: their output does not depend on the display language.</summary>
    private static async Task<(bool ok, List<(string name, string display, bool block, bool enabled)> rules)> RulesAsync(ToolContext ctx, string path)
    {
        // "No rule for that program" is an ordinary answer, not a failure: only the DONE line at the end says that
        // the rules were really read.
        var script = "$ProgressPreference='SilentlyContinue'; $filters = @(Get-NetFirewallApplicationFilter -Program '" + path.Replace("'", "''") + "' -ErrorAction SilentlyContinue); " +
                     "foreach ($f in $filters) { $r = $f | Get-NetFirewallRule; if ($r.Direction -eq 'Inbound') { 'RULE|' + $r.Name + '|' + $r.Action + '|' + $r.Enabled + '|' + $r.DisplayName } }; 'DONE'; exit 0";
        var (code, text, errors) = await RunBothAsync(ctx, "powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        var rules = new List<(string, string, bool, bool)>();
        if (code != 0 || !text.Split('\n').Any(l => l.Trim() == "DONE"))
        {
            var why = errors.Trim().Length > 0 ? errors : text;
            rules.Add((Format.Clip(why.Trim().Replace('\n', ' '), 200) is { Length: > 0 } said ? said : $"powershell exit {code}, no answer", "", false, false));
            return (false, rules);
        }

        foreach (var line in text.Split('\n'))
        {
            var f = line.Trim().Split('|', 5);
            if (f.Length == 5 && f[0] == "RULE") rules.Add((f[1], f[4], f[2].Equals("Block", StringComparison.OrdinalIgnoreCase), f[3].Equals("True", StringComparison.OrdinalIgnoreCase)));
        }

        return (true, rules);
    }

    /// <summary>Exit code and everything the program said, errors last.</summary>
    private static async Task<(int code, string text)> RunAsync(ToolContext ctx, string file, string arguments)
    {
        var (code, output, errors) = await RunBothAsync(ctx, file, arguments);
        return (code, output + errors);
    }

    /// <summary>Output and errors apart: PowerShell writes progress records to the error stream, which must not be read as an answer.</summary>
    private static async Task<(int code, string output, string errors)> RunBothAsync(ToolContext ctx, string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var (output, errors) = (new StringBuilder(), new StringBuilder());
        try
        {
            var code = await Shell.RunAsync(psi, (text, isError) =>
            {
                var sb = isError ? errors : output;
                lock (sb) sb.Append(text);
            }, TimeSpan.FromSeconds(60), ctx.Ct);
            return (code, output.ToString(), PsErrors.ToPlainText(errors.ToString()));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (1, "", $"cannot start {file}: {ex.Message}");
        }
    }

    /// <summary>For an undo step, which runs when the session ends and has nobody to report to.</summary>
    private static void RunBlocking(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
        if (p == null || !p.WaitForExit(30000) || p.ExitCode != 0) throw new InvalidOperationException($"{file} did not succeed");
    }
}
