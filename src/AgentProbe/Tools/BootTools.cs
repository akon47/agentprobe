using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Tools;

internal enum EndKind
{
    Running,
    Clean,
    Unexpected,
    Hibernated,
    Unknown,
}

/// <summary>One start of Windows and how it ended, as the System log tells it.</summary>
internal sealed class BootSession
{
    /// <summary>When the kernel started; null for the session that was running when the time looked at begins.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>Kernel-Boot 27: 0 a cold boot, 1 fast startup, 2 resumed from hibernation.</summary>
    public int? BootType { get; set; }

    public EndKind End { get; set; }

    /// <summary>When a clean shutdown or a hibernation ended it.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>EventLog 6008: the last time Windows stamped that it was running. The real end came later, before the next start.</summary>
    public DateTime? LastAliveUtc { get; set; }

    public DateTime? NextStartUtc { get; set; }

    public int? NextBootType { get; set; }

    /// <summary>The program that asked for the shutdown or restart (User32 1074).</summary>
    public string? By { get; set; }

    public string? Action { get; set; }

    public bool Has41 { get; set; }

    public bool Has6008 { get; set; }

    /// <summary>Kernel-Power 41 BugcheckCode, or the bugcheck that WER-SystemErrorReporting 1001 names.</summary>
    public string? Bugcheck { get; set; }

    public bool PowerButton { get; set; }

    public bool SleepInProgress { get; set; }

    public bool BugcheckFromEfi { get; set; }

    public int WheaBootErrors { get; set; }

    /// <summary>A shutdown or restart had begun (1074 or Kernel-Power 109) but the next start says it did not finish.</summary>
    public bool ShutdownUnderway { get; set; }

    internal DateTime? CleanEndUtc { get; set; }

    internal DateTime? SleepUtc { get; set; }

    /// <summary>How long it ran; for an unexpected end the time up to the last-alive stamp, so at least that long.</summary>
    public TimeSpan? Up => StartUtc is not { } start ? null : End switch
    {
        EndKind.Running => DateTime.UtcNow - start,
        EndKind.Clean or EndKind.Hibernated when EndUtc is { } end => end - start,
        EndKind.Unexpected when LastAliveUtc is { } alive => alive - start,
        _ => null,
    };
}

/// <summary>
/// The starts of Windows and how each one ended (shut down by whom, hibernated, or unexpectedly: a reset, a power
/// loss or a blue screen), from the System log alone. Nothing is stored on the target: what the log has is what is known.
/// </summary>
internal static partial class BootHistory
{
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    private const string KernelBoot = "Microsoft-Windows-Kernel-Boot";
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string BugcheckProvider = "Microsoft-Windows-WER-SystemErrorReporting";

    public static IEnumerable<Tool> Tools =>
    [
        new("boots", "Logs", "Every start of Windows and how it ended: shut down by whom, or unexpectedly (reset, power loss, blue screen)",
            """
            boots [--since 30d] [--max 40] [--logs DIR[\*.log]]
              One line per start, oldest first: when it started, how long it ran, how it ended. An unexpected end has
              its last-alive stamp (the last time Windows noted that it ran: the end came between it and the next
              start) and what Kernel-Power 41 recorded (a bugcheck code, the power button held). Then the counts:
              unexpected ends by uptime and by day, how soon the next start came, who shut the PC down.
              --logs DIR   for each start, the file of that folder last written during it (when, how many lines, its
                           last line), and how many files were created during it: what an app wrote up to a reset.
              `crashes --system` weighs what was recorded; `events --around unexpected --all-logs` shows what every
              log has just before those ends.
            """, BootsAsync) { Quiet = true },
    ];

    public sealed class Result
    {
        public List<BootSession> Sessions { get; } = [];

        public DateTime SinceUtc { get; init; }

        /// <summary>The oldest event the System log still holds: before it nothing is known.</summary>
        public DateTime? LogStartsUtc { get; set; }

        public bool Truncated => LogStartsUtc is { } first && first > SinceUtc + TimeSpan.FromMinutes(5);
    }

    public static Result Load(TimeSpan since, out string? error)
    {
        var result = new Result { SinceUtc = DateTime.UtcNow - since };
        var xpath = "*[System[(EventID=12 or EventID=13 or EventID=27 or EventID=41 or EventID=42 or EventID=109 or EventID=1001 or EventID=1074 or EventID=6005 or EventID=6006 or EventID=6008)" +
                    $" and TimeCreated[timediff(@SystemTime) <= {(long)since.TotalMilliseconds}]]]";
        var events = EventTools.Query("System", xpath, 100_000, out error, _ => false, newestFirst: false).Where(Relevant).OrderBy(e => e.TimeUtc).ToList();
        var oldest = EventTools.Query("System", "*", 1, out _, _ => false, newestFirst: false);
        if (oldest.Count > 0) result.LogStartsUtc = oldest[0].TimeUtc;

        // The starts. Every Kernel-General 12 is one (the kernel's own start time): two of them never merge, a PC
        // that resets a minute after it started has started twice. Kernel-Boot 27 (the kind of start) and EventLog
        // 6005 come a few seconds after the start they belong to; one that belongs to no start is a start of its
        // own (a fast startup, or a Windows that does not log 12).
        var starts = new List<(DateTime at, int? type, bool has27, bool has6005)>();
        foreach (var e in events.Where(e => e.Is(KernelGeneral, 12)))
        {
            var start = DateTime.TryParse(e.Field("StartTime", 6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && Math.Abs((t - e.TimeUtc).TotalMinutes) < 10 ? t : e.TimeUtc;
            starts.Add((start, null, false, false));
        }

        starts.Sort((x, y) => x.at.CompareTo(y.at));
        void Attach(DateTime at, int? type, bool is27, TimeSpan within)
        {
            var i = starts.FindLastIndex(s => s.at <= at + TimeSpan.FromSeconds(5));
            if (i >= 0 && at - starts[i].at <= within && !(is27 ? starts[i].has27 : starts[i].has6005))
            {
                var s = starts[i];
                starts[i] = (s.at, s.type ?? type, s.has27 || is27, s.has6005 || !is27);
                return;
            }

            starts.Add((at, type, is27, !is27));
            starts.Sort((x, y) => x.at.CompareTo(y.at));
        }

        foreach (var e in events)
        {
            if (e.Is(KernelBoot, 27)) Attach(e.TimeUtc, int.TryParse(e.Field("BootType", 0), out var bt) ? bt : null, true, TimeSpan.FromMinutes(3));
        }

        foreach (var e in events)
        {
            if (e.Is("EventLog", 6005)) Attach(e.TimeUtc, null, false, TimeSpan.FromMinutes(5));
        }

        var sessions = starts.Select(s => new BootSession { StartUtc = s.at, BootType = s.type }).ToList();
        BootSession? before = null; // the session that was running when the time looked at begins

        int StartIndex(DateTime t)
        {
            var k = -1;
            for (var i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].StartUtc <= t + TimeSpan.FromSeconds(1)) k = i;
                else break;
            }

            return k;
        }

        BootSession? Session(int index) => index >= 0 ? sessions[index] : index == -1 ? before ??= new BootSession() : null;

        foreach (var e in events)
        {
            var k = StartIndex(e.TimeUtc);
            if (e.Is(KernelPower, 41) || e.Is("EventLog", 6008) || e.Is(BugcheckProvider, 1001))
            {
                // Logged in the start after the end they describe.
                if (Session(k - 1) is not { } s) continue;
                if (e.Is(KernelPower, 41))
                {
                    s.Has41 = true;
                    if (long.TryParse(e.Field("BugcheckCode", 0), out var code) && code != 0) s.Bugcheck ??= "0x" + code.ToString("X", CultureInfo.InvariantCulture);
                    if (e.Field("PowerButtonTimestamp", 6) is { } pb && pb != "0" && pb.Length > 0) s.PowerButton = true;
                    if (string.Equals(e.Field("LongPowerButtonPressDetected", 15), "true", StringComparison.OrdinalIgnoreCase)) s.PowerButton = true;
                    if (e.Field("SleepInProgress", 5) is { } sleep && sleep != "0" && sleep.Length > 0 && !sleep.Equals("false", StringComparison.OrdinalIgnoreCase)) s.SleepInProgress = true;
                    if (string.Equals(e.Field("BugcheckInfoFromEFI", 12), "true", StringComparison.OrdinalIgnoreCase)) s.BugcheckFromEfi = true;
                    if (int.TryParse(e.Field("WHEABootErrorCount", 20), out var whea)) s.WheaBootErrors += whea;
                }
                else if (e.Is("EventLog", 6008))
                {
                    s.Has6008 = true;
                    s.LastAliveUtc ??= e.LastAliveUtc();
                }
                else
                {
                    var text = e.Field("param1", 0);
                    s.Bugcheck = text is { Length: > 0 } ? Format.Clip(text, 120) : s.Bugcheck ?? "(code not recorded)";
                }

                continue;
            }

            if (Session(k) is not { } own) continue;
            if (e.Is(KernelGeneral, 13))
            {
                own.CleanEndUtc = DateTime.TryParse(e.Field("StopTime", 0), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : e.TimeUtc;
            }
            else if (e.Is("EventLog", 6006))
            {
                own.CleanEndUtc ??= e.TimeUtc;
            }
            else if (e.Is("User32", 1074))
            {
                var process = (e.Field("param1", 0) ?? "").Split(" (")[0];
                own.By = process.Length > 0 ? Path.GetFileName(process) : null;
                own.Action ??= e.Field("param5", 4) is { Length: > 0 } type ? type : null;
                own.ShutdownUnderway = true;
            }
            else if (e.Is(KernelPower, 109))
            {
                own.Action = e.Field("ShutdownActionType", 0) switch
                {
                    "2" => "sleep",
                    "3" => "hibernate",
                    "4" => "shutdown",
                    "5" => "restart",
                    "6" => "power off",
                    _ => own.Action,
                };
                own.ShutdownUnderway = true;
            }
            else if (e.Is(KernelPower, 42))
            {
                own.SleepUtc = e.TimeUtc;
            }
        }

        if (before != null) result.Sessions.Add(before);
        result.Sessions.AddRange(sessions);
        for (var i = 0; i < result.Sessions.Count; i++)
        {
            var s = result.Sessions[i];
            var next = i + 1 < result.Sessions.Count ? result.Sessions[i + 1] : null;
            s.NextStartUtc = next?.StartUtc;
            s.NextBootType = next?.BootType;
            if (s.Has41 || s.Has6008 || s.Bugcheck != null)
            {
                s.End = EndKind.Unexpected;
            }
            else if (s.CleanEndUtc != null)
            {
                s.End = EndKind.Clean;
                s.EndUtc = s.CleanEndUtc;
            }
            else if (next != null && next.BootType is 1 or 2)
            {
                s.End = EndKind.Hibernated;
                s.EndUtc = s.SleepUtc;
            }
            else
            {
                s.End = next == null ? EndKind.Running : EndKind.Unknown;
            }
        }

        return result;
    }

    private static bool Relevant(EventRecord e) => e.Provider switch
    {
        KernelGeneral => e.Id is 12 or 13,
        KernelBoot => e.Id == 27,
        KernelPower => e.Id is 41 or 42 or 109,
        "EventLog" => e.Id is 6005 or 6006 or 6008,
        "User32" => e.Id == 1074,
        BugcheckProvider => e.Id == 1001,
        _ => false,
    };

    private static string StartType(int? type) => type switch
    {
        1 => "fast startup",
        2 => "resumed from hibernation",
        _ => "",
    };

    public static string Describe(BootSession s)
    {
        var end = s.End switch
        {
            EndKind.Running => "running now",
            EndKind.Clean => s.By != null ? $"{s.Action ?? "shutdown"} by {s.By}" : $"{s.Action ?? "shut down"} (who asked is not recorded)",
            EndKind.Hibernated => s.NextBootType == 1 ? "shut down with fast startup (hibernated)" : "hibernated",
            EndKind.Unknown => "end not recorded (no shutdown and no unexpected-shutdown event before the next start)",
            _ => Unexpected(s),
        };
        var kind = StartType(s.BootType);
        return end + (kind.Length > 0 ? $"; this start was {kind}" : "");
    }

    private static string Unexpected(BootSession s)
    {
        var sb = new StringBuilder("UNEXPECTED");
        if (s.LastAliveUtc is { } alive) sb.Append($", last alive {(s.StartUtc is { } st ? Format.TimeAfter(alive, st) : Format.Time(alive))}");
        else sb.Append(", no last-alive stamp");
        if (s.NextStartUtc is { } next) sb.Append($", next start {(s.LastAliveUtc is { } a ? Format.TimeAfter(next, a) : Format.Time(next))}");
        if (s.Bugcheck != null) sb.Append($"; BLUE SCREEN, bugcheck {s.Bugcheck}");
        if (s.PowerButton) sb.Append("; the power button was held");
        if (s.SleepInProgress) sb.Append("; while going to sleep or waking");
        if (s.ShutdownUnderway) sb.Append($"; a {s.Action ?? "shutdown"}{(s.By != null ? $" by {s.By}" : "")} had begun");
        return sb.ToString();
    }

    private static Task<int> BootsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var since = a.GetDuration("since") ?? TimeSpan.FromDays(30);
        var max = Math.Max(1, a.GetInt("max", 40));
        var history = Load(since, out var error);
        if (error != null && history.Sessions.Count == 0) return Task.FromResult(ctx.Fail("boots: could not read the System log: " + error));

        var o = ctx.Out;
        if (error != null) o.WriteLine($"! the System log could not be read to the end ({error}): what follows may be incomplete");
        var sessions = history.Sessions;
        if (sessions.Count == 0)
        {
            o.WriteLine($"no start of Windows recorded in the last {Format.Duration(since)}" + (history.LogStartsUtc is { } ls ? $" (the System log reaches back to {Format.Time(ls)})" : ""));
            return Task.FromResult(0);
        }

        LogFolder? logs = null;
        if (a.Get("logs") is { } logSpec)
        {
            logs = LogFolder.Open(ctx.Runtime.ResolvePath(logSpec), out var why);
            if (logs == null) return Task.FromResult(ctx.Fail("boots --logs: " + why));
        }

        var shown = sessions.TakeLast(max).ToList();
        if (sessions.Count > shown.Count) o.WriteLine($"… {sessions.Count - shown.Count} older starts not shown (--max {sessions.Count}); the counts below include them");
        foreach (var s in shown)
        {
            var start = s.StartUtc is { } st ? Format.Time(st) : $"(before {Format.Time(history.SinceUtc)[..5]})";
            var up = s.Up is { } u ? (s.End == EndKind.Unexpected ? "≥" : "") + Format.Uptime(u) : "?";
            o.WriteLine($"{start,-14}  up {up,-11}  {Describe(s)}");
            if (logs != null && s.StartUtc is { } from) o.WriteLine("    " + logs.During(from, s.NextStartUtc ?? DateTime.UtcNow.AddMinutes(1)));
        }

        o.WriteLine();
        foreach (var line in Summary(history, since)) o.WriteLine(line);
        return Task.FromResult(0);
    }

    private static IEnumerable<string> Summary(Result history, TimeSpan since)
    {
        var sessions = history.Sessions;
        var unexpected = sessions.Where(s => s.End == EndKind.Unexpected).ToList();
        var starts = sessions.Count(s => s.StartUtc != null);
        string Count(EndKind kind, string name) => sessions.Count(s => s.End == kind) is var n && n > 0 ? $", {n} {name}" : "";
        yield return $"{starts} starts in the last {Format.Duration(since)}; ends: {unexpected.Count} UNEXPECTED{Count(EndKind.Clean, "shut down or restarted")}{Count(EndKind.Hibernated, "hibernated")}{Count(EndKind.Unknown, "not recorded")}{Count(EndKind.Running, "running now")}";
        if (unexpected.Count > 0)
        {
            int Up(Func<TimeSpan, bool> range) => unexpected.Count(s => s.Up is { } u && range(u));
            var noStamp = unexpected.Count(s => s.Up == null);
            yield return $"unexpected ends by uptime: under 2m {Up(u => u.TotalMinutes < 2)}, 2-30m {Up(u => u.TotalMinutes is >= 2 and < 30)}, 30m-3h {Up(u => u.TotalMinutes is >= 30 and < 180)}, 3h and more {Up(u => u.TotalMinutes >= 180)}" +
                         (noStamp > 0 ? $", unknown {noStamp}" : "");
            var days = unexpected.Select(s => (s.LastAliveUtc ?? s.NextStartUtc ?? DateTime.MinValue).ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture))
                .GroupBy(d => d).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            yield return "unexpected ends by day: " + string.Join(", ", days.Take(8).Select(g => $"{g.Key} {g.Count()}")) + (days.Count > 8 ? $" (+{days.Count - 8} more days)" : "");
            var gaps = unexpected.Where(s => s.LastAliveUtc != null && s.NextStartUtc != null).Select(s => s.NextStartUtc!.Value - s.LastAliveUtc!.Value).ToList();
            if (gaps.Count > 0)
            {
                yield return $"next start after an unexpected end, counted from its last-alive stamp: within 2m {gaps.Count(g => g.TotalMinutes < 2)}, 2-10m {gaps.Count(g => g.TotalMinutes is >= 2 and < 10)}, later {gaps.Count(g => g.TotalMinutes >= 10)}" +
                             $"; at night (00-06h) {unexpected.Count(s => s.NextStartUtc is { } n && n.ToLocalTime().Hour < 6)}";
            }

            yield return "(an unexpected end happened after its last-alive stamp and before the next start; how often the stamp is written decides how close it is)";
        }

        var by = sessions.Where(s => s.End == EndKind.Clean).GroupBy(s => s.By ?? "(not recorded)").OrderByDescending(g => g.Count()).ToList();
        if (by.Count > 0) yield return "shut down or restarted by: " + string.Join(", ", by.Take(6).Select(g => $"{g.Key} {g.Count()}"));
        if (WakeUpType() is { } wake) yield return $"this start: the firmware reports it was started by \"{wake}\" (SMBIOS wake-up type; only the current start's is known)";
        if (history.Truncated) yield return $"! the System log reaches back only to {Format.Time(history.LogStartsUtc!.Value)}: earlier starts are not known";
    }

    /// <summary>The files of a folder, to say what an app wrote during each start.</summary>
    private sealed class LogFolder
    {
        private readonly List<FileInfo> _files;

        private LogFolder(List<FileInfo> files) => _files = files;

        public static LogFolder? Open(string spec, out string? why)
        {
            why = null;
            var dir = spec;
            var pattern = "*";
            if (!Directory.Exists(spec) && Path.GetFileName(spec).IndexOfAny(['*', '?']) >= 0)
            {
                dir = Path.GetDirectoryName(spec) ?? spec;
                pattern = Path.GetFileName(spec);
            }

            if (!Directory.Exists(dir))
            {
                why = $"{dir} does not exist on the target.";
                return null;
            }

            try
            {
                var files = new DirectoryInfo(dir).EnumerateFiles(pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(20000).ToList();
                return new LogFolder(files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                why = ex.Message;
                return null;
            }
        }

        public string During(DateTime fromUtc, DateTime toUtc)
        {
            var created = _files.Count(f => f.CreationTimeUtc >= fromUtc && f.CreationTimeUtc < toUtc);
            var last = _files.Where(f => f.LastWriteTimeUtc >= fromUtc && f.LastWriteTimeUtc < toUtc).MaxBy(f => f.LastWriteTimeUtc);
            var made = $"{created} file{(created == 1 ? "" : "s")} created during it";
            if (last == null) return $"log: no file of the folder was last written during this start ({made})";
            var (lines, lastLine) = Tail(last);
            return $"log: {last.Name} last written {Format.TimeAfter(last.LastWriteTimeUtc, fromUtc)}{(lines >= 0 ? $", {lines} lines" : "")}, last line \"{Format.Clip(lastLine, 140)}\" ({made})";
        }

        private static (long lines, string last) Tail(FileInfo f)
        {
            try
            {
                using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[2];
                var utf16 = s.Read(head, 0, 2) == 2 && head[0] == 0xFF && head[1] == 0xFE;
                long lines = -1;
                if (s.Length <= 64L << 20)
                {
                    // Line breaks: the byte 0A, or in UTF-16 the character 0A 00 (a byte 0A can be half of another character).
                    s.Position = 0;
                    lines = 0;
                    var buffer = new byte[1 << 16]; // even, so a UTF-16 character never straddles two reads
                    int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (utf16)
                        {
                            for (var i = 0; i + 1 < n; i += 2) if (buffer[i] == 0x0A && buffer[i + 1] == 0) lines++;
                        }
                        else
                        {
                            for (var i = 0; i < n; i++) if (buffer[i] == 0x0A) lines++;
                        }
                    }
                }

                var take = (int)Math.Min(s.Length, 8192);
                s.Position = s.Length - take;
                var tail = new byte[take];
                s.ReadExactly(tail, 0, take);
                string text;
                if (utf16) text = Encoding.Unicode.GetString(tail, take % 2, take - (take % 2));
                else
                {
                    var decoder = new LineDecoder();
                    text = decoder.Feed(tail) + decoder.Flush();
                }

                if (lines >= 0 && text.Length > 0 && !text.EndsWith('\n')) lines++; // a last line without a line break is a line too
                var last = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                return (lines, last);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (-1, $"(cannot be read: {ex.Message})");
            }
        }
    }

    // ------------------------------------------------------------------ for triage

    /// <summary>A line for `triage` when the PC ended unexpectedly in the last 30 days, and what to run about it.</summary>
    public static string? TriageLine(out string? finding)
    {
        finding = null;
        var since = TimeSpan.FromDays(30);
        var history = Load(since, out _);
        var unexpected = history.Sessions.Where(s => s.End == EndKind.Unexpected).ToList();
        if (unexpected.Count == 0) return null;
        var last = unexpected[^1];
        var bugchecks = unexpected.Count(s => s.Bugcheck != null);
        var dumps = KernelDumps.Find();
        var dumpText = dumps.MinidumpError != null ? "kernel dumps ? (listing them needs an elevated target)" : $"kernel dumps {dumps.Minidumps.Count + (dumps.MemoryDmp != null ? 1 : 0)}";
        var plural = unexpected.Count == 1 ? "" : "s";
        finding = $"{unexpected.Count} unexpected shutdown{plural} (a reset, a power loss or a blue screen): `crashes --system` weighs what Windows recorded about them, `boots` shows when each start ended";
        return $"resets   {unexpected.Count} unexpected shutdown{plural} in 30d (last {Format.Time(last.LastAliveUtc ?? last.NextStartUtc ?? DateTime.UtcNow)}), bugchecks recorded {bugchecks}, {dumpText}" +
               (history.Truncated ? $"; the System log reaches back only to {Format.Time(history.LogStartsUtc!.Value)}" : "");
    }

    // ------------------------------------------------------------------ crashes --system

    public sealed class KernelDumps
    {
        public List<FileInfo> Minidumps { get; } = [];

        public string MinidumpDir { get; private set; } = "";

        public string? MinidumpError { get; private set; }

        public FileInfo? MemoryDmp { get; private set; }

        public string MemoryDmpPath { get; private set; } = "";

        public List<FileInfo> LiveReports { get; } = [];

        public string? LiveError { get; private set; }

        public static KernelDumps Find()
        {
            var d = new KernelDumps();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            using var cc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            d.MinidumpDir = Environment.ExpandEnvironmentVariables(cc?.GetValue("MinidumpDir") as string ?? @"%SystemRoot%\Minidump");
            d.MemoryDmpPath = Environment.ExpandEnvironmentVariables(cc?.GetValue("DumpFile") as string ?? @"%SystemRoot%\MEMORY.DMP");
            try
            {
                if (Directory.Exists(d.MinidumpDir)) d.Minidumps.AddRange(new DirectoryInfo(d.MinidumpDir).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                d.MinidumpError = ex.Message;
            }

            var memory = new FileInfo(d.MemoryDmpPath);
            if (memory.Exists) d.MemoryDmp = memory;
            var live = Path.Combine(windows, "LiveKernelReports");
            try
            {
                if (Directory.Exists(live))
                    d.LiveReports.AddRange(Directory.EnumerateFiles(live, "*.dmp", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                d.LiveError = ex.Message;
            }

            return d;
        }
    }

    public static int SystemReport(ToolContext ctx, TimeSpan since)
    {
        var o = ctx.Out;
        var ms = (long)since.TotalMilliseconds;
        var history = Load(since, out var error);
        if (error != null && history.Sessions.Count == 0) return ctx.Fail("crashes --system: could not read the System log: " + error);
        var span = $"last {Format.Duration(since)}";
        o.WriteLine($"the PC as a whole, {span}" + (history.Truncated ? $" (the System log reaches back only to {Format.Time(history.LogStartsUtc!.Value)})" : "") + (error != null ? $" (! the System log could not be read to the end: {error})" : "") + ":");

        // Unexpected ends and what Kernel-Power 41 recorded about them.
        var unexpected = history.Sessions.Where(s => s.End == EndKind.Unexpected).ToList();
        var bugchecked = unexpected.Where(s => s.Bugcheck != null).ToList();
        var button = unexpected.Count(s => s.PowerButton);
        if (unexpected.Count == 0)
        {
            o.WriteLine("unexpected shutdowns: none");
        }
        else
        {
            var last = unexpected[^1];
            o.WriteLine($"unexpected shutdowns: {unexpected.Count} (last {Format.Time(last.LastAliveUtc ?? last.NextStartUtc ?? DateTime.UtcNow)}): Kernel-Power 41 for {unexpected.Count(s => s.Has41)}, EventLog 6008 for {unexpected.Count(s => s.Has6008)}");
            o.WriteLine($"  recorded with them: a bugcheck code {bugchecked.Count}, the power button held {button}, while going to sleep or waking {unexpected.Count(s => s.SleepInProgress)}, " +
                        $"a bugcheck the firmware kept (BugcheckInfoFromEFI) {unexpected.Count(s => s.BugcheckFromEfi)}, WHEA errors at the next start {unexpected.Sum(s => s.WheaBootErrors)}, a shutdown under way {unexpected.Count(s => s.ShutdownUnderway)}");
        }

        // Blue screens: System 1001 and what Windows Error Reporting filed.
        const int reportCap = 50_000;
        List<EventRecord> Reports(string name, out string? why) => EventTools.Query("Application",
            $"*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {ms}]] and EventData[Data='{name}']]", reportCap, out why, _ => false);
        string Counted(List<EventRecord> list) => list.Count >= reportCap ? $"{reportCap}+" : list.Count.ToString(CultureInfo.InvariantCulture);
        var blue = Reports("BlueScreen", out var werError);
        var live = Reports("LiveKernelEvent", out _);
        var codes = live.GroupBy(e => e.Field("P1", 5) ?? "?").OrderByDescending(g => g.Count()).Take(3).Select(g => $"0x{g.Key} x{g.Count()}");
        o.WriteLine($"blue screens recorded: {bugchecked.Count}" + (bugchecked.Count > 0 ? $" ({string.Join(", ", bugchecked.Select(s => s.Bugcheck).Distinct().Take(4))})" : "") +
                    $"; error reports filed as BlueScreen {(werError != null ? "? (" + werError + ")" : Counted(blue))}");
        if (live.Count > 0)
            o.WriteLine($"live kernel events: {Counted(live)} error reports (codes {string.Join(", ", codes)}): a driver or device stopped responding and Windows took a dump of the kernel without restarting (0x141 and 0x117 are the display driver)");

        // Dumps, and whether one could have been written.
        var dumps = KernelDumps.Find();
        var inSpan = dumps.Minidumps.Count(f => f.LastWriteTimeUtc >= history.SinceUtc);
        var dumpParts = new List<string>
        {
            dumps.MinidumpError != null ? $"{dumps.MinidumpDir} not listed (needs an elevated target)"
                : dumps.Minidumps.Count == 0 ? $"{dumps.MinidumpDir} none" : $"{dumps.MinidumpDir} {dumps.Minidumps.Count} (newest {Format.Time(dumps.Minidumps[0].LastWriteTimeUtc)}, {inSpan} in this time)",
            dumps.MemoryDmp != null ? $"{dumps.MemoryDmpPath} {Format.Bytes(dumps.MemoryDmp.Length)} from {Format.Time(dumps.MemoryDmp.LastWriteTimeUtc)}" : $"{dumps.MemoryDmpPath} none",
            dumps.LiveError != null ? "LiveKernelReports not listed (needs an elevated target)" : $"LiveKernelReports {dumps.LiveReports.Count}" + (dumps.LiveReports.Count > 0 ? $" (newest {Format.Time(dumps.LiveReports[0].LastWriteTimeUtc)} {dumps.LiveReports[0].Directory?.Name})" : ""),
        };
        o.WriteLine("kernel dumps: " + string.Join(", ", dumpParts));
        var setup = DumpSetup(out var dumpsOff, out var noPageFile);
        o.WriteLine("dump setup: " + setup);

        var volmgr = EventTools.Query("System", $"*[System[Provider[@Name='volmgr'] and (Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {ms}]]]", 500, out _);
        o.WriteLine("dump write problems (volmgr warnings and errors): " + (volmgr.Count == 0 ? "none" : Grouped(volmgr)));
        var whea = EventTools.Query("System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[timediff(@SystemTime) <= {ms}]]]", 500, out _);
        var wheaErrors = EventTools.Query("Microsoft-Windows-Kernel-WHEA/Errors", $"*[System[TimeCreated[timediff(@SystemTime) <= {ms}]]]", 500, out var wheaLogError, _ => false);
        o.WriteLine("hardware errors (WHEA-Logger): " + (whea.Count == 0 ? "none" : Grouped(whea)) +
                    "; Kernel-WHEA/Errors log: " + (wheaLogError != null ? wheaLogError : wheaErrors.Count == 0 ? "none"
                        : $"{wheaErrors.Count} event{(wheaErrors.Count == 1 ? "" : "s")} ({string.Join(", ", wheaErrors.GroupBy(e => e.Id).Select(g => $"#{g.Key} x{g.Count()}"))}, last {Format.Time(wheaErrors.Max(e => e.TimeUtc))})"));

        var srt = Path.Combine(Environment.SystemDirectory, "LogFiles", "Srt", "SrtTrail.txt");
        if (File.Exists(srt))
        {
            var ran = File.GetLastWriteTimeUtc(srt);
            o.WriteLine($"startup repair: last ran {Format.Time(ran)}{(ran < history.SinceUtc ? " (before this time span)" : "")}: `read {srt}`. Its screen is blue too, and a person can take it for a blue screen");
        }

        // What it adds up to, and what it cannot rule out.
        var listable = dumps.MinidumpError == null;
        var anyDump = dumps.Minidumps.Count > 0 || dumps.MemoryDmp != null;
        var doubts = new List<string>();
        if (dumpsOff) doubts.Add("dumps are turned off, so a blue screen would leave none");
        if (noPageFile) doubts.Add("there is no page file on the boot volume, through which Windows writes a dump, so one may not have been writable");
        if (volmgr.Count > 0) doubts.Add("volmgr reported problems (above)");
        if (!listable) doubts.Add("the dump folder could not be listed");
        string verdict;
        if (unexpected.Count == 0 && bugchecked.Count == 0)
        {
            verdict = $"no unexpected shutdown or blue screen recorded in the {span}" + (history.Truncated ? " that the System log still covers" : "");
        }
        else if (bugchecked.Count > 0)
        {
            verdict = $"{bugchecked.Count} blue screen{(bugchecked.Count == 1 ? "" : "s")} (bugcheck {string.Join(", ", bugchecked.Select(s => s.Bugcheck).Distinct().Take(3))})" +
                      (anyDump ? ": `pull` the newest dump and open it in WinDbg (!analyze -v)" : listable ? ", and no dump of them is left" : "") +
                      (unexpected.Count > bugchecked.Count ? $"; the other {unexpected.Count - bugchecked.Count} unexpected ends recorded no bugcheck" : "");
        }
        else
        {
            var noBugcheck = unexpected.Count == 1 ? "no bugcheck was recorded for the unexpected end" : $"no bugcheck was recorded for any of the {unexpected.Count} unexpected ends";
            var person = button > 0 ? $"; {button} of them came right after the power button was held (a person turned the PC off)" : "";
            verdict = doubts.Count == 0 && !anyDump
                ? $"consistent with hard resets or losses of power: {noBugcheck} and no kernel dump was written although dumps are set up{person}. A crash whose dump failed to be written cannot be ruled out entirely: `crashdumps --no-autoreboot` keeps a blue screen on the screen"
                : $"{noBugcheck}{person}, but {string.Join("; ", doubts.DefaultIfEmpty("a kernel dump exists (above)"))}: a blue screen is not ruled out. `crashdumps --no-autoreboot` keeps one on the screen";
        }

        o.WriteLine("-> " + verdict);
        if (unexpected.Count > 0) o.WriteLine("`boots` lists when each start ended; `events --around unexpected --all-logs` shows what the logs have just before");
        return 0;
    }

    private static string Grouped(List<EventRecord> events)
        => string.Join("; ", events.GroupBy(e => (e.Provider, e.Id)).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => $"#{g.Key.Id} x{g.Count()} (last {Format.Time(g.Max(e => e.TimeUtc))}): {Format.Clip(g.First().Message, 120)}"));

    /// <summary>What kind of dump a blue screen writes, where, whether the PC restarts by itself, and the page file it needs.</summary>
    public static string DumpSetup(out bool off, out bool noPageFile)
    {
        using var cc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
        var enabled = cc?.GetValue("CrashDumpEnabled") as int? ?? 7;
        var kind = enabled switch
        {
            0 => "NONE (dumps are turned off)",
            1 when cc?.GetValue("FilterPages") is 1 => "active memory dump",
            1 => "complete memory dump",
            2 => "kernel memory dump",
            3 => $"small memory dumps into {Environment.ExpandEnvironmentVariables(cc?.GetValue("MinidumpDir") as string ?? @"%SystemRoot%\Minidump")}",
            7 => "automatic memory dump",
            _ => $"CrashDumpEnabled={enabled}",
        };
        off = enabled == 0;
        var reboot = cc?.GetValue("AutoReboot") is 0 ? "stays on the blue screen (AutoReboot=0)" : "restarts by itself after a blue screen";
        var dedicated = cc?.GetValue("DedicatedDumpFile") as string;

        using var mm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        var existing = (mm?.GetValue("ExistingPageFiles") as string[] ?? []).Select(p => p.Replace(@"\??\", "")).ToList();
        var setting = (mm?.GetValue("PagingFiles") as string[] ?? []).FirstOrDefault(p => p.Trim().Length > 0) ?? "";
        var boot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var onBoot = existing.FirstOrDefault(p => p.StartsWith(boot, StringComparison.OrdinalIgnoreCase));
        noPageFile = onBoot == null && dedicated == null && !off;
        string page;
        if (onBoot != null)
        {
            long size = -1;
            try
            {
                var f = new DirectoryInfo(Path.GetDirectoryName(onBoot) ?? boot).EnumerateFiles(Path.GetFileName(onBoot), new EnumerationOptions { AttributesToSkip = 0 }).FirstOrDefault();
                if (f != null) size = f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // size unknown
            }

            var managed = setting.StartsWith("?:", StringComparison.Ordinal) || setting.EndsWith(" 0 0", StringComparison.Ordinal) || setting.Split(' ').Length == 1 ? "system managed" : $"set to {string.Join("-", setting.Split(' ').Skip(1))} MB";
            page = $"page file {onBoot}{(size >= 0 ? " " + Format.Bytes(size) + " now" : "")} ({managed})";
        }
        else
        {
            page = $"NO page file on the boot volume {boot}" + (existing.Count > 0 ? $" (there is one at {string.Join(", ", existing)})" : "");
        }

        return $"{kind}, {reboot}, {page}" + (dedicated != null ? $", dedicated dump file {dedicated}" : "");
    }

    // ------------------------------------------------------------------ SMBIOS

    /// <summary>
    /// What the firmware says started the PC this time (SMBIOS system information, wake-up type): "power switch",
    /// "AC power restored" and so on. It is the firmware's own value, for the current start only; null when absent.
    /// </summary>
    public static unsafe string? WakeUpType()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size < 8) return null;
        var buffer = new byte[size];
        fixed (byte* b = buffer) size = GetSystemFirmwareTable(rsmb, 0, b, size);
        if (size < 8) return null;
        var length = BitConverter.ToInt32(buffer, 4);
        var at = 8;
        var end = Math.Min(buffer.Length, 8 + length);
        while (at + 4 <= end)
        {
            var type = buffer[at];
            var formatted = buffer[at + 1];
            if (formatted < 4) return null;
            if (type == 1 && formatted > 0x18)
            {
                return buffer[at + 0x18] switch
                {
                    1 => "other",
                    2 => "unknown",
                    3 => "APM timer",
                    4 => "modem ring",
                    5 => "LAN remote (wake on LAN)",
                    6 => "power switch",
                    7 => "PCI PME# (a device woke it)",
                    8 => "AC power restored (power came back)",
                    var v => $"reserved value {v}",
                };
            }

            if (type == 127) return null; // end of table
            var next = at + formatted;
            while (next + 1 < end && (buffer[next] != 0 || buffer[next + 1] != 0)) next++;
            at = next + 2;
        }

        return null;
    }

    /// <summary>The kind of the current start (Kernel-Boot 27), or null when it is not in the log.</summary>
    public static string? CurrentStartKind()
    {
        var e = EventTools.Query("System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]", 1, out _, _ => false).FirstOrDefault();
        if (e == null || e.TimeUtc < DateTime.UtcNow - SystemTools.Uptime() - TimeSpan.FromMinutes(5)) return null;
        return e.Field("BootType", 0) switch
        {
            "0" => "cold boot",
            "1" => "fast startup",
            "2" => "resumed from hibernation",
            var v => $"boot type {v}",
        };
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);
}
