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
            firewall dismiss --auto [off]   from now until the session ends, close every such prompt as soon as it
                                     appears (for a target without administrator rights, where `allow` is not
                                     possible and the prompt can come back at every start of the program).
                                     It closes whatever PickerHost.exe shows, a Store app's file picker included.
            firewall show <exe>      the inbound rules that exist for that program, and what they do
            firewall leftovers       the rules in this PC's local firewall rule store for programs in agentprobe
                                     workspaces. After prompts were closed, block rules for those programs were
                                     found there: a closed prompt does not take them back.
            firewall cleanup         (elevated target) remove the leftovers: rules that Windows made when it asked,
                                     or that an agentprobe session made, for a program file that is not found any more
              Without an allow rule a program still answers on 127.0.0.1; other PCs cannot reach it.
            """, FirewallAsync),
    ];

    private static async Task<int> FirewallAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "auto");
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        if (verb is not ("allow" or "dismiss" or "show" or "leftovers" or "cleanup") || (a.Positionals.Count < 2 && verb is "allow" or "show"))
            return ctx.Fail("firewall: usage: firewall allow <exe> | firewall dismiss [pid | --auto [off]] | firewall show <exe> | firewall leftovers | firewall cleanup   (see `agentprobe help firewall`)");

        if (verb == "leftovers") return ListLeftovers(ctx);
        if (verb == "cleanup") return await CleanupAsync(ctx);
        if (verb == "dismiss" && a.Has("auto") && a.Positionals.Count > 1 && int.TryParse(a.Positionals[1], out _))
            return ctx.Fail("firewall dismiss: give a pid to close that one prompt, or --auto to close them all as they appear, not both.");
        if (verb == "dismiss" && a.Has("auto")) return Auto(ctx, (a.Get("auto") ?? (a.Positionals.Count > 1 ? a.Positionals[1] : "")).Equals("off", StringComparison.OrdinalIgnoreCase));
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
        lock (ctx.Runtime.SessionFirewallRules) ctx.Runtime.SessionFirewallRules.Add(name); // this session's own: gone again at its end, so not a leftover
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
        if (SystemPrompts.Find().Any(p => p.Pid == prompt.Pid)) return $"{prompt.Describe()} is still there.";
        Interlocked.Increment(ref runtime.PromptsClosed);
        return null;
    }

    // Seen in the field: after prompts were closed this way, block rules for those programs' paths were on the
    // PC, and without administrator rights they could not be removed. So nothing here says "no rule was made".
    private const string AfterClosing = "Nothing was allowed. Windows may keep block rules for that program's path all the same (seen after prompts were closed this way; `firewall leftovers` lists them, removing them needs administrator rights), and may ask again when the program listens the next time: " +
                                        "`firewall dismiss --auto` closes every further prompt of this session, `firewall allow <exe>` prevents them on an elevated target.";

    /// <summary><c>firewall dismiss --auto</c>: a watch for the rest of the session that closes such prompts as they appear.</summary>
    private static int Auto(ToolContext ctx, bool off)
    {
        var runtime = ctx.Runtime;
        if (off)
        {
            var was = Interlocked.Exchange(ref runtime.PromptWatch, null);
            was?.Cancel();
            if (was != null) runtime.Audit.Write("firewall: prompts are no longer closed automatically");
            ctx.Out.WriteLine(was != null ? $"prompts are no longer closed automatically ({runtime.PromptsClosed} were closed in this session)" : "it was not on");
            return 0;
        }

        var watch = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref runtime.PromptWatch, watch, null) != null)
        {
            watch.Dispose();
            ctx.Out.WriteLine($"already on: {runtime.PromptsClosed} prompt{(runtime.PromptsClosed == 1 ? "" : "s")} closed in this session so far (`firewall dismiss --auto off` stops it)");
            return 0;
        }

        runtime.Audit.Write("firewall: from now on every prompt that PickerHost.exe shows (the Windows Firewall question; also the file picker of a Store app) is closed as it appears, until the session ends");

        // Reverted with everything else the session changed: at its end, and before a reboot or an update (which
        // then say so), since the watch does not carry over to the next start of agentprobe.
        runtime.AddUndo("closing firewall prompts automatically", () => Interlocked.Exchange(ref runtime.PromptWatch, null)?.Cancel());
        _ = Task.Run(async () =>
        {
            try
            {
                while (!watch.IsCancellationRequested)
                {
                    await Task.Delay(500, watch.Token);
                    try
                    {
                        foreach (var prompt in Prompts())
                        {
                            if (watch.IsCancellationRequested || runtime.Paused) break; // switched off, or the person at the PC said "not now"
                            Close(runtime, prompt);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // the next round looks again
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // switched off
            }
            finally
            {
                // However the watch ended, "already on" must not be said of one that is not running.
                Interlocked.CompareExchange(ref runtime.PromptWatch, null, watch);
            }
        });
        ctx.Out.WriteLine("from now until the session ends, every prompt that PickerHost.exe shows (the Windows Firewall question; also a Store app's file picker) is closed as it appears, like its Cancel: " +
                          "the programs stay reachable on 127.0.0.1 only. Each one is in the activity log; `firewall dismiss --auto off` stops it, `firewall leftovers` lists the rules Windows keeps.");
        return 0;
    }

    /// <summary>
    /// A firewall rule as Windows stores it, for a program in an agentprobe workspace.
    /// <c>Asked</c>: Windows made it itself when it asked the person about that program (its id says so).
    /// <c>Ours</c>: made by `firewall allow` of some agentprobe session.
    /// </summary>
    internal sealed record Leftover(string Id, string Name, string Program, bool Block, bool Enabled, bool Inbound, bool Exists, bool Asked, bool Ours);

    /// <summary>
    /// The rules of this PC's local firewall rule store for programs that lie in an agentprobe workspace folder
    /// (...\agentprobe\work-...), read from where Windows keeps them. Not the rules this session's own
    /// `firewall allow` made: those go when the session ends. Null when the store cannot be read.
    /// </summary>
    /// <param name="workspace">Only the rules for programs of that workspace folder (its name is what is compared: Windows may have stored the path in another spelling).</param>
    internal static List<Leftover>? Leftovers(TargetRuntime runtime, string? workspace = null)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            if (key == null) return null;
            var folder = workspace == null ? null : @"\agentprobe\" + Path.GetFileName(workspace.TrimEnd('\\')) + @"\";
            var found = new List<Leftover>();
            foreach (var id in key.GetValueNames())
            {
                if (key.GetValue(id) is not string rule || rule.IndexOf(@"\agentprobe\work-", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var fields = rule.Split('|').Select(f => f.Split('=', 2)).Where(f => f.Length == 2).GroupBy(f => f[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
                var program = Environment.ExpandEnvironmentVariables(fields.GetValueOrDefault("App", ""));
                var name = fields.GetValueOrDefault("Name", "");
                if (program.IndexOf(@"\agentprobe\work-", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (folder != null && program.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                lock (runtime.SessionFirewallRules)
                {
                    if (runtime.SessionFirewallRules.Contains(name)) continue;
                }

                found.Add(new Leftover(id, name, program,
                    fields.GetValueOrDefault("Action", "").Equals("Block", StringComparison.OrdinalIgnoreCase),
                    fields.GetValueOrDefault("Active", "").Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    fields.GetValueOrDefault("Dir", "").Equals("In", StringComparison.OrdinalIgnoreCase),
                    File.Exists(program),
                    id.StartsWith("TCP Query User{", StringComparison.OrdinalIgnoreCase) || id.StartsWith("UDP Query User{", StringComparison.OrdinalIgnoreCase),
                    name.StartsWith("agentprobe session ", StringComparison.Ordinal)));
            }

            return found;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int ListLeftovers(ToolContext ctx)
    {
        if (Leftovers(ctx.Runtime) is not { } rules) return ctx.Fail("firewall leftovers: the local firewall rule store of this PC could not be read.");
        if (rules.Count == 0)
        {
            ctx.Out.WriteLine("no rule for a program in an agentprobe workspace in this PC's local firewall rule store");
            return 0;
        }

        var t = new Table("ACTION", "ENABLED", "DIR", "FILE", "MADE BY", "PROGRAM");
        foreach (var r in rules.OrderBy(r => r.Program, StringComparer.OrdinalIgnoreCase).Take(60))
            t.Add(r.Block ? "block" : "allow", r.Enabled ? "yes" : "no", r.Inbound ? "in" : "out", r.Exists ? "exists" : "not found", r.Asked ? "Windows, when it asked" : r.Ours ? "an agentprobe session" : "someone else", r.Program);
        ctx.Out.Write(t.ToString());
        var removable = rules.Count(Removable);
        ctx.Out.WriteLine((rules.Count > 60 ? $"... {rules.Count - 60} more. " : "") +
                          $"{rules.Count} rule{(rules.Count == 1 ? "" : "s")} for programs in agentprobe workspaces, in this PC's local firewall rule store. " +
                          $"{removable} of them {(removable == 1 ? "is" : "are")} left over (made by Windows when it asked, or by an agentprobe session, for a program file that is not found any more). " +
                          (Environment.IsPrivilegedProcess ? "`firewall cleanup` removes those." : "Removing them needs administrator rights: `firewall cleanup` on an elevated target (restart agentprobe there with Run as administrator)."));
        return 0;
    }

    /// <summary>What `cleanup` may remove: a rule Windows made when it asked, or one of an agentprobe session, whose program file is not there any more. Never a rule someone else made.</summary>
    private static bool Removable(Leftover r) => !r.Exists && (r.Asked || r.Ours);

    private static async Task<int> CleanupAsync(ToolContext ctx)
    {
        if (Leftovers(ctx.Runtime) is not { } rules) return ctx.Fail("firewall cleanup: the local firewall rule store of this PC could not be read. Nothing was changed.");
        var dead = rules.Where(Removable).ToList();
        if (dead.Count == 0)
        {
            ctx.Out.WriteLine(rules.Count == 0 ? "no rule for a program in an agentprobe workspace; nothing to remove" : $"nothing to remove: none of the {rules.Count} rule{(rules.Count == 1 ? "" : "s")} is a leftover (`firewall leftovers` says why)");
            return 0;
        }

        if (!Environment.IsPrivilegedProcess)
            return ctx.Fail($"firewall cleanup: {dead.Count} rule{(dead.Count == 1 ? "" : "s")} could be removed, but that needs an elevated target (restart agentprobe there with Run as administrator). Nothing was changed.");

        ctx.Runtime.Audit.Write($"firewall cleanup: removing {dead.Count} leftover rules: {Format.Clip(string.Join("; ", dead.Select(r => r.Program).Distinct(StringComparer.OrdinalIgnoreCase)), 600)}");

        // The ids go to PowerShell as data, in an environment variable, never as text of the command: an id
        // holds the program's path, which is whatever someone named a folder. Each is taken literally, not as a
        // pattern (-Name reads [ ] * ? as wildcards).
        const string script = "$ProgressPreference='SilentlyContinue'; $failed = 0; foreach ($id in ($env:AGENTPROBE_RULE_IDS -split \"`n\")) { if ($id.Length -eq 0) { continue }; " +
                              "try { Remove-NetFirewallRule -Name ([System.Management.Automation.WildcardPattern]::Escape($id)) -ErrorAction Stop } catch { $failed++; 'FAILED|' + $_.Exception.Message } }; 'DONE|' + $failed; exit 0";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["AGENTPROBE_RULE_IDS"] = string.Join("\n", dead.Select(r => r.Id));
        var said = new StringBuilder();
        try
        {
            await Shell.RunAsync(psi, (text, isError) =>
            {
                if (!isError) lock (said) said.Append(text);
            }, TimeSpan.FromSeconds(120), ctx.Ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ctx.Fail($"firewall cleanup: cannot start powershell.exe: {ex.Message}. Nothing was changed.");
        }

        var why = said.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("FAILED|", StringComparison.Ordinal)).Select(l => l[7..]).Distinct().Take(2).ToList();
        var still = Leftovers(ctx.Runtime)?.Count(Removable);
        if (still == null) return ctx.Fail("firewall cleanup: the rule store could not be read again, so what was removed is not known.");
        ctx.Runtime.Audit.Write($"firewall cleanup: {dead.Count - still} of {dead.Count} rules removed");
        ctx.Out.WriteLine($"removed {dead.Count - still} of {dead.Count} leftover rules" + (still > 0 ? $"; {still} are still there" + (why.Count > 0 ? ": " + Format.Clip(string.Join("; ", why), 300) : "") : ""));
        return still == 0 ? 0 : 1;
    }

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
