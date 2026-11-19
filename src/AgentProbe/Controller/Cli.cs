using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentProbe.Target;
using AgentProbe.Tools;
using AgentProbe.Transport;

namespace AgentProbe.Controller;

/// <summary>The command line an AI agent (or a person) uses on the developer PC.</summary>
internal static class Cli
{
    private static readonly string[] ExecLikeTools = ["exec", "start"];
    private static bool _otherRights;

    public static async Task<int> RunAsync(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Out.NewLine = "\n"; // one byte per line break is enough for an agent
        Console.Error.NewLine = "\n";
        var command = argv[0].ToLowerInvariant();
        var rest = argv[1..];

        // Global options may also come before the command: agentprobe -t LAB1 ps
        string? target = null;
        var full = false;
        string? outPath = null;
        while (command is "-t" or "--target" or "--full" or "-o" or "--out")
        {
            if (command == "--full") full = true;
            else if (rest.Length == 0) return Fail($"{command} needs a value.");
            else
            {
                if (command is "-t" or "--target") target = rest[0];
                else outPath = rest[0];
                rest = rest[1..];
            }

            if (rest.Length == 0) return Fail("No command given. Run `agentprobe help`.");
            command = rest[0].ToLowerInvariant();
            rest = rest[1..];
        }

        var args = ExtractGlobals(command, rest, ref target, ref full, ref outPath);

        switch (command)
        {
            case "help" or "--help" or "-h" or "/?":
                return Help(args);
            case "version" or "--version":
                Console.WriteLine(AppInfo.Version);
                return 0;
            case "guide":
                Console.Write(Guide());
                return 0;
            case "setup":
            {
                var result = Setup.Run(args);
                if (result == 0 && !args.Contains("--remove") && !args.Contains("--firewall-only"))
                {
                    Console.WriteLine();
                    await Home.ShowAsync(openBrowser: false, interactive: false); // what to do next, right here
                }

                return result;
            }
            case "stop":
                return await TryConnectAsync() is { } p ? await RequestAsync(p, new PipeMsg { Op = "stop" }, "stop", true) : Say("daemon is not running");
            case "list" or "targets":
                return await DaemonAsync(new PipeMsg { Op = "list", Args = args }, "list", true);
            case "dashboard" or "home":
                return await Home.ShowAsync(openBrowser: command == "dashboard", interactive: command == "home");
            case "name":
                if (args.Length == 0) return Fail("usage: agentprobe name [-t target] <new-name>   (use \"\" to clear)");
                return await DaemonAsync(new PipeMsg { Op = "name", Target = target, Text = args[0] }, "name", true);
            case "compare":
                return await Compare.RunAsync(args);
            case "push":
            {
                if (args.Length is < 1 or > 2) return Fail("usage: agentprobe push <local-path> [remote-path]   (remote defaults to the session workspace)");
                var local = Path.GetFullPath(args[0]);
                var remote = args.Length == 2 ? args[1] : Path.GetFileName(local.TrimEnd('\\', '/'));
                return await DaemonAsync(new PipeMsg { Op = "push", Target = target, LocalPath = local, RemotePath = remote }, "push", full);
            }
        }

        if (ToolRegistry.Find(command) is not { } tool)
            return Fail($"Unknown command '{command}'. Run `agentprobe help`.");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(tool.Usage.TrimEnd());
            return 0;
        }

        var toolArgs = ExecLikeTools.Contains(tool.Name) ? BuildExecArgs(args) : args;
        if (toolArgs == null) return 2;

        // `pull` takes the local destination as its optional second argument.
        if (tool.Name == "pull" && toolArgs.Length >= 2 && outPath == null)
        {
            outPath = toolArgs[1];
            toolArgs = [toolArgs[0], .. toolArgs[2..]];
        }

        if (string.Equals(target, "local", StringComparison.OrdinalIgnoreCase))
            return await LocalRunner.RunAsync(tool, toolArgs, Console.Out, Console.Error, outPath);

        var req = new PipeMsg
        {
            Op = "run",
            Target = target,
            Tool = tool.Name,
            Args = toolArgs,
            LocalPath = Path.GetFullPath(outPath ?? "."),
        };
        if (tool.Name == "reboot")
        {
            var a = Util.ArgParser.Parse(toolArgs, "wait", "force");
            req.Wait = a.Flag("wait");
            req.TimeoutSec = (int)(a.GetDuration("timeout")?.TotalSeconds ?? 0);
        }

        return await DaemonAsync(req, tool.Name, full);
    }

    /// <summary>Pulls -t/--target, --full and -o/--out out of the arguments (for exec/start only before the command text).</summary>
    private static string[] ExtractGlobals(string command, string[] args, ref string? target, ref bool full, ref string? outPath)
    {
        var result = new List<string>();
        var execLike = ExecLikeTools.Contains(command);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (execLike && !a.StartsWith('-'))
            {
                result.AddRange(args[i..]); // the command text starts here: leave it untouched
                break;
            }

            if (a is "-t" or "--target" && i + 1 < args.Length) target = args[++i];
            else if (a.StartsWith("--target=", StringComparison.Ordinal)) target = a[9..];
            else if (a == "--full" && command is not ("dump" or "crashdumps")) full = true; // for those two it is the tool's own option
            else if (a is "-o" or "--out" && i + 1 < args.Length) outPath = args[++i];
            else result.Add(a);
        }

        return [.. result];
    }

    /// <summary>
    /// exec/start: options are only read up to the first word of the command, so
    /// <c>exec Get-Process -Name foo</c> reaches PowerShell intact. -f and - (stdin) are read here, on the developer PC.
    /// </summary>
    private static string[]? BuildExecArgs(string[] args)
    {
        var options = new List<string>();
        string? script = null;
        var i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-")
            {
                script = Console.In.ReadToEnd();
                i++;
                break;
            }

            if (!a.StartsWith('-')) break;
            if (a is "-f" or "--file")
            {
                if (++i >= args.Length || !File.Exists(args[i]))
                {
                    Console.Error.WriteLine($"exec: script file not found: {(i < args.Length ? args[i] : "(missing)")}");
                    return null;
                }

                script = File.ReadAllText(args[i]);
                continue;
            }

            if (a is "--shell" or "--cwd" or "--timeout" or "--name")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine($"{a} needs a value.");
                    return null;
                }

                options.Add(a);
                options.Add(args[++i]);
                continue;
            }

            break; // unknown dash-word: part of the command itself
        }

        script ??= string.Join(' ', args[i..]);
        if (string.IsNullOrWhiteSpace(script))
        {
            Console.Error.WriteLine("No command given. Example: agentprobe exec 'Get-Process | Select-Object -First 5'");
            return null;
        }

        options.Add("--script");
        options.Add(script);
        return [.. options];
    }

    // ---------------------------------------------------------------- daemon plumbing

    public static async Task<int> DaemonAsync(PipeMsg req, string tool, bool full, TextWriter? capture = null)
    {
        var pipe = await ConnectOrStartAsync();
        if (pipe == null)
        {
            return Fail(_otherRights
                ? "agentprobe's background process is running with different rights (started from an administrator terminal, or the other way round). Run `agentprobe stop` from that kind of terminal, then try again."
                : "Could not start the agentprobe background process. Try `agentprobe daemon` to see why.");
        }
        return await RequestAsync(pipe, req, tool, full, capture);
    }

    private static async Task<int> RequestAsync(NamedPipeClientStream pipe, PipeMsg req, string tool, bool full, TextWriter? capture = null)
    {
        await using var _ = pipe;
        var line = JsonSerializer.SerializeToUtf8Bytes(req, JsonCtx.Default.PipeMsg);
        await pipe.WriteAsync(line);
        pipe.WriteByte((byte)'\n');
        await pipe.FlushAsync();

        var limiter = new OutputLimiter(tool, full || capture != null);
        var reader = new StreamReader(pipe, Encoding.UTF8);
        var code = 75;
        var gotExit = false;
        while (await reader.ReadLineAsync() is { } l)
        {
            var ev = JsonSerializer.Deserialize(l, JsonCtx.Default.PipeMsg);
            if (ev == null) continue;
            switch (ev.Ev)
            {
                case "out":
                    if (capture != null) capture.Write(ev.Text);
                    else limiter.Write(ev.Text ?? "", false);
                    break;
                case "err":
                    if (capture != null) capture.Write(ev.Text);
                    else limiter.Write(ev.Text ?? "", true);
                    break;
                case "saved":
                    if (capture != null) capture.Write($"saved: {ev.Text}\n");
                    else limiter.Write($"saved: {ev.Text}\n", false);
                    break;
                case "exit":
                    code = ev.Code;
                    gotExit = true;
                    break;
            }

            if (gotExit) break;
        }

        limiter.Finish();
        if (!gotExit) Console.Error.WriteLine("[agentprobe daemon closed the connection]");
        return code;
    }

    private static async Task<NamedPipeClientStream?> TryConnectAsync(int timeoutMs = 250)
    {
        var pipe = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMs);
            return pipe;
        }
        catch (UnauthorizedAccessException)
        {
            // The background process exists but was started with different rights (an administrator terminal vs a normal one).
            _otherRights = true;
            await pipe.DisposeAsync();
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    public static async Task<NamedPipeClientStream?> ConnectOrStartAsync()
    {
        if (await TryConnectAsync() is { } existing) return existing;

        // Not running yet: start it detached (own hidden console, no inherited handles) and wait for its pipe.
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, "daemon") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        for (var i = 0; i < 40; i++)
        {
            if (await TryConnectAsync(250) is { } p) return p;
        }

        return null;
    }

    // ---------------------------------------------------------------- help

    private static int Help(string[] args)
    {
        if (args.Length > 0)
        {
            if (ToolRegistry.Find(args[0]) is { } t)
            {
                Console.WriteLine($"{t.Name} - {t.Summary}\n\n{t.Usage.TrimEnd()}");
                return 0;
            }

            return Fail($"No command '{args[0]}'.");
        }

        Console.Write($"""
            agentprobe {AppInfo.Version} - let an AI agent debug and test another Windows PC on your network

            ON THE PC TO TEST      copy agentprobe.exe there and double-click it (or: agentprobe share). Approve once.
            ON THE DEVELOPER PC    run the commands below; the agent does the same. First time: agentprobe setup

            Session:
              list [--all]       connected targets (--all: also ones that went offline, with their last command)
              dashboard          the same as a live web page, for people; also prints what to give an AI agent
                                 (an instructions line, the MCP command)
              home               the start screen for people: those texts with keys/clicks to copy them, and a
                                 command line (also shown when agentprobe starts without arguments in a console)
              name <new-name>    give a target a short name; -t accepts that name from then on
              setup [--remove]   one-time setup of the developer PC: adds this folder to your PATH and opens the
                                 firewall port (asks for admin once). Until then, call the exe by its full path.
              guide              how an AI agent should use this tool (workflow + cheap ways to look)
              mcp                run as an MCP server (stdio), for agents that are set up through MCP:
                                 claude mcp add agentprobe -- "<path>\agentprobe.exe" mcp
              stop               stop the background process on this PC

            Targets:  -t <name>  |  -t a,b  |  -t "LAB-*"  |  -t all  |  -t local (this PC)
                      With one connected target, -t can be omitted. Several targets: identical results are grouped.
            Output:   long output is cut to head+tail and saved to a local file; --full prints everything.
                      -o <path> chooses where files sent back (screenshot, dump, pull) are saved.

            {ToolRegistry.HelpList()}
            Details: agentprobe help <command>

            """.Replace("\r\n", "\n"));
        return 0;
    }

    public static string Guide()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent-guide.md");
        if (s == null) return "guide not embedded in this build\n";
        return new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int Say(string message)
    {
        Console.WriteLine(message);
        return 0;
    }
}

/// <summary>Runs a tool on this PC (<c>-t local</c>): same diagnostics for the developer machine, and one side of `compare`.</summary>
internal static class LocalRunner
{
    private static readonly Lazy<TargetRuntime> Runtime = new(() => new TargetRuntime(null, Environment.CurrentDirectory));

    public static async Task<int> RunAsync(Tool tool, string[] args, TextWriter output, TextWriter error, string? outPath)
    {
        if (tool.Name is "reboot" or "end") return Write(error, $"{tool.Name} is not available for -t local.");

        // Every -t local call is a new process, so anything that remembers state between calls cannot work here.
        var stateful = tool.Name is "watch" or "dbg" or "trace" or "start" or "jobs" or "logs" or "history" or "result" or "crashdumps" or "wpr"
                       || (tool.Name == "read" && args.Contains("--new"))
                       || (tool.Name == "envsnap" && (args.Contains("--save") || args.Contains("--diff")))
                       || (tool.Name == "kill" && args.Length > 0 && !args[0].StartsWith('-'));
        if (stateful)
            return Write(error, $"`{string.Join(' ', new[] { tool.Name }.Concat(args.Take(2)))}` keeps state between calls and needs a real session: -t local runs each command in a fresh process. " +
                                "Share this PC (agentprobe share) and target it by name instead.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var ctx = new ToolContext
        {
            Out = output,
            Err = error,
            Ct = cts.Token,
            Runtime = Runtime.Value,
            SendFile = (name, path, zip, delete) =>
            {
                var dest = Path.GetFullPath(outPath ?? ".");
                if (zip)
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(path, dest, true);
                }
                else
                {
                    if (Directory.Exists(dest)) dest = Path.Combine(dest, name);
                    if (!string.Equals(Path.GetFullPath(path), dest, StringComparison.OrdinalIgnoreCase)) File.Copy(path, dest, true);
                }

                if (delete) File.Delete(path);
                output.WriteLine($"saved: {dest}");
                return Task.CompletedTask;
            },
        };

        try
        {
            return await tool.Run(ctx, args);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static int Write(TextWriter w, string message)
    {
        w.WriteLine(message);
        return 2;
    }
}

/// <summary>
/// One-time setup of the developer PC: put this exe's folder on the user's PATH (so an agent can simply type
/// <c>agentprobe</c>) and open the firewall port targets dial in to. <c>setup --remove</c> undoes both.
/// </summary>
internal static partial class Setup
{
    private const string RuleName = "agentprobe";

    public static int Run(string[] args)
    {
        var remove = args.Contains("--remove");
        if (args.Contains("--firewall-only")) return Firewall(remove); // the elevated child started below

        var code = 0;
        if (!args.Contains("--no-path")) Console.WriteLine(UserPath.Update(Path.GetDirectoryName(AppInfo.ExePath)!, remove));
        if (args.Contains("--path-only")) return 0;

        if (Environment.IsPrivilegedProcess)
        {
            code = Firewall(remove);
        }
        else
        {
            Console.WriteLine(remove ? "Removing the firewall rule (asking for administrator rights)..." : $"Opening TCP/UDP {AppInfo.Port} for incoming target PCs (asking for administrator rights)...");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(AppInfo.ExePath, "setup --firewall-only" + (remove ? " --remove" : "")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit();
                code = p?.ExitCode ?? 1;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                code = 1; // UAC declined
            }
        }

        if (code != 0)
        {
            Console.Error.WriteLine("Firewall not changed (administrator rights were declined). Targets on the same network may not find this PC;");
            Console.Error.WriteLine("they can still connect when started as: agentprobe share <address-of-this-pc>, if the port is reachable.");
            return 1;
        }

        Console.WriteLine(remove ? "Firewall rule removed." : "Firewall rule added. Now run agentprobe.exe on the PC you want to test; it will find this PC by itself.");
        return 0;
    }

    private static int Firewall(bool remove)
    {
        Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (remove) return 0;
        var ok = Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={AppInfo.Port} profile=any") &
                 Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport={AppInfo.Port} profile=any");
        return ok ? 0 : 1;
    }

    private static bool Netsh(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
        p!.WaitForExit();
        return p.ExitCode == 0;
    }
}

/// <summary>The current user's PATH (HKCU\Environment). Edited entry by entry; everything else is left exactly as it was.</summary>
internal static partial class UserPath
{
    private static string Normalize(string dir) => Environment.ExpandEnvironmentVariables(dir).Trim().Trim('"').TrimEnd('\\');

    /// <summary>True when <paramref name="dir"/> is on the user or machine PATH as stored in the registry (not this process's copy).</summary>
    public static bool Contains(string dir)
    {
        var wanted = Normalize(dir);
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var value = Environment.GetEnvironmentVariable("PATH", scope) ?? "";
            if (value.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(e => Normalize(e).Equals(wanted, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    public static string Update(string dir, bool remove)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment");
        var kind = key.GetValue("Path") != null ? key.GetValueKind("Path") : Microsoft.Win32.RegistryValueKind.ExpandString;
        var current = key.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        // Keep every existing entry byte for byte (empty ones included), so adding and later removing restores the exact value.
        var entries = current.Length == 0 ? [] : current.Split(';').ToList();
        var wanted = Normalize(dir);
        var index = entries.FindIndex(e => e.Length > 0 && Normalize(e).Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (remove)
        {
            if (index < 0) return "PATH: this folder was not on your PATH; nothing to remove.";
            entries.RemoveAt(index);
        }
        else
        {
            if (index >= 0 || Contains(dir)) return $"PATH: {wanted} is already on your PATH.";
            if (entries.Count > 0 && entries[^1].Length == 0) entries.Insert(entries.Count - 1, wanted); // value ended with ';'
            else entries.Add(wanted);
        }

        key.SetValue("Path", string.Join(';', entries), kind);
        Broadcast();
        return remove
            ? $"PATH: removed {wanted} from your user PATH."
            : $"PATH: added {wanted} to your user PATH. Open a NEW terminal (and restart your agent) so `agentprobe` is found.\n" +
              "      Keep the exe in this folder, or run setup again after moving it.";
    }

    /// <summary>Tells running programs (Explorer, new terminals) that environment variables changed.</summary>
    private static unsafe void Broadcast()
    {
        fixed (char* name = "Environment") SendMessageTimeoutW(0xFFFF, 0x001A, 0, (nint)name, 0x0002, 2000, out _);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
