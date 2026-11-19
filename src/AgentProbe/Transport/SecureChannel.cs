using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentProbe.Transport;

/// <summary>One decrypted frame: either a JSON message or a data chunk belonging to request <see cref="DataId"/>.</summary>
internal readonly record struct Frame(Msg? Message, int DataId, byte[]? Data, bool Eof);

/// <summary>
/// Mutually authenticated, encrypted channel over a TCP stream (SSH-like, no certificates).
/// <para>
/// Handshake: both sides send a long-term P-256 public key plus an ephemeral ECDH key, then each signs the transcript
/// with its long-term key. The initiator (target) only proceeds if the responder's key is the one the human approved.
/// Traffic keys come from HKDF over the ephemeral ECDH secret, so every connection has fresh keys (forward secrecy).
/// Frames are AES-256-GCM with a per-direction counter nonce.
/// </para>
/// </summary>
internal sealed class SecureChannel : IAsyncDisposable
{
    private const uint Magic = 0x42525041; // "APRB"
    private const int MaxFrame = 4 << 20;
    private const int TagSize = 16;
    private const byte KindMessage = 1;
    private const byte KindData = 2;

    private readonly Stream _stream;
    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _lenBuf = new byte[4];
    private ulong _sendCounter;
    private ulong _recvCounter;
    private volatile bool _broken;

    private SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey, byte[] peerKey)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
        PeerPublicKey = peerKey;
        PeerFingerprint = Identity.FingerprintOf(peerKey);
    }

    public byte[] PeerPublicKey { get; }

    public string PeerFingerprint { get; }

    /// <summary>Target side. <paramref name="acceptPeer"/> sees the responder's long-term key before anything is signed.</summary>
    public static async Task<SecureChannel> InitiateAsync(Stream stream, Identity me, Func<byte[], bool> acceptPeer, CancellationToken ct)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var m1 = BuildHello(me.PublicKey, eph.ExportSubjectPublicKeyInfo());
        await WriteBlobAsync(stream, m1, ct);

        var m2 = await ReadBlobAsync(stream, ct);
        var (peerStatic, peerEph, sigOffset) = ParseHello(m2);
        if (!acceptPeer(peerStatic)) throw new AuthenticationFailedException("The developer PC's key is not the one that was approved.");

        var m2Body = m2.AsSpan(0, sigOffset);
        var sig = ReadSigned(m2, sigOffset);
        using (var peerKey = ECDsa.Create())
        {
            peerKey.ImportSubjectPublicKeyInfo(peerStatic, out _);
            if (!peerKey.VerifyHash(Transcript(m1, m2Body, "responder"), sig, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new AuthenticationFailedException("Bad signature from the developer PC.");
        }

        var mySig = me.Key.SignHash(Transcript(m1, m2, "initiator"), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await WriteBlobAsync(stream, mySig, ct);

        var (i2r, r2i) = DeriveKeys(eph, peerEph, m1, m2);
        return new SecureChannel(stream, i2r, r2i, peerStatic);
    }

    /// <summary>Controller side. Accepts any target key; the target identifies itself by that key.</summary>
    public static async Task<SecureChannel> RespondAsync(Stream stream, Identity me, CancellationToken ct)
    {
        var m1 = await ReadBlobAsync(stream, ct);
        var (peerStatic, peerEph, _) = ParseHello(m1, hasSignature: false);

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var body = BuildHello(me.PublicKey, eph.ExportSubjectPublicKeyInfo());
        var sig = me.Key.SignHash(Transcript(m1, body, "responder"), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var m2 = new byte[body.Length + 2 + sig.Length];
        body.CopyTo(m2, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(m2.AsSpan(body.Length), (ushort)sig.Length);
        sig.CopyTo(m2, body.Length + 2);
        await WriteBlobAsync(stream, m2, ct);

        var peerSig = await ReadBlobAsync(stream, ct);
        using (var peerKey = ECDsa.Create())
        {
            peerKey.ImportSubjectPublicKeyInfo(peerStatic, out _);
            if (!peerKey.VerifyHash(Transcript(m1, m2, "initiator"), peerSig, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new AuthenticationFailedException("Bad signature from the target PC.");
        }

        var (i2r, r2i) = DeriveKeys(eph, peerEph, m1, m2);
        return new SecureChannel(stream, r2i, i2r, peerStatic);
    }

    public Task SendAsync(Msg msg, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, JsonCtx.Default.Msg);
        var payload = new byte[json.Length + 1];
        payload[0] = KindMessage;
        json.CopyTo(payload, 1);
        return WriteFrameAsync(payload, ct);
    }

    public Task SendDataAsync(int id, ReadOnlySpan<byte> data, bool eof, CancellationToken ct = default)
    {
        var payload = new byte[data.Length + 6];
        payload[0] = KindData;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), id);
        payload[5] = eof ? (byte)1 : (byte)0;
        data.CopyTo(payload.AsSpan(6));
        return WriteFrameAsync(payload, ct);
    }

    /// <summary>Returns null when the peer closed the connection.</summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            await _stream.ReadExactlyAsync(_lenBuf, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var len = BinaryPrimitives.ReadInt32LittleEndian(_lenBuf);
        if (len < TagSize + 1 || len > MaxFrame + TagSize) throw new InvalidDataException("Bad frame length.");
        var buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            await _stream.ReadExactlyAsync(buf.AsMemory(0, len), ct);
            var plain = new byte[len - TagSize];
            _recv.Decrypt(Nonce(_recvCounter++), buf.AsSpan(0, len - TagSize), buf.AsSpan(len - TagSize, TagSize), plain);
            return plain[0] switch
            {
                KindMessage => new Frame(JsonSerializer.Deserialize(plain.AsSpan(1), JsonCtx.Default.Msg), 0, null, false),
                KindData => new Frame(null, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(1)), plain[6..], plain[5] != 0),
                _ => throw new InvalidDataException("Unknown frame kind."),
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private async Task WriteFrameAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length > MaxFrame) throw new ArgumentException("Frame too large.");
        var frame = new byte[4 + plain.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, plain.Length + TagSize);
        await _writeLock.WaitAsync(ct); // waiting for our turn may be cancelled; a frame, once started, may not
        try
        {
            if (_broken) throw new IOException("The connection is closed.");
            _send.Encrypt(Nonce(_sendCounter++), plain, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, TagSize));
            try
            {
                // No cancellation here: a frame cut off halfway, or a counter used for a frame that never went out,
                // would leave the peer unable to decrypt anything that follows.
                await _stream.WriteAsync(frame, CancellationToken.None);
                await _stream.FlushAsync(CancellationToken.None);
            }
            catch
            {
                _broken = true;
                try
                {
                    _stream.Dispose();
                }
                catch (IOException)
                {
                    // already gone
                }

                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(n.AsSpan(4), counter);
        return n;
    }

    private static byte[] BuildHello(byte[] staticKey, byte[] ephKey)
    {
        var buf = new byte[4 + 1 + 2 + staticKey.Length + 2 + ephKey.Length + 32];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        s[4] = AppInfo.ProtocolVersion;
        var o = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)staticKey.Length);
        staticKey.CopyTo(s[(o + 2)..]);
        o += 2 + staticKey.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)ephKey.Length);
        ephKey.CopyTo(s[(o + 2)..]);
        o += 2 + ephKey.Length;
        RandomNumberGenerator.Fill(s[o..]);
        return buf;
    }

    private static (byte[] staticKey, byte[] ephKey, int end) ParseHello(byte[] m, bool hasSignature = true)
    {
        var s = m.AsSpan();
        if (s.Length < 7 || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) throw new InvalidDataException("Not an agentprobe peer.");
        if (s[4] != AppInfo.ProtocolVersion) throw new InvalidDataException($"Protocol version mismatch (peer {s[4]}, this {AppInfo.ProtocolVersion}). Use the same agentprobe version on both PCs.");
        // Everything here is unauthenticated input: check every length before using it.
        var o = 5;
        var a = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        if (a == 0 || o + 2 + a + 2 > s.Length) throw new InvalidDataException("Malformed hello.");
        var staticKey = s.Slice(o + 2, a).ToArray();
        o += 2 + a;
        var b = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        if (b == 0 || o + 2 + b + 32 > s.Length) throw new InvalidDataException("Malformed hello.");
        var ephKey = s.Slice(o + 2, b).ToArray();
        o += 2 + b + 32;
        if (!hasSignature && o != s.Length) throw new InvalidDataException("Malformed hello.");
        return (staticKey, ephKey, o);
    }

    private static byte[] ReadSigned(byte[] m, int offset)
    {
        if (offset + 2 > m.Length) throw new InvalidDataException("Malformed hello.");
        var len = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(offset));
        if (len == 0 || offset + 2 + len > m.Length) throw new InvalidDataException("Malformed hello.");
        return m.AsSpan(offset + 2, len).ToArray();
    }

    private static byte[] Transcript(ReadOnlySpan<byte> m1, ReadOnlySpan<byte> m2, string role)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData("agentprobe-handshake-v1"u8);
        h.AppendData(m1);
        h.AppendData(m2);
        h.AppendData(Encoding.ASCII.GetBytes(role));
        return h.GetHashAndReset();
    }

    private static (byte[] i2r, byte[] r2i) DeriveKeys(ECDiffieHellman mine, byte[] peerEph, byte[] m1, byte[] m2)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerEph, out _);
        var secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
        var salt = Transcript(m1, m2, "keys");
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, "agentprobe traffic keys v1"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return (okm[..32], okm[32..]);
    }

    private static async Task WriteBlobAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(data, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadBlobAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        var n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n is <= 0 or > 8192) throw new InvalidDataException("Malformed handshake.");
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch
        {
            // already gone
        }

        _send.Dispose();
        _recv.Dispose();
    }
}

internal sealed class AuthenticationFailedException(string message) : Exception(message);
