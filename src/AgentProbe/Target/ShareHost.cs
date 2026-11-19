using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentProbe.Transport;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Target;

/// <summary>
/// "Share this PC" mode: what runs when the exe is double-clicked on the PC to be tested.
/// It finds developer PCs on the LAN, asks the person at this PC once, then dials OUT to the approved one,
/// so this PC needs no firewall rule, no account and no installation.
/// </summary>
internal sealed partial class ShareHost
{
    private readonly ConcurrentDictionary<string, ControllerInfo> _seen = new();
    private readonly HashSet<string> _rejected = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _extraHosts = [];
    private readonly bool _autoYes;
    private readonly string? _trustPrefix;
    private TargetRuntime _runtime = null!;
    private Identity _identity = null!;
    private string? _trustedFingerprint;
    private byte[]? _trustedKey;
    private IPEndPoint? _lastEndpoint;
    private string _controllerName = "developer PC";
    private int _ended;
    private bool _switchToDeveloper;

    /// <summary>Exit code of share mode when the person pressed D: "this is my developer PC".</summary>
    public const int SwitchToDeveloper = 100;

    private ShareHost(ArgParser args)
    {
        _autoYes = args.Flag("yes");
        _trustPrefix = args.Get("trust")?.Replace("-", "").ToUpperInvariant();
        if (args.Get("controller") is { } hosts) _extraHosts.AddRange(hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _extraHosts.AddRange(args.Positionals);
        Label = args.Get("name");
        if (args.Get("lang") is { } lang) Lang.Set(lang);
        IsResume = args.Flag("resume");
    }

    private string? Label { get; }

    private bool IsResume { get; }

    public static async Task<int> RunAsync(string[] argv)
    {
        var args = ArgParser.Parse(argv, "yes", "resume");
        if (args.Get("lang") is { } lang) Lang.Set(lang);
        if (args.Get("trust") is { } trust && trust.Replace("-", "").Length != 16)
        {
            // A shortened key would accept every developer PC whose key merely starts the same way.
            Console.WriteLine(Lang.T(
                "--trust needs the developer PC's full key, as `agentprobe list` shows it there (for example 3F9A-21C4-88D0-B7E1).",
                "--trust에는 개발 PC의 키 전체가 필요합니다. 개발 PC에서 `agentprobe list`가 보여 주는 값입니다 (예: 3F9A-21C4-88D0-B7E1)."));
            return 2;
        }

        // Two instances on one PC would fight over the workspace, the banner and the resume-after-reboot state.
        using var single = new Mutex(true, $"Local\\{AppInfo.Name}-share", out var first);
        if (!first)
        {
            Console.WriteLine(Lang.T(
                "agentprobe is already sharing this PC (look for its other window). Close that one first to start again.",
                "agentprobe가 이미 이 PC를 공유하고 있습니다 (다른 agentprobe 창을 확인하세요). 다시 시작하려면 그 창을 먼저 닫으세요."));
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine();
                Console.Write(Lang.T("Press Enter to close.", "Enter를 누르면 닫힙니다."));
                Console.ReadLine();
            }

            return 1;
        }

        var host = new ShareHost(args);
        var code = await host.RunAsync();
        GC.KeepAlive(single);
        return host._switchToDeveloper ? SwitchToDeveloper : code;
    }

    private async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            Console.Title = Lang.T("agentprobe - sharing this PC", "agentprobe - 이 PC 공유 중");
        }
        catch (IOException)
        {
            // no console window (started hidden)
        }

        string? workDir = null;
        List<string>? resumedHistory = null;
        if (IsResume)
        {
            var state = Resume.Take();
            if (state == null)
            {
                Console.WriteLine(Lang.T("Nothing to resume (the previous session ended or expired).", "이어서 할 세션이 없습니다 (이전 세션이 끝났거나 만료됨)."));
                return 0;
            }

            _identity = Identity.FromPrivateKey(Convert.FromBase64String(state.TargetKey));
            _trustedKey = Convert.FromBase64String(state.ControllerKey);
            _trustedFingerprint = Identity.FingerprintOf(_trustedKey);
            if (IPAddress.TryParse(state.ControllerHost, out var ip)) _lastEndpoint = new IPEndPoint(ip, state.ControllerPort);
            workDir = Directory.Exists(state.WorkDir) ? state.WorkDir : null;
            if (state.ControllerName.Length > 0) _controllerName = state.ControllerName;
            resumedHistory = state.History;
        }
        else
        {
            _identity = Identity.Create();
        }

        _runtime = new TargetRuntime(Label, workDir)
        {
            PrepareReboot = PrepareReboot,
            RequestEnd = removeExe => _ = Task.Run(() => End(removeExe, askedToEnd: true)),
        };
        _instance = this;
        WatchLogoffAndShutdown();
        ConsoleLog.Start();
        if (resumedHistory != null) _runtime.SeedHistory(resumedHistory);
        _runtime.Audit.Line += line => ConsoleLog.Write("  " + line[11..]); // time of day is enough on screen

        using var close = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => End(false));
        using var ctrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => End(false));
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => End(false));

        PrintBanner();
        KeepAwake(true);
        _runtime.Audit.Write(IsResume ? "sharing resumed after reboot" : $"sharing started by {Environment.UserName}");

        var probing = Task.Run(() => Discovery.ProbeAsync(OnControllerSeen, _extraHosts, _stop.Token));
        try
        {
            await ConnectLoopAsync();
        }
        catch (OperationCanceledException)
        {
            // window closed or `end`
        }

        await probing.ContinueWith(_ => { }, TaskScheduler.Default);
        return 0;
    }

    private void PrintBanner()
    {
        var name = $"{Environment.MachineName}{(Label != null ? $" \"{Label}\"" : "")}";
        var log = _runtime.Audit.FilePath ?? Lang.T("(log file unavailable)", "(로그 파일을 만들 수 없음)");
        if (Lang.Korean)
        {
            var who = Environment.IsPrivilegedProcess ? "관리자 권한" : "일반 사용자 권한";
            Console.WriteLine($"agentprobe {AppInfo.Version}  -  같은 네트워크의 개발 PC가 이 PC를 디버깅하고 테스트하게 합니다");
            Console.WriteLine();
            Console.WriteLine($"  이 PC      {name}   사용자 {Environment.UserName} ({who})");
            Console.WriteLine("  활동 기록  원격에서 하는 모든 일이 아래에 나열되고 이 파일에 저장됩니다");
            Console.WriteLine($"             {log}");
            Console.WriteLine("  중지       언제든 이 창을 닫으면 됩니다. 설치되는 것은 없습니다.");
        }
        else
        {
            var who = Environment.IsPrivilegedProcess ? "administrator" : "standard user rights";
            Console.WriteLine($"agentprobe {AppInfo.Version}  -  let a developer PC on your network debug and test this PC");
            Console.WriteLine();
            Console.WriteLine($"  This PC    {name}   user {Environment.UserName} ({who})");
            Console.WriteLine("  Activity   everything the remote side does is listed below and saved to");
            Console.WriteLine($"             {log}");
            Console.WriteLine("  Stop       close this window at any time. Nothing is installed.");
        }

        Console.WriteLine();
    }

    private void OnControllerSeen(ControllerInfo info)
    {
        _seen[info.Fingerprint] = info;
    }

    private async Task ConnectLoopAsync()
    {
        var ct = _stop.Token;
        var announcedWaiting = false;
        while (!ct.IsCancellationRequested)
        {
            if (_trustedFingerprint == null)
            {
                var chosen = await ChooseControllerAsync(ct);
                if (chosen == null) continue;
                _trustedFingerprint = chosen.Fingerprint;
                _lastEndpoint = chosen.Endpoint;
                _controllerName = $"{chosen.Name} ({chosen.User})";
            }

            // The developer PC may have moved (DHCP): prefer where it answered a probe most recently.
            var endpoint = _seen.TryGetValue(_trustedFingerprint, out var live) ? live.Endpoint : _lastEndpoint;
            if (live != null) _controllerName = $"{live.Name} ({live.User})";
            if (endpoint == null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(5000);
                    await tcp.ConnectAsync(endpoint, connectCts.Token);
                }

                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                await using var channel = await HandshakeAsync(tcp.GetStream(), ct);
                _trustedKey = channel.PeerPublicKey;
                _lastEndpoint = endpoint;
                announcedWaiting = false;

                _runtime.Audit.Write($"connected to {_controllerName} at {endpoint.Address}  key {channel.PeerFingerprint}");
                Badge.Show(
                    Lang.T($"Remote control active  -  {_controllerName}", $"원격 제어 중  -  {_controllerName}"),
                    Lang.T("agentprobe: this PC is being controlled remotely. Close its window to stop.", "agentprobe: 이 PC가 원격으로 제어되고 있습니다. 멈추려면 agentprobe 창을 닫으세요."));
                var session = new TargetSession(channel, _runtime, _controllerName, a => Badge.SetDetail(DateTime.Now.ToString("HH:mm:ss") + "  " + a));
                try
                {
                    await session.RunAsync(BuildHello(), ct);
                }
                finally
                {
                    Badge.Hide();
                }

                if (!ct.IsCancellationRequested) _runtime.Audit.Write("connection lost; reconnecting (running commands and jobs continue)");
            }
            catch (AuthenticationFailedException ex)
            {
                // Someone answered the probe with the approved fingerprint but could not prove the key:
                // forget that address and go back to the last one that did.
                _seen.TryRemove(_trustedFingerprint, out _);
                _runtime.Audit.Write("refused: " + ex.Message);
                await Task.Delay(5000, ct);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Anything a peer on the network sends before it is authenticated can end up here (bad lengths,
                // garbage keys, a dropped link). None of it may take this process down: keep trying.
                if (ct.IsCancellationRequested) break;
                _seen.TryRemove(_trustedFingerprint, out _);
                if (!announcedWaiting)
                {
                    Console.WriteLine(Lang.T($"  Waiting for {_controllerName} to come back...", $"  {_controllerName}이(가) 다시 연결되기를 기다리는 중..."));
                    announcedWaiting = true;
                }
            }

            await Task.Delay(2000, ct);
        }
    }

    private async Task<SecureChannel> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15000);
        return await SecureChannel.InitiateAsync(
            stream,
            _identity,
            key => _trustedKey != null ? key.AsSpan().SequenceEqual(_trustedKey) : Identity.FingerprintOf(key) == _trustedFingerprint,
            cts.Token);
    }

    /// <summary>Waits for developer PCs to show up and asks the person at this PC which one may take control.</summary>
    private async Task<ControllerInfo?> ChooseControllerAsync(CancellationToken ct)
    {
        Console.WriteLine(Lang.T(
            "Looking for a developer PC on this network...  (it shows up as soon as `agentprobe` runs there)",
            "이 네트워크에서 개발 PC를 찾는 중...  (개발 PC에서 `agentprobe`를 실행하면 바로 나타납니다)"));
        var canAsk = !Console.IsInputRedirected && !IsResume;
        if (canAsk)
        {
            Console.WriteLine(Lang.T(
                "Is THIS your developer PC (the one your AI agent runs on)? Press D.",
                "이 PC가 개발 PC(AI 에이전트를 돌리는 PC)인가요? D 키를 누르세요."));
        }

        List<ControllerInfo> candidates;
        var waited = 0;
        while (true)
        {
            candidates = _seen.Values.Where(c => !_rejected.Contains(c.Fingerprint)).OrderBy(c => c.Name).ToList();
            if (candidates.Count > 0) break;
            if (canAsk && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.D)
            {
                // First run on a developer PC: nothing has been shared yet, so just change roles.
                _switchToDeveloper = true;
                _runtime.Audit.Write("switched to the developer role; this PC was not shared");
                KeepAwake(false);
                _stop.Cancel();
                return null;
            }

            await Task.Delay(500, ct);
            waited += 500;
            if (waited == 20000)
            {
                Console.WriteLine(Lang.T(
                    "  Still looking. Check that both PCs are on the same network and that the developer PC ran\n" +
                    "  `agentprobe setup` once. Different subnet or VPN? Start this as: agentprobe share <developer-pc-address>",
                    "  아직 찾는 중입니다. 두 PC가 같은 네트워크에 있는지, 개발 PC에서 `agentprobe setup`을 한 번\n" +
                    "  실행했는지 확인하세요. 서브넷이 다르거나 VPN이면: agentprobe share <개발 PC 주소>"));
            }
        }

        await Task.Delay(1200, ct); // give other developer PCs a moment to answer too
        candidates = _seen.Values.Where(c => !_rejected.Contains(c.Fingerprint)).OrderBy(c => c.Name).ToList();

        var preTrusted = _trustPrefix == null ? null : candidates.FirstOrDefault(c => c.Fingerprint.Replace("-", "").StartsWith(_trustPrefix, StringComparison.Ordinal));
        if (preTrusted != null) return preTrusted;
        if (_trustPrefix != null)
        {
            foreach (var c in candidates) _rejected.Add(c.Fingerprint); // not the pinned key: never ask about it
            return null;
        }

        if (_autoYes) return candidates[0];

        if (Console.IsInputRedirected)
        {
            Console.WriteLine(Lang.T("Cannot ask for approval without a console. Start with --yes or --trust <key>.", "콘솔이 없어 승인을 물을 수 없습니다. --yes 또는 --trust <키>로 시작하세요."));
            _stop.Cancel();
            return null;
        }

        Console.WriteLine();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            Console.WriteLine(Lang.T(
                $"  [{i + 1}] {c.Name}  (user {c.User})  {c.Endpoint.Address}  key {c.Fingerprint}",
                $"  [{i + 1}] {c.Name}  (사용자 {c.User})  {c.Endpoint.Address}  키 {c.Fingerprint}"));
        }

        Console.WriteLine();
        if (Lang.Korean)
        {
            var rights = Environment.IsPrivilegedProcess ? "관리자 권한" : $"{Environment.UserName}의 권한";
            Console.Write(candidates.Count == 1
                ? $"{candidates[0].Name}이(가) 이 PC에서 {rights}으로 명령을 실행하도록 허용할까요? [Y/n] "
                : $"어느 PC가 이 PC에서 {rights}으로 명령을 실행하도록 허용할까요? [1-{candidates.Count}, n = 허용 안 함] ");
        }
        else
        {
            var rights = Environment.IsPrivilegedProcess ? "ADMINISTRATOR rights" : $"the rights of {Environment.UserName}";
            Console.Write(candidates.Count == 1
                ? $"Let {candidates[0].Name} run commands on this PC with {rights}? [Y/n] "
                : $"Which one may run commands on this PC with {rights}? [1-{candidates.Count}, n = none] ");
        }

        var answer = (await Task.Run(Console.ReadLine, ct) ?? "n").Trim().ToLowerInvariant();
        Console.WriteLine();

        ControllerInfo? chosen = null;
        if (candidates.Count == 1 && answer is "" or "y" or "yes") chosen = candidates[0];
        else if (int.TryParse(answer, out var n) && n >= 1 && n <= candidates.Count) chosen = candidates[n - 1];

        if (chosen == null)
        {
            foreach (var c in candidates) _rejected.Add(c.Fingerprint);
            Console.WriteLine(Lang.T("Not allowed. Still looking for other developer PCs; close this window to quit.", "허용하지 않았습니다. 다른 개발 PC를 계속 찾습니다. 끝내려면 이 창을 닫으세요."));
        }

        return chosen;
    }

    private Dictionary<string, string> BuildHello()
    {
        using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = $"{os?.GetValue("CurrentBuild")}.{os?.GetValue("UBR")}";
        var product = (os?.GetValue("ProductName") as string ?? "Windows").Replace("Microsoft ", "");
        if (int.TryParse(os?.GetValue("CurrentBuild") as string, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return new Dictionary<string, string>
        {
            ["name"] = Environment.MachineName,
            ["label"] = _runtime.Label ?? "",
            ["user"] = Environment.UserName,
            ["admin"] = Environment.IsPrivilegedProcess ? "1" : "0",
            ["os"] = $"{product} {os?.GetValue("DisplayVersion")} ({build})",
            ["arch"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ["ver"] = AppInfo.Version,
            ["machine"] = MachineId(),
            ["started"] = _runtime.StartedUtc.Ticks.ToString(),
            ["utc"] = DateTime.UtcNow.Ticks.ToString(),
            ["resumed"] = IsResume ? "1" : "0",
        };
    }

    /// <summary>Stable per-PC-and-user id (a hash, never the raw MachineGuid) so the developer PC can remember a label.</summary>
    private static string MachineId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid + "|" + Environment.UserName)), 0, 8);
    }

    private string PrepareReboot()
    {
        if (_trustedKey == null || _lastEndpoint == null) throw new InvalidOperationException("not connected");
        var how = Resume.Register(new ResumeState
        {
            ControllerKey = Convert.ToBase64String(_trustedKey),
            ControllerHost = _lastEndpoint.Address.ToString(),
            ControllerPort = _lastEndpoint.Port,
            TargetKey = Convert.ToBase64String(_identity.ExportPrivateKey()),
            WorkDir = _runtime.HasWorkDir ? _runtime.WorkDir : "",
            ControllerName = _controllerName,
            History = _runtime.ExportHistory(60),
            ExpiresUtc = DateTime.UtcNow.AddHours(24),
        });

        // Settings this session changed (crash-dump capture, a running trace) cannot be tracked across a reboot,
        // so they are reverted now rather than left behind. They can be turned on again afterwards.
        var reverted = _runtime.RunUndo();
        if (reverted.Count > 0) how += "; reverted before the reboot: " + string.Join(", ", reverted) + " (turn on again afterwards if needed)";

        _runtime.RebootPending = true;
        _ = Task.Run(async () =>
        {
            // If the reboot does not happen (aborted with shutdown /a, or blocked), stop pretending it will:
            // otherwise closing this window later would leave the autostart entry and the workspace behind.
            while (_runtime.RebootPending)
            {
                await Task.Delay(5000);
                if (_runtime.RebootDeadlineUtc is { } deadline && DateTime.UtcNow > deadline)
                {
                    _runtime.RebootPending = false;
                    Resume.Unregister();
                    _runtime.Audit.Write("the requested reboot did not happen; the resume entry was removed");
                }
            }
        });
        return how;
    }

    private static ShareHost? _instance;

    /// <summary>
    /// Closing the window is not the only way a session ends: logging off or shutting down must clean up too.
    /// .NET's signal API does not report logoff, so the console control handler is registered directly.
    /// </summary>
    private static unsafe void WatchLogoffAndShutdown() => SetConsoleCtrlHandler(&OnConsoleControl, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int OnConsoleControl(uint type)
    {
        if (type is 5 or 6) _instance?.End(false); // CTRL_LOGOFF_EVENT, CTRL_SHUTDOWN_EVENT
        return 0; // let the default handling continue
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int SetConsoleCtrlHandler(delegate* unmanaged[Stdcall]<uint, int> handler, int add);

    /// <summary>Ends sharing: kill what we started, remove the workspace and any autostart. Runs once.</summary>
    private void End(bool removeExe, bool askedToEnd = false)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        Badge.Hide();
        KeepAwake(false);

        // An explicit `end` always wins over a reboot that was requested earlier but has not happened.
        if (askedToEnd) _runtime.RebootPending = false;
        if (_runtime.RebootPending)
        {
            // Windows is going down for the reboot we were asked for: keep the workspace and the resume entry.
            _runtime.Audit.Write("rebooting; will resume after logon");
        }
        else
        {
            _runtime.Audit.Write("sharing stopped");
            _runtime.Cleanup();
            Resume.Unregister();
            if (removeExe) ScheduleSelfDelete();
        }

        _stop.Cancel();
        Environment.Exit(0);
    }

    private static void ScheduleSelfDelete()
    {
        // The exe cannot delete itself while running: let cmd do it a moment after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{AppInfo.ExePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>A test PC that falls asleep mid-session looks like a hang, so keep it awake while sharing.</summary>
    private static void KeepAwake(bool on) => SetThreadExecutionState(on ? 0x80000001u : 0x80000000u); // ES_CONTINUOUS | ES_SYSTEM_REQUIRED

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}

/// <summary>
/// The activity log on screen. Writing to a Windows console blocks while someone has text selected in it
/// (QuickEdit), which would freeze whatever command was logging. So lines go through a queue drained by its own
/// task, and QuickEdit is switched off for this window.
/// </summary>
internal static partial class ConsoleLog
{
    private static readonly System.Threading.Channels.Channel<string> Queue =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(2000)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var input = GetStdHandle(-10);
        if (GetConsoleMode(input, out var mode)) SetConsoleMode(input, (mode & ~0x0040u) | 0x0080u); // QUICK_EDIT off, EXTENDED_FLAGS on
        _ = Task.Run(async () =>
        {
            await foreach (var line in Queue.Reader.ReadAllAsync())
            {
                try
                {
                    Console.WriteLine(line);
                }
                catch (IOException)
                {
                    // console gone
                }
            }
        });
    }

    public static void Write(string line)
    {
        if (_started == 0) Console.WriteLine(line);
        else Queue.Writer.TryWrite(line);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
