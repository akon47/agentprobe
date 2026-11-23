using System.Diagnostics;
using AgentProbe.Util;

namespace AgentProbe.Tools;

internal static class SessionTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("history", "Session", "What was run on this target, and where it stopped",
            """
            history [--last 20]
              One line per command: run number, start time, duration, exit code, command.
              After a reboot, or an update of agentprobe on that PC, the earlier commands are still listed
              ("before reboot" / "cut off by reboot", "before the update"); their output is not kept.
              "running" = still going; "orphaned" = it finished (or is still running) after the connection dropped,
              so its result was never delivered. Fetch that result with `result <#>`.
              Use this first after a reconnect to see how far the previous work got.
            """, HistoryAsync) { Quiet = true },
        new("result", "Session", "Output and exit code of an earlier run (e.g. one cut off by a disconnect)",
            "result <run#> [--tail N] [--grep REGEX]", ResultAsync) { Quiet = true },
        new("reboot", "Session", "Reboot the target and come back automatically",
            """
            reboot [--wait] [--timeout 10m] [--delay 5]
              Registers a one-shot autostart, reboots, and reconnects when the same user logs on again.
              --wait blocks until the target is back (or the timeout passes) and reports how long it took.
              Unattended PCs need Windows auto-logon, otherwise someone has to log on after the reboot.
              Background jobs do not survive a reboot; the session workspace and trust do.
            """, RebootAsync),
        new("selfupdate", "Session", "Replace agentprobe on the target with a pushed exe and resume (run by `-t <name> update`)",
            """
            selfupdate <pushed-exe> [--force]
              Not normally called by hand: `agentprobe -t <name> update` pushes this PC's exe and runs this.
              The target restarts agentprobe from the new exe in a new window. Approval and the workspace carry over;
              settings that tools changed are reverted; commands and background jobs that are running are no longer
              followed, which is why --force is needed while there are any.
            """, SelfUpdateAsync),
        new("end", "Session", "End the session: stop jobs, delete the workspace, close agentprobe on the target",
            """
            end [--remove-exe]
              --remove-exe also deletes agentprobe.exe from the target. The activity log stays: it belongs to that PC's owner.
            """, EndAsync),
    ];

    private static Task<int> HistoryAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var runs = ctx.Runtime.History().Where(r => r.Tool is not ("history" or "result")).TakeLast(a.GetInt("last", 20)).ToList();
        if (runs.Count == 0)
        {
            ctx.Out.WriteLine("nothing has been run in this session yet");
            return Task.FromResult(0);
        }

        var t = new Table(">#", "START", ">TOOK", "RESULT", "COMMAND");
        foreach (var r in runs)
        {
            var result = r.ExitCode is { } code
                ? (r.Orphaned ? $"orphaned exit {code}" : $"exit {code}")
                : r.BeforeReboot ? (r.Restart == "update" ? (r.Tool.StartsWith("selfupdate", StringComparison.Ordinal) ? "updated" : "no longer followed after the update")
                    : r.Tool.StartsWith("reboot", StringComparison.Ordinal) ? "rebooted" : "cut off by reboot") : "running";
            if (r.BeforeReboot && r.ExitCode != null) result += r.Restart == "update" ? " (before the update)" : " (before reboot)";
            t.Add(r.No, r.StartUtc.ToLocalTime().ToString("HH:mm:ss"), Format.Duration((r.EndUtc ?? DateTime.UtcNow) - r.StartUtc), result, Format.Clip(r.CommandLine, 110));
        }

        ctx.Out.Write(t.ToString());
        var jobs = ctx.Runtime.Jobs.List().Count(j => j.ExitCode == null);
        if (jobs > 0) ctx.Out.WriteLine($"{jobs} background job(s) still running (see `jobs`)");
        return Task.FromResult(0);
    }

    private static Task<int> ResultAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        if (a.Positionals.Count == 0 || !int.TryParse(a.Positionals[0].TrimStart('#'), out var no)) return Task.FromResult(ctx.Fail("result: which run number? (see `history`)"));
        var run = ctx.Runtime.FindRun(no);
        if (run == null) return Task.FromResult(ctx.Fail($"result: no run #{no} in this session (history keeps the last 300)."));

        if (run.BeforeReboot)
        {
            ctx.Out.WriteLine(run.Restart == "update"
                ? $"[run #{run.No} ran before agentprobe was updated on that PC: {(run.ExitCode is { } u ? $"exit {u}" : "it was still running then, and is not followed any more")}. Its output was not kept across the update.]"
                : $"[run #{run.No} ran before the reboot: {(run.ExitCode is { } c ? $"exit {c}" : "it was cut off by the reboot")}. Its output was not kept across the reboot.]");
            return Task.FromResult(run.ExitCode ?? 0);
        }

        var text = TextFilter.Apply(run.Output(), a.Get("grep"), null, a.Has("tail") ? a.GetInt("tail", 50) : null, ctx.Err);
        ctx.Out.Write(text);
        if (text.Length > 0 && !text.EndsWith('\n')) ctx.Out.WriteLine();
        var state = run.ExitCode is { } code ? $"exit {code} after {Format.Duration(run.EndUtc!.Value - run.StartUtc)}" : $"still running ({Format.Duration(DateTime.UtcNow - run.StartUtc)})";
        ctx.Out.WriteLine($"[run #{run.No}: {state}{(run.OutputChars > 256 * 1024 ? "; only the last 256K of output is kept" : "")}]");
        return Task.FromResult(run.ExitCode ?? 0);
    }

    private static Task<int> RebootAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "wait", "force");
        if (ctx.Runtime.PrepareReboot == null) return Task.FromResult(ctx.Fail("reboot: not available here."));
        var delay = Math.Clamp(a.GetInt("delay", 5), 3, 600);
        string how;
        try
        {
            how = ctx.Runtime.PrepareReboot();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(ctx.Fail("reboot: could not register the resume step: " + ex.Message));
        }

        using var p = Process.Start(new ProcessStartInfo("shutdown.exe", $"/r /t {delay} /c \"agentprobe: reboot requested by the debugging session\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        p!.WaitForExit(10000);
        if (p.ExitCode != 0)
        {
            ctx.Runtime.RebootPending = false;
            Target.Resume.Unregister();
            return Task.FromResult(ctx.Fail($"reboot: shutdown.exe failed with exit code {p.ExitCode}."));
        }

        ctx.Runtime.RebootDeadlineUtc = DateTime.UtcNow.AddSeconds(delay + 180);
        ctx.Runtime.Audit.Write($"reboot requested; resume via {how}");
        ctx.Out.WriteLine($"rebooting in {delay}s; resume registered as a {how}");
        return Task.FromResult(0);
    }

    private static async Task<int> SelfUpdateAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "force");
        if (ctx.Runtime.UpdateTo == null) return ctx.Fail("selfupdate: not available here (only inside a shared session).");
        if (a.Positionals.Count == 0) return ctx.Fail("selfupdate: which exe? This is normally run by `agentprobe -t <name> update`.");
        var path = ctx.Runtime.ResolvePath(a.Positionals[0]);
        if (!File.Exists(path)) return ctx.Fail($"selfupdate: {path} does not exist on the target.");
        var version = await Controller.Updater.ReportedVersionAsync(path, ctx.Ct);
        if (version == null) return ctx.Fail($"selfupdate: {path} does not run as agentprobe on this PC. Nothing was changed.");

        // Whatever runs in this session is tied to this process and cannot move to the new one.
        var jobs = ctx.Runtime.Jobs.List().Count(j => j.ExitCode == null);
        var runs = ctx.Runtime.History().Count(r => r.ExitCode == null && !r.BeforeReboot && r.Tool != "selfupdate");
        if ((jobs > 0 || runs > 0) && !a.Flag("force"))
        {
            ctx.Err.WriteLine($"selfupdate: {runs} command(s) and {jobs} background job(s) are running in this session. After the restart they are no longer followed " +
                              "(they keep running, but their output and exit codes are lost). Wait for them or stop them, or pass --force. Nothing was changed.");
            return 4; // held back: --force goes ahead
        }

        try
        {
            var how = ctx.Runtime.UpdateTo(path, version);
            ctx.Out.WriteLine(how + (jobs > 0 || runs > 0 ? $" NO LONGER FOLLOWED: {runs} command(s) and {jobs} background job(s) that were running; they run on, without their output or exit codes." : ""));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ctx.Fail("selfupdate: " + ex.Message);
        }
    }

    private static Task<int> EndAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "remove-exe");
        if (ctx.Runtime.RequestEnd == null) return Task.FromResult(ctx.Fail("end: not available here."));
        var remove = a.Flag("remove-exe");
        ctx.Runtime.Audit.Write("session ended by the developer PC" + (remove ? " (removing the exe)" : ""));
        ctx.Out.WriteLine("session ended: jobs stopped, workspace removed" + (remove ? ", exe deleted" : "") + $". Activity log kept on the target: {ctx.Runtime.Audit.FilePath}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(400); // let this reply get out first
            ctx.Runtime.RequestEnd(remove);
        });
        return Task.FromResult(0);
    }
}
