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

/// <summary>One IPv4 network this PC is on, and how a developer PC is looked for there.</summary>
/// <param name="Cidr">For example 192.168.0.0/24.</param>
/// <param name="Adapter">The adapter's name as Windows shows it.</param>
/// <param name="Broadcast">The subnet's broadcast address, or null when the network has none (/31, /32).</param>
/// <param name="SweptAddresses">How many addresses get their own probe; 0 when the network is too large for that.</param>
/// <param name="CarriesBroadcast">False for tunnel adapters (most VPNs), which deliver unicast packets only.</param>
internal sealed record SearchNetwork(string Cidr, string Adapter, IPAddress? Broadcast, int SweptAddresses, bool CarriesBroadcast)
{
    /// <summary>False when no probe can reach a developer PC on this network: it has to be named with <c>share &lt;address&gt;</c>.</summary>
    public bool Searched => SweptAddresses > 0 || (CarriesBroadcast && Broadcast != null);
}

/// <summary>Where the probes go, and enough about it to tell the person at the target what is and is not searched.</summary>
internal sealed class SearchPlan
{
    public List<SearchNetwork> Networks { get; } = [];

    public List<IPEndPoint> Broadcasts { get; } = [];

    /// <summary>One endpoint per address of every network small enough to try address by address.</summary>
    public List<IPEndPoint> Sweep { get; } = [];

    /// <summary>The addresses given on the command line (<c>share &lt;address&gt;</c>).</summary>
    public List<IPEndPoint> Hosts { get; } = [];

    /// <summary>Names given on the command line that did not resolve to an IPv4 address.</summary>
    public List<string> Unresolved { get; } = [];
}

/// <summary>
/// Discovery without opening any port on the target.
/// The target sends a probe from an ephemeral port and the controller answers it; the answer gets back in because
/// Windows Firewall lets replies to a PC's own outgoing packets through (for a broadcast: UnicastResponseToMulticast,
/// a few seconds by default). Only the controller listens, so only the developer PC needs a firewall rule.
/// Probes go out as broadcasts and, because tunnel adapters (VPNs) carry neither broadcast nor multicast, also to
/// every single address of each network small enough for that.
/// </summary>
internal static class Discovery
{
    private static readonly byte[] ProbeBytes = Encoding.ASCII.GetBytes($"agentprobe/{AppInfo.ProtocolVersion} probe");
    private const string ReplyPrefix = "agentprobe-here ";

    /// <summary>Networks up to this size (/22 = 1022 addresses) are probed address by address.</summary>
    public const int MinSweepPrefix = 22;

    /// <summary>Upper bound on one sweep over all adapters together.</summary>
    public const int MaxSweepAddresses = 4096;

    private const int RoundMs = 2000;
    private const int SweepEveryMs = 6000;        // while no developer PC answers
    private const int SweepEveryQuietMs = 30000;  // once one does: only to notice another one arriving
    private const int AnsweredRecentlyMs = 10000;
    private const int ForgetResponderMs = 30000;

    /// <summary>Target side: keeps probing until cancelled, reporting every controller that answers.</summary>
    /// <param name="connected">
    /// True while a session is running. The address-by-address sweep stops then: the developer PC is known and is
    /// still probed directly, and a sweep every half minute for hours would look like a port scan to a managed network.
    /// </param>
    public static async Task ProbeAsync(Action<ControllerInfo> found, IReadOnlyList<string> extraHosts, Func<bool> connected, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        IgnorePortUnreachable(udp.Client);
        var port = AppInfo.Port;

        // Whoever answered is probed directly every round from then on, so the sweep can slow down.
        var responders = new System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, long>();
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
                if (info == null) continue;
                responders[r.RemoteEndPoint] = Environment.TickCount64;
                found(info);
            }
        }, ct);

        var lastSweep = long.MinValue / 2;
        while (!ct.IsCancellationRequested)
        {
            var plan = Plan(extraHosts, port);
            var now = Environment.TickCount64;
            var answered = false;
            foreach (var (endpoint, at) in responders)
            {
                if (now - at > ForgetResponderMs) responders.TryRemove(endpoint, out _);
                else if (now - at < AnsweredRecentlyMs) answered = true;
            }

            var targets = new List<IPEndPoint>(plan.Broadcasts);
            targets.AddRange(plan.Hosts);
            targets.AddRange(responders.Keys);
            if (!connected() && now - lastSweep >= (answered ? SweepEveryQuietMs : SweepEveryMs))
            {
                targets.AddRange(plan.Sweep);
                lastSweep = now;
            }

            try
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    try
                    {
                        await udp.SendAsync(ProbeBytes, targets[i], ct);
                    }
                    catch (SocketException)
                    {
                        // network down or unreachable subnet; try again next round
                    }

                    if (i % 128 == 127) await Task.Delay(5, ct); // a sweep is a few thousand packets: do not send it as one burst
                }

                await Task.Delay(RoundMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await receive.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// A swept address that exists but runs no agentprobe answers "port unreachable", which Windows reports as a
    /// failed receive on this socket. Those answers mean nothing here, so they are switched off (SIO_UDP_CONNRESET).
    /// </summary>
    private static void IgnorePortUnreachable(Socket socket)
    {
        try
        {
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // the receive loop copes with them anyway
        }
    }

    /// <summary>Works out where to probe from the adapters as they are right now (a VPN may connect later).</summary>
    public static SearchPlan Plan(IReadOnlyList<string> extraHosts, int port)
    {
        var plan = new SearchPlan();
        var broadcasts = new HashSet<IPAddress> { IPAddress.Broadcast };
        var networks = new HashSet<string>();
        plan.Broadcasts.Add(new IPEndPoint(IPAddress.Broadcast, port));

        foreach (var nic in SafeInterfaces())
        {
            UnicastIPAddressInformationCollection addresses;
            try
            {
                addresses = nic.GetIPProperties().UnicastAddresses;
            }
            catch (NetworkInformationException)
            {
                continue; // the adapter went away while it was being read
            }

            foreach (var ua in addresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ua.Address.GetAddressBytes());
                var mask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ua.IPv4Mask.GetAddressBytes());
                var prefix = System.Numerics.BitOperations.PopCount(mask);
                if (prefix == 0 || mask != uint.MaxValue << (32 - prefix)) continue; // no mask, or not a prefix
                var first = ip & mask;
                var last = first | ~mask;
                var cidr = $"{ToAddress(first)}/{prefix}";
                if (!networks.Add(cidr)) continue;

                // Subnet-directed broadcast: the limited broadcast only leaves through one adapter on multi-homed PCs.
                IPAddress? broadcast = null;
                if (prefix <= 30)
                {
                    broadcast = ToAddress(last);
                    if (broadcasts.Add(broadcast)) plan.Broadcasts.Add(new IPEndPoint(broadcast, port));
                    first++; // the network and broadcast addresses are not hosts
                    last--;
                }

                var swept = 0;
                var count = prefix == 32 ? 0 : (int)Math.Min(last - first + 1, int.MaxValue);
                if (count > 0 && prefix >= MinSweepPrefix && plan.Sweep.Count + count <= MaxSweepAddresses)
                {
                    for (var a = first; a <= last; a++) plan.Sweep.Add(new IPEndPoint(ToAddress(a), port));
                    swept = count;
                }

                plan.Networks.Add(new SearchNetwork(cidr, nic.Name, broadcast, swept, CarriesBroadcast(nic)));
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
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                addrs = [];
            }

            var v4 = addrs.Where(x => x.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (v4.Count == 0) plan.Unresolved.Add(host);
            foreach (var a in v4) plan.Hosts.Add(new IPEndPoint(a, p));
        }

        return plan;
    }

    private static IPAddress ToAddress(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return new IPAddress(b);
    }

    /// <summary>Ethernet and Wi-Fi deliver a broadcast to the whole subnet; PPP and tunnel adapters (VPNs) do not.</summary>
    public static bool CarriesBroadcast(NetworkInterface nic) => nic.NetworkInterfaceType
        is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT
        or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211;

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

            // Anyone on a searched network can send this packet, and what it says is shown to the person who
            // decides whom to trust: the key must look like a key, and the names are plain short text, with nothing
            // in them that a console would act on (an escape sequence could hide or rewrite the line).
            if (port is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(fp, @"\A[0-9A-F]{4}(-[0-9A-F]{4}){3}\z")) return null;
            static string Plain(string? text)
            {
                var clean = new string((text ?? "").Where(c => !char.IsControl(c) && c != '\u001b' && !char.IsSurrogate(c)).Take(40).ToArray()).Trim();
                return clean.Length > 0 ? clean : "?";
            }

            return new ControllerInfo
            {
                Name = Plain(d.GetValueOrDefault("name")),
                User = Plain(d.GetValueOrDefault("user")),
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
