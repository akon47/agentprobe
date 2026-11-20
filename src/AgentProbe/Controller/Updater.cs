using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentProbe.Controller;

/// <summary>A published release: its version and where its exe and that exe's SHA-256 are.</summary>
internal sealed record UpdateInfo(string Version, string ExeUrl, string ShaUrl, string PageUrl);

/// <summary>An update that did not go through. <see cref="Changed"/> says whether the exe on disk is still the one from before.</summary>
internal sealed class UpdateFailedException(string message, bool changed) : Exception(message)
{
    public bool Changed { get; } = changed;
}

/// <summary>
/// Updating a program that is one exe and nothing else. The new exe is downloaded next to the running one and
/// checked against the release's SHA-256 (and started once, to see that it runs). A running exe cannot be
/// overwritten but it can be renamed, so the old one is moved aside and the new one takes its name: every later
/// start, including the background process restarting itself, is the new version. What was moved aside is deleted
/// as soon as nothing runs from it any more, so no second file stays behind.
/// </summary>
internal static partial class Updater
{
    public const string ReleaseFeed = "https://api.github.com/repos/akon47/agentprobe/releases/latest";
    private const string ExeAsset = "agentprobe.exe";
    private const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>AGENTPROBE_UPDATE_URL points at another feed of the same shape (a mirror, or the test suite's).</summary>
    private static string? FeedOverride => Environment.GetEnvironmentVariable("AGENTPROBE_UPDATE_URL") is { Length: > 0 } url ? url : null;

    public static string Feed => FeedOverride ?? ReleaseFeed;

    /// <summary>
    /// Whether to look for a new release without being asked. Off with AGENTPROBE_NO_UPDATE_CHECK=1, and for a
    /// separate test instance unless it was given a feed of its own.
    /// </summary>
    public static bool ChecksByItself =>
        Environment.GetEnvironmentVariable("AGENTPROBE_NO_UPDATE_CHECK") != "1" && (FeedOverride != null || AppInfo.Instance.Length == 0);

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Only https, except for a feed on this PC itself (the test suite serves one over http).</summary>
    private static Uri Checked(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InvalidDataException($"refusing the update address {url}: it must be https");
        return uri;
    }

    /// <summary>The newest published release. Throws when it cannot be read: "could not check" is not "up to date".</summary>
    public static async Task<UpdateInfo> LatestAsync(CancellationToken ct)
    {
        using var client = Client();
        using var response = await client.GetAsync(Checked(Feed), ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{Feed} answered {(int)response.StatusCode} {response.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var version = (root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "").TrimStart('v', 'V');
        if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"the release feed names no usable version (tag '{version}')");

        string? exe = null, sha = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url == null) continue;
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (string.Equals(name, ExeAsset + ".sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
            }
        }

        if (exe == null || sha == null) throw new InvalidDataException($"release {version} has no {ExeAsset} with a .sha256 next to it");
        Checked(exe);
        Checked(sha);
        return new UpdateInfo(version, exe, sha, root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "");
    }

    /// <summary>True when <paramref name="latest"/> is a later version than <paramref name="current"/> (1.2.3, or 1.2.3-beta.1).</summary>
    public static bool IsNewer(string latest, string current)
    {
        static (Version number, bool pre) Parse(string text)
        {
            var dash = text.IndexOf('-');
            return (Version.TryParse(dash < 0 ? text : text[..dash], out var v) ? v : new Version(0, 0), dash >= 0);
        }

        var (l, lPre) = Parse(latest.TrimStart('v', 'V'));
        var (c, cPre) = Parse(current.TrimStart('v', 'V'));
        return l != c ? l > c : cPre && !lPre; // the final 1.2.3 comes after 1.2.3-beta
    }

    /// <summary>
    /// Downloads the release's exe next to <paramref name="exePath"/>, checks it against the published SHA-256 and
    /// starts it once to see that it runs. Returns the path of the checked file; nothing has been replaced yet.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, string exePath, CancellationToken ct)
    {
        var file = exePath + ".new-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using var client = Client();
            client.Timeout = TimeSpan.FromMinutes(10);
            var expected = Sha256Pattern().Match(await client.GetStringAsync(Checked(info.ShaUrl), ct));
            if (!expected.Success) throw new InvalidDataException("the release's .sha256 file holds no SHA-256");

            using (var response = await client.GetAsync(Checked(info.ExeUrl), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"the download answered {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("the download is implausibly large");
                await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += n) > MaxBytes) throw new InvalidDataException("the download is implausibly large");
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }

            string actual;
            await using (var stream = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the downloaded file does not match the release's SHA-256 (expected {expected.Value.ToLowerInvariant()[..12]}..., got {actual.ToLowerInvariant()[..12]}...)");

            var reported = await ReportedVersionAsync(file, ct);
            if (reported == null) throw new InvalidDataException("the downloaded exe does not start on this PC");
            return file;
        }
        catch
        {
            TryDelete(file);
            throw;
        }
    }

    /// <summary>
    /// Downloads, checks and installs a release in the place of <paramref name="exePath"/>. On failure nothing of
    /// the download stays behind, and the exception says whether the exe on disk was touched.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo info, string exePath, CancellationToken ct)
    {
        string file;
        try
        {
            file = await DownloadAsync(info, exePath, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new UpdateFailedException(ex is OperationCanceledException ? "it was cancelled, or the download did not answer in time" : ex.GetBaseException().Message, changed: false);
        }

        try
        {
            Replace(file, exePath);
        }
        catch (UpdateFailedException)
        {
            TryDelete(file);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(file);
            throw new UpdateFailedException($"{exePath} could not be replaced ({ex.Message})", changed: false);
        }
    }

    /// <summary>What <c>&lt;exe&gt; version</c> prints, or null when the file does not run as agentprobe.</summary>
    public static async Task<string?> ReportedVersionAsync(string exe, CancellationToken ct)
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo(exe, "version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(15000);
            var text = await p.StandardOutput.ReadToEndAsync(limit.Token);
            await p.WaitForExitAsync(limit.Token);
            text = text.Trim();
            return p.ExitCode == 0 && VersionPattern().IsMatch(text) ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            // Whatever that file is, it was started only to ask its version: do not leave it running.
            try
            {
                if (p is { HasExited: false }) p.Kill(entireProcessTree: true);
            }
            catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // it ended by itself in the meantime
            }

            ct.ThrowIfCancellationRequested(); // being cancelled is not the same as "does not run"
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in the place of <paramref name="exePath"/>. Processes that run from the old
    /// file keep running; it is moved aside under a name that <see cref="CleanupOld"/> removes later.
    /// </summary>
    public static void Replace(string newExe, string exePath)
    {
        var aside = exePath + ".old-" + DateTime.UtcNow.Ticks.ToString("x");
        File.Move(exePath, aside);
        try
        {
            File.Move(newExe, exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the working exe back: never leave the PC without one. If even that fails, say exactly where it is.
            try
            {
                File.Move(aside, exePath);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                throw new UpdateFailedException(
                    $"the new exe could not be put in place ({ex.Message}) and the previous one could not be moved back ({back.Message}). " +
                    $"The previous exe is now {aside}: rename it to {Path.GetFileName(exePath)}", changed: true);
            }

            throw;
        }
    }

    /// <summary>Deletes what earlier updates moved aside or left half-downloaded, as far as nothing runs from it any more.</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = AppInfo.ExePath;
            var dir = Path.GetDirectoryName(exe);
            if (dir == null) return;
            foreach (var pattern in new[] { ".old-*", ".new-*" })
            {
                foreach (var file in Directory.EnumerateFiles(dir, Path.GetFileName(exe) + pattern))
                {
                    if (!file.Equals(exe, StringComparison.OrdinalIgnoreCase)) TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a folder that cannot be listed has nothing of ours to clean
        }
    }

    /// <summary>
    /// For a process that itself still runs from a file an update moved aside and is about to end: has that file
    /// (and any other leftover) deleted a moment after this process is gone, the way `end --remove-exe` removes the exe.
    /// </summary>
    public static void CleanupOldAfterExit()
    {
        try
        {
            var pattern = AppInfo.ExePath + ".old-*";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{pattern}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the next start of agentprobe removes it
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still running from it, or a download in progress: a later start removes it
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{64}\b")]
    private static partial Regex Sha256Pattern();
}
