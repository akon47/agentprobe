using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using AgentProbe.Transport;

namespace AgentProbe.Controller;

/// <summary>What the daemon forwards to a waiting CLI for one request.</summary>
internal readonly record struct Reply(Msg? Message, byte[]? Data, bool Eof);

/// <summary>A connected target PC as the daemon sees it: identity, facts from its hello, and in-flight requests.</summary>
internal sealed class TargetConnection(SecureChannel channel, IPAddress address, Dictionary<string, string> info)
{
    /// <summary>A request whose client stopped reading is failed once this many replies are waiting for it.</summary>
    private const int MaxQueuedReplies = 20000;

    private readonly ConcurrentDictionary<int, Channel<Reply>> _pending = new();
    private readonly ConcurrentDictionary<int, bool> _isForeground = new();
    private readonly CancellationTokenSource _abort = new();
    private int _nextId;
    private int _foreground;

    public SecureChannel Channel { get; } = channel;

    public IPAddress Address { get; } = address;

    public Dictionary<string, string> Info { get; } = info;

    public string Fingerprint => Channel.PeerFingerprint;

    public string Machine => Info.GetValueOrDefault("name", "?");

    public string MachineId => Info.GetValueOrDefault("machine", Fingerprint);

    /// <summary>The key this connection is registered under in the daemon (set by the daemon).</summary>
    public string RecordKey { get; set; } = "";

    /// <summary>Label set on the developer PC (wins) or given on the target with --name.</summary>
    public string? Label { get; set; }

    /// <summary>The name used to address this target: its label, else its machine name (made unique by the daemon).</summary>
    public string DisplayName { get; set; } = "";

    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>When the last frame of any kind arrived. The target sends a heartbeat every 5 s.</summary>
    public DateTime LastFrameUtc { get; private set; } = DateTime.UtcNow;

    public string LastCommand { get; private set; } = "";

    /// <summary>Commands in flight that someone asked for (dashboard refreshes do not count as "busy").</summary>
    public int Busy => _foreground;

    public bool Alive { get; private set; } = true;

    public bool IsAdmin => Info.GetValueOrDefault("admin") == "1";

    public (int id, ChannelReader<Reply> replies) Begin(string description, bool background = false)
    {
        var id = Interlocked.Increment(ref _nextId);

        // Unbounded: the one frame reader must never wait for a slow client, or every other command on this
        // target would freeze with it. A client that stops reading is failed instead (see PumpAsync).
        // (Not SingleReader: that variant cannot report how many items are waiting, which PumpAsync needs.)
        var ch = System.Threading.Channels.Channel.CreateUnbounded<Reply>(new UnboundedChannelOptions { SingleWriter = true });
        _pending[id] = ch;
        if (!background)
        {
            _isForeground[id] = true;
            Interlocked.Increment(ref _foreground);
            LastActivityUtc = DateTime.UtcNow;
            if (description.Length > 0) LastCommand = description;
        }

        return (id, ch.Reader);
    }

    public void End(int id)
    {
        if (_pending.TryRemove(id, out var ch)) ch.Writer.TryComplete();
        if (_isForeground.TryRemove(id, out _))
        {
            Interlocked.Decrement(ref _foreground);
            LastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Drops the connection: the frame reader stops and every waiting request is told the link is gone.</summary>
    public void Abort()
    {
        Alive = false;
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }
    }

    /// <summary>Reads frames until the target disconnects, routing each to the request it belongs to.</summary>
    public async Task PumpAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token);
        try
        {
            while (await Channel.ReceiveAsync(linked.Token) is { } frame)
            {
                LastFrameUtc = DateTime.UtcNow;
                if (frame.Message is { Type: "stat", Info: { } stat })
                {
                    // Only the two numbers the dashboard shows, and only if they really are numbers:
                    // nothing a target sends is trusted to be harmless text.
                    foreach (var key in new[] { "cpu", "mem" })
                        if (stat.TryGetValue(key, out var value) && int.TryParse(value, out var n) && n is >= 0 and <= 100) Info[key] = n.ToString();
                    continue;
                }

                var id = frame.Message?.Id ?? frame.DataId;
                if (!_pending.TryGetValue(id, out var ch)) continue;
                if (ch.Reader.Count > MaxQueuedReplies)
                {
                    ch.Writer.TryComplete(new IOException("the client stopped reading this command's output"));
                    continue;
                }

                ch.Writer.TryWrite(new Reply(frame.Message, frame.Data, frame.Eof));
            }
        }
        finally
        {
            Alive = false;
            foreach (var ch in _pending.Values) ch.Writer.TryComplete();
        }
    }
}
