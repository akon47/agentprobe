using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentProbe.Transport;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>
/// Updates, seen from the background process: it looks for a new release now and then, replaces the exe when asked,
/// hands over to a fresh copy of itself, and can bring a connected PC to this PC's version.
/// </summary>
internal sealed partial class Daemon
{
    private static string HandoverPath => Path.Combine(AppInfo.StateDir, "handover.json");

    /// <summary>Exit code of an update that did nothing because something is running; --force goes ahead.</summary>
    public const int HeldBack = 4;

    private UpdateInfo? _latest;
    private string _updateError = "";
    private int _updating;

    /// <summary>True once this process has installed an update and is about to make way for its successor.</summary>
    public bool Leaving { get; private set; }

    /// <summary>The version a release offers when that is newer than this exe, else "".</summary>
    public string NewerVersion => _latest is { } latest && Updater.IsNewer(latest.Version, AppInfo.Version) ? latest.Version : "";

    public string ReleasePage => _latest?.PageUrl ?? "";

    /// <summary>Connected PCs whose agentprobe is another version than this PC's, as "name (version)".</summary>
    public List<string> OtherVersions() =>
        _targets.Values.Where(r => r.Online && r.Version != AppInfo.Version).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => $"{r.Name} ({r.Version})").ToList();

    public (int online, int busy) Load()
    {
        var online = _targets.Values.Where(r => r.Online).ToList();
        return (online.Count, online.Sum(r => r.Connection!.Busy));
    }

    private static void WaitForExit(int pid, int milliseconds)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(milliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    private async Task WatchForUpdatesAsync()
    {
        // What an update moved aside can go once nothing runs from it: a command that was still running from the
        // old exe when this process started has ended a little later.
        _ = Task.Run(async () =>
        {
            foreach (var seconds in new[] { 8, 60 })
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Updater.CleanupOld();
            }
        });

        if (!Updater.ChecksByItself) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token); // not in the way of whatever started this process
            while (true)
            {
                await CheckForUpdateAsync(_stop.Token);
                await Task.Delay(TimeSpan.FromHours(12), _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>Asks the release feed. False when it could not be read; <see cref="_updateError"/> then says why.</summary>
    private async Task<bool> CheckForUpdateAsync(CancellationToken ct)
    {
        try
        {
            _latest = await Updater.LatestAsync(ct);
            _updateError = "";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or IOException or TaskCanceledException)
        {
            _updateError = ex is TaskCanceledException ? "no answer in time" : ex.GetBaseException().Message;
            Log("update check failed: " + _updateError);
            return false;
        }
    }

    /// <summary>
    /// `agentprobe update`: replaces this PC's exe with the newest release and restarts this process from it.
    /// Commands running on connected PCs are not harmed by that, but whoever waits for one here loses the
    /// connection for a moment, so that case needs <paramref name="force"/> and is explained first.
    /// </summary>
    public async Task<int> UpdateSelfAsync(bool checkOnly, bool force, bool ko, Func<string, Task> output, Func<string, Task> error, CancellationToken ct)
    {
        string L(string english, string korean) => ko ? korean : english;
        if (!await CheckForUpdateAsync(ct))
        {
            await error(L($"Could not check for a new version: {_updateError}", $"새 버전을 확인하지 못했습니다: {_updateError}"));
            return 1;
        }

        var latest = _latest!;
        if (!Updater.IsNewer(latest.Version, AppInfo.Version))
        {
            await output(L($"agentprobe {AppInfo.Version} is the newest version.\n", $"agentprobe {AppInfo.Version}이(가) 최신 버전입니다.\n"));
            return 0;
        }

        if (checkOnly)
        {
            await output(L(
                $"agentprobe {latest.Version} is available (this PC has {AppInfo.Version}). Install it with: agentprobe update\n",
                $"agentprobe {latest.Version}이(가) 나왔습니다 (이 PC는 {AppInfo.Version}). 설치: agentprobe update\n") + (latest.PageUrl.Length > 0 ? latest.PageUrl + "\n" : ""));
            return 0;
        }

        var (online, busy) = Load();
        if (busy > 0 && !force)
        {
            await error(BusyNotice(ko) + L("\nTo update now anyway: agentprobe update --force", "\n그래도 지금 업데이트하려면: agentprobe update --force"));
            return HeldBack;
        }

        if (Interlocked.Exchange(ref _updating, 1) != 0)
        {
            await error(L("An update is already in progress.", "이미 업데이트가 진행 중입니다."));
            return 1;
        }

        var leaving = false;
        try
        {
            await output(L($"downloading agentprobe {latest.Version}...\n", $"agentprobe {latest.Version} 내려받는 중...\n"));
            await Updater.InstallAsync(latest, AppInfo.ExePath, ct);
            Log($"updated {AppInfo.ExePath} from {AppInfo.Version} to {latest.Version}; restarting");

            // The successor is started before anything is promised; it waits until this process has left.
            if (StartSuccessor() is { } problem)
            {
                await error(L(
                    $"{AppInfo.ExePath} is now agentprobe {latest.Version}, but the background process could not be restarted from it ({problem}). It keeps running as {AppInfo.Version}; `agentprobe stop` ends it, and the next command then starts the new version.",
                    $"{AppInfo.ExePath}은(는) 이제 agentprobe {latest.Version}이지만, 백그라운드 프로세스를 새 exe로 다시 시작하지 못했습니다 ({problem}). {AppInfo.Version}(으)로 계속 실행됩니다. `agentprobe stop`으로 끝내면 다음 명령부터 새 버전이 뜹니다."));
                return 1;
            }

            leaving = Leaving = true;
            await output(L(
                $"updated {AppInfo.ExePath} from {AppInfo.Version} to {latest.Version}. agentprobe's background process restarts now.\n",
                $"{AppInfo.ExePath}을(를) {AppInfo.Version}에서 {latest.Version}(으)로 업데이트했습니다. agentprobe의 백그라운드 프로세스가 지금 다시 시작됩니다.\n"));
            if (online > 0)
            {
                await output(L(
                    $"{online} connected PC{(online == 1 ? "" : "s")} will reconnect by {(online == 1 ? "itself" : "themselves")} within seconds. `agentprobe -t all update` brings them to {latest.Version} too.\n",
                    $"연결된 PC {online}대는 몇 초 안에 스스로 다시 연결됩니다. `agentprobe -t all update`로 그 PC들도 {latest.Version}(으)로 올릴 수 있습니다.\n"));
            }

            LeaveSoon();
            return 0;
        }
        catch (UpdateFailedException ex)
        {
            Log("update failed: " + ex.Message);
            await error(ex.Changed
                ? L($"The update failed: {ex.Message}", $"업데이트에 실패했습니다: {ex.Message}")
                : L($"The update failed and nothing was changed: {ex.Message}", $"업데이트에 실패했고 아무것도 바뀌지 않았습니다: {ex.Message}"));
            return 1;
        }
        finally
        {
            if (!leaving) Interlocked.Exchange(ref _updating, 0); // whatever happened, the next attempt may run
        }
    }

    /// <summary>What an update means while commands are running, for whoever is about to start one.</summary>
    public string BusyNotice(bool ko)
    {
        var busyOn = _targets.Values.Where(r => r.Online && r.Connection!.Busy > 0).Select(r => $"{r.Name} ({r.Connection!.Busy})").ToList();
        return ko
            ? $"지금 실행 중인 명령이 있습니다: {string.Join(", ", busyOn)}.\n" +
              "업데이트하면 이 PC의 agentprobe 백그라운드 프로세스가 다시 시작됩니다. 대상 PC에서 돌던 명령은 계속 돌고 대상 PC는 몇 초 안에 스스로 다시 연결되지만,\n" +
              "여기서 그 명령을 기다리던 agentprobe 호출은 \"connection lost\"와 실행 번호를 남기고 끝납니다 (결과는 나중에 `agentprobe result <번호>`로 받습니다)."
            : $"Commands are running right now: {string.Join(", ", busyOn)}.\n" +
              "Updating restarts agentprobe's background process on this PC. The commands keep running on the target and each PC reconnects by itself within seconds,\n" +
              "but an agentprobe call that is waiting for one here ends with \"connection lost\" and its run number (`agentprobe result <n>` fetches the result afterwards).";
    }

    /// <summary>
    /// Starts the new exe as the next background process; it waits until this one has left. The dashboard's
    /// address is passed on, so a page that is open in a browser keeps working across the restart.
    /// Returns why it could not be started, or null.
    /// </summary>
    private string? StartSuccessor()
    {
        try
        {
            var note = new Dictionary<string, string> { ["token"] = DashboardToken, ["until"] = DateTime.UtcNow.AddSeconds(60).Ticks.ToString() };
            File.WriteAllText(HandoverPath, JsonSerializer.Serialize(note, JsonCtx.Default.DictionaryStringString));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the dashboard page then has to be opened again; the update itself is done
        }

        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"daemon --after {Environment.ProcessId}") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log("could not start the updated background process: " + ex.Message);
            try
            {
                File.Delete(HandoverPath);
            }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
            {
                // it expires by itself within a minute
            }

            return ex.Message;
        }
    }

    /// <summary>Leaves the field to the successor that <see cref="StartSuccessor"/> started.</summary>
    private void LeaveSoon()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(400); // let the answer to whoever asked get out first

            // Close the connections properly instead of just vanishing: a command that is waiting here then ends
            // with "connection lost ... run #N", which is what tells its caller how to fetch the result later.
            foreach (var record in _targets.Values) record.Connection?.Abort();
            await Task.Delay(700);
            Environment.Exit(0);
        });
    }

    /// <summary>The other half of <see cref="HandOver"/>: keep the dashboard address of the process this one replaces.</summary>
    private void TakeOverFromUpdate()
    {
        try
        {
            if (!File.Exists(HandoverPath)) return;
            var note = JsonSerializer.Deserialize(File.ReadAllText(HandoverPath), JsonCtx.Default.DictionaryStringString);
            File.Delete(HandoverPath);
            if (note != null && long.TryParse(note.GetValueOrDefault("until"), out var until) && DateTime.UtcNow.Ticks < until
                && note.GetValueOrDefault("token") is { Length: 24 } token && token.All(Uri.IsHexDigit))
            {
                DashboardToken = token;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a new address is fine too
        }
    }

    /// <summary>`agentprobe -t &lt;name&gt; update`: sends this PC's exe to connected PCs and has each restart from it.</summary>
    private async Task<int> UpdateTargetsAsync(string? spec, bool checkOnly, bool force, bool ko, Func<string, Task> output, Func<string, Task> error, CancellationToken ct)
    {
        var targets = Resolve(spec, out var problem, ko);
        if (targets.Count == 0)
        {
            await error(problem);
            return 3;
        }

        if (checkOnly)
        {
            // Only say what an update would do: nothing is sent and nothing restarts.
            foreach (var t in targets)
            {
                var theirs = t.Info.GetValueOrDefault("ver", "?");
                await output(theirs == AppInfo.Version
                    ? (ko ? $"{t.DisplayName}: agentprobe {theirs}, 이 PC와 같습니다.\n" : $"{t.DisplayName}: agentprobe {theirs}, the same as this PC.\n")
                    : (ko ? $"{t.DisplayName}: agentprobe {theirs}, 이 PC는 {AppInfo.Version}. 맞추려면: agentprobe -t {t.DisplayName} update\n"
                          : $"{t.DisplayName}: agentprobe {theirs}, this PC has {AppInfo.Version}. To bring it to that: agentprobe -t {t.DisplayName} update\n"));
            }

            return 0;
        }

        // Several PCs are updated side by side (each one takes a transfer and a restart), a handful at a time.
        using var gate = new SemaphoreSlim(8);
        var results = await Task.WhenAll(targets.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await UpdateTargetAsync(t, force, ko, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        var worst = 0;
        foreach (var (code, text) in results)
        {
            await (code == 0 ? output(text + "\n") : error(text));
            if (code != 0) worst = code;
        }

        return worst;
    }

    /// <summary>Brings one connected PC to this PC's version. The session there resumes by itself, approval included.</summary>
    public async Task<(int code, string text)> UpdateTargetAsync(TargetConnection t, bool force, bool ko, CancellationToken ct)
    {
        string L(string english, string korean) => ko ? korean : english;
        var name = t.DisplayName;
        var theirs = t.Info.GetValueOrDefault("ver", "?");
        if (theirs == AppInfo.Version && !force)
        {
            // Same number is not proof of the same file (two builds of one version), but it is the normal case.
            return (0, L(
                $"{name} already runs agentprobe {theirs}. (To send this PC's exe anyway, for another build of the same version: --force)",
                $"{name}은(는) 이미 agentprobe {theirs}입니다. (같은 버전의 다른 빌드라서 그래도 이 PC의 exe를 보내려면: --force)"));
        }

        if (Updater.IsNewer(theirs, AppInfo.Version) && !force)
        {
            return (HeldBack, L(
                $"{name} runs agentprobe {theirs}, which is newer than this PC's {AppInfo.Version}. Update this PC first (agentprobe update), or pass --force to put {AppInfo.Version} there.",
                $"{name}의 agentprobe {theirs}이(가) 이 PC의 {AppInfo.Version}보다 새 버전입니다. 이 PC를 먼저 업데이트하거나(agentprobe update), --force로 {AppInfo.Version}을(를) 그 PC에 넣으세요."));
        }

        if (t.Busy > 0 && !force)
        {
            return (HeldBack, L(
                $"{name} is running {t.Busy} command{(t.Busy == 1 ? "" : "s")} right now. Updating restarts agentprobe on that PC: what runs there is no longer followed and its result is not delivered. " +
                $"Wait for it, or update anyway with: agentprobe -t {name} update --force",
                $"{name}에서 지금 명령 {t.Busy}개가 실행 중입니다. 업데이트하면 그 PC의 agentprobe가 다시 시작되어, 돌던 명령을 더는 따라가지 못하고 결과도 받지 못합니다. " +
                $"끝나기를 기다리거나, 그래도 하려면: agentprobe -t {name} update --force"));
        }

        var said = new StringBuilder();
        Task Collect(string s)
        {
            lock (said) said.Append(s);
            return Task.CompletedTask;
        }

        var sink = new RunSink(Collect, Collect, _ => Task.CompletedTask, _ => Task.CompletedTask);
        const string pushed = "agentprobe-update.exe";
        var code = await RunOneAsync(t, new PipeMsg { Op = "push", LocalPath = AppInfo.ExePath, RemotePath = pushed }, sink, ct);
        if (code != 0) return (code, L($"{name}: could not send the new exe: ", $"{name}: 새 exe를 보내지 못했습니다: ") + said.ToString().Trim());

        said.Clear();
        code = await RunOneAsync(t, new PipeMsg { Op = "run", Tool = "selfupdate", Args = force ? [pushed, "--force"] : [pushed] }, sink, ct);
        if (code == 127)
        {
            return (code, L(
                $"{name} runs agentprobe {theirs}, which cannot replace itself yet. Copy the new exe there by hand once; from then on this command does it.",
                $"{name}의 agentprobe {theirs}은(는) 아직 스스로 교체하지 못합니다. 이번 한 번만 새 exe를 직접 복사해 주세요. 그 뒤로는 이 명령으로 됩니다."));
        }

        if (code != 0) return (code, $"{name}: " + said.ToString().Trim());

        // The PC restarts agentprobe and dials in again; say so only once that has happened.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (_targets.Values.FirstOrDefault(x => x.Online && x.Connection != t && x.Connection!.Fingerprint == t.Fingerprint) is { } r)
            {
                // What the target itself reported about things it stopped or could no longer follow goes along.
                var told = said.ToString();
                var also = told.IndexOf(" Stopped or reverted", StringComparison.Ordinal) is >= 0 and var at1 ? told[at1..].Trim()
                    : told.IndexOf(" NO LONGER FOLLOWED", StringComparison.Ordinal) is >= 0 and var at2 ? told[at2..].Trim() : "";
                return r.Version == AppInfo.Version
                    ? (0, L($"{r.Name} is back on agentprobe {r.Version} (was {theirs}); the session and its workspace carried over.", $"{r.Name}이(가) agentprobe {r.Version}(으)로 돌아왔습니다 (이전 {theirs}). 세션과 작업 폴더는 그대로입니다.") + (also.Length > 0 ? " " + also : ""))
                    : (1, L($"{r.Name} reconnected, but with agentprobe {r.Version} instead of {AppInfo.Version}.", $"{r.Name}이(가) 다시 연결됐지만 agentprobe {AppInfo.Version}이(가) 아니라 {r.Version}입니다."));
            }

            await Task.Delay(500, ct);
        }

        return (1, L(
            $"{name} was told to restart with the new exe but has not reconnected within 60s. Look at its agentprobe window; `agentprobe list --all` shows when it was last seen.",
            $"{name}에 새 exe로 다시 시작하라고 했지만 60초 안에 다시 연결되지 않았습니다. 그 PC의 agentprobe 창을 확인하세요. `agentprobe list --all`이 마지막으로 본 시각을 보여 줍니다."));
    }
}
