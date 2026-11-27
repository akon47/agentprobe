using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentProbe.Tools;

/// <summary>
/// The text inside a binary: what log messages a deployed build can write, which version string it carries.
/// Runs of ASCII/UTF-8 and of UTF-16 text, and for a .NET assembly the string literals of its code (the #US heap),
/// which are found by the metadata, not by guessing.
/// </summary>
internal static class BinaryStrings
{
    private const long MaxBytes = 256L << 20;

    public sealed record Found(long Offset, string Kind, string Text);

    public sealed class Result
    {
        public List<Found> Matches { get; } = [];

        /// <summary>How many strings of the minimum length there are in all (matching or not).</summary>
        public int Total { get; set; }

        public int Literals { get; set; }

        /// <summary>Only the first part of a big file was searched.</summary>
        public long? SearchedBytes { get; set; }
    }

    public static Result Find(string path, Regex re, int minLength)
    {
        var result = new Result();
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var take = (int)Math.Min(fs.Length, MaxBytes);
            if (fs.Length > take) result.SearchedBytes = take;
            bytes = new byte[take];
            fs.ReadExactly(bytes, 0, take);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(long offset, string kind, string text)
        {
            result.Total++;
            if (re.IsMatch(text) && seen.Add(text)) result.Matches.Add(new Found(offset, kind, text));
        }

        var literals = Literals(bytes, out var heapFrom, out var heapTo);
        foreach (var (offset, text) in literals)
        {
            result.Literals++;
            if (text.Length >= minLength) Add(offset, "#US", text);
        }

        foreach (var (offset, text) in Utf8Runs(bytes, minLength)) Add(offset, text.All(c => c < 128) ? "ascii" : "utf8", text);
        foreach (var align in new[] { 0, 1 })
        {
            foreach (var (offset, text) in Utf16Runs(bytes, align, minLength))
            {
                if (offset >= heapFrom && offset < heapTo) continue; // the literals, read above with their real boundaries
                Add(offset, "utf16", text);
            }
        }

        result.Matches.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        return result;
    }

    /// <summary>The string literals of a .NET assembly, with the file offset of each (after its length byte or bytes).</summary>
    private static List<(long offset, string text)> Literals(byte[] bytes, out long heapFrom, out long heapTo)
    {
        heapFrom = heapTo = -1;
        var list = new List<(long, string)>();
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, false));
            if (!pe.HasMetadata) return list;
            var md = pe.GetMetadataReader();
            var heapStart = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.UserString);
            var handle = MetadataTokens.UserStringHandle(1);
            var size = md.GetHeapSize(HeapIndex.UserString);
            (heapFrom, heapTo) = (heapStart, heapStart + size);
            while (!handle.IsNil && MetadataTokens.GetHeapOffset(handle) < size)
            {
                var text = md.GetUserString(handle);
                var at = heapStart + MetadataTokens.GetHeapOffset(handle);
                list.Add((at + (text.Length * 2 + 1 < 0x80 ? 1 : text.Length * 2 + 1 < 0x4000 ? 2 : 4), text));
                handle = md.GetNextHandle(handle);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            // not .NET, or metadata it cannot read: the scans below still run
        }

        return list;
    }

    private static bool Printable(int c) => c == '\t' || (c >= 0x20 && c != 0x7F && !(c >= 0x80 && c < 0xA0) && !(c >= 0xD800 && c <= 0xDFFF) && !(c >= 0xE000 && c <= 0xF8FF) && c != 0xFFFD && c != 0xFEFF
                                                       && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Format));

    /// <summary>Runs of printable text in UTF-8 (which covers ASCII).</summary>
    private static IEnumerable<(long, string)> Utf8Runs(byte[] b, int minLength)
    {
        var sb = new StringBuilder();
        long start = 0;
        var i = 0;
        while (i < b.Length)
        {
            var c = b[i];
            int code, len;
            if (c < 0x80) (code, len) = (c, 1);
            else if (c >= 0xC2 && c <= 0xDF && i + 1 < b.Length && (b[i + 1] & 0xC0) == 0x80) (code, len) = (((c & 0x1F) << 6) | (b[i + 1] & 0x3F), 2);
            else if (c >= 0xE0 && c <= 0xEF && i + 2 < b.Length && (b[i + 1] & 0xC0) == 0x80 && (b[i + 2] & 0xC0) == 0x80) (code, len) = (((c & 0x0F) << 12) | ((b[i + 1] & 0x3F) << 6) | (b[i + 2] & 0x3F), 3);
            else (code, len) = (-1, 1);
            if (code >= 0 && (len == 1 || code >= (len == 2 ? 0x80 : 0x800)) && Printable(code))
            {
                if (sb.Length == 0) start = i;
                sb.Append((char)code);
            }
            else
            {
                if (sb.Length >= minLength) yield return (start, sb.ToString());
                sb.Clear();
            }

            i += len;
        }

        if (sb.Length >= minLength) yield return (start, sb.ToString());
    }

    /// <summary>Runs of printable UTF-16 (little-endian) text, starting at an even or an odd byte.</summary>
    private static IEnumerable<(long, string)> Utf16Runs(byte[] b, int align, int minLength)
    {
        var sb = new StringBuilder();
        long start = 0;
        int ascii = 0, notFromAscii = 0;
        bool Text()
        {
            // Mostly-ASCII text runs straight into the bytes after it when nothing ends it (a pointer, a length):
            // one or two odd characters at the end of such a run are those bytes, not text.
            if (ascii * 4 >= sb.Length * 3)
                for (var k = 0; k < 2 && sb.Length > 0 && sb[^1] >= 0x2000; k++) sb.Length--;
            return sb.Length >= minLength && (ascii * 2 >= sb.Length || notFromAscii > 0);
        }

        for (var i = align; i + 1 < b.Length; i += 2)
        {
            var code = b[i] | (b[i + 1] << 8);
            if (code >= 0x20 && Printable(code))
            {
                if (sb.Length == 0)
                {
                    start = i;
                    ascii = notFromAscii = 0;
                }

                sb.Append((char)code);
                if (code < 0x80) ascii++;

                // Plain ASCII text read two bytes at a time looks like CJK (U+2020..U+7E7E). A run of such characters
                // only is taken for that, not for text; one character that two ASCII bytes cannot make is enough.
                else if (b[i] < 0x20 || b[i] > 0x7E || b[i + 1] > 0x7E) notFromAscii++;
                continue;
            }

            if (Text()) yield return (start, sb.ToString());
            sb.Clear();
        }

        if (Text()) yield return (start, sb.ToString());
    }
}
