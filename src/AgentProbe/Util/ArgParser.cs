namespace AgentProbe.Util;

/// <summary>
/// Minimal option parser: <c>--key value</c>, <c>--key=value</c>, boolean flags and positionals.
/// Flags (options without a value) must be declared so <c>--tree foo</c> is not read as tree=foo.
/// </summary>
internal sealed class ArgParser
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _all = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];

    public static ArgParser Parse(IReadOnlyList<string> args, params string[] flagNames)
    {
        var known = new HashSet<string>(flagNames, StringComparer.OrdinalIgnoreCase);
        var p = new ArgParser();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                for (i++; i < args.Count; i++) p.Positionals.Add(args[i]);
                break;
            }

            if (a.Length > 1 && a[0] == '-' && !IsNumber(a))
            {
                var name = a.TrimStart('-');
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (value == null && known.Contains(name))
                {
                    p._flags.Add(name);
                    continue;
                }

                if (value == null)
                {
                    if (i + 1 < args.Count) value = args[++i];
                    else
                    {
                        p._flags.Add(name);
                        continue;
                    }
                }

                p._options[name] = value;
                if (!p._all.TryGetValue(name, out var values)) p._all[name] = values = [];
                values.Add(value);
                continue;
            }

            p.Positionals.Add(a);
        }

        return p;
    }

    private static bool IsNumber(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    public bool Has(string name) => _flags.Contains(name) || _options.ContainsKey(name);

    public bool Flag(string name) => _flags.Contains(name) || (_options.TryGetValue(name, out var v) && v is "true" or "1" or "yes");

    public string? Get(string name, string? alt = null)
    {
        if (_options.TryGetValue(name, out var v)) return v;
        if (alt != null && _options.TryGetValue(alt, out v)) return v;
        return null;
    }

    /// <summary>Every value of an option that may be given more than once (<c>--env A=1 --env B=2</c>), in order.</summary>
    public IReadOnlyList<string> GetAll(string name) => _all.TryGetValue(name, out var v) ? v : [];

    public int GetInt(string name, int fallback, string? alt = null)
        => int.TryParse(Get(name, alt), out var v) ? v : fallback;

    public double GetDouble(string name, double fallback)
        => double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Parses durations like <c>90</c>, <c>30s</c>, <c>10m</c>, <c>2h</c>, <c>7d</c>.</summary>
    public TimeSpan? GetDuration(string name)
    {
        var v = Get(name);
        return v == null ? null : ParseDuration(v);
    }

    public static TimeSpan? ParseDuration(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.Length == 0) return null;
        if (v.EndsWith("ms", StringComparison.Ordinal))
            return double.TryParse(v[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
        var unit = v[^1];
        var num = char.IsLetter(unit) ? v[..^1] : v;
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
        return unit switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromSeconds(n),
        };
    }
}
