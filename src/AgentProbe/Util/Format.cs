using System.Globalization;
using System.Text;

namespace AgentProbe.Util;

/// <summary>Compact, token-friendly formatting helpers.</summary>
internal static class Format
{
    public static string Bytes(long b)
    {
        double v = b;
        if (v < 1024) return b + "B";
        v /= 1024;
        if (v < 1024) return v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "K";
        v /= 1024;
        if (v < 1024) return v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "M";
        v /= 1024;
        return v.ToString(v < 10 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + "G";
    }

    public static string Mb(long b) => (b / (1024.0 * 1024)).ToString(b < 10L * 1024 * 1024 ? "0.#" : "0", CultureInfo.InvariantCulture);

    public static string SignedBytes(long b) => (b >= 0 ? "+" : "-") + Bytes(Math.Abs(b));

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return $"{(int)t.TotalMilliseconds}ms";
        if (t.TotalMinutes < 1) return t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        return $"{(int)t.TotalDays}d{t.Hours:00}h";
    }

    public static string Pct(double v) => v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

    public static string Time(DateTime t) => t.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A time shown next to <paramref name="reference"/>: only the time of day when both are on the same day.</summary>
    public static string TimeAfter(DateTime t, DateTime reference)
        => t.ToLocalTime().Date == reference.ToLocalTime().Date ? t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : Time(t);

    /// <summary>An uptime as h:mm:ss, with days in front when it is that long: 0:00:14, 8:14:39, 3d 04:05:06.</summary>
    public static string Uptime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
}

/// <summary>Space-aligned table. Columns are only as wide as their widest cell, the last column is never padded.</summary>
internal sealed class Table
{
    private readonly string[] _headers;
    private readonly bool[] _rightAlign;
    private readonly List<string[]> _rows = [];

    public Table(params string[] headers)
    {
        _headers = headers;
        _rightAlign = new bool[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            if (headers[i].StartsWith('>'))
            {
                _rightAlign[i] = true;
                _headers[i] = headers[i][1..];
            }
        }
    }

    public int Count => _rows.Count;

    public void Add(params object?[] cells)
    {
        var row = new string[_headers.Length];
        for (var i = 0; i < row.Length; i++) row[i] = i < cells.Length ? Convert.ToString(cells[i], CultureInfo.InvariantCulture) ?? "" : "";
        _rows.Add(row);
    }

    public override string ToString()
    {
        var widths = new int[_headers.Length];
        for (var i = 0; i < widths.Length; i++) widths[i] = _headers[i].Length;
        foreach (var r in _rows)
            for (var i = 0; i < widths.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);

        var sb = new StringBuilder();
        Append(sb, _headers, widths);
        foreach (var r in _rows) Append(sb, r, widths);
        return sb.ToString();
    }

    private void Append(StringBuilder sb, string[] cells, int[] widths)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            var last = i == cells.Length - 1;
            if (_rightAlign[i]) sb.Append(cells[i].PadLeft(widths[i]));
            else sb.Append(last ? cells[i] : cells[i].PadRight(widths[i]));
            if (!last) sb.Append("  ");
        }

        // Trim trailing padding.
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        sb.Append('\n');
    }
}
