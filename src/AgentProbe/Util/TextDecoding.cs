using System.Text;

namespace AgentProbe.Util;

/// <summary>
/// Decodes child-process output line by line: strict UTF-8 first, then the OEM code page.
/// Native tools on non-English Windows often write in the OEM code page (e.g. 949 on Korean Windows)
/// even when PowerShell itself was switched to UTF-8.
/// </summary>
internal sealed class LineDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)Native.Win32.GetOEMCP());
        }
        catch
        {
            return Encoding.Latin1;
        }
    });

    private readonly List<byte> _pending = [];

    /// <summary>Feeds bytes, returns the complete lines (with trailing newline) decoded so far.</summary>
    public string Feed(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            _pending.AddRange(data[start..(i + 1)]);
            sb.Append(Decode(_pending));
            _pending.Clear();
            start = i + 1;
        }

        if (start < data.Length) _pending.AddRange(data[start..]);
        return sb.ToString();
    }

    /// <summary>Returns whatever is left without a trailing newline.</summary>
    public string Flush()
    {
        if (_pending.Count == 0) return "";
        var s = Decode(_pending);
        _pending.Clear();
        return s;
    }

    private static string Decode(List<byte> bytes)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
        string s;
        try
        {
            s = StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            s = Oem.Value.GetString(span);
        }

        // Agents only need \n. Remove a BOM and every carriage return that merely ends the line: CRLF, and the
        // CR CR LF that C programs writing "\r\n" in text mode produce. A CR followed by more text is different:
        // that is a progress bar redrawing the line, and only the last version is kept.
        s = s.Replace("\uFEFF", "");
        var newline = s.EndsWith('\n');
        s = (newline ? s[..^1] : s).TrimEnd('\r');
        var cr = s.LastIndexOf('\r');
        if (cr >= 0) s = s[(cr + 1)..];
        if (newline) s += "\n";
        return s;
    }
}
