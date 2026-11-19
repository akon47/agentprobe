using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AgentProbe.Native;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Tools;

/// <summary>Who is listening, who is connected to what, and can this PC reach a given endpoint.</summary>
internal static class NetTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("net", "System", "Listening ports and connections per process; reachability checks",
            """
            net [--name PROCESS | --pid N] [--port N] [--listen] [--all]
              Listeners, plus established connections summarized per process (--all lists each connection).
            net check <host:port> [more...]
              From the target: DNS lookup, TCP connect and time for each endpoint; also shows proxy settings.
              Answers "the app cannot reach the server on that PC" without guessing.
            """, NetAsync) { Quiet = true },
    ];

    private sealed record Conn(string Proto, string Local, int LocalPort, string Remote, int RemotePort, int State, int Pid);

    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static unsafe List<Conn> Connections()
    {
        var list = new List<Conn>();
        foreach (var (af, isUdp) in new[] { (2, false), (23, false), (2, true), (23, true) })
        {
            uint size = 0;
            if (isUdp) Win32.GetExtendedUdpTable(null, ref size, false, af, 1, 0);
            else Win32.GetExtendedTcpTable(null, ref size, false, af, 5, 0);
            if (size == 0) continue;
            var buffer = new byte[size + 4096];
            size = (uint)buffer.Length;
            fixed (byte* b = buffer)
            {
                var rc = isUdp ? Win32.GetExtendedUdpTable(b, ref size, false, af, 1, 0) : Win32.GetExtendedTcpTable(b, ref size, false, af, 5, 0);
                if (rc != 0) continue;
                var count = *(uint*)b;
                var row = b + 4;
                for (var i = 0; i < count; i++)
                {
                    if (!isUdp && af == 2)
                    {
                        list.Add(new Conn("tcp", new IPAddress(*(uint*)(row + 4)).ToString(), Port(*(uint*)(row + 8)), new IPAddress(*(uint*)(row + 12)).ToString(), Port(*(uint*)(row + 16)), (int)*(uint*)row, (int)*(uint*)(row + 20)));
                        row += 24;
                    }
                    else if (!isUdp)
                    {
                        list.Add(new Conn("tcp6", new IPAddress(new ReadOnlySpan<byte>(row, 16)).ToString(), Port(*(uint*)(row + 20)), new IPAddress(new ReadOnlySpan<byte>(row + 24, 16)).ToString(), Port(*(uint*)(row + 44)), (int)*(uint*)(row + 48), (int)*(uint*)(row + 52)));
                        row += 56;
                    }
                    else if (af == 2)
                    {
                        list.Add(new Conn("udp", new IPAddress(*(uint*)row).ToString(), Port(*(uint*)(row + 4)), "", 0, 0, (int)*(uint*)(row + 8)));
                        row += 12;
                    }
                    else
                    {
                        list.Add(new Conn("udp6", new IPAddress(new ReadOnlySpan<byte>(row, 16)).ToString(), Port(*(uint*)(row + 20)), "", 0, 0, (int)*(uint*)(row + 24)));
                        row += 28;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>True when any process listens on that TCP port, on any address (IPv4 or IPv6). Does not connect to it.</summary>
    public static bool IsListening(int port) => Connections().Any(c => c.Proto.StartsWith("tcp", StringComparison.Ordinal) && c.State == 2 && c.LocalPort == port);

    private static string State(int s) => s switch
    {
        2 => "listen",
        3 => "syn-sent",
        4 => "syn-rcvd",
        5 => "established",
        6 or 7 => "fin-wait",
        8 => "close-wait",
        9 or 10 => "closing",
        11 => "time-wait",
        _ => "closed",
    };

    private static Task<int> NetAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "listen", "all");
        if (a.Positionals.Count > 0 && a.Positionals[0].Equals("check", StringComparison.OrdinalIgnoreCase)) return CheckAsync(ctx, a.Positionals.Skip(1).ToList());

        var names = ProcessSnapshot.Take().ToDictionary(p => p.Pid, p => p.Name);
        IEnumerable<Conn> conns = Connections();
        if (a.Get("pid") is { } pidText && int.TryParse(pidText, out var pid)) conns = conns.Where(c => c.Pid == pid);
        if (a.Get("name") is { } name) conns = conns.Where(c => names.GetValueOrDefault(c.Pid, "").Contains(name.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase));
        if (a.Get("port") is { } portText && int.TryParse(portText, out var port)) conns = conns.Where(c => c.LocalPort == port || c.RemotePort == port);
        var all = conns.ToList();
        string Owner(Conn c) => $"{names.GetValueOrDefault(c.Pid, "?")}({c.Pid})";

        var listeners = all.Where(c => c.Proto.StartsWith("tcp", StringComparison.Ordinal) && c.State == 2)
            .GroupBy(c => (c.LocalPort, c.Pid)).OrderBy(g => g.Key.LocalPort).ToList();
        ctx.Out.WriteLine($"listening (tcp): {listeners.Count}");
        var lt = new Table(">PORT", "BIND", "PROCESS");
        foreach (var g in listeners) lt.Add(g.Key.LocalPort, string.Join(",", g.Select(c => c.Local is "0.0.0.0" or "::" ? "any" : c.Local).Distinct()), Owner(g.First()));
        if (lt.Count > 0) ctx.Out.Write(lt.ToString());
        if (a.Flag("listen")) return Task.FromResult(0);

        var live = all.Where(c => c.Proto.StartsWith("tcp", StringComparison.Ordinal) && c.State != 2).ToList();
        ctx.Out.WriteLine();
        if (a.Flag("all") || a.Has("pid") || a.Has("name") || a.Has("port"))
        {
            ctx.Out.WriteLine($"connections (tcp): {live.Count}");
            var t = new Table("PROCESS", "LOCAL", "REMOTE", "STATE");
            foreach (var c in live.OrderBy(c => c.Pid).Take(300)) t.Add(Owner(c), $"{c.Local}:{c.LocalPort}", $"{c.Remote}:{c.RemotePort}", State(c.State));
            if (t.Count > 0) ctx.Out.Write(t.ToString());
        }
        else
        {
            ctx.Out.WriteLine($"connections (tcp): {live.Count}, by process (--all or --name lists each one)");
            var t = new Table("PROCESS", ">COUNT", "REMOTE ENDPOINTS");
            foreach (var g in live.GroupBy(c => c.Pid).OrderByDescending(g => g.Count()).Take(25))
            {
                var remotes = g.Where(c => !IPAddress.IsLoopback(IPAddress.Parse(c.Remote))).GroupBy(c => $"{c.Remote}:{c.RemotePort}").Select(x => x.Key).Take(4).ToList();
                t.Add(Owner(g.First()), g.Count(), remotes.Count > 0 ? string.Join(" ", remotes) : "(loopback only)");
            }

            if (t.Count > 0) ctx.Out.Write(t.ToString());
        }

        var udp = all.Count(c => c.Proto.StartsWith("udp", StringComparison.Ordinal));
        ctx.Out.WriteLine($"udp endpoints: {udp}");
        return Task.FromResult(0);
    }

    private static async Task<int> CheckAsync(ToolContext ctx, List<string> endpoints)
    {
        if (endpoints.Count == 0) return ctx.Fail("net check: give at least one host:port.");
        using (var inet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            var proxy = inet?.GetValue("ProxyEnable") is 1 ? $"system proxy {inet.GetValue("ProxyServer")}" : "no system proxy";
            var pac = inet?.GetValue("AutoConfigURL") is string url ? $", PAC {url}" : "";
            var env = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            ctx.Out.WriteLine($"{proxy}{pac}{(env != null ? $", env proxy {env}" : "")}");
        }

        var failed = 0;
        foreach (var ep in endpoints)
        {
            var spec = ep.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(ep, UriKind.Absolute, out var uri) ? $"{uri.Host}:{uri.Port}" : ep;
            var i = spec.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(spec[(i + 1)..], out var port))
            {
                ctx.Out.WriteLine($"{ep}: expected host:port");
                failed++;
                continue;
            }

            var host = spec[..i];
            IPAddress[] addresses;
            var sw = Stopwatch.StartNew();
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ctx.Ct);
            }
            catch (SocketException ex)
            {
                ctx.Out.WriteLine($"{ep}: DNS FAILED ({ex.SocketErrorCode})");
                failed++;
                continue;
            }

            var dnsMs = sw.ElapsedMilliseconds;
            var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            sw.Restart();
            try
            {
                using var c = new TcpClient(address.AddressFamily);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
                cts.CancelAfter(5000);
                await c.ConnectAsync(address, port, cts.Token);
                ctx.Out.WriteLine($"{ep}: OK  {address} connect {sw.ElapsedMilliseconds}ms (dns {dnsMs}ms{(addresses.Length > 1 ? $", {addresses.Length} addresses" : "")})");
            }
            catch (OperationCanceledException)
            {
                ctx.Out.WriteLine($"{ep}: TIMEOUT connecting to {address} after 5s (filtered by a firewall, or host down)");
                failed++;
            }
            catch (SocketException ex)
            {
                ctx.Out.WriteLine($"{ep}: FAILED {address} {ex.SocketErrorCode}{(ex.SocketErrorCode == SocketError.ConnectionRefused ? " (host is up, nothing listens on that port)" : "")}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
