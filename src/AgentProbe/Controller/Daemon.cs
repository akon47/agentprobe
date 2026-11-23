using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentProbe.Transport;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>What the daemon remembers about a target, online or not (for `list` and the dashboard).</summary>
internal sealed class TargetRecord
{
    /// <summary>What this record is registered under: the machine id, plus the key fingerprint for a second PC that reports the same id.</summary>
    public string Key { get; set; } = "";

    public string MachineId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Machine { get; set; } = "";

    public string User { get; set; } = "";

    public string Address { get; set; } = "";

    /// <summary>IPv4 addresses the PC says it also has (its other adapters). Shown, never used to pick a target: it is the PC's own claim.</summary>
    public List<string> OtherAddresses { get; set; } = [];

    public string Os { get; set; } = "";

    public string Version { get; set; } = "";

    public bool Admin { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public string LastCommand { get; set; } = "";

    public TargetConnection? Connection { get; set; }

    public bool Online => Connection is { Alive: true };
}

/// <summary>
/// Background process on the developer PC. Targets dial in to it and stay connected; each short-lived CLI call
/// (<c>agentprobe exec ...</c>) talks to it over a per-user named pipe. Started automatically by the first CLI call.
/// </summary>
internal sealed partial class Daemon
{
    private readonly ConcurrentDictionary<string, TargetRecord> _targets = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _logGate = new();
    private Identity _identity = null!;
    private Dictionary<string, string> _labels = new();

    private static string LabelsPath => Path.Combine(AppInfo.StateDir, "labels.json");

    private static string LogPath => Path.Combine(AppInfo.StateDir, "daemon.log");

    public string DashboardToken { get; private set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    public int DashboardPort { get; private set; }

    private long _dashboardSeenTicks;
    private int _pipeRequests; // agentprobe commands being served right now

    /// <summary>Called for every request an open dashboard page makes (it polls every few seconds).</summary>
    public void DashboardSeen() => Interlocked.Exchange(ref _dashboardSeenTicks, DateTime.UtcNow.Ticks);

    public IEnumerable<TargetRecord> Records => _targets.Values;

    public static async Task<int> RunAsync(string[] args)
    {
        // Started by a background process that has just updated the exe and is about to leave: wait for it to go.
        var after = ArgParser.Parse(args).GetInt("after", 0);
        if (after > 0) WaitForExit(after, 20000);

        using var single = new Mutex(true, $"Local\\{AppInfo.PipeName}-daemon", out var created);
        if (!created) return 0; // already running

        var d = new Daemon();
        try
        {
            await d.ServeAsync();
            return 0;
        }
        catch (Exception ex)
        {
            d.Log("fatal: " + ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task ServeAsync()
    {
        Directory.CreateDirectory(AppInfo.StateDir);
        _identity = Identity.LoadOrCreate(Path.Combine(AppInfo.StateDir, "controller.key"));
        LoadLabels();
        Updater.CleanupOld();
        TakeOverFromUpdate();

        var listener = new TcpListener(IPAddress.Any, AppInfo.Port);
        listener.Start();
        Log($"daemon {AppInfo.Version} listening on {AppInfo.Port}, key {_identity.Fingerprint}");

        var dashboard = new Dashboard(this);
        DashboardPort = dashboard.Start(_stop.Token);
        _ = Task.Run(WatchForUpdatesAsync);

        var tasks = new[]
        {
            Task.Run(() => AcceptTargetsAsync(listener)),
            Task.Run(() => Discovery.RespondAsync(_identity.Fingerprint, AppInfo.Port, _stop.Token)),
            Task.Run(AcceptPipesAsync),
        };
        var ended = await Task.WhenAny(tasks);
        if (ended.IsFaulted) Log("daemon stopping: " + ended.Exception!.GetBaseException());
        _stop.Cancel();
        listener.Stop();
        if (ended.IsFaulted) throw ended.Exception!.GetBaseException();
    }

    public void Log(string message)
    {
        lock (_logGate)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // logging must never take the daemon down
            }
        }
    }

    // ---------------------------------------------------------------- targets

    private async Task AcceptTargetsAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => ServeTargetAsync(tcp));
        }
    }

    private async Task ServeTargetAsync(TcpClient tcp)
    {
        var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            tcp.NoDelay = true;
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            SecureChannel channel;
            Dictionary<string, string> hello;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                cts.CancelAfter(15000);
                channel = await SecureChannel.RespondAsync(tcp.GetStream(), _identity, cts.Token);
                var first = await channel.ReceiveAsync(cts.Token);
                if (first?.Message is not { Type: "hello", Info: { } info }) throw new InvalidDataException("expected hello");

                // A target describes itself; treat that as untrusted text: known keys only, bounded length, no control characters.
                hello = new Dictionary<string, string>();
                foreach (var key in new[] { "name", "label", "user", "admin", "os", "arch", "ver", "machine", "started", "utc", "resumed", "addrs" })
                {
                    if (!info.TryGetValue(key, out var value) || value == null) continue;
                    var clean = new string(value.Where(ch => !char.IsControl(ch)).Take(120).ToArray()).Trim();
                    if (clean.Length > 0) hello[key] = clean;
                }
            }

            await using (channel)
            {
                var conn = new TargetConnection(channel, remote, hello);
                var record = Register(conn);
                Log($"target connected: {record.Name} ({conn.Machine}, {remote}) key {conn.Fingerprint}");

                // A PC that loses power or its network sends no goodbye. It does send a heartbeat every 5 s, so a
                // long silence means the link is dead: drop it, and commands waiting on it are told at once.
                using var watchdogStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var watchdog = Task.Run(async () =>
                {
                    try
                    {
                        while (conn.Alive)
                        {
                            await Task.Delay(5000, watchdogStop.Token);
                            if (DateTime.UtcNow - conn.LastFrameUtc > TimeSpan.FromSeconds(25))
                            {
                                Log($"target {record.Name} went silent; dropping the connection");
                                conn.Abort();
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // connection ended normally
                    }
                });

                try
                {
                    await conn.PumpAsync(_stop.Token);
                }
                finally
                {
                    conn.Abort();
                    watchdogStop.Cancel();
                    record.LastSeenUtc = DateTime.UtcNow;
                    record.LastCommand = conn.LastCommand.Length > 0 ? conn.LastCommand : record.LastCommand;
                    Log($"target disconnected: {record.Name}");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unauthenticated peers can send anything; none of it may stop the daemon.
            Log($"connection from {remote} dropped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            tcp.Dispose();
        }
    }

    private TargetRecord Register(TargetConnection conn)
    {
        var machineId = conn.MachineId;
        lock (_targets)
        {
            // Normally one record per machine id. If a live connection with a DIFFERENT key already holds that id,
            // this is another PC reporting the same id (cloned disk image): give it its own record instead of
            // letting it take the first one's place.
            // A PC that comes back with the key it had before (after a reboot, or after restarting for an update)
            // keeps the record it had, whichever of the clones reconnects first.
            var key = machineId;
            if (_targets.Values.FirstOrDefault(r => r.MachineId == machineId && r.Connection?.Fingerprint == conn.Fingerprint) is { } mine)
            {
                key = mine.Key;
            }
            else if (_targets.TryGetValue(machineId, out var holder) && holder.Connection is { Alive: true } current
                     && current.Fingerprint != conn.Fingerprint && DateTime.UtcNow - current.LastFrameUtc < TimeSpan.FromSeconds(15))
            {
                key = machineId + ":" + conn.Fingerprint;
            }

            conn.RecordKey = key;
            var targetLabel = conn.Info.GetValueOrDefault("label");
            conn.Label = _labels.GetValueOrDefault(key) ?? (string.IsNullOrEmpty(targetLabel) ? null : targetLabel);

            // Addressable name: label, else machine name; made unique against other PCs.
            var baseName = conn.Label ?? conn.Machine;
            var name = baseName;
            for (var n = 2; _targets.Values.Any(r => r.Key != key && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{baseName}-{n}";
            conn.DisplayName = name;

            var record = _targets.GetOrAdd(key, _ => new TargetRecord { Key = key, MachineId = machineId });

            // The same PC connecting again means its previous link is stale (it may not know yet): retire it now.
            if (record.Connection is { Alive: true } previous && previous != conn) previous.Abort();

            record.Name = name;
            record.Machine = conn.Machine;
            record.User = conn.Info.GetValueOrDefault("user", "?");
            record.Address = conn.Address.ToString();
            record.OtherAddresses = conn.Info.GetValueOrDefault("addrs", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : "")
                .Where(a => a.Length > 0 && a != record.Address).Distinct().Take(6).ToList();
            record.Os = conn.Info.GetValueOrDefault("os", "?");
            record.Version = conn.Info.GetValueOrDefault("ver", "?");
            record.Admin = conn.IsAdmin;
            record.LastSeenUtc = DateTime.UtcNow;
            record.Connection = conn;
            return record;
        }
    }

    private void LoadLabels()
    {
        try
        {
            if (File.Exists(LabelsPath))
                _labels = JsonSerializer.Deserialize(File.ReadAllText(LabelsPath), JsonCtx.Default.DictionaryStringString) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _labels = new();
        }
    }

    public string SetLabel(string key, string? label)
    {
        var machineId = key;
        lock (_targets)
        {
            if (!_targets.TryGetValue(key, out var record)) return "no such target";
            label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
            if (label != null && (label.Contains(',') || label.Contains('*') || label.Contains(' ')))
                return "a name cannot contain spaces, commas or *";
            if (label != null && _targets.Values.Any(r => r != record && r.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
                return $"another target is already called {label}";

            if (label == null) _labels.Remove(machineId);
            else _labels[machineId] = label;
            File.WriteAllText(LabelsPath, JsonSerializer.Serialize(_labels, JsonCtx.Default.DictionaryStringString));

            record.Name = label ?? record.Machine;
            if (record.Connection != null)
            {
                record.Connection.Label = label;
                record.Connection.DisplayName = record.Name;
            }

            return "";
        }
    }

    /// <summary>Resolves -t: nothing (the only target), a name, a comma list, a * wildcard, or "all".</summary>
    /// <param name="ko">A person on a Korean Windows asked (see <see cref="PipeMsg.Lang"/>): the error is in Korean.</param>
    private List<TargetConnection> Resolve(string? spec, out string error, bool ko = false)
    {
        string L(string english, string korean) => ko ? korean : english;
        error = "";
        var online = _targets.Values.Where(r => r.Online).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (online.Count == 0)
        {
            error = L(
                "No target PC is connected. Run agentprobe.exe on the PC to test (same network or VPN) and approve this PC there.\n" +
                "If it cannot find this PC, run `agentprobe setup` here once (opens the firewall port), or start it as: agentprobe share <this-pc-address>",
                "연결된 대상 PC가 없습니다. 테스트할 PC(같은 네트워크 또는 VPN)에서 agentprobe.exe를 실행하고 거기서 이 PC를 승인하세요.\n" +
                "대상이 이 PC를 찾지 못하면 여기서 `agentprobe setup`을 한 번 실행하거나(방화벽 포트), 대상에서: agentprobe share <이 PC의 주소>");
            return [];
        }

        if (string.IsNullOrEmpty(spec))
        {
            if (online.Count == 1) return [online[0].Connection!];
            error = L(
                $"{online.Count} targets are connected; pick one with -t <name> (or -t all):\n  ",
                $"대상 PC {online.Count}대가 연결되어 있습니다. -t <이름>으로 하나를 고르세요 (또는 -t all):\n  ") + string.Join(", ", online.Select(r => r.Name));
            return [];
        }

        var result = new List<TargetConnection>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            List<TargetRecord> match;
            if (part is "all" or "*") match = online;
            else if (part.Contains('*'))
            {
                var re = new System.Text.RegularExpressions.Regex(
                    "^" + System.Text.RegularExpressions.Regex.Escape(part).Replace("\\*", ".*") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                match = online.Where(r => re.IsMatch(r.Name) || re.IsMatch(r.Machine)).ToList();
            }
            else
            {
                match = online.Where(r => r.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count == 0) match = online.Where(r => r.Machine.Equals(part, StringComparison.OrdinalIgnoreCase) || r.Address == part).ToList();
            }

            if (match.Count == 0)
            {
                var offline = _targets.Values.FirstOrDefault(r => !r.Online && r.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                error = offline != null
                    ? L(
                        $"{offline.Name} is offline (last seen {Format.Duration(DateTime.UtcNow - offline.LastSeenUtc)} ago; last command: {Format.Clip(offline.LastCommand, 80)}).",
                        $"{offline.Name}은(는) 연결이 끊겨 있습니다 ({Format.Duration(DateTime.UtcNow - offline.LastSeenUtc)} 전에 마지막으로 연결됨, 마지막 명령: {Format.Clip(offline.LastCommand, 80)}).")
                    : L($"No connected target matches '{part}'. Connected: ", $"'{part}'에 해당하는 연결된 대상 PC가 없습니다. 연결된 PC: ") + string.Join(", ", online.Select(r => r.Name));
                return [];
            }

            foreach (var r in match)
                if (!result.Contains(r.Connection!)) result.Add(r.Connection!);
        }

        return result;
    }

    // ---------------------------------------------------------------- CLI pipe

    private async Task AcceptPipesAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(AppInfo.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }

            _ = Task.Run(() => ServePipeAsync(pipe));
        }
    }

    private async Task ServePipeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var writeLock = new SemaphoreSlim(1, 1);

        async Task Emit(PipeMsg m)
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(m, JsonCtx.Default.PipeMsg);
            await writeLock.WaitAsync();
            try
            {
                await pipe.WriteAsync(line);
                pipe.WriteByte((byte)'\n');
                await pipe.FlushAsync();
            }
            catch (IOException)
            {
                cts.Cancel(); // the CLI went away (Ctrl+C, or the agent's tool call timed out)
            }
            finally
            {
                writeLock.Release();
            }
        }

        try
        {
            var reader = new StreamReader(pipe, Encoding.UTF8);
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) return;
            var req = JsonSerializer.Deserialize(line, JsonCtx.Default.PipeMsg);
            if (req?.Op == null) return;

            // If the CLI disappears mid-command, cancel the remote work instead of leaving it running.
            var watch = Task.Run(async () =>
            {
                try
                {
                    await reader.ReadLineAsync(cts.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // fall through to cancel
                }

                cts.Cancel();
            });

            Interlocked.Increment(ref _pipeRequests);
            int code;
            try
            {
                code = await HandleAsync(req, Emit, cts.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _pipeRequests);
            }

            await Emit(new PipeMsg { Ev = "exit", Code = code });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            // client gone
        }
        catch (Exception ex)
        {
            Log("pipe request failed: " + ex);
            await Emit(new PipeMsg { Ev = "err", Text = "agentprobe daemon error: " + ex.Message + "\n" });
            await Emit(new PipeMsg { Ev = "exit", Code = 1 });
        }
    }

    private async Task<int> HandleAsync(PipeMsg req, Func<PipeMsg, Task> emit, CancellationToken ct)
    {
        Task Out(string text) => emit(new PipeMsg { Ev = "out", Text = text });
        Task Err(string text) => emit(new PipeMsg { Ev = "err", Text = text.EndsWith('\n') ? text : text + "\n" });

        // What this process says itself is in Korean for a person on a Korean Windows; an agent always gets English.
        var ko = req.Lang == "ko";
        string L(string english, string korean) => ko ? korean : english;

        switch (req.Op)
        {
            case "ping":
                await Out($"{AppInfo.Version}\n");
                return 0;

            case "stop":
                await Out(L("daemon stopped\n", "백그라운드 프로세스를 종료했습니다\n"));
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Environment.Exit(0);
                });
                return 0;

            case "stop-idle":
            {
                // Closing the start screen's window ends this process too, unless that would cut someone off:
                // a connected PC, another agentprobe command that is being served (one that waits for a PC to come
                // back from a reboot or an update, for instance), or a dashboard page that asked for something
                // recently (a page in a background tab is slowed down by the browser, hence the generous window).
                var now = DateTime.UtcNow;
                var online = _targets.Values.Count(r => r.Online);
                var others = Volatile.Read(ref _pipeRequests) - 1; // this request is one of them
                var viewed = now - new DateTime(Interlocked.Read(ref _dashboardSeenTicks), DateTimeKind.Utc) < TimeSpan.FromSeconds(90);
                if (online > 0 || others > 0 || viewed)
                {
                    await Out(L(
                        "daemon kept running: " + (online > 0 ? $"{online} target PC{(online == 1 ? " is" : "s are")} connected"
                            : others > 0 ? "another agentprobe command is in progress"
                            : "the dashboard was used in a browser within the last 90 s") + "\n",
                        "백그라운드 프로세스를 그대로 둡니다: " + (online > 0 ? $"대상 PC {online}대가 연결되어 있습니다"
                            : others > 0 ? "다른 agentprobe 명령이 실행 중입니다"
                            : "최근 90초 안에 브라우저에서 대시보드를 사용했습니다") + "\n"));
                    return 0;
                }

                goto case "stop";
            }

            case "list":
                await Out(ListText(req.Args?.Contains("--all") == true, ko));
                return 0;

            case "dashboard":
                await Out($"http://127.0.0.1:{DashboardPort}/?k={DashboardToken}\n");
                return 0;

            case "home": // what the start screen needs, as key=value lines
                await Out($"url=http://127.0.0.1:{DashboardPort}/?k={DashboardToken}\nkey={_identity.Fingerprint}\naddr={string.Join(", ", LocalAddresses().Select(a => a.Split(' ')[0]))}" +
                          $"\ntargets={string.Join(", ", _targets.Values.Where(r => r.Online).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase))}" +
                          $"\nupdate={NewerVersion}\nother={string.Join(", ", OtherVersions())}\n");
                return 0;

            case "update":
            {
                var options = req.Args ?? [];
                if (options.FirstOrDefault(o => o is not ("--check" or "--force")) is { } unknown)
                {
                    await Err(L($"update: unknown option '{unknown}'. It takes --check and --force.", $"update: 알 수 없는 옵션 '{unknown}'. --check와 --force만 받습니다."));
                    return 2;
                }

                return req.Target != null
                    ? await UpdateTargetsAsync(req.Target, options.Contains("--check"), options.Contains("--force"), ko, Out, Err, ct)
                    : await UpdateSelfAsync(options.Contains("--check"), options.Contains("--force"), ko, Out, Err, ct);
            }

            case "name":
            {
                var targets = Resolve(req.Target, out var error, ko);
                if (targets.Count != 1)
                {
                    await Err(error.Length > 0 ? error : L("name: pick exactly one target with -t.", "name: -t로 대상 PC를 정확히 하나 고르세요."));
                    return 2;
                }

                var problem = SetLabel(targets[0].RecordKey, req.Text);
                if (problem.Length > 0)
                {
                    await Err("name: " + problem);
                    return 2;
                }

                await Out(L($"{targets[0].Machine} is now called {targets[0].DisplayName}\n", $"{targets[0].Machine}의 이름을 {targets[0].DisplayName}(으)로 바꿨습니다\n"));
                return 0;
            }

            case "run" or "push":
            {
                var targets = Resolve(req.Target, out var error, ko);
                if (targets.Count == 0)
                {
                    await Err(error);
                    return 3;
                }

                if (targets.Count == 1)
                {
                    var sink = new RunSink(Out, Err, no => emit(new PipeMsg { Ev = "started", Code = no }), path => emit(new PipeMsg { Ev = "saved", Text = path }));
                    var code = await RunOneAsync(targets[0], req, sink, ct);
                    if (code == 0 && req.Tool == "reboot")
                    {
                        var ra = ArgParser.Parse(req.Args ?? [], "wait", "force");
                        if (req.Wait || ra.Flag("wait"))
                        {
                            var timeout = (int)(ra.GetDuration("timeout")?.TotalSeconds ?? req.TimeoutSec);
                            code = await WaitForRebootAsync(targets[0], timeout, Math.Clamp(ra.GetInt("delay", 5), 3, 600), Out, Err, ct);
                        }
                    }

                    return code;
                }

                return await RunManyAsync(targets, req, Out, ct);
            }

            default:
                await Err($"unknown daemon operation '{req.Op}' (CLI and daemon versions differ? run `agentprobe stop`).");
                return 2;
        }
    }

    private string ListText(bool includeOffline, bool ko = false)
    {
        var records = _targets.Values.Where(r => includeOffline || r.Online)
            .OrderByDescending(r => r.Online).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (records.Count == 0 && ko)
        {
            return "연결된 대상 PC가 없습니다.\n" +
                   "  1. 테스트할 PC에 agentprobe.exe를 복사해 실행하고 \"이 PC 공유\"를 고릅니다 (같은 네트워크 또는 VPN).\n" +
                   "  2. 거기서 이 PC를 승인합니다 (" + Environment.MachineName + ", 키 " + _identity.Fingerprint + ").\n" +
                   "대상이 이 PC를 찾지 못하면: 여기서 `agentprobe setup`을 한 번 실행하거나(방화벽), 대상에서\n" +
                   "  agentprobe share <이 PC의 주소>     (대상에서 `agentprobe share --dry-run`을 실행하면 어디를 찾는지 보여 줍니다)\n" +
                   "이 PC의 주소: " + string.Join(", ", LocalAddresses()) + "\n" + UpdateNotes(ko);
        }

        if (records.Count == 0)
        {
            return "No target PC is connected.\n" +
                   "  1. Copy agentprobe.exe to the PC you want to test and run it (same network or VPN).\n" +
                   "  2. Approve this PC there (" + Environment.MachineName + ", key " + _identity.Fingerprint + ").\n" +
                   "If the target cannot find this PC: run `agentprobe setup` here once (firewall), or on the target run\n" +
                   "  agentprobe share <address-of-this-pc>     (`agentprobe share --dry-run` there shows where it looks)\n" +
                   "This PC's addresses: " + string.Join(", ", LocalAddresses()) + "\n" + UpdateNotes(ko);
        }

        var t = new Table("NAME", "MACHINE", "USER", "ADDRESS", "RIGHTS", "STATE", "OS", "LAST COMMAND");
        foreach (var r in records)
        {
            var c = r.Connection;
            var state = r.Online
                ? (c!.Busy > 0 ? $"busy({c.Busy})" : "idle") + " " + Format.Duration(DateTime.UtcNow - c.ConnectedUtc)
                : "offline " + Format.Duration(DateTime.UtcNow - r.LastSeenUtc);
            var last = r.Online && c!.LastCommand.Length > 0 ? c.LastCommand : r.LastCommand;
            t.Add(r.Name, r.Machine == r.Name ? "=" : r.Machine, r.User, r.Address, r.Admin ? "admin" : "user", state, r.Os, Format.Clip(last, 50));
        }

        // A PC with several adapters is often known by another address than the one it connects from.
        var others = records.Where(r => r.Online && r.OtherAddresses.Count > 0).Select(r => $"{r.Name} {string.Join(", ", r.OtherAddresses)}").ToList();
        var also = others.Count == 0 ? "" : (ko
            ? "ADDRESS는 각 PC가 접속해 온 주소입니다. 그 PC가 알려 온 다른 주소: "
            : "ADDRESS is the address each PC connects from. Other addresses it reports having: ") + string.Join("; ", others) + "\n";
        return t.ToString() + also + UpdateNotes(ko);
    }

    /// <summary>One line per thing that can be updated: this PC from a newer release, a connected PC from this PC.</summary>
    private string UpdateNotes(bool ko)
    {
        var sb = new StringBuilder();
        if (NewerVersion.Length > 0)
        {
            sb.Append(ko
                ? $"agentprobe {NewerVersion}이(가) 나왔습니다 (이 PC는 {AppInfo.Version}): agentprobe update\n"
                : $"agentprobe {NewerVersion} is available (this PC has {AppInfo.Version}): agentprobe update\n");
        }

        foreach (var r in _targets.Values.Where(r => r.Online && r.Version != AppInfo.Version).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(ko
                ? $"{r.Name}은(는) agentprobe {r.Version}입니다 (이 PC는 {AppInfo.Version}): agentprobe -t {r.Name} update\n"
                : $"{r.Name} runs agentprobe {r.Version} (this PC has {AppInfo.Version}): agentprobe -t {r.Name} update\n");
        }

        return sb.ToString();
    }

    private sealed record RunSink(Func<string, Task> Out, Func<string, Task> Err, Func<int, Task> Started, Func<string, Task> Saved);

    /// <summary>The only commands that may send a file back. A file offered in reply to anything else is refused.</summary>
    private static readonly HashSet<string> FileSendingTools = new(StringComparer.OrdinalIgnoreCase) { "screenshot", "dump", "pull", "wpr" };

    private async Task<int> RunOneAsync(TargetConnection t, PipeMsg req, RunSink sink, CancellationToken ct, string? blobPrefix = null)
    {
        var description = req.Op == "push" ? $"push {req.RemotePath}" : $"{req.Tool} {string.Join(' ', (req.Args ?? []).Where(a => a != "--script"))}".Trim();

        // Prepare a push before talking to the target: a problem with the local file is this PC's problem,
        // and must not be reported as a lost connection.
        string? tempZip = null;
        FileStream? source = null;
        var isFolder = false;
        if (req.Op == "push")
        {
            var local = req.LocalPath!;
            try
            {
                isFolder = Directory.Exists(local);
                if (!isFolder && !File.Exists(local))
                {
                    await sink.Err($"push: {local} does not exist on this PC.");
                    return 2;
                }

                if (isFolder)
                {
                    tempZip = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-push-{Guid.NewGuid():N}.zip");
                    ZipFile.CreateFromDirectory(local, tempZip, CompressionLevel.Fastest, includeBaseDirectory: false);
                }

                source = new FileStream(tempZip ?? local, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                if (tempZip != null) TryDelete(tempZip);
                await sink.Err($"push: cannot read {local} on this PC: {ex.Message}");
                return 2;
            }
        }

        var (id, replies) = t.Begin(Format.Clip(description, 120), req.Background);
        Download? download = null;
        var localFailure = false;
        var runNo = 0;
        try
        {
            if (source != null)
            {
                await t.Channel.SendAsync(new Msg { Type = "upload", Id = id, Path = req.RemotePath, Size = source.Length, Zip = isFolder, Name = Path.GetFileName(req.LocalPath!.TrimEnd('\\', '/')) }, ct);
                var buf = new byte[256 * 1024];
                int n;
                while ((n = await source.ReadAsync(buf, ct)) > 0) await t.Channel.SendDataAsync(id, buf.AsSpan(0, n), false, ct);
                await t.Channel.SendDataAsync(id, [], true, ct);
            }
            else
            {
                await t.Channel.SendAsync(new Msg { Type = "run", Id = id, Tool = req.Tool, Args = req.Args ?? [], Background = req.Background }, ct);
            }

            await foreach (var reply in replies.ReadAllAsync(ct))
            {
                if (reply.Message is { } m)
                {
                    switch (m.Type)
                    {
                        case "started":
                            runNo = m.Code;
                            await sink.Started(m.Code);
                            break;
                        case "out":
                            await (m.Err ? sink.Err(m.Text ?? "") : sink.Out(m.Text ?? ""));
                            break;
                        case "blob":
                            download?.Abort();
                            download = null;
                            if (req.Op != "run" || !FileSendingTools.Contains(req.Tool ?? ""))
                            {
                                // A target does not get to write files on this PC unless a file was asked for.
                                await sink.Err("[refused a file the target tried to send: this command does not return files]");
                                Log($"refused unsolicited file '{m.Name}' from {t.DisplayName} during {req.Tool}");
                                break;
                            }

                            try
                            {
                                download = Download.Begin(req.LocalPath, blobPrefix, m.Name, m.Zip);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                            {
                                localFailure = true;
                                await sink.Err($"cannot save the file on this PC: {ex.Message}");
                            }

                            break;
                        case "exit":
                            return localFailure && m.Code == 0 ? 1 : m.Code;
                    }
                }
                else if (reply.Data != null && download != null)
                {
                    try
                    {
                        download.Write(reply.Data);
                        if (!reply.Eof) continue;
                        var saved = download.Finish();
                        download = null;
                        await sink.Saved(saved);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        download?.Abort();
                        download = null;
                        localFailure = true;
                        await sink.Err($"cannot save the file on this PC: {ex.Message}");
                    }
                }
            }

            // The reply stream ended without an exit message: the connection dropped.
            await sink.Err(runNo > 0
                ? $"[connection to {t.DisplayName} lost. The command keeps running there as run #{runNo}; when it reconnects: agentprobe result {runNo}]"
                : $"[connection to {t.DisplayName} lost]");
            return 75;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await t.Channel.SendAsync(new Msg { Type = "cancel", Id = id }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // target already gone
            }

            return 130;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or System.Threading.Channels.ChannelClosedException)
        {
            await sink.Err($"[connection to {t.DisplayName} lost: {ex.Message}]");
            return 75;
        }
        finally
        {
            t.End(id);
            download?.Abort();
            if (source != null) await source.DisposeAsync();
            if (tempZip != null) TryDelete(tempZip);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp file that cannot be removed now is cleaned up by Windows later
        }
    }

    /// <summary>
    /// A file (or zipped folder) arriving from a target. The name the target suggests is reduced to a bare file name,
    /// so whatever it says, the result lands in the folder the user chose and nowhere else. Data goes to a temporary
    /// file first and only takes its final name once it is complete.
    /// </summary>
    private sealed class Download
    {
        private readonly string _final;
        private readonly string _temp;
        private readonly bool _zip;
        private FileStream? _stream;

        private Download(string final, string temp, bool zip)
        {
            _final = final;
            _temp = temp;
            _zip = zip;
            _stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }

        public static Download Begin(string? requested, string? prefix, string? suggestedName, bool zip)
        {
            var name = (prefix ?? "") + SafeName(suggestedName);
            var where = string.IsNullOrEmpty(requested) ? Environment.CurrentDirectory : requested;
            var isFolderTarget = Directory.Exists(where) || where.EndsWith('\\') || where.EndsWith('/');

            // Like `copy`: into an existing folder under the item's own name, otherwise the given path IS the copy.
            string final;
            if (isFolderTarget) final = Path.Combine(where, name);
            else if (prefix == null) final = where;
            else final = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(where)) ?? ".", prefix + Path.GetFileName(where)); // several targets, one requested name
            final = Path.GetFullPath(final);

            if (zip) return new Download(final, Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-pull-{Guid.NewGuid():N}.zip"), true);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            return new Download(final, final + ".part", false);
        }

        private static string SafeName(string? suggested)
        {
            var name = Path.GetFileName((suggested ?? "").Replace('/', '\\').TrimEnd('\\'));
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim(' ', '.');
            return name.Length == 0 ? "file.bin" : name.Length > 120 ? name[..120] : name;
        }

        public void Write(byte[] data) => _stream!.Write(data);

        /// <summary>Completes the transfer and returns a description of where it landed.</summary>
        public string Finish()
        {
            var size = _stream!.Length;
            _stream.Dispose();
            _stream = null;
            if (_zip)
            {
                Directory.CreateDirectory(_final);
                ZipFile.ExtractToDirectory(_temp, _final, overwriteFiles: true); // refuses entries that would leave the folder
                File.Delete(_temp);
                return $"{_final}\\ ({Format.Bytes(size)} zipped)";
            }

            File.Move(_temp, _final, overwrite: true);
            return $"{_final} ({Format.Bytes(size)})";
        }

        public void Abort()
        {
            _stream?.Dispose();
            _stream = null;
            TryDelete(_temp);
        }
    }

    /// <summary>
    /// Runs one tool on one target for the dashboard and returns its combined output.
    /// Files the tool sends back are saved to <paramref name="localPath"/> (a file, or a folder when it ends in a slash).
    /// </summary>
    public async Task<(int code, string text)> RunCapturedAsync(string key, string tool, string[] args, bool background, string localPath, CancellationToken ct)
    {
        if (!_targets.TryGetValue(key, out var record) || record.Connection is not { Alive: true } conn) return (3, "That PC is offline.");
        var sb = new StringBuilder();
        Task Add(string s)
        {
            lock (sb) sb.Append(s);
            return Task.CompletedTask;
        }

        var sink = new RunSink(Add, Add, _ => Task.CompletedTask, p => background ? Task.CompletedTask : Add("saved on this PC: " + p + "\n"));
        var req = new PipeMsg { Op = "run", Tool = tool, Args = args, Background = background, LocalPath = localPath };
        var code = await RunOneAsync(conn, req, sink, ct);
        return (code, sb.ToString());
    }

    /// <summary>
    /// Fan-out: run on many targets at once and print each distinct result once, with the list of targets that produced it.
    /// For a fleet of look-alike PCs this turns hundreds of outputs into a handful of lines.
    /// </summary>
    private async Task<int> RunManyAsync(List<TargetConnection> targets, PipeMsg req, Func<string, Task> output, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(48);
        var results = await Task.WhenAll(targets.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var sb = new StringBuilder();
                Task Add(string s)
                {
                    lock (sb) sb.Append(s);
                    return Task.CompletedTask;
                }

                var sink = new RunSink(Add, Add, _ => Task.CompletedTask, p => Add("saved: " + p + "\n"));
                var code = await RunOneAsync(t, req, sink, ct, blobPrefix: t.DisplayName + "-");
                return (t.DisplayName, code, text: sb.ToString().TrimEnd() + "\n");
            }
            finally
            {
                gate.Release();
            }
        }));

        var worst = 0;
        foreach (var g in results.GroupBy(r => (r.code, r.text)).OrderByDescending(g => g.Count()))
        {
            var names = g.Select(r => r.DisplayName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = string.Join(", ", names.Take(12)) + (names.Count > 12 ? $", +{names.Count - 12} more" : "");
            await output($"== {names.Count} target{(names.Count == 1 ? "" : "s")} (exit {g.Key.code}): {shown}\n{g.Key.text}");
            if (g.Key.code != 0) worst = g.Key.code;
        }

        return worst;
    }

    private async Task<int> WaitForRebootAsync(TargetConnection before, int timeoutSec, int delaySec, Func<string, Task> output, Func<string, Task> err, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSec > 0 ? timeoutSec : 600);
        var goDown = TimeSpan.FromSeconds(delaySec + 90);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (before.Alive && sw.Elapsed < goDown) await Task.Delay(500, ct);
        if (before.Alive)
        {
            await err($"{before.DisplayName} did not go down within {Format.Duration(goDown)} (shutdown blocked or aborted?).");
            return 1;
        }

        var downAt = sw.Elapsed;
        while (sw.Elapsed < timeout)
        {
            if (_targets.TryGetValue(before.RecordKey, out var r) && r.Online && r.Connection != before)
            {
                await output($"{r.Name} is back: down {Format.Duration(sw.Elapsed - downAt)}, reconnected {Format.Duration(sw.Elapsed)} after the reboot command.\n");
                return 0;
            }

            await Task.Delay(1000, ct);
        }

        await err($"{before.DisplayName} did not reconnect within {Format.Duration(timeout)}. It resumes when the same user logs on " +
                  "(enable auto-logon on unattended test PCs). `agentprobe list --all` shows when it was last seen.");
        return 1;
    }

    /// <summary>IPv4 addresses of this PC per adapter, so a person can pick the one the target's network can reach.</summary>
    private static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                // A tunnel adapter is a VPN: say so, because that address is the one a PC across the VPN has to use.
                var kind = Discovery.CarriesBroadcast(nic) ? "" : ", VPN";
                foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                    list.Add($"{ua.Address} ({nic.Name}{kind})");
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // no adapter list
        }

        return list;
    }

    public static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);
}
