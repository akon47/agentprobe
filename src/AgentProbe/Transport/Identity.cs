using System.Security.Cryptography;
using AgentProbe.Util;

namespace AgentProbe.Transport;

/// <summary>
/// A long-term P-256 signing key, like an SSH host key. The controller keeps one per user;
/// a target creates a fresh one each run and only writes it to disk (DPAPI-protected) across a reboot.
/// </summary>
internal sealed class Identity : IDisposable
{
    private Identity(ECDsa key)
    {
        Key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Fingerprint = FingerprintOf(PublicKey);
    }

    public ECDsa Key { get; }

    public byte[] PublicKey { get; }

    public string Fingerprint { get; }

    public static Identity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static Identity FromPrivateKey(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new Identity(key);
    }

    public byte[] ExportPrivateKey() => Key.ExportPkcs8PrivateKey();

    /// <summary>Loads (or creates) the identity stored at <paramref name="path"/>, protected with DPAPI.</summary>
    public static Identity LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path)) return FromPrivateKey(Dpapi.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or System.ComponentModel.Win32Exception)
        {
            // Unreadable (e.g. copied from another account): replace it.
        }

        var id = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Dpapi.Protect(id.ExportPrivateKey()));
        return id;
    }

    /// <summary>Short human-comparable fingerprint: first 8 bytes of SHA-256 over the public key, as XXXX-XXXX-XXXX-XXXX.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var hex = Convert.ToHexString(hash, 0, 8);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    public void Dispose() => Key.Dispose();
}
