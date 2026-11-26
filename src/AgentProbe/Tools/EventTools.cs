using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using AgentProbe.Util;

namespace AgentProbe.Tools;

internal sealed class EventRecord
{
    public DateTime TimeUtc { get; set; }

    public int Level { get; set; }

    public string Log { get; set; } = "";

    public string Provider { get; set; } = "";

    public int Id { get; set; }

    public string Message { get; set; } = "";

    public List<(string name, string value)> Data { get; } = [];

    public string? Field(string name, int index)
        => Data.FirstOrDefault(d => d.name.Equals(name, StringComparison.OrdinalIgnoreCase)).value ?? (index < Data.Count ? Data[index].value : null);
}

/// <summary>Windows event logs, filtered on the target so only the relevant few lines come back.</summary>
internal static class EventTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("events", "Logs", "Windows event log entries, filtered (default: errors of the last hour)",
            """
            events [--since 1h] [--level error|warning|info] [--log Application,System] [--provider NAME] [--id 1000]
                   [--grep REGEX] [--max 30] [--long]
              One line per event, newest first: time, level, source, id, start of the message. --long prints whole messages.
              Other logs work too, e.g. --log Microsoft-Windows-Kernel-PnP/Configuration
            """, EventsAsync) { Quiet = true },
        new("crashes", "Logs", "Recent application crashes and hangs, with faulting module and exception code",
            """
            crashes [--since 7d] [--max 20] [name]
              From Application Error / .NET Runtime / Application Hang events, plus crash dump files Windows kept
              (%LOCALAPPDATA%\CrashDumps). `pull` a dump to analyze it on the developer PC.
            """, CrashesAsync) { Quiet = true },
    ];

    public static List<EventRecord> Query(string log, string xpath, int max) => Query(log, xpath, max, out _);

    /// <summary>Reads events with wevtutil. <paramref name="error"/> is set when the log could not be read at all.</summary>
    public static List<EventRecord> Query(string log, string xpath, int max, out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo("wevtutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "qe", log, "/q:" + xpath, "/f:RenderedXml", "/rd:true", "/c:" + max.ToString(CultureInfo.InvariantCulture), "/e:Events" })
            psi.ArgumentList.Add(arg);

        var result = new List<EventRecord>();
        byte[] bytes;
        try
        {
            using var p = Process.Start(psi)!;
            using var ms = new MemoryStream();
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.BaseStream.CopyTo(ms);
            if (!p.WaitForExit(60000))
            {
                Shell.KillTree(p);
                error = "the query took longer than 60 s";
                return result;
            }

            var stderr = err.Result;
            bytes = ms.ToArray();
            if (p.ExitCode != 0)
            {
                // Typically: no such log, or a log (Security) that needs administrator rights.
                var why = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"wevtutil exit code {p.ExitCode}";
                error = Format.Clip(why, 200);
                return result;
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            error = "cannot run wevtutil.exe: " + ex.Message;
            return result;
        }

        if (bytes.Length == 0) return result;

        // wevtutil writes in the console code page when redirected: decode leniently, then parse.
        var decoder = new LineDecoder();
        var xml = decoder.Feed(bytes) + decoder.Flush();
        result.AddRange(Parse(xml, log));
        return result;
    }

    /// <summary>Events out of their XML (one &lt;Event&gt; element after the other), as wevtutil and the event log API write them.</summary>
    public static List<EventRecord> Parse(string xml, string log)
    {
        var result = new List<EventRecord>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { CheckCharacters = false, ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Ignore });
            EventRecord? current = null;
            var inRendering = false;
            reader.Read();
            while (!reader.EOF)
            {
                var consumed = false; // ReadElementContentAsString leaves the reader on the NEXT node
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "Event":
                            current = new EventRecord { Log = log };
                            inRendering = false;
                            break;
                        case "Provider" when current != null && !inRendering:
                            current.Provider = reader.GetAttribute("Name") ?? "";
                            break;
                        case "EventID" when current != null:
                            current.Id = int.TryParse(reader.ReadElementContentAsString(), out var id) ? id : 0;
                            consumed = true;
                            break;
                        case "Level" when current != null && !inRendering:
                            current.Level = int.TryParse(reader.ReadElementContentAsString(), out var lv) ? lv : 0;
                            consumed = true;
                            break;
                        case "TimeCreated" when current != null:
                            if (DateTime.TryParse(reader.GetAttribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) current.TimeUtc = t;
                            break;
                        case "Data" when current != null && !inRendering:
                            var name = reader.GetAttribute("Name") ?? "";
                            if (reader.IsEmptyElement)
                            {
                                current.Data.Add((name, ""));
                            }
                            else
                            {
                                current.Data.Add((name, reader.ReadElementContentAsString()));
                                consumed = true;
                            }

                            break;
                        case "RenderingInfo":
                            inRendering = true;
                            break;
                        case "Message" when current != null && inRendering:
                            current.Message = reader.ReadElementContentAsString();
                            consumed = true;
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Event" && current != null)
                {
                    if (current.Message.Length == 0) current.Message = string.Join(" | ", current.Data.Select(d => d.value).Where(v => v.Length > 0));
                    result.Add(current);
                    current = null;
                }

                if (!consumed) reader.Read();
            }
        }
        catch (XmlException)
        {
            // return what parsed so far
        }

        return result;
    }

    private static string LevelName(int level) => level switch
    {
        1 => "CRIT",
        2 => "ERROR",
        3 => "WARN",
        _ => "info",
    };

    private static string XPath(TimeSpan since, string? level, string? provider, string? id)
    {
        var conditions = new List<string> { $"TimeCreated[timediff(@SystemTime) <= {(long)since.TotalMilliseconds}]" };
        switch ((level ?? "error").ToLowerInvariant())
        {
            case "critical":
                conditions.Add("Level=1");
                break;
            case "error":
                conditions.Add("(Level=1 or Level=2)");
                break;
            case "warning" or "warn":
                conditions.Add("(Level=1 or Level=2 or Level=3)");
                break;
        }

        if (provider != null) conditions.Add($"Provider[@Name='{provider.Replace("'", "")}']");
        if (id != null) conditions.Add("(" + string.Join(" or ", id.Split(',').Where(x => int.TryParse(x, out _)).Select(x => "EventID=" + x)) + ")");
        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    private static Task<int> EventsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "long");
        var since = a.GetDuration("since") ?? TimeSpan.FromHours(1);
        var max = a.GetInt("max", 30);
        var logs = (a.Get("log") ?? "Application,System").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var xpath = XPath(since, a.Get("level"), a.Get("provider"), a.Get("id"));
        var grep = a.Get("grep");
        System.Text.RegularExpressions.Regex? re = null;
        if (grep != null)
        {
            try
            {
                re = new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
        foreach (var failure in failures) ctx.Err.WriteLine("could not read " + failure);
        if (failures.Count == logs.Length) return Task.FromResult(1);

        var events = fetched.Where(e => re == null || re.IsMatch(e.Message) || re.IsMatch(e.Provider))
            .OrderByDescending(e => e.TimeUtc).ToList();
        if (events.Count == 0)
        {
            var read = logs.Where(l => !failures.Any(x => x.StartsWith(l + ":", StringComparison.Ordinal)));
            ctx.Out.WriteLine($"no {(a.Get("level") ?? "error")}-level events in {string.Join("/", read)} during the last {Format.Duration(since)}" +
                              (hitLimit ? $" among the newest {fetch} per log that were examined (raise --max or narrow --since)" : ""));
            return Task.FromResult(failures.Count > 0 ? 1 : 0);
        }

        // Identical repeats collapse into one line with a count.
        var groups = events.GroupBy(e => (e.Log, e.Provider, e.Id, e.Level, msg: Format.Clip(e.Message, 70))).Take(max).ToList();
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            var first = g.First();
            var count = g.Count() > 1 ? $" x{g.Count()}" : "";
            sb.Append($"{Format.Time(first.TimeUtc)} {LevelName(first.Level)} {first.Log[..3]} {first.Provider}#{first.Id}{count}: ");
            sb.Append(a.Flag("long") ? first.Message.Trim().Replace("\r", "") : Format.Clip(first.Message, 260)).Append('\n');
        }

        ctx.Out.Write(sb.ToString());
        if (hitLimit || events.Count > groups.Sum(g => g.Count()))
            ctx.Out.WriteLine($"… only the newest {fetch} events per log were examined, so more exist and the xN counts are lower bounds (narrow with --since/--provider/--grep, or raise --max)");
        return Task.FromResult(failures.Count > 0 ? 1 : 0);
    }

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
        foreach (var e in Query("Application", xpath, max * 3).OrderByDescending(e => e.TimeUtc))
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
        return new[] { "Application", "System" }.SelectMany(l => Query(l, xpath, 200))
            .GroupBy(e => e.Provider).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    }

    private static Task<int> CrashesAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
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
