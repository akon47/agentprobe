using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AgentProbe.Util;

namespace AgentProbe.Tools;

internal sealed class EventRecord
{
    public DateTime TimeUtc { get; set; }

    public int Level { get; set; }

    public string Log { get; set; } = "";

    public string Provider { get; set; } = "";

    /// <summary>The classic event source name a provider also has (BugCheck for WER-SystemErrorReporting), or "".</summary>
    public string SourceName { get; set; } = "";

    public int Id { get; set; }

    public long RecordId { get; set; }

    public string Message { get; set; } = "";

    /// <summary>True when <see cref="Message"/> is the provider's own text, not the event's data put together.</summary>
    public bool Rendered { get; set; }

    public List<(string name, string value)> Data { get; } = [];

    public byte[] Binary { get; set; } = [];

    public string? Field(string name, int index)
        => Data.FirstOrDefault(d => d.name.Equals(name, StringComparison.OrdinalIgnoreCase)).value ?? (index < Data.Count ? Data[index].value : null);

    public bool Is(string provider, int id) => Id == id && Provider.Equals(provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// EventLog 6008 ("the previous system shutdown was unexpected"): the last time the PC stamped that it was alive.
    /// Read from the event's binary data (a SYSTEMTIME in UTC), not from the message, whose date and time are
    /// written in the PC's language (with direction marks, and "오후 7:13" for 19:13 on a Korean PC).
    /// </summary>
    public DateTime? LastAliveUtc()
    {
        if (!Is("EventLog", 6008)) return null;
        if (Binary.Length >= 32 && SystemTime(Binary, 16) is { } utc) return utc;
        var text = string.Join(' ', Data.Take(2).Select(d => d.value).Reverse());
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
    }

    private static DateTime? SystemTime(byte[] b, int at)
    {
        int W(int i) => BitConverter.ToUInt16(b, at + (i * 2));
        try
        {
            return new DateTime(W(0), W(1), W(3), W(4), W(5), W(6), W(7), DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Windows event logs, filtered on the target so only the relevant few lines come back.</summary>
internal static partial class EventTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("events", "Logs", "Windows event log entries, filtered (default: errors of the last hour)",
            """
            events [--since 1h] [--level error|warning|info] [--log Application,System | --all-logs] [--provider NAME[,NAME]]
                   [--id 41,6008] [--grep REGEX] [--max 30] [--long] [--data]
              One line per event, newest first: time, level, source, id, start of the message. --long prints whole
              messages, --data the event's own fields as Name=Value (what the message leaves out: Kernel-Power 41's
              BugcheckCode, PowerButtonTimestamp, ...). Other logs work too: --log Microsoft-Windows-Kernel-PnP/Configuration.
              --all-logs searches every log of the PC that holds events. A provider can be given by its classic source
              name (BugCheck); when nothing is found the answer says whether each provider exists on this PC.
            events --around TIME[,TIME...] [--before 2m] [--after 30s] [--all-logs] [--level ..] [--per-window 3]
            events --around unexpected [--since 30d] ...      the unexpected ends `boots` lists
            events --around-file FILE ...                      times in a file on the target, one per line
              "What happened in every log around these moments?" TIME is 2026-11-24 06:35:22, 11-24 06:35:22 or
              06:35:22 (today), local time. Each window runs from --before ahead of the time to --after past it; for
              `unexpected` from --before ahead of the last-alive stamp to the next start. All levels unless --level says
              otherwise. The answer counts which (log, provider, id) came up in how many windows, then the last events of
              each window (newest windows first) and which windows had no event at all.
            """, EventsAsync) { Quiet = true },
        new("crashes", "Logs", "Recent application crashes and hangs, with faulting module and exception code",
            """
            crashes [--since 7d] [--max 20] [name]
              From Application Error / .NET Runtime / Application Hang events, plus crash dump files Windows kept
              (%LOCALAPPDATA%\CrashDumps). `pull` a dump to analyze it on the developer PC.
            crashes --system [--since 30d]
              The PC as a whole: blue screens and resets. Unexpected shutdowns and what Kernel-Power 41 recorded about
              them, bugchecks, kernel dumps, whether a dump could have been written (dump settings, page file, volmgr
              errors), hardware errors (WHEA), Startup Repair. Ends with what that evidence is consistent with and what
              it cannot rule out. `boots` lists the starts and how each ended.
            """, CrashesAsync) { Quiet = true },
    ];

    public static List<EventRecord> Query(string log, string xpath, int max) => Query(log, xpath, max, out _);

    /// <summary>
    /// Reads events through the event log API, newest first unless <paramref name="newestFirst"/> is false.
    /// <paramref name="error"/> is set when the log could not be read, or not to the end. Messages are formatted
    /// for the events <paramref name="withMessage"/> picks (all by default): formatting is the slow part.
    /// </summary>
    public static List<EventRecord> Query(string log, string xpath, int max, out string? error, Func<EventRecord, bool>? withMessage = null, bool newestFirst = true, TimeSpan? limit = null)
    {
        var result = new List<EventRecord>();
        Native.EventLog.Read(log, xpath, max, (xml, message) =>
        {
            foreach (var e in Parse(xml, log))
            {
                if (!e.Rendered && (withMessage == null || withMessage(e)))
                {
                    if (message() is { } text)
                    {
                        e.Message = Clean(text);
                        e.Rendered = true;
                    }
                }

                result.Add(e);
            }

            return true;
        }, out error, newestFirst, limit);
        return result;
    }

    /// <summary>Events out of their XML (one &lt;Event&gt; element after the other), as wevtutil and the event log API write them.</summary>
    public static List<EventRecord> Parse(string xml, string log)
    {
        var result = new List<EventRecord>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { CheckCharacters = false, ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Ignore });
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Event")
                {
                    if (XNode.ReadFrom(reader) is XElement el) result.Add(FromXml(el, log));
                }
                else
                {
                    reader.Read();
                }
            }
        }
        catch (XmlException)
        {
            // return what parsed so far
        }

        return result;
    }

    private static EventRecord FromXml(XElement el, string log)
    {
        var e = new EventRecord { Log = log };
        foreach (var part in el.Elements())
        {
            switch (part.Name.LocalName)
            {
                case "System":
                    foreach (var s in part.Elements())
                    {
                        switch (s.Name.LocalName)
                        {
                            case "Provider":
                                e.Provider = (string?)s.Attribute("Name") ?? "";
                                e.SourceName = (string?)s.Attribute("EventSourceName") ?? "";
                                break;
                            case "EventID":
                                e.Id = int.TryParse(s.Value, out var id) ? id : 0;
                                break;
                            case "Level":
                                e.Level = int.TryParse(s.Value, out var lv) ? lv : 0;
                                break;
                            case "EventRecordID":
                                e.RecordId = long.TryParse(s.Value, out var rid) ? rid : 0;
                                break;
                            case "Channel" when s.Value.Length > 0:
                                e.Log = s.Value;
                                break;
                            case "TimeCreated":
                                if (DateTime.TryParse((string?)s.Attribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) e.TimeUtc = t;
                                break;
                        }
                    }

                    break;
                case "EventData":
                    foreach (var d in part.Elements())
                    {
                        if (d.Name.LocalName == "Data") e.Data.Add(((string?)d.Attribute("Name") ?? "", Clean(d.Value)));
                        else if (d.Name.LocalName == "Binary") e.Binary = Hex(d.Value);
                    }

                    break;
                case "UserData":
                    // <UserData><SomeEvent xmlns=..><Field>value</Field>...: the leaves are the fields.
                    foreach (var leaf in part.Descendants().Where(x => !x.HasElements && x.Parent != part)) e.Data.Add((leaf.Name.LocalName, Clean(leaf.Value)));
                    break;
                case "RenderingInfo":
                    if (part.Elements().FirstOrDefault(x => x.Name.LocalName == "Message") is { } m && m.Value.Length > 0)
                    {
                        e.Message = Clean(m.Value);
                        e.Rendered = true;
                    }

                    break;
            }
        }

        if (e.LastAliveUtc() is { } alive)
        {
            // Its own message has the time in the PC's language and 12-hour form: say it plainly instead.
            e.Message = $"The previous system shutdown was unexpected. Last alive: {alive.ToLocalTime():yyyy-MM-dd HH:mm:ss} (the last time Windows stamped that it was running).";
            e.Rendered = true;
        }

        if (e.Message.Length == 0) e.Message = string.Join(" | ", e.Data.Select(d => d.value).Where(v => v.Length > 0));
        return e;
    }

    private static byte[] Hex(string text)
    {
        text = text.Trim();
        if (text.Length % 2 != 0) return [];
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Takes out the invisible direction marks (U+200E and its kind) Windows puts around dates and numbers in
    /// messages: in a console they come out as '?' or as nothing at all, and they break any search for the date.
    /// </summary>
    public static string Clean(string text) => DirectionMarks().Replace(text, "");

    [GeneratedRegex("[\u200E\u200F\u202A-\u202E\u2066-\u2069\u061C]")]
    private static partial Regex DirectionMarks();

    public static string LevelName(int level) => level switch
    {
        1 => "CRIT",
        2 => "ERROR",
        3 => "WARN",
        _ => "info",
    };

    private static string? LevelCondition(string? level) => (level ?? "error").ToLowerInvariant() switch
    {
        "critical" or "crit" => "Level=1",
        "error" => "(Level=1 or Level=2)",
        "warning" or "warn" => "(Level=1 or Level=2 or Level=3)",
        _ => null,
    };

    private static List<string> Names(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.Replace("'", "")).Where(n => n.Length > 0).ToList();

    /// <summary>The XPath conditions inside System[...] for provider, id and level; the time condition is added by the caller.</summary>
    private static List<string> Conditions(string? level, string? provider, string? id)
    {
        var conditions = new List<string>();
        if (LevelCondition(level) is { } lc) conditions.Add(lc);
        var providers = Names(provider);
        if (providers.Count > 0) conditions.Add("(" + string.Join(" or ", providers.Select(p => $"Provider[@Name='{p}'] or Provider[@EventSourceName='{p}']")) + ")");
        var ids = Names(id).Where(x => int.TryParse(x, out _)).ToList();
        if (ids.Count > 0) conditions.Add("(" + string.Join(" or ", ids.Select(x => "EventID=" + x)) + ")");
        return conditions;
    }

    private static string XPath(TimeSpan since, string? level, string? provider, string? id)
    {
        var conditions = Conditions(level, provider, id);
        conditions.Insert(0, $"TimeCreated[timediff(@SystemTime) <= {(long)since.TotalMilliseconds}]");
        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    /// <summary>Every log of this PC that holds events, and a line about the ones that could not even be looked at (or null).</summary>
    private static List<string> LogsWithEvents(out string? unreadable)
    {
        var logs = new List<string>();
        var why = new Dictionary<string, int>();
        foreach (var name in Native.EventLog.LogNames())
        {
            var count = Native.EventLog.RecordCount(name, out var error);
            if (count == null) why[error ?? "?"] = why.GetValueOrDefault(error ?? "?") + 1;
            else if (count > 0) logs.Add(name);
        }

        unreadable = why.Count == 0 ? null
            : $"{why.Values.Sum()} more logs could not be opened, so it is not known whether they hold events: " + string.Join("; ", why.OrderByDescending(w => w.Value).Select(w => $"{w.Value} {w.Key}"));
        return logs;
    }

    private static Task<int> EventsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "long", "data", "all-logs");
        if (a.Has("around") || a.Has("around-file")) return Task.FromResult(Around(ctx, a));

        var since = a.GetDuration("since") ?? TimeSpan.FromHours(1);
        var max = a.GetInt("max", 30);
        var allLogs = a.Flag("all-logs");
        string? unreadableLogs = null;
        var logs = allLogs ? LogsWithEvents(out unreadableLogs) : Names(a.Get("log") ?? "Application,System");
        var xpath = XPath(since, a.Get("level"), a.Get("provider"), a.Get("id"));
        Regex? re = null;
        if (a.Get("grep") is { } grep)
        {
            try
            {
                re = new Regex(grep, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("events: bad --grep pattern: " + ex.Message));
            }
        }

        var fetch = re != null ? max * 20 : max;
        var fetched = new List<EventRecord>();
        var failures = new List<string>();
        var hitLimit = false;
        foreach (var log in logs)
        {
            var part = Query(log, xpath, fetch, out var queryError);
            if (queryError != null) failures.Add($"{log}: {queryError}");
            if (part.Count >= fetch) hitLimit = true; // there may be more than we asked for
            fetched.AddRange(part);
        }

        // A log that could not be read is reported as such, never as "no events".
        foreach (var failure in failures.Take(allLogs ? 5 : int.MaxValue)) ctx.Err.WriteLine("could not read " + failure);
        if (allLogs && failures.Count > 5) ctx.Err.WriteLine($"… and {failures.Count - 5} more logs could not be read");
        if (logs.Count > 0 && failures.Count == logs.Count) return Task.FromResult(1);

        var events = fetched.Where(e => re == null || re.IsMatch(e.Message) || re.IsMatch(e.Provider))
            .OrderByDescending(e => e.TimeUtc).ToList();
        var where = allLogs ? $"any of the {logs.Count - failures.Count} logs that hold events"
            : string.Join("/", logs.Where(l => !failures.Any(x => x.StartsWith(l + ":", StringComparison.Ordinal))));
        if (events.Count == 0)
        {
            ctx.Out.WriteLine($"no {(a.Get("level") ?? "error")}-level events in {where} during the last {Format.Duration(since)}" +
                              (hitLimit ? $" among the newest {fetch} per log that were examined (raise --max or narrow --since)" : ""));
            foreach (var p in Names(a.Get("provider"))) ctx.Out.WriteLine(ProviderNote(p));
            if (unreadableLogs != null) ctx.Out.WriteLine(unreadableLogs);
            return Task.FromResult(failures.Count > 0 ? 1 : 0);
        }

        // Identical repeats collapse into one line with a count.
        var data = a.Flag("data");
        var groups = events.GroupBy(e => (e.Log, e.Provider, e.Id, e.Level, msg: Format.Clip(e.Message, 70), data: data ? DataLine(e) : "")).Take(max).ToList();
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            var first = g.First();
            var count = g.Count() > 1 ? $" x{g.Count()}" : "";
            sb.Append(EventLine(first, a.Flag("long") ? int.MaxValue : 260, count)).Append('\n');
            if (data) sb.Append("  data: ").Append(g.Key.data.Length > 0 ? g.Key.data : "(none)").Append('\n');
        }

        ctx.Out.Write(sb.ToString());
        if (unreadableLogs != null) ctx.Out.WriteLine(unreadableLogs);
        if (hitLimit || events.Count > groups.Sum(g => g.Count()))
            ctx.Out.WriteLine($"… only the newest {fetch} events per log were examined, so more exist and the xN counts are lower bounds (narrow with --since/--provider/--grep, or raise --max)");
        return Task.FromResult(failures.Count > 0 ? 1 : 0);
    }

    private static string ShortLog(string log) => log switch
    {
        "Application" => "App",
        "System" => "Sys",
        "Security" => "Sec",
        _ => log,
    };

    private static string EventLine(EventRecord e, int messageChars, string count = "")
        => $"{Format.Time(e.TimeUtc)} {LevelName(e.Level)} {ShortLog(e.Log)} {e.Provider}#{e.Id}{count}: " +
           (messageChars == int.MaxValue ? e.Message.Trim().Replace("\r", "") : Format.Clip(e.Message, messageChars));

    /// <summary>The event's own fields as Name=Value, one line.</summary>
    public static string DataLine(EventRecord e)
    {
        var parts = e.Data.Select((d, i) => $"{(d.name.Length > 0 ? d.name : $"[{i}]")}={Format.Clip(d.value, 100)}").ToList();
        if (e.Binary.Length > 0) parts.Add($"binary={Convert.ToHexString(e.Binary, 0, Math.Min(e.Binary.Length, 48))}{(e.Binary.Length > 48 ? $"…({e.Binary.Length} bytes)" : "")}");
        return string.Join(" ", parts);
    }

    /// <summary>For a provider that brought no events: is there one by that name on this PC at all?</summary>
    private static string ProviderNote(string name)
    {
        if (Native.EventLog.PublisherExists(name)) return $"provider {name}: registered on this PC";
        using var classic = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog");
        if (classic != null)
        {
            foreach (var log in classic.GetSubKeyNames())
            {
                using var source = classic.OpenSubKey($@"{log}\{name}");
                if (source != null) return $"provider {name}: registered on this PC (a classic event source of the {log} log)";
            }
        }

        var similar = Native.EventLog.PublisherNames().Where(p => p.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return $"provider {name}: NOT registered on this PC, so no event can come from it" + (similar.Count > 0 ? $" (names that contain it: {string.Join(", ", similar)})" : "");
    }

    // ------------------------------------------------------------------ events --around

    private sealed class Window
    {
        public DateTime FromUtc { get; init; }

        public DateTime ToUtc { get; init; }

        public string Label { get; init; } = "";

        public List<EventRecord> Events { get; } = [];
    }

    /// <summary>"2026-11-24 06:35:22", "11-24 06:35:22", "06:35:22" (today), with or without seconds or a T: local time.</summary>
    public static DateTime? ParseLocalTime(string text)
    {
        text = Clean(text).Trim().Replace('T', ' ');
        string[] full = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm"];
        if (DateTime.TryParseExact(text, full, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) return t.ToUniversalTime();
        string[] noYear = ["MM-dd HH:mm:ss", "MM-dd HH:mm", "M-d HH:mm:ss", "M-d H:mm:ss", "MM-dd H:mm:ss"];
        if (DateTime.TryParseExact(text, noYear, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t))
        {
            t = new DateTime(DateTime.Now.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Local);
            if (t > DateTime.Now.AddDays(1)) t = t.AddYears(-1); // "12-30" asked in January
            return t.ToUniversalTime();
        }

        string[] timeOnly = ["HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm"];
        if (DateTime.TryParseExact(text, timeOnly, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.NoCurrentDateDefault, out t))
            return (DateTime.Today + t.TimeOfDay).ToUniversalTime();
        return null;
    }

    private static int Around(ToolContext ctx, ArgParser a)
    {
        var before = a.GetDuration("before") ?? TimeSpan.FromMinutes(2);
        var after = a.GetDuration("after");
        var windows = new List<Window>();
        var spec = a.Get("around") ?? "";
        string what;
        if (spec.Equals("unexpected", StringComparison.OrdinalIgnoreCase))
        {
            var since = a.GetDuration("since") ?? TimeSpan.FromDays(30);
            var history = BootHistory.Load(since, out var error);
            if (error != null && history.Sessions.Count == 0) return ctx.Fail("events --around unexpected: could not read the System log: " + error);
            foreach (var s in history.Sessions.Where(s => s.End == EndKind.Unexpected && s.LastAliveUtc != null && s.NextStartUtc != null))
            {
                var to = after is { } af ? Min(s.NextStartUtc!.Value, s.LastAliveUtc!.Value + af) : s.NextStartUtc!.Value;
                windows.Add(new Window { FromUtc = s.LastAliveUtc!.Value - before, ToUtc = to, Label = $"last alive {Format.Time(s.LastAliveUtc!.Value)}, next start {Format.TimeAfter(s.NextStartUtc!.Value, s.LastAliveUtc!.Value)}" });
            }

            var noStamp = history.Sessions.Count(s => s.End == EndKind.Unexpected && (s.LastAliveUtc == null || s.NextStartUtc == null));
            what = $"{windows.Count} unexpected ends of the last {Format.Duration(since)}, each from {Format.Duration(before)} before its last-alive stamp to " +
                   (after != null ? $"{Format.Duration(after.Value)} after it (or the next start, if sooner)" : "the next start") +
                   (noStamp > 0 ? $"; {noStamp} more unexpected ends have no last-alive stamp or next start and are left out" : "");
            if (windows.Count == 0)
            {
                ctx.Out.WriteLine($"no unexpected end with a last-alive stamp in the last {Format.Duration(since)}" + (noStamp > 0 ? $" ({noStamp} without one)" : "") + ": nothing to look around");
                return 0;
            }
        }
        else
        {
            var texts = new List<string>();
            if (a.Get("around-file") is { } file)
            {
                var path = ctx.Runtime.ResolvePath(file);
                if (!File.Exists(path)) return ctx.Fail($"events: {path} does not exist on the target.");
                texts.AddRange(File.ReadAllLines(path));
            }

            texts.AddRange(spec.Split(',', StringSplitOptions.RemoveEmptyEntries));
            var bad = new List<string>();
            foreach (var text in texts.Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (ParseLocalTime(text) is { } t) windows.Add(new Window { FromUtc = t - before, ToUtc = t + (after ?? TimeSpan.FromSeconds(30)), Label = Format.Time(t) });
                else bad.Add(text);
            }

            if (bad.Count > 0) return ctx.Fail($"events --around: cannot read {string.Join(", ", bad.Take(3).Select(b => $"'{b}'"))} as a time (2026-11-24 06:35:22, 11-24 06:35:22 or 06:35:22, local time).");
            if (windows.Count == 0) return ctx.Fail("events --around: no time given.");
            what = $"{windows.Count} times, each from {Format.Duration(before)} before to {Format.Duration(after ?? TimeSpan.FromSeconds(30))} after";
        }

        windows = windows.OrderByDescending(w => w.FromUtc).ToList();
        var allLogs = a.Flag("all-logs");
        string? unreadableLogs = null;
        var logs = allLogs ? LogsWithEvents(out unreadableLogs) : Names(a.Get("log") ?? "Application,System");
        var perWindow = Math.Max(1, a.GetInt("per-window", 3));
        var conditions = Conditions(a.Get("level") ?? "info", a.Get("provider"), a.Get("id"));
        var failures = new List<string>();
        var capped = new List<string>();
        const int perQuery = 5000;
        foreach (var log in logs)
        {
            // Several windows in one query (fewer passes over a big log); fewer when the log service finds the query too complex.
            var batch = 16;
            for (var i = 0; i < windows.Count;)
            {
                var group = windows.Skip(i).Take(batch).ToList();
                var times = string.Join(" or ", group.Select(w => $"TimeCreated[@SystemTime>='{Iso(w.FromUtc)}' and @SystemTime<'{Iso(w.ToUtc)}']"));
                var xpath = $"*[System[({times}){string.Concat(conditions.Select(c => " and " + c))}]]";
                var shown = new Dictionary<Window, int>();
                var part = Query(log, xpath, perQuery, out var error, e =>
                {
                    // Messages only for the events that can be printed: the newest few of each window (results come newest first).
                    var w = group.FirstOrDefault(x => e.TimeUtc >= x.FromUtc && e.TimeUtc < x.ToUtc);
                    if (w == null) return false;
                    shown[w] = shown.GetValueOrDefault(w) + 1;
                    return shown[w] <= perWindow;
                });
                if (error != null && part.Count == 0 && batch > 1 && error.Contains("query", StringComparison.OrdinalIgnoreCase))
                {
                    batch = Math.Max(1, batch / 4);
                    continue;
                }

                if (error != null)
                {
                    failures.Add($"{log}: {error}");
                    break;
                }

                if (part.Count >= perQuery) capped.Add(log);
                foreach (var e in part)
                {
                    foreach (var w in group.Where(x => e.TimeUtc >= x.FromUtc && e.TimeUtc < x.ToUtc)) w.Events.Add(e);
                }

                i += group.Count;
            }
        }

        var o = ctx.Out;
        var searched = logs.Count - failures.Count;
        o.WriteLine($"{what}; searched {(allLogs ? $"{searched} logs that hold events" : string.Join("/", logs.Where(l => !failures.Any(f => f.StartsWith(l + ":", StringComparison.Ordinal)))))}" +
                    (a.Get("level") is { } lv ? $", {lv}-level and worse" : ", all levels"));
        foreach (var f in failures.Take(5)) o.WriteLine("could not read " + f);
        if (failures.Count > 5) o.WriteLine($"… and {failures.Count - 5} more logs could not be read");
        if (unreadableLogs != null) o.WriteLine(unreadableLogs);
        if (capped.Count > 0) o.WriteLine($"! more than {perQuery} events in these windows in {string.Join(", ", capped.Take(3))}: only the newest {perQuery} of each query were counted, so the numbers below are lower bounds");

        var kinds = windows.SelectMany(w => w.Events.Select(e => (e.Log, e.Provider, e.Id, w))).GroupBy(x => (x.Log, x.Provider, x.Id))
            .Select(g => (key: g.Key, windows: g.Select(x => x.w).Distinct().Count(), total: g.Count())).OrderByDescending(k => k.windows).ThenByDescending(k => k.total).ToList();
        if (kinds.Count == 0)
        {
            o.WriteLine($"no event at all in any of the {windows.Count} windows");
            return failures.Count > 0 ? 1 : 0;
        }

        o.WriteLine($"seen in how many of the {windows.Count} windows (events in all):");
        foreach (var k in kinds.Take(15)) o.WriteLine($"  {k.windows,3}  {ShortLog(k.key.Log)} {k.key.Provider}#{k.key.Id} ({k.total})");
        if (kinds.Count > 15) o.WriteLine($"  … {kinds.Count - 15} more kinds of event, each in at most {kinds[15].windows} windows");

        var detail = Math.Max(1, a.GetInt("max", 12));
        var withEvents = windows.Where(w => w.Events.Count > 0).ToList();
        o.WriteLine($"the last {perWindow} events of each window, newest window first:");
        foreach (var w in withEvents.Take(detail))
        {
            o.WriteLine($"  {w.Label}: {w.Events.Count} events");
            foreach (var e in w.Events.OrderByDescending(e => e.TimeUtc).Take(perWindow)) o.WriteLine("    " + EventLine(e, 160));
        }

        if (withEvents.Count > detail) o.WriteLine($"  … {withEvents.Count - detail} more windows with events not shown (--max {withEvents.Count})");
        var empty = windows.Where(w => w.Events.Count == 0).ToList();
        if (empty.Count > 0) o.WriteLine($"{empty.Count} of {windows.Count} windows had no event at all: {Format.Clip(string.Join("; ", empty.Select(w => w.Label)), 600)}");
        return failures.Count > 0 ? 1 : 0;
    }

    private static DateTime Min(DateTime x, DateTime y) => x < y ? x : y;

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ crashes

    /// <summary>A crash or hang event as one line (without its time), or null for any other event.</summary>
    public static string? CrashLine(EventRecord e)
    {
        string line;
        if (e.Provider == "Application Error" && e.Id == 1000)
        {
            var code = e.Field("ExceptionCode", 6) ?? "?";
            var meaning = uint.TryParse(code.Replace("0x", ""), NumberStyles.HexNumber, null, out var c) ? Native.ProcessDetails.ExitCodeName(c) : code;
            var offset = (e.Field("FaultingOffset", 7) ?? "").TrimStart('0');
            line = $"CRASH {e.Field("AppName", 0)} {e.Field("AppVersion", 1)} in {e.Field("ModuleName", 3)} {e.Field("ModuleVersion", 4)}+0x{(offset.Length > 0 ? offset : "0")}  {meaning}  (pid {Pid(e.Field("ProcessId", 8))})";
        }
        else if (e.Provider == "Application Hang" && e.Id == 1002)
        {
            line = $"HANG  {e.Field("AppName", 0)} {e.Field("AppVersion", 1)} stopped responding and was closed";
        }
        else if (e.Provider == ".NET Runtime" && e.Id == 1026)
        {
            var text = e.Message.Replace("\r", "");
            var app = text.Split('\n').FirstOrDefault(l => l.StartsWith("Application:", StringComparison.Ordinal))?[12..].Trim() ?? "?";
            var info = text.Split('\n').FirstOrDefault(l => l.Contains("Exception Info:", StringComparison.Ordinal) || l.StartsWith("Exception", StringComparison.Ordinal))?.Trim() ?? "";
            var frame = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal))?.Trim() ?? "";
            line = $".NET  {app}: {Format.Clip(info.Replace("Exception Info: ", ""), 160)} {Format.Clip(frame, 120)}";
        }
        else
        {
            return null;
        }

        return line;
    }

    /// <summary>One line per crash/hang, newest first.</summary>
    public static List<string> Crashes(TimeSpan since, int max, string? nameFilter = null)
    {
        var xpath = $"*[System[(EventID=1000 or EventID=1002 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= {(long)since.TotalMilliseconds}]]]";
        var lines = new List<string>();
        foreach (var e in Query("Application", xpath, max * 3, out _, e => e.Provider == ".NET Runtime").OrderByDescending(e => e.TimeUtc))
        {
            if (CrashLine(e) is not { } line) continue;
            if (nameFilter != null && !line.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add($"{Format.Time(e.TimeUtc)} {line}");
            if (lines.Count >= max) break;
        }

        return lines;
    }

    /// <summary>Application Error stores the process id in hexadecimal, with or without "0x": show it as the decimal pid everything else uses.</summary>
    private static string Pid(string? raw)
    {
        var hex = (raw ?? "").Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) ? pid.ToString(CultureInfo.InvariantCulture) : (raw ?? "?");
    }

    public static List<(string provider, int count)> ErrorSummary(TimeSpan since)
    {
        var xpath = XPath(since, "error", null, null);
        return new[] { "Application", "System" }.SelectMany(l => Query(l, xpath, 200, out _, _ => false))
            .GroupBy(e => e.Provider).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    }

    private static Task<int> CrashesAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "system");
        if (a.Flag("system")) return Task.FromResult(BootHistory.SystemReport(ctx, a.GetDuration("since") ?? TimeSpan.FromDays(30)));

        var since = a.GetDuration("since") ?? TimeSpan.FromDays(7);
        var filter = a.Positionals.Count > 0 ? a.Positionals[0] : null;
        var lines = Crashes(since, a.GetInt("max", 20), filter);
        ctx.Out.WriteLine(lines.Count == 0
            ? $"no application crashes or hangs recorded in the last {Format.Duration(since)}{(filter != null ? $" matching '{filter}'" : "")}"
            : string.Join('\n', lines));

        // Blue screens: the bugcheck event plus the kernel minidumps Windows kept.
        var bugchecks = Query("System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and TimeCreated[timediff(@SystemTime) <= {(long)since.TotalMilliseconds}]]]", 5);
        foreach (var b in bugchecks) ctx.Out.WriteLine($"{Format.Time(b.TimeUtc)} BSOD  {Format.Clip(b.Message, 220)}");
        try
        {
            var kernelDumps = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"));
            if (kernelDumps.Exists)
            {
                foreach (var d in kernelDumps.GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
                    ctx.Out.WriteLine($"kernel dump  {Format.Time(d.LastWriteTimeUtc)}  {Format.Bytes(d.Length)}  {d.FullName}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (bugchecks.Count > 0) ctx.Out.WriteLine(@"kernel dumps are in C:\Windows\Minidump (listing them needs agentprobe elevated on the target)");
        }

        // Resets and power losses leave no crash event: say that there were some, rather than nothing.
        var unexpected = BootHistory.Load(since, out _).Sessions.Count(s => s.End == EndKind.Unexpected);
        if (unexpected > 0) ctx.Out.WriteLine($"the PC as a whole: {unexpected} unexpected shutdown{(unexpected == 1 ? "" : "s")} (reset, power loss or blue screen) in the last {Format.Duration(since)}: `crashes --system`, `boots`");

        var dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
        if (Directory.Exists(dumpDir))
        {
            var dumps = new DirectoryInfo(dumpDir).GetFiles("*.dmp").Where(f => f.LastWriteTimeUtc > DateTime.UtcNow - since && (filter == null || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(8).ToList();
            if (dumps.Count > 0)
            {
                ctx.Out.WriteLine($"dump files in {dumpDir}:");
                foreach (var d in dumps) ctx.Out.WriteLine($"  {Format.Time(d.LastWriteTimeUtc)}  {Format.Bytes(d.Length)}  {d.Name}");
            }
        }

        return Task.FromResult(0);
    }
}
