using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgentProbe.Transport;

internal sealed class ControllerInfo
{
    public required string Name { get; init; }

    public required string User { get; init; }

    public required string Fingerprint { get; init; }

    public required IPEndPoint Endpoint { get; init; }
}

/// <summary>
/// LAN discovery without opening any port on the target.
/// The target broadcasts a probe from an ephemeral port; Windows Firewall allows unicast replies to a broadcast
/// for a few seconds by default (UnicastResponseToMulticast), so the controller's reply gets back in.
/// Only the controller listens, so only the developer PC needs a firewall rule.
/// </summary>
internal static class Discovery
{
    private static readonly byte[] ProbeBytes = Encoding.ASCII.GetBytes($"agentprobe/{AppInfo.ProtocolVersion} probe");
    private const string ReplyPrefix = "agentprobe-here ";

    /// <summary>Target side: keeps probing until cancelled, reporting every controller that answers.</summary>
    public static async Task ProbeAsync(Action<ControllerInfo> found, IReadOnlyList<string> extraHosts, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var port = AppInfo.Port;
        var receive = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    r = await udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue; // ICMP port unreachable from a unicast probe shows up here
                }

                var info = ParseReply(r.Buffer, r.RemoteEndPoint.Address);
                if (info != null) found(info);
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            foreach (var target in Targets(extraHosts, port))
            {
                try
                {
                    await udp.SendAsync(ProbeBytes, target, ct);
                }
                catch (SocketException)
                {
                    // network down or unreachable subnet; try again next round
                }
            }

            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await receive.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static IEnumerable<IPEndPoint> Targets(IReadOnlyList<string> extraHosts, int port)
    {
        var seen = new HashSet<IPAddress> { IPAddress.Broadcast };
        yield return new IPEndPoint(IPAddress.Broadcast, port);

        // Subnet-directed broadcasts: the limited broadcast only leaves through one adapter on multi-homed PCs.
        foreach (var nic in SafeInterfaces())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 255)) continue;
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                var addr = new IPAddress(b);
                if (seen.Add(addr)) yield return new IPEndPoint(addr, port);
            }
        }

        foreach (var host in extraHosts)
        {
            var (h, p) = SplitHostPort(host, port);
            IPAddress[] addrs;
            try
            {
                addrs = IPAddress.TryParse(h, out var a) ? [a] : Dns.GetHostAddresses(h);
            }
            catch (SocketException)
            {
                continue;
            }

            foreach (var a in addrs.Where(x => x.AddressFamily == AddressFamily.InterNetwork))
                yield return new IPEndPoint(a, p);
        }
    }

    public static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        var i = value.LastIndexOf(':');
        return i > 0 && int.TryParse(value[(i + 1)..], out var p) ? (value[..i], p) : (value, defaultPort);
    }

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var n in all)
            if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                yield return n;
    }

    private static ControllerInfo? ParseReply(byte[] data, IPAddress from)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            if (!text.StartsWith(ReplyPrefix, StringComparison.Ordinal)) return null;
            var d = JsonSerializer.Deserialize(text[ReplyPrefix.Length..], JsonCtx.Default.DictionaryStringString);
            if (d == null || !d.TryGetValue("fp", out var fp) || !int.TryParse(d.GetValueOrDefault("port"), out var port)) return null;
            if (port is < 1 or > 65535 || fp.Length is < 8 or > 64) return null; // anyone on the LAN can send this packet
            return new ControllerInfo
            {
                Name = d.GetValueOrDefault("name") ?? "?",
                User = d.GetValueOrDefault("user") ?? "?",
                Fingerprint = fp,
                Endpoint = new IPEndPoint(from, port),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Controller side: answers probes until cancelled.</summary>
    public static async Task RespondAsync(string fingerprint, int tcpPort, CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, AppInfo.Port));
        var reply = Encoding.UTF8.GetBytes(ReplyPrefix + JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["name"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["fp"] = fingerprint,
                ["port"] = tcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ver"] = AppInfo.Version,
            },
            JsonCtx.Default.DictionaryStringString));

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (r.Buffer.AsSpan().SequenceEqual(ProbeBytes))
            {
                try
                {
                    await udp.SendAsync(reply, r.RemoteEndPoint, ct);
                }
                catch (SocketException)
                {
                    // target went away
                }
            }
        }
    }
}
