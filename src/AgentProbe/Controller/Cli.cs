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
        return await DispatchAsync(argv);
    }

    /// <summary>One command line, already split into words. Also what each step of `run` goes through.</summary>
    public static async Task<int> DispatchAsync(string[] argv)
    {
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

        // AGENTPROBE_TARGET names the PC for commands that run on one, when -t is not given: a piece of work then
        // keeps its target whatever else connects. (Not for `update`, where -t changes what is updated.)
        var targetFromEnvironment = false;
        if (target == null && (command is "name" or "push" or "run" || ToolRegistry.Find(command) != null) && DefaultTarget() is { } fromEnvironment)
        {
            target = fromEnvironment;
            targetFromEnvironment = true;
        }

        switch (command)
        {
            case "help" or "--help" or "-h" or "/?":
                return Help(args);
            case "version" or "--version":
                Console.WriteLine(AppInfo.Version);
                return 0;
            case "guide":
            {
                var text = Guide(args.FirstOrDefault(), out var known);
                (known ? Console.Out : Console.Error).Write(text);
                return known ? 0 : 2;
            }
            case "run":
                return await Steps.RunAsync(args, target, Console.Out);
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
                return await TryConnectAsync() is { } p
                    ? await RequestAsync(p, new PipeMsg { Op = args.Contains("--if-idle") ? "stop-idle" : "stop" }, "stop", true)
                    : Say(Util.Lang.P("daemon is not running", "백그라운드 프로세스가 실행 중이 아닙니다"));
            case "update":
            {
                // With -t: bring connected PCs to this PC's version. Without: update this PC from the newest release.
                var remote = string.Equals(target, "local", StringComparison.OrdinalIgnoreCase) ? null : target;
                var request = new PipeMsg { Op = "update", Target = remote, Args = args };
                if (remote != null) return await DaemonAsync(request, "update", true);

                // The background process does it when there is one, because it has to restart itself afterwards.
                if (await TryConnectAsync(1500) is { } running) return await RequestAsync(running, request, "update", true);
                if (Mutex.TryOpenExisting($"Local\\{AppInfo.PipeName}-daemon", out var alive))
                {
                    // It is running but could not be reached: replacing the exe now would leave it serving the old version unnoticed.
                    alive.Dispose();
                    return Fail(_otherRights
                        ? Util.Lang.P(
                            "agentprobe's background process is running with different rights (started from an administrator terminal, or the other way round). Run `agentprobe update` from that kind of terminal.",
                            "agentprobe의 백그라운드 프로세스가 다른 권한으로 실행 중입니다 (관리자 터미널에서 시작했거나 그 반대). 그쪽 종류의 터미널에서 `agentprobe update`를 실행하세요.")
                        : Util.Lang.P(
                            "agentprobe's background process is running but did not answer, so nothing was changed. Try again, or `agentprobe stop` first.",
                            "agentprobe의 백그라운드 프로세스가 실행 중이지만 응답하지 않아 아무것도 바꾸지 않았습니다. 다시 시도하거나 먼저 `agentprobe stop`을 실행하세요."));
                }

                if (args.FirstOrDefault(o => o is not ("--check" or "--force")) is { } unknown)
                    return Fail(Util.Lang.P($"update: unknown option '{unknown}'. It takes --check and --force.", $"update: 알 수 없는 옵션 '{unknown}'. --check와 --force만 받습니다."));
                return await UpdateWithoutDaemonAsync(args.Contains("--check"));
            }
            case "list" or "targets":
                return await DaemonAsync(new PipeMsg { Op = "list", Args = args }, "list", true);
            case "dashboard" or "home":
                return await Home.ShowAsync(openBrowser: command == "dashboard", interactive: command == "home", ownWindow: args.Contains("--own-window"));
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

        var quietOk = false;
        var toolArgs = ExecLikeTools.Contains(tool.Name) ? BuildExecArgs(tool.Name, args, out quietOk) : args;
        if (toolArgs == null) return 2;

        // `pull` takes the local destination as its optional second argument.
        if (tool.Name == "pull" && toolArgs.Length >= 2 && outPath == null)
        {
            outPath = toolArgs[1];
            toolArgs = [toolArgs[0], .. toolArgs[2..]];
        }

        // A screenshot nobody gave a place for goes to the temp folder, not into whatever folder the agent
        // happens to work in (a project folder would collect PNGs). Its path is printed.
        if (tool.Name == "screenshot" && outPath == null)
        {
            outPath = Path.Combine(Path.GetTempPath(), AppInfo.Name, "screenshots");
            Directory.CreateDirectory(outPath);

            // One target: a file name of its own, so that two screenshots in the same second (of two PCs, by two
            // agents) are never one file. Several targets: the folder, where each file gets its PC's name.
            if (target == null || !(target.Contains(',') || target.Contains('*') || target.Equals("all", StringComparison.OrdinalIgnoreCase)))
                outPath = Path.Combine(outPath, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.png");
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

        var answered = await DaemonAsync(req, tool.Name, full, quietOk: quietOk);
        if (answered == 3 && targetFromEnvironment) Console.Error.WriteLine($"(the target name '{target}' came from the environment variable AGENTPROBE_TARGET)");
        return answered;
    }

    /// <summary>AGENTPROBE_TARGET: the target for commands that name none, or null.</summary>
    public static string? DefaultTarget() => Environment.GetEnvironmentVariable("AGENTPROBE_TARGET") is { } t && t.Trim().Length > 0 ? t.Trim() : null;

    /// <summary>Pulls -t/--target, --full and -o/--out out of the arguments (for exec/start only before the command text).</summary>
    private static string[] ExtractGlobals(string command, string[] args, ref string? target, ref bool full, ref string? outPath)
    {
        var result = new List<string>();
        var execLike = ExecLikeTools.Contains(command);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (execLike && (!a.StartsWith('-') || a is "-" or "--"))
            {
                result.AddRange(args[i..]); // the command text (or a script's own arguments) starts here: leave it untouched
                break;
            }

            if (a == "--")
            {
                result.AddRange(args[i..]); // what follows belongs to the tool (the arguments of a program it starts)
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
    private static string[]? BuildExecArgs(string tool, string[] args, out bool quietOk)
    {
        quietOk = false;
        var options = new List<string>();
        string? script = null;
        var wholeScript = false; // -f or stdin: a script of its own, with its own arguments after it
        var i = 0;
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-")
            {
                script = Console.In.ReadToEnd();
                wholeScript = true;
                i++;
                break;
            }

            if (!a.StartsWith('-')) break;
            if (a == "--quiet-ok" && tool == "exec")
            {
                quietOk = true; // read here, on the developer PC: it is about what is printed, not about what runs
                continue;
            }

            if (a is "-f" or "--file")
            {
                if (++i >= args.Length || !File.Exists(args[i]))
                {
                    Console.Error.WriteLine($"{tool}: script file not found: {(i < args.Length ? args[i] : "(missing)")}");
                    return null;
                }

                script = File.ReadAllText(args[i]);
                wholeScript = true;
                continue;
            }

            if (a == "--exe" && tool == "start")
            {
                // start --exe <path> [args]: the program itself, no shell; everything after the path is for the program.
                if (++i >= args.Length)
                {
                    Console.Error.WriteLine("--exe needs the path of a program on the target.");
                    return null;
                }

                return [.. options, "--exe", args[i], "--", .. args[(i + 1)..]];
            }

            if (a == "--lnk" && tool == "start")
            {
                // start --lnk <shortcut> [more args]: what the shortcut starts; what follows is added to its arguments.
                if (++i >= args.Length)
                {
                    Console.Error.WriteLine("--lnk needs the path of a shortcut (.lnk) on the target.");
                    return null;
                }

                return [.. options, "--lnk", args[i], "--", .. args[(i + 1)..]];
            }

            if (a == "--env" && tool == "start")
            {
                if (i + 1 >= args.Length || !args[i + 1].Contains('='))
                {
                    Console.Error.WriteLine("--env needs NAME=VALUE.");
                    return null;
                }

                options.Add(a);
                options.Add(args[++i]);
                continue;
            }

            if (wholeScript && a == "--") break; // the script's arguments start here

            if (a == "--dismiss-firewall" && tool == "start")
            {
                options.Add(a);
                continue;
            }

            if (a is "--shell" or "--cwd" or "--timeout" or "--name" || (tool == "start" && a is "--replace" or "--replace-wait"))
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

        if (wholeScript)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                Console.Error.WriteLine($"{tool}: the script is empty.");
                return null;
            }

            // What follows the file name goes to the script as its arguments (a leading -- only separates).
            var rest = args[i..];
            if (rest.Length > 0 && rest[0] == "--") rest = rest[1..];
            return [.. options, "--as-file", "--script", script!, "--", .. rest];
        }

        // `exec '<command>' --full`: the whole command was given in quotes and an option of agentprobe follows it.
        // Sent along, it becomes part of the command on the target (PowerShell then fails in its own words, or a
        // program gets an argument nobody meant for it). It is refused here, where the answer can say what to do.
        // (Only the options that are hardly anything but agentprobe's: --name, --out or -t after a quoted
        // command are as likely the program's own.)
        if (args.Length - i >= 2 && args[i].Any(char.IsWhiteSpace) &&
            args[(i + 1)..].FirstOrDefault(x => x is "--full" or "--timeout" or "--shell" or "--cwd" or "--target" or "--quiet-ok" ||
                                                x.StartsWith("--timeout=", StringComparison.Ordinal) || x.StartsWith("--shell=", StringComparison.Ordinal) || x.StartsWith("--cwd=", StringComparison.Ordinal) || x.StartsWith("--target=", StringComparison.Ordinal)) is { } late)
        {
            Console.Error.WriteLine($"{tool}: '{late}' comes after the command, where it would be sent to the target as part of the command. Nothing was run. " +
                                    $"agentprobe's options go before the command: agentprobe {tool} {late} ... '<command>'. (To pass it to the command on purpose, put it inside the quotes.)");
            return null;
        }

        script = string.Join(' ', args[i..]);
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

    /// <summary>
    /// Ends the background process unless it is serving a connected PC or an open dashboard (`stop --if-idle`).
    /// Never starts one. Used when the start screen's own window is closed, so that closing it leaves nothing running.
    /// </summary>
    public static async Task StopIfIdleAsync()
    {
        try
        {
            if (await TryConnectAsync() is { } pipe) await RequestAsync(pipe, new PipeMsg { Op = "stop-idle" }, "stop", true, TextWriter.Null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            // it went away by itself
        }
    }

    /// <summary>`agentprobe update` on a PC where no background process runs (a PC that is only ever shared, for one).</summary>
    private static async Task<int> UpdateWithoutDaemonAsync(bool checkOnly)
    {
        static string L(string english, string korean) => Util.Lang.P(english, korean);
        try
        {
            var latest = await Updater.LatestAsync(CancellationToken.None);
            if (!Updater.IsNewer(latest.Version, AppInfo.Version))
                return Say(L($"agentprobe {AppInfo.Version} is the newest version.", $"agentprobe {AppInfo.Version}이(가) 최신 버전입니다."));
            if (checkOnly)
            {
                return Say(L(
                    $"agentprobe {latest.Version} is available (this PC has {AppInfo.Version}). Install it with: agentprobe update",
                    $"agentprobe {latest.Version}이(가) 나왔습니다 (이 PC는 {AppInfo.Version}). 설치: agentprobe update") + (latest.PageUrl.Length > 0 ? "\n" + latest.PageUrl : ""));
            }

            Console.WriteLine(L($"downloading agentprobe {latest.Version}...", $"agentprobe {latest.Version} 내려받는 중..."));
            await Updater.InstallAsync(latest, AppInfo.ExePath, CancellationToken.None);
            Updater.CleanupOldAfterExit(); // this process still runs from the file that was moved aside
            Console.WriteLine(L(
                $"updated {AppInfo.ExePath} from {AppInfo.Version} to {latest.Version}. The next start uses it.",
                $"{AppInfo.ExePath}을(를) {AppInfo.Version}에서 {latest.Version}(으)로 업데이트했습니다. 다음 실행부터 새 버전입니다."));
            if (Mutex.TryOpenExisting($"Local\\{AppInfo.Name}-share{AppInfo.Instance}", out var sharing))
            {
                sharing.Dispose();
                Console.WriteLine(L(
                    "This PC is being shared right now, by the old version: that session goes on untouched. It uses the new version when sharing is started again.",
                    "이 PC는 지금 이전 버전으로 공유되고 있습니다. 그 세션은 그대로 이어지고, 공유를 다시 시작하면 새 버전을 씁니다."));
            }

            return 0;
        }
        catch (UpdateFailedException ex)
        {
            Console.Error.WriteLine(ex.Changed
                ? L($"The update failed: {ex.Message}", $"업데이트에 실패했습니다: {ex.Message}")
                : L($"The update failed and nothing was changed: {ex.Message}", $"업데이트에 실패했고 아무것도 바뀌지 않았습니다: {ex.Message}"));
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or IOException or TaskCanceledException)
        {
            // Reading the release feed failed: the check itself, before anything was downloaded.
            Console.Error.WriteLine(L(
                $"Could not check for a new version: {(ex is TaskCanceledException ? "no answer in time" : ex.GetBaseException().Message)}",
                $"새 버전을 확인하지 못했습니다: {(ex is TaskCanceledException ? "제때 응답이 없었습니다" : ex.GetBaseException().Message)}"));
            return 1;
        }
    }

    public static async Task<int> DaemonAsync(PipeMsg req, string tool, bool full, TextWriter? capture = null, bool quietOk = false)
    {
        var pipe = await ConnectOrStartAsync();
        if (pipe == null)
        {
            return Fail(_otherRights
                ? "agentprobe's background process is running with different rights (started from an administrator terminal, or the other way round). Run `agentprobe stop` from that kind of terminal, then try again."
                : "Could not start the agentprobe background process. Try `agentprobe daemon` to see why.");
        }
        return await RequestAsync(pipe, req, tool, full, capture, quietOk);
    }

    private static async Task<int> RequestAsync(NamedPipeClientStream pipe, PipeMsg req, string tool, bool full, TextWriter? capture = null, bool quietOk = false)
    {
        await using var _ = pipe;
        if (Util.Lang.Korean && Util.Lang.Person) req.Lang = "ko";
        var line = JsonSerializer.SerializeToUtf8Bytes(req, JsonCtx.Default.PipeMsg);
        await pipe.WriteAsync(line);
        pipe.WriteByte((byte)'\n');
        await pipe.FlushAsync();

        var limiter = new OutputLimiter(tool, full || capture != null, quietOk: quietOk);
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

        limiter.Finish(gotExit ? code : 1);
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

            if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(Steps.Usage.TrimEnd());
                return 0;
            }

            return Fail(Util.Lang.P($"No command '{args[0]}'.", $"'{args[0]}' 명령은 없습니다."));
        }

        if (Util.Lang.Korean && Util.Lang.Person)
        {
            Console.Write($"""
                agentprobe {AppInfo.Version} - AI 에이전트가 같은 네트워크의 다른 Windows PC를 디버깅하고 테스트하게 합니다

                테스트할 PC    agentprobe.exe를 복사해 더블클릭하고 1을 누릅니다 (또는: agentprobe share). 한 번 승인합니다.
                개발 PC        아래 명령을 실행합니다. 에이전트도 같은 명령을 씁니다. 처음 한 번: agentprobe setup

                세션:
                  list [--all]       연결된 대상 PC (--all: 연결이 끊긴 PC와 그 마지막 명령도 표시)
                  dashboard          같은 내용을 실시간 웹 페이지로 엽니다. AI 에이전트에게 줄 문구도 출력합니다
                  home               시작 화면: 그 문구를 숫자 키나 클릭으로 복사하고, 명령도 입력할 수 있습니다
                  name <새 이름>     대상 PC에 짧은 이름을 붙입니다. 그 뒤로 -t에 그 이름을 씁니다
                  setup [--remove]   개발 PC 설정(한 번): 이 폴더를 PATH에 추가하고 방화벽 포트를 엽니다
                                     (관리자 권한을 한 번 묻습니다)
                  guide [주제]       AI 에이전트용 사용 안내 (영어). 짧은 핵심과, 필요할 때 읽는 주제들 (guide all: 전부)
                  mcp                MCP 서버(stdio)로 실행: claude mcp add agentprobe -- "<경로>\agentprobe.exe" mcp
                  stop [--if-idle]   이 PC의 백그라운드 프로세스 종료 (--if-idle: 연결된 PC와 열린 대시보드가 없을 때만)
                  update [--check]   이 PC의 agentprobe를 최신 릴리스로 교체하고 백그라운드 프로세스를 다시 시작 (--check: 확인만)
                                     -t <이름> update: 연결된 PC를 이 PC와 같은 버전으로 (세션과 승인은 유지)

                대상 지정:  -t <이름>  |  -t a,b  |  -t "LAB-*"  |  -t all  |  -t local (이 PC)
                            연결된 대상이 하나면 -t를 생략할 수 있습니다. 여러 대상의 같은 결과는 묶어서 보여 줍니다.
                여러 단계:  run <파일|->       한 줄에 하나씩 적은 명령을 순서대로 실행하고 실패한 곳에서 멈춥니다.
                                               모두 성공하면 한 줄, 실패하면 그 단계와 대상 PC의 화면 상태를 보여 줍니다 (run --help)
                출력:       긴 출력은 앞뒤만 보여 주고 전체를 이 PC의 파일로 저장합니다. --full은 전부 출력합니다.
                            -o <경로>는 받아 온 파일(screenshot, dump, pull)을 저장할 위치입니다.
                            AGENTPROBE_TARGET 환경 변수는 -t를 생략했을 때 쓸 대상 PC입니다.

                아래 명령들의 설명과 출력은 에이전트가 읽는 글이라 영어입니다. 자세히: agentprobe help <명령>

                {ToolRegistry.HelpList()}
                """.Replace("\r\n", "\n"));
            return 0;
        }

        Console.Write($"""
            agentprobe {AppInfo.Version} - let an AI agent debug and test another Windows PC on your network

            ON THE PC TO TEST      copy agentprobe.exe there, double-click it, press 1 (or: agentprobe share). Approve once.
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
              guide [topic]      how an AI agent should use this tool: the short core, and topics to read when
                                 needed (guide ui, guide exec, ...; guide all is everything)
              mcp                run as an MCP server (stdio), for agents that are set up through MCP:
                                 claude mcp add agentprobe -- "<path>\agentprobe.exe" mcp
              stop [--if-idle]   stop the background process on this PC (--if-idle: only when no PC is connected and
                                 no dashboard is open; closing the start screen's own window does the same)
              update [--check]   replace this PC's agentprobe with the newest release (checksum verified) and restart
                                 its background process; --check only says whether there is one; --force also while
                                 commands are running. With -t <name>: bring a connected PC to this PC's version;
                                 its session, approval and workspace carry over.

            Targets:  -t <name>  |  -t a,b  |  -t "LAB-*"  |  -t all  |  -t local (this PC)
                      With one connected target, -t can be omitted. Several targets: identical results are grouped.
            Steps:    run <file|->       several commands in one go, one per line, until one fails: one line when all
                                         went well, the failed step with what the target shows when not (run --help)
            Output:   long output is cut to head+tail and saved to a local file; --full prints everything.
                      -o <path> chooses where files sent back (screenshot, dump, pull) are saved.
                      The environment variable AGENTPROBE_TARGET names the target for commands without -t.

            {ToolRegistry.HelpList()}
            Details: agentprobe help <command>

            """.Replace("\r\n", "\n"));
        return 0;
    }

    /// <summary>
    /// The guide is one file, cut into a core and topics by marker lines (<c>&lt;!-- topic: name | what it covers --&gt;</c>).
    /// An agent reads the core once and it stays in its context for the whole session: so the core is short, and
    /// what is needed only now and then is a topic, read when it is needed.
    /// No topic: the core and the list of topics. A topic's name: that part. "all": everything.
    /// </summary>
    public static string Guide(string? topic, out bool known)
    {
        known = true;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent-guide.md");
        if (s == null) return "guide not embedded in this build\n";
        var text = new StreamReader(s, Encoding.UTF8).ReadToEnd().Replace("\r\n", "\n");

        var core = new StringBuilder();
        var topics = new List<(string name, string summary, StringBuilder body)>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("<!-- topic:", StringComparison.Ordinal) && line.TrimEnd().EndsWith("-->", StringComparison.Ordinal))
            {
                var parts = line.Trim()[11..^3].Split('|', 2);
                topics.Add((parts[0].Trim().ToLowerInvariant(), parts.Length > 1 ? parts[1].Trim() : "", new StringBuilder()));
                continue;
            }

            (topics.Count > 0 ? topics[^1].body : core).Append(line).Append('\n');
        }

        var list = new StringBuilder();
        foreach (var (name, summary, _) in topics) list.Append("  guide ").Append(name.PadRight(9)).Append(summary).Append('\n');
        topic = topic?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(topic))
        {
            return topics.Count == 0 ? core.ToString()
                : core.ToString().TrimEnd() + "\n\n## More, read when you need it\n\n`agentprobe guide <topic>` (`guide all` is everything at once):\n\n```\n" + list + "```\n";
        }

        if (topic == "all") return core.ToString().TrimEnd() + "\n\n" + string.Join("\n", topics.Select(t => t.body.ToString().Trim() + "\n"));
        foreach (var (name, _, body) in topics)
        {
            if (name == topic) return body.ToString().Trim() + "\n";
        }

        known = false;
        return $"guide: no topic '{topic}'. Topics:\n{list}  guide all      everything\n";
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
                       || (tool.Name is "ui" or "screenshot" && args.Contains("--diff"))
                       || (tool.LoudWhen?.Invoke(args) == true)
                       || (tool.Name == "firewall" && args.Contains("--auto"))
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
