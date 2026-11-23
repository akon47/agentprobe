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
    private bool _minimized;
    private bool _resumePaused;
    private int _minimizedDone;
    private byte[]? _trustedKey;
    private IPEndPoint? _lastEndpoint;
    private string _controllerName = "developer PC";
    private int _ended;
    private bool _switchToDeveloper;
    private bool _resumedAfterUpdate;
    private volatile bool _connected; // a developer PC is connected right now
    private int _continueRunsAfter;
    private string? _handingOverTo; // the version a new instance of this exe is taking the session over as

    /// <summary>Exit code of share mode when the person pressed D: "this is my developer PC".</summary>
    public const int SwitchToDeveloper = 100;

    /// <summary>The person has just picked "share this PC" at the role question, so offering D again would be noise.</summary>
    public static bool RoleChosen { get; set; }

    private ShareHost(ArgParser args)
    {
        _autoYes = args.Flag("yes");
        _minimized = args.Flag("minimized");
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
        var args = ArgParser.Parse(argv, "yes", "resume", "dry-run", "minimized");
        if (args.Get("lang") is { } lang) Lang.Set(lang);
        if (args.Flag("dry-run")) return await new ShareHost(args).DryRunAsync();
        if (args.Get("trust") is { } trust && trust.Replace("-", "").Length != 16)
        {
            // A shortened key would accept every developer PC whose key merely starts the same way.
            Console.WriteLine(Lang.T(
                "--trust needs the developer PC's full key, as `agentprobe list` shows it there (for example 3F9A-21C4-88D0-B7E1).",
                "--trust에는 개발 PC의 키 전체가 필요합니다. 개발 PC에서 `agentprobe list`가 보여 주는 값입니다 (예: 3F9A-21C4-88D0-B7E1)."));
            return 2;
        }

        // Started by an instance that has just replaced the exe and is about to leave: let it go first.
        if (int.TryParse(args.Get("after"), out var previous) && previous > 0)
        {
            try
            {
                using var old = Process.GetProcessById(previous);
                old.WaitForExit(20000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }

        Controller.Updater.CleanupOld();

        // Two instances on one PC would fight over the workspace, the banner and the resume-after-reboot state.
        using var single = new Mutex(true, $"Local\\{AppInfo.Name}-share{AppInfo.Instance}", out var first);
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
            _resumedAfterUpdate = state.Reason == "update";
            _continueRunsAfter = state.LastRun;
            _minimized |= state.Minimized;
            _resumePaused = state.Paused;
        }
        else
        {
            _identity = Identity.Create();
        }

        _runtime = new TargetRuntime(Label, workDir)
        {
            PrepareReboot = PrepareReboot,
            UpdateTo = UpdateTo,
            RequestEnd = removeExe => _ = Task.Run(() => End(removeExe, askedToEnd: true)),
        };
        _instance = this;
        WatchLogoffAndShutdown();
        ConsoleLog.Start();
        if (resumedHistory != null) _runtime.SeedHistory(resumedHistory, _resumedAfterUpdate ? "update" : "reboot");
        if (_resumePaused)
        {
            _runtime.Paused = true; // paused before the restart: still paused
            _runtime.Audit.Write("still PAUSED by the person at this PC (from before the restart): Enter in this window resumes");
        }

        WatchPauseKey();
        _runtime.ContinueRunsAfter(_continueRunsAfter); // a run number printed before an update never comes to mean another command
        _runtime.Audit.Line += line => ConsoleLog.Write("  " + line[11..]); // time of day is enough on screen

        using var close = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => End(false));
        using var ctrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => End(false));
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => End(false));

        PrintBanner();
        KeepAwake(true);
        _runtime.Audit.Write(_resumedAfterUpdate ? $"sharing resumed as agentprobe {AppInfo.Version} after an update"
            : IsResume ? "sharing resumed after reboot"
            : $"sharing started by {Environment.UserName}");

        var probing = Task.Run(() => Discovery.ProbeAsync(OnControllerSeen, _extraHosts, () => _connected, _stop.Token));
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
            Console.WriteLine($"agentprobe {AppInfo.Version}  -  이 PC 공유 중: {name}, 사용자 {Environment.UserName} ({who})");
            Console.WriteLine($"  원격에서 하는 모든 일이 아래에 나오고 여기에 남습니다: {log}");
            Console.WriteLine("  멈추려면 이 창을 닫으세요. 설치되는 것은 없습니다.");
            Console.WriteLine("  잠깐만 멈추려면 이 창에서 스페이스바를 누르세요: Enter를 누를 때까지 원격 명령을 받지 않습니다.");
        }
        else
        {
            var who = Environment.IsPrivilegedProcess ? "administrator" : "standard user rights";
            Console.WriteLine($"agentprobe {AppInfo.Version}  -  sharing this PC: {name}, user {Environment.UserName} ({who})");
            Console.WriteLine($"  Everything the remote side does is listed below and saved to {log}");
            Console.WriteLine("  Close this window to stop. Nothing is installed.");
            Console.WriteLine("  To pause only, press the space bar in this window: remote commands are refused until you press Enter.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// <c>share --dry-run</c>: where this PC looks for a developer PC and which ones answer, without sharing anything.
    /// It answers "why is it not found?" at the PC to test, and it never connects to what it finds.
    /// </summary>
    private async Task<int> DryRunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var plan = Discovery.Plan(_extraHosts, AppInfo.Port);
        var broadcast = Lang.T("broadcast", "브로드캐스트");
        var each = Lang.T("each address", "주소마다");
        var none = Lang.T("NOT SEARCHED", "검색 안 함");
        var given = Lang.T("address", "주소");
        static int Cells(string s) => s.Sum(c => c >= 0x1100 ? 2 : 1); // the labels are ASCII or Hangul
        var width = new[] { broadcast, each, none, given }.Max(Cells) + 2;
        string Row(string how, string where, string rest) =>
            $"  {how}{new string(' ', width - Cells(how))}{where,-20}{rest}".TrimEnd();

        Console.WriteLine(Lang.T(
            $"Where this PC looks for a developer PC (UDP port {AppInfo.Port}). This command only looks; nothing is shared.",
            $"이 PC가 개발 PC를 찾는 곳 (UDP 포트 {AppInfo.Port}). 이 명령은 찾기만 하고 아무것도 공유하지 않습니다."));
        Console.WriteLine(Row(broadcast, IPAddress.Broadcast.ToString(), ""));
        foreach (var n in plan.Networks)
        {
            if (n.Broadcast != null && n.CarriesBroadcast) Console.WriteLine(Row(broadcast, n.Broadcast.ToString(), $"{n.Cidr}  {n.Adapter}"));
            if (n.SweptAddresses > 0)
                Console.WriteLine(Row(each, n.Cidr, Lang.T($"{n.SweptAddresses} addresses  {n.Adapter}", $"주소 {n.SweptAddresses}개  {n.Adapter}")));
            if (!n.Searched) Console.WriteLine(Row(none, n.Cidr, $"{n.Adapter}  ({NotSearchedReason(n)})"));
        }

        foreach (var h in plan.Hosts) Console.WriteLine(Row(given, h.ToString(), ""));
        foreach (var h in plan.Unresolved) Console.WriteLine(Row(none, h, Lang.T("(the name does not resolve to an IPv4 address)", "(이름을 IPv4 주소로 찾을 수 없음)")));
        const int seconds = 3;
        var answers = new ConcurrentDictionary<string, ControllerInfo>();
        using (var cts = new CancellationTokenSource(seconds * 1000))
        {
            await Discovery.ProbeAsync(c => answers[$"{c.Fingerprint} {c.Endpoint.Address}"] = c, _extraHosts, () => false, cts.Token);
        }

        Console.WriteLine();
        Console.WriteLine(answers.IsEmpty
            ? Lang.T($"No developer PC answered within {seconds} s.", $"{seconds}초 안에 응답한 개발 PC가 없습니다.")
            : Lang.T($"Developer PCs that answered within {seconds} s:", $"{seconds}초 안에 응답한 개발 PC:"));
        // One developer PC answers from each of its addresses this PC can reach: one line per key.
        foreach (var pc in answers.Values.GroupBy(c => c.Fingerprint).OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase))
        {
            var c = pc.First();
            var at = string.Join(", ", pc.Select(x => x.Endpoint.Address.ToString()).Distinct().Order(StringComparer.Ordinal));
            Console.WriteLine(Lang.T(
                $"  {c.Name}  (user {c.User})  key {c.Fingerprint}  at {at}",
                $"  {c.Name}  (사용자 {c.User})  키 {c.Fingerprint}  주소 {at}"));
        }

        Console.WriteLine(Lang.T(
            "A developer PC anywhere else (behind a router, on a network not searched): agentprobe share <developer-pc-address>",
            "그 밖의 곳에 있는 개발 PC (라우터 너머, 검색하지 않는 네트워크): agentprobe share <개발 PC 주소>"));
        return 0;
    }

    private static string NotSearchedReason(SearchNetwork n) => n.Broadcast == null
        ? Lang.T("the adapter has no subnet to search", "어댑터에 검색할 서브넷이 없음")
        : Lang.T("a VPN adapter carries no broadcast, and there are too many addresses to try one by one", "VPN 어댑터는 브로드캐스트를 전달하지 않고, 하나씩 시도하기에는 주소가 너무 많음");

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
                    _runtime.Paused ? Lang.T($"Remote control PAUSED  -  {_controllerName}", $"원격 제어 일시 멈춤  -  {_controllerName}")
                        : Lang.T($"Remote control active  -  {_controllerName}", $"원격 제어 중  -  {_controllerName}"),
                    _runtime.Paused ? Lang.T("agentprobe: paused here. Press Enter in its window to resume.", "agentprobe: 여기서 일시 멈춤. 계속하려면 agentprobe 창에서 Enter를 누르세요.")
                        : Lang.T("agentprobe: this PC is being controlled remotely. Close its window to stop.", "agentprobe: 이 PC가 원격으로 제어되고 있습니다. 멈추려면 agentprobe 창을 닫으세요."));
                var session = new TargetSession(channel, _runtime, _controllerName, a => Badge.SetDetail(DateTime.Now.ToString("HH:mm:ss") + "  " + a));
                _connected = true;
                MinimizeOnce();
                try
                {
                    await session.RunAsync(BuildHello(), ct);
                }
                finally
                {
                    _connected = false;
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
            "Looking for a developer PC...  (agentprobe has to be running there)",
            "개발 PC를 찾는 중...  (개발 PC에서 agentprobe가 켜져 있어야 합니다)"));
        var canAsk = !Console.IsInputRedirected && !IsResume && !RoleChosen;
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
                    "  Still looking. Check that both PCs are on the same network or VPN and that the developer PC ran\n" +
                    "  `agentprobe setup` once. Different subnet? Start this as: agentprobe share <developer-pc-address>",
                    "  아직 찾는 중입니다. 두 PC가 같은 네트워크나 VPN에 있는지, 개발 PC에서 `agentprobe setup`을 한 번\n" +
                    "  실행했는지 확인하세요. 서브넷이 다르면: agentprobe share <개발 PC 주소>"));
                foreach (var n in Discovery.Plan(_extraHosts, AppInfo.Port).Networks.Where(n => !n.Searched))
                    Console.WriteLine(Lang.T($"  Not searched: {n.Cidr} ({n.Adapter}): {NotSearchedReason(n)}", $"  검색하지 않음: {n.Cidr} ({n.Adapter}): {NotSearchedReason(n)}"));
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

        // Asked as a clickable menu where the console can show one; as typed text otherwise (below).
        if (AskWithMenu(candidates, ct) is { } answered)
        {
            if (answered.chosen == null) Deny(candidates);
            else Console.WriteLine(Lang.T($"  Allowed: {answered.chosen.Name}  key {answered.chosen.Fingerprint}", $"  허용함: {answered.chosen.Name}  키 {answered.chosen.Fingerprint}"));
            return answered.chosen;
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

        if (chosen == null) Deny(candidates);
        return chosen;
    }

    private void Deny(List<ControllerInfo> candidates)
    {
        foreach (var c in candidates) _rejected.Add(c.Fingerprint);
        Console.WriteLine(Lang.T("Not allowed. Still looking for other developer PCs; close this window to quit.", "허용하지 않았습니다. 다른 개발 PC를 계속 찾습니다. 끝내려면 이 창을 닫으세요."));
    }

    /// <summary>
    /// The approval question as a menu: one line per developer PC that answered, and one to allow none.
    /// Returns null when this console cannot show a menu.
    /// </summary>
    private static (ControllerInfo? chosen, bool asked)? AskWithMenu(List<ControllerInfo> candidates, CancellationToken ct)
    {
        var rights = Environment.IsPrivilegedProcess
            ? Lang.T("ADMINISTRATOR rights", "관리자 권한")
            : Lang.T($"the rights of {Environment.UserName}", $"{Environment.UserName}의 권한");
        var question = Lang.T(
            $"Which developer PC may run commands on this PC, with {rights}?",
            $"어느 개발 PC가 이 PC에서 {rights}으로 명령을 실행하도록 허용할까요?");
        var options = candidates
            .Select(c => new Controller.MenuOption(
                Lang.T($"Allow {c.Name}", $"{c.Name} 허용"),
                Lang.T($"user {c.User}   {c.Endpoint.Address}   key {c.Fingerprint}", $"사용자 {c.User}   {c.Endpoint.Address}   키 {c.Fingerprint}")))
            .Append(new Controller.MenuOption(Lang.T("Allow none", "허용 안 함")))
            .ToList();
        var title = Lang.T($"agentprobe - sharing this PC ({Environment.MachineName})", $"agentprobe - 이 PC 공유 ({Environment.MachineName})");

        // Nothing is preselected: allowing a PC takes a click on its line or its number, never a stray Enter.
        var picked = Controller.ConsoleMenu.Choose(title, question, options, -1, ct);
        if (picked == null) return null;
        return (picked >= 0 && picked < candidates.Count ? candidates[picked.Value] : null, true);
    }

    /// <summary>
    /// <c>share --minimized</c>: once a developer PC is connected this window has said what it had to say, and on
    /// a test PC it lies in front of the app under test. It goes to the taskbar, once; the banner on the screen
    /// and the log in the window stay as they are. Only when this process has the console to itself, never the
    /// terminal of someone who started sharing from their shell. (A terminal that puts every new console in a tab
    /// of one window is minimized as a whole: there is no window of our own to tell apart.)
    /// </summary>
    private unsafe void MinimizeOnce()
    {
        if (!_minimized || Interlocked.Exchange(ref _minimizedDone, 1) != 0) return;
        var list = stackalloc uint[4];
        if (GetConsoleProcessList(list, 4) != 1) return; // the console is shared with a shell
        var console = GetConsoleWindow();
        if (console != 0) ShowWindow(console, 6); // SW_MINIMIZE
    }

    /// <summary>
    /// The space bar in this window pauses remote commands and Enter resumes them: the person at the PC can say
    /// "not now" (someone else needs the PC for a moment) without ending the session.
    /// Two keys, not one that toggles: a key held a moment too long, pressed twice, or pressed while the
    /// connection was down and read later can then only pause again, never undo the pause. And keys without a
    /// letter: with a Korean (or other composing) input method on, a letter key starts a composition and does
    /// not arrive until the next key is pressed.
    /// </summary>
    private void WatchPauseKey()
    {
        if (Console.IsInputRedirected) return;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    // Only while a developer PC is connected: before that, this window's keys answer its own questions.
                    if (_connected && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Spacebar || key.KeyChar == ' ') SetPaused(true);
                        else if (key.Key == ConsoleKey.Enter) SetPaused(false);
                        continue; // the next key right away: a held key must not be worked off for seconds
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    return; // no keyboard to read
                }

                await Task.Delay(150);
            }
        });
    }

    private void SetPaused(bool paused)
    {
        if (_runtime.Paused == paused) return;
        _runtime.Paused = paused;
        _runtime.Audit.Write(paused
            ? "PAUSED by the person at this PC: remote commands are refused until Enter is pressed here"
            : "resumed by the person at this PC: remote commands are accepted again");
        ConsoleLog.Write(paused
            ? Lang.T("  == PAUSED: remote commands are refused. Press Enter to resume. ==", "  == 일시 멈춤: 원격 명령을 받지 않습니다. 계속하려면 Enter를 누르세요. ==")
            : Lang.T("  == Resumed. ==", "  == 다시 받습니다. =="));
        if (_connected)
        {
            Badge.Show(
                paused ? Lang.T($"Remote control PAUSED  -  {_controllerName}", $"원격 제어 일시 멈춤  -  {_controllerName}")
                    : Lang.T($"Remote control active  -  {_controllerName}", $"원격 제어 중  -  {_controllerName}"),
                paused ? Lang.T("agentprobe: paused here. Press Enter in its window to resume.", "agentprobe: 여기서 일시 멈춤. 계속하려면 agentprobe 창에서 Enter를 누르세요.")
                    : Lang.T("agentprobe: this PC is being controlled remotely. Close its window to stop.", "agentprobe: 이 PC가 원격으로 제어되고 있습니다. 멈추려면 agentprobe 창을 닫으세요."));
        }
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
            ["addrs"] = string.Join(' ', OwnAddresses()),
        };
    }

    /// <summary>
    /// This PC's IPv4 addresses, for the developer PC's list: a PC with several network adapters is often known to
    /// its user by another address than the one the connection happens to come from.
    /// </summary>
    private static List<string> OwnAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var text = ua.Address.ToString();
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !text.StartsWith("169.254.", StringComparison.Ordinal) && !list.Contains(text) && list.Count < 6) list.Add(text);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // the adapter list is not available: the connection's own address is still known
        }

        return list;
    }

    /// <summary>Stable per-PC-and-user id (a hash, never the raw MachineGuid) so the developer PC can remember a label.</summary>
    private static string MachineId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid + "|" + Environment.UserName)), 0, 8);
    }

    /// <summary>What the next instance needs to carry this session on without asking the person again.</summary>
    private ResumeState ResumeStateFor(string reason, TimeSpan validFor)
    {
        if (_trustedKey == null || _lastEndpoint == null) throw new InvalidOperationException("not connected");
        return new ResumeState
        {
            Reason = reason,
            ControllerKey = Convert.ToBase64String(_trustedKey),
            ControllerHost = _lastEndpoint.Address.ToString(),
            ControllerPort = _lastEndpoint.Port,
            TargetKey = Convert.ToBase64String(_identity.ExportPrivateKey()),
            WorkDir = _runtime.HasWorkDir ? _runtime.WorkDir : "",
            ControllerName = _controllerName,
            // The commands so far, so that `history` still shows where the work stood (their output is not carried).
            History = _runtime.ExportHistory(60),
            LastRun = _runtime.LastRunNumber,
            Minimized = _minimized,
            Paused = _runtime.Paused,
            ExpiresUtc = DateTime.UtcNow.Add(validFor),
        };
    }

    /// <summary>
    /// The developer PC sent a newer exe: put it in this exe's place and continue the session from it. The running
    /// file is moved aside (it cannot be overwritten), the new instance starts in its own window and takes over as
    /// soon as this one has left; approval and the workspace go with it.
    /// </summary>
    private string UpdateTo(string newExe, string version)
    {
        var state = ResumeStateFor("update", TimeSpan.FromMinutes(5));
        Controller.Updater.Replace(newExe, AppInfo.ExePath);
        try
        {
            Resume.Register(state, autostart: false);
            var start = new ProcessStartInfo(AppInfo.ExePath) { UseShellExecute = true };
            foreach (var word in new[] { "share", "--resume", "--after", Environment.ProcessId.ToString() }) start.ArgumentList.Add(word);
            if (Label != null)
            {
                start.ArgumentList.Add("--name");
                start.ArgumentList.Add(Label);
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Resume.Unregister();
            throw new InvalidOperationException($"the exe was replaced with {version}, but the new instance could not be started ({ex.Message}). This session continues on {AppInfo.Version}; the new version is used the next time agentprobe is started here.");
        }

        // What this session changed or left running in the background (crash-dump capture, a trace, a watch, a
        // debug-output capture) cannot be followed by the new instance, so it is reverted and stopped now rather
        // than left behind.
        var reverted = _runtime.RunUndo();
        reverted.AddRange(_runtime.StopCaptures());
        _handingOverTo = version;
        _runtime.Audit.Write($"updating agentprobe {AppInfo.Version} -> {version} on request of {_controllerName}; the session continues in a new window");
        _ = Task.Run(async () =>
        {
            await Task.Delay(700); // let the answer reach the developer PC first
            End(false);
        });
        return $"agentprobe on this PC is now {version} (was {AppInfo.Version}); restarting from it, the session resumes by itself. Run numbers continue and `history` keeps the commands so far (not their output)." +
               (reverted.Count > 0 ? " Stopped or reverted before the restart: " + string.Join(", ", reverted) + " (turn on again afterwards if needed)." : "");
    }

    private string PrepareReboot()
    {
        var how = Resume.Register(ResumeStateFor("", TimeSpan.FromHours(24)));

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
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetConsoleProcessList(uint* list, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

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
        if (_handingOverTo != null && !askedToEnd)
        {
            // A new instance of the updated exe is waiting to carry the session on: leave the workspace and its state to it.
            _runtime.Audit.Write($"restarting as agentprobe {_handingOverTo}");
        }
        else if (_runtime.RebootPending)
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
