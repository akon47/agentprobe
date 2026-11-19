using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentProbe.Tools;
using AgentProbe.Transport;

namespace AgentProbe.Controller;

/// <summary>
/// The web dashboard for people: every target at a glance (list or a wall of live screen thumbnails), a detail
/// panel per PC, and a console that runs the same commands the CLI does.
/// <para>
/// It is served on 127.0.0.1 only and every request must carry the random token that <c>agentprobe dashboard</c>
/// puts in the URL, so other users and web pages on this PC cannot read or drive anything.
/// Thumbnails are captured on demand, only for tiles the browser says are visible, and are cached briefly so many
/// viewers cost the target one small capture.
/// </para>
/// </summary>
internal sealed class Dashboard(Daemon daemon)
{
    private static readonly TimeSpan ThumbCache = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, (DateTime at, int width, byte[] png)> _thumbs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _thumbLocks = new();
    private readonly Lazy<byte[]> _page = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dashboard.html");
        if (s == null) return Encoding.UTF8.GetBytes("dashboard.html is missing from this build");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    public int Start(CancellationToken ct)
    {
        TcpListener? listener = null;
        var port = AppInfo.Port + 1;
        for (var i = 0; i < 20; i++, port++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                break;
            }
            catch (SocketException)
            {
                listener = null;
            }
        }

        if (listener == null) return 0;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ServeAsync(client, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            listener.Stop();
        }, ct);
        return port;
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            var (method, target, body) = await ReadRequestAsync(stream, ct);
            if (method == null) return;
            var uri = new Uri("http://localhost" + target);
            if (uri.AbsolutePath.StartsWith("/font/", StringComparison.Ordinal))
            {
                await ServeFontAsync(stream, uri.AbsolutePath[6..]);
                return;
            }

            var query = ParseQuery(uri.Query);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(query.GetValueOrDefault("k") ?? ""), Encoding.UTF8.GetBytes(daemon.DashboardToken)))
            {
                await RespondAsync(stream, 403, "text/plain", "Open the dashboard with: agentprobe dashboard"u8.ToArray());
                return;
            }

            var id = query.GetValueOrDefault("id") ?? "";
            switch (uri.AbsolutePath)
            {
                case "/":
                    await RespondAsync(stream, 200, "text/html; charset=utf-8", _page.Value);
                    break;

                case "/api/info":
                {
                    var info = new Dictionary<string, string>
                    {
                        ["exe"] = AppInfo.ExePath,
                        ["onPath"] = UserPath.Contains(Path.GetDirectoryName(AppInfo.ExePath)!) ? "1" : "0",
                        ["version"] = AppInfo.Version,
                    };
                    await RespondAsync(stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(info, JsonCtx.Default.DictionaryStringString));
                    break;
                }

                case "/guide":
                    await RespondAsync(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Cli.Guide()));
                    break;

                case "/api/targets":
                    await RespondAsync(stream, 200, "application/json", Encoding.UTF8.GetBytes(TargetsJson()));
                    break;

                case "/api/name" when method == "POST":
                {
                    var problem = daemon.SetLabel(id, query.GetValueOrDefault("name"));
                    await RespondAsync(stream, problem.Length == 0 ? 200 : 400, "text/plain", Encoding.UTF8.GetBytes(problem));
                    break;
                }

                case "/api/thumb":
                {
                    var width = Math.Clamp(int.TryParse(query.GetValueOrDefault("w"), out var w) ? w : 480, 120, 1920);
                    var png = await ThumbAsync(id, width, query.ContainsKey("fresh"), ct);
                    if (png == null) await RespondAsync(stream, 404, "text/plain", "no screen available"u8.ToArray());
                    else await RespondAsync(stream, 200, "image/png", png);
                    break;
                }

                case "/api/run" when method == "POST":
                {
                    var (ok, text) = await RunAsync(id, body, query.ContainsKey("bg"), ct);
                    await RespondAsync(stream, ok ? 200 : 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
                    break;
                }

                default:
                    await RespondAsync(stream, 404, "text/plain", "not found"u8.ToArray());
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or UriFormatException or OperationCanceledException or InvalidDataException)
        {
            // browser went away, or sent something that is not HTTP
        }
    }

    /// <summary>The UI typeface is embedded in the exe, so the page needs no internet and looks the same everywhere.</summary>
    private static async Task ServeFontAsync(NetworkStream stream, string name)
    {
        await using var font = name.Contains('/') || name.Contains('\\') ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream("font/" + name);
        if (font == null)
        {
            await RespondAsync(stream, 404, "text/plain", "not found"u8.ToArray());
            return;
        }

        using var ms = new MemoryStream();
        await font.CopyToAsync(ms);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: font/woff2\r\nContent-Length: {ms.Length}\r\nCache-Control: public, max-age=31536000, immutable\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length));
    }

    private static async Task<(string? method, string target, string body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(10000);
        var buffer = new byte[16 * 1024];
        var used = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (used == buffer.Length) throw new InvalidDataException("headers too large");
            var n = await stream.ReadAsync(buffer.AsMemory(used), timeout.Token);
            if (n == 0) return (null, "", "");
            used += n;
            headerEnd = buffer.AsSpan(0, used).IndexOf("\r\n\r\n"u8);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var parts = head[0].Split(' ');
        if (parts.Length < 2) throw new InvalidDataException("bad request line");
        var length = 0;
        foreach (var h in head.Skip(1))
        {
            if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(h[15..].Trim(), out length);
        }

        if (length > 256 * 1024) throw new InvalidDataException("body too large");
        var body = new byte[length];
        var have = Math.Min(length, used - (headerEnd + 4));
        Array.Copy(buffer, headerEnd + 4, body, 0, have);
        while (have < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(have), timeout.Token);
            if (n == 0) break;
            have += n;
        }

        return (parts[0], parts[1], Encoding.UTF8.GetString(body, 0, have));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i > 0) d[Uri.UnescapeDataString(pair[..i])] = Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            else d[Uri.UnescapeDataString(pair)] = "";
        }

        return d;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    private string TargetsJson()
    {
        var now = DateTime.UtcNow;
        var list = daemon.Records.OrderByDescending(r => r.Online).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r =>
        {
            var c = r.Connection;
            var online = r.Online;
            return new Dictionary<string, string>
            {
                ["id"] = r.Key,
                ["name"] = r.Name,
                ["machine"] = r.Machine,
                ["user"] = r.User,
                ["address"] = r.Address,
                ["os"] = r.Os,
                ["version"] = r.Version,
                ["admin"] = r.Admin ? "1" : "0",
                ["key"] = c?.Fingerprint ?? "",
                ["state"] = !online ? "offline" : c!.Busy > 0 ? "busy" : "idle",
                ["since"] = ((long)(now - (online ? c!.ConnectedUtc : r.LastSeenUtc)).TotalSeconds).ToString(),
                ["last"] = online && c!.LastCommand.Length > 0 ? c.LastCommand : r.LastCommand,
                ["lastAge"] = online && c!.LastCommand.Length > 0 ? ((long)(now - c.LastActivityUtc).TotalSeconds).ToString() : "",
                ["cpu"] = online ? c!.Info.GetValueOrDefault("cpu", "") : "",
                ["mem"] = online ? c!.Info.GetValueOrDefault("mem", "") : "",
            };
        }).ToList();
        return JsonSerializer.Serialize(list, JsonCtx.Default.ListDictionaryStringString);
    }

    /// <summary>A small PNG of the target's screen. One capture at a time per target, reused for a few seconds.</summary>
    private async Task<byte[]?> ThumbAsync(string id, int width, bool fresh, CancellationToken ct)
    {
        if (!fresh && _thumbs.TryGetValue(id, out var cached) && cached.width >= width && DateTime.UtcNow - cached.at < ThumbCache) return cached.png;
        var gate = _thumbLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!fresh && _thumbs.TryGetValue(id, out cached) && cached.width >= width && DateTime.UtcNow - cached.at < ThumbCache) return cached.png;
            var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-thumb-{Guid.NewGuid():N}.png");
            try
            {
                var (code, _) = await daemon.RunCapturedAsync(id, "screenshot", ["--thumb", width.ToString()], background: true, file, ct);
                if (code != 0 || !File.Exists(file)) return null;
                var png = await File.ReadAllBytesAsync(file, ct);
                _thumbs[id] = (DateTime.UtcNow, width, png);
                return png;
            }
            finally
            {
                File.Delete(file);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs one command line typed in the dashboard (or a panel refresh when <paramref name="background"/>).</summary>
    private async Task<(bool ok, string text)> RunAsync(string id, string commandLine, bool background, CancellationToken ct)
    {
        var (tool, args, error) = CommandLine.Parse(commandLine);
        if (tool == null || error != null) return (false, error ?? "unknown command");
        if (tool.Name == "push") return (false, "push sends a file from this PC: use the terminal for it (agentprobe push <local> [remote]).");

        // Files a command sends back (screenshot, dump, pull) land in the user's Downloads folder.
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", AppInfo.Name);
        Directory.CreateDirectory(downloads);
        var (code, text) = await daemon.RunCapturedAsync(id, tool.Name, args, background, downloads + Path.DirectorySeparatorChar, ct);
        if (text.Length > 400_000) text = text[..200_000] + "\n[... output cut ...]\n" + text[^100_000..];
        if (code != 0 && !background) text = text.TrimEnd() + $"\n[exit {code}]";
        return (code != 3, text);
    }
}
