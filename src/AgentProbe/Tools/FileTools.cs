using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentProbe.Util;

namespace AgentProbe.Tools;

internal static partial class FileTools
{
    private static readonly ConcurrentDictionary<string, long> ReadCursors = new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<Tool> Tools =>
    [
        new("push", "Files", "Copy a file or folder from the developer PC to the target",
            """
            push <local-path> [remote-path]
              A folder is sent zipped and unpacked. A relative remote path lands in the session workspace
              (the default folder of `exec`), which is deleted when the session ends.
            """, (ctx, _) => Task.FromResult(ctx.Fail("push runs from the developer PC: agentprobe push <local> [remote]"))),
        new("pull", "Files", "Copy a file or folder from the target to the developer PC",
            """
            pull <remote-path> [local-path]
              Like `copy`: into local-path\<name> when local-path is an existing folder (default: the current
              folder), otherwise local-path itself becomes the copy. Folders travel zipped.
            """, PullAsync),
        new("ls", "Files", "List a folder compactly (size, modified, name)",
            """
            ls [path] [-r] [--glob *.log] [--since 2h] [--sort name|size|time] [--top 200]
            """, LsAsync) { Quiet = true },
        new("read", "Files", "Read a text file, or just the part you need",
            """
            read <file> [--tail N] [--head N] [--lines 100-180] [--grep REGEX] [--between 12:02 12:05] [--new]
              --new    only complete lines appended since the last `read --new` of this file (first call: last 40 lines).
                       Ideal for following a log between test steps without re-reading it.
              --grep   matching lines with line numbers; the WHOLE file is searched whatever its size.
                       Without --head/--tail the last 300 matches are shown and the total is stated.
              --between FROM TO   the log lines of a time span: from FROM up to, not including, TO (HH:MM or
                       HH:MM:SS, 24-hour; a span may cross midnight). A line's time is the first HH:MM[:SS] that
                       starts within its first 48 characters ("1:05:03 PM" is read as 13:05:03); a line without one
                       (a stack trace) belongs to the line before it. It is the time of day only: in a log of several
                       days, every day's span is shown. Combines with --grep and --lines, not with --new.
              Encoding is detected per line (UTF-8, UTF-16, ANSI). Big files without a filter return their last 200 lines.
            """, ReadAsync) { Quiet = true },
        new("stat", "Files", "Facts about a file: version, x86/x64, .NET or native, SHA-256, blocked-from-internet flag",
            """
            stat <file> [more files...]
              For a shortcut (.lnk) also what it starts: the target, its arguments and the "Start in" folder
              (`start --lnk <shortcut>` starts exactly that).
            """, StatAsync) { Quiet = true },
        new("locks", "Files", "Which processes keep a file open (why it cannot be deleted/replaced)",
            "locks <file> [more files...]", LocksAsync) { Quiet = true },
    ];

    private static async Task<int> PullAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return ctx.Fail("pull: which remote path?");
        var path = ctx.Runtime.ResolvePath(args[0]);
        if (Directory.Exists(path))
        {
            var zip = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-pull-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(path, zip, CompressionLevel.Fastest, false);
            ctx.Runtime.Audit.Write($"pull folder {path}");
            await ctx.SendFile(Path.GetFileName(path.TrimEnd('\\')), zip, true, true);
            return 0;
        }

        if (!File.Exists(path)) return ctx.Fail($"pull: {path} does not exist.");
        ctx.Runtime.Audit.Write($"pull {path}");
        await ctx.SendFile(Path.GetFileName(path), path, false, false);
        return 0;
    }

    private static Task<int> LsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "r", "recursive");
        var path = ctx.Runtime.ResolvePath(a.Positionals.Count > 0 ? a.Positionals[0] : ".");
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            ctx.Out.WriteLine($"{Format.Bytes(fi.Length)}  {Format.Time(fi.LastWriteTimeUtc)}  {fi.FullName}");
            return Task.FromResult(0);
        }

        if (!Directory.Exists(path)) return Task.FromResult(ctx.Fail($"ls: {path} does not exist."));
        var recursive = a.Flag("r") || a.Flag("recursive");
        var top = a.GetInt("top", 200);
        var since = a.GetDuration("since") is { } d ? DateTime.UtcNow - d : (DateTime?)null;
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos(a.Get("glob") ?? "*", options)
            .Where(e => since == null || e.LastWriteTimeUtc >= since)
            .Select(e => (info: e, size: e is FileInfo f ? f.Length : -1L));
        entries = (a.Get("sort") ?? (since != null ? "time" : "name")) switch
        {
            "size" => entries.OrderByDescending(e => e.size),
            "time" => entries.OrderByDescending(e => e.info.LastWriteTimeUtc),
            _ => entries.OrderBy(e => e.size >= 0).ThenBy(e => e.info.FullName, StringComparer.OrdinalIgnoreCase),
        };

        var t = new Table(">SIZE", "MODIFIED", "NAME");
        var total = 0;
        long bytes = 0;
        foreach (var (info, size) in entries)
        {
            total++;
            if (size > 0) bytes += size;
            if (total > top) continue;
            var name = recursive ? Path.GetRelativePath(path, info.FullName) : info.Name;
            t.Add(size < 0 ? "<dir>" : Format.Bytes(size), Format.Time(info.LastWriteTimeUtc), size < 0 ? name + "\\" : name);
        }

        ctx.Out.WriteLine($"{path}  ({total} entries, {Format.Bytes(bytes)})");
        if (total > 0) ctx.Out.Write(t.ToString());
        if (total > top) ctx.Out.WriteLine($"… {total - top} more (use --top, --glob or --since)");
        return Task.FromResult(0);
    }

    private static Task<int> ReadAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "new");
        TimeSpan betweenFrom = default, betweenTo = default;
        var between = a.Has("between");
        if (between)
        {
            // --between 12:02 12:05 (the second time arrives as a plain word, before or after the file's name),
            // or 12:02-12:05 / 12:02..12:05 in one.
            var parts = (a.Get("between") ?? "").Split(["..", "-"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var second = parts.Count == 1 ? a.Positionals.FindIndex(x => TimeOfDay(x) != null) : -1;
            if (second >= 0)
            {
                parts.Add(a.Positionals[second]);
                a.Positionals.RemoveAt(second);
            }

            if (parts.Count != 2 || TimeOfDay(parts[0]) is not { } t0 || TimeOfDay(parts[1]) is not { } t1)
                return Task.FromResult(ctx.Fail("read: --between wants two times of day: --between 12:02 12:05   (HH:MM or HH:MM:SS)"));
            (betweenFrom, betweenTo) = (t0, t1);
            if (a.Flag("new")) return Task.FromResult(ctx.Fail("read: --between goes through the whole file and does not combine with --new (which reads only what was appended)."));
        }

        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("read: which file?"));
        var path = ctx.Runtime.ResolvePath(a.Positionals[0]);
        if (!File.Exists(path)) return Task.FromResult(ctx.Fail($"read: {path} does not exist."));

        System.Text.RegularExpressions.Regex? grep = null;
        if (a.Get("grep") is { } pattern)
        {
            try
            {
                grep = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("read: bad --grep pattern: " + ex.Message));
            }
        }

        int? head = a.Has("head") ? Math.Max(1, a.GetInt("head", 50)) : null;
        int? tail = a.Has("tail") ? Math.Max(1, a.GetInt("tail", 50)) : null;
        long rangeFrom = 0, rangeTo = long.MaxValue;
        var hasRange = false;
        if (a.Get("lines") is { } range)
        {
            var parts = range.Split('-');
            if (!long.TryParse(parts[0], out rangeFrom)) return Task.FromResult(ctx.Fail("read: --lines wants A-B."));
            rangeTo = parts.Length > 1 && long.TryParse(parts[1], out var to) ? to : rangeFrom;
            hasRange = true;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var length = fs.Length;
        var utf16 = DetectUtf16(fs);
        var isNew = a.Flag("new");
        var note = "";
        long start = 0;
        var dropFirst = false; // a read that starts in the middle of the file starts in the middle of a line

        if (isNew)
        {
            if (ReadCursors.TryGetValue(path, out var cursor) && cursor <= length)
            {
                start = cursor;
            }
            else
            {
                start = Math.Max(0, length - (64 * 1024));
                dropFirst = start > 0;
                tail = 40;
                note = cursor > length ? "[file was truncated/rotated; showing its end]" : "[first read: showing the end; later --new calls return only appended lines]";
            }
        }
        else if (grep == null && !between && head == null && tail == null && !hasRange && length > 2 * 1024 * 1024)
        {
            start = length - (512 * 1024);
            dropFirst = true;
            tail = 200;
            note = $"[file is {Format.Bytes(length)}; showing its last 200 lines. Use --grep/--lines/--head/--tail to look elsewhere, or `pull` it]";
        }
        else if (tail != null && grep == null && !between && !hasRange && head == null && length > 8 * 1024 * 1024)
        {
            // Only the end is wanted: do not read gigabytes to reach it.
            start = Math.Max(0, length - Math.Max(4L * 1024 * 1024, tail.Value * 8192L));
            dropFirst = start > 0;
        }

        if (utf16 != null && start % 2 != 0) start--;

        // With --grep, --between, --lines or --head the file is scanned from its first byte to its last, whatever
        // its size: "no matches" must mean there are none.
        var numbered = (grep != null || between || hasRange) && start == 0;
        var inSpan = false; // --between: the last line that had a time was inside the span
        long timed = 0;
        TimeSpan? firstTime = null, lastTime = null;
        const int maxShown = 5000;
        var window = new Queue<(long no, string text)>();
        var windowSize = tail ?? (head == null ? (grep != null ? 300 : maxShown) : int.MaxValue);
        var listed = new List<(long no, string text)>();
        long lineNo = 0, matched = 0, consumed = start;
        foreach (var (text, end, complete) in Lines(fs, start, utf16))
        {
            if (dropFirst)
            {
                dropFirst = false;
                consumed = end;
                continue;
            }

            if (isNew && !complete) break; // still being written: it is returned whole by the next call
            consumed = end;
            lineNo++;
            if (hasRange && lineNo > rangeTo) break;
            if (between && TimeIn(text) is { } at)
            {
                inSpan = betweenFrom <= betweenTo ? at >= betweenFrom && at < betweenTo : at >= betweenFrom || at < betweenTo;
                if (!hasRange || lineNo >= rangeFrom)
                {
                    timed++;
                    firstTime ??= at;
                    lastTime = at;
                }
            }

            if (hasRange && lineNo < rangeFrom) continue;
            if (between && !inSpan) continue;

            if (grep != null && !grep.IsMatch(text)) continue;
            matched++;
            if (head != null && tail == null)
            {
                listed.Add((lineNo, text));
                if (listed.Count >= head) break;
            }
            else
            {
                window.Enqueue((lineNo, text));
                if (window.Count > windowSize) window.Dequeue();
            }
        }

        if (isNew) ReadCursors[path] = consumed;
        IEnumerable<(long no, string text)> result = head != null && tail == null ? listed : window;
        if (head != null && tail != null) result = window.Take(head.Value);
        var shown = result.ToList();
        if (tail == null && head == null && matched > shown.Count)
            note = grep != null ? $"[{matched} matches; showing the last {shown.Count}. Narrow the pattern or use --head/--tail]" : $"[{matched} lines; showing the last {shown.Count}. Use --lines/--head/--tail]";
        if (between && shown.Count == 0)
        {
            static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            var where = hasRange ? $"of lines {rangeFrom}-{rangeTo}" : "of this file";
            note = timed == 0
                ? $"[no line {where} has a time of day (HH:MM or HH:MM:SS) within its first 48 characters: --between has nothing to go by]"
                : $"[no {(grep != null ? "matching " : "")}line from {Clock(betweenFrom)} up to {Clock(betweenTo)}; the {timed} lines {where} that have a time run from {Clock(firstTime!.Value)} to {Clock(lastTime!.Value)}]";
        }

        var sb = new StringBuilder();
        foreach (var (no, text) in shown)
        {
            if (numbered) sb.Append(no).Append(": ");
            sb.Append(text).Append('\n');
        }

        ctx.Out.Write(sb.ToString());
        if (note.Length > 0) ctx.Out.WriteLine(note);
        else if (shown.Count == 0) ctx.Out.WriteLine(isNew ? "[no new lines]" : grep != null ? "[no matches]" : "[empty]");
        return Task.FromResult(0);
    }

    /// <summary>"12:02", "12:02:30" or "12:02:30.250" as a time of day, or null.</summary>
    private static TimeSpan? TimeOfDay(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:[.,]([0-9]{1,3}))?)?$");
        return m.Success ? TimeFrom(m, null) : null;
    }

    private static TimeSpan? TimeFrom(System.Text.RegularExpressions.Match m, string? half)
    {
        int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value), sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (half != null)
        {
            // A 12-hour clock: 12 AM is midnight, 1 PM is 13.
            if (h is < 1 or > 12) return null;
            h = (h % 12) + (half is "P" or "p" ? 12 : 0);
        }

        if (h > 23 || min > 59 || sec > 59) return null;
        var ms = m.Groups[4].Success ? int.Parse(m.Groups[4].Value.PadRight(3, '0')) : 0;
        return new TimeSpan(0, h, min, sec, ms);
    }

    /// <summary>The time of day a log line starts with: the first HH:MM[:SS[.fff]] within its first 48 characters, or null.</summary>
    private static TimeSpan? TimeIn(string line)
    {
        var m = LineTime().Match(line.Length > 80 ? line[..80] : line); // the time has to START within the first 48 characters
        if (!m.Success || m.Index >= 48) return null;
        return TimeFrom(m, m.Groups[5].Success ? m.Groups[5].Value : null);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![0-9:.])([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:[.,]([0-9]{1,3}))?)?(?![0-9:])(?: ?([AaPp])[Mm]\b)?")]
    private static partial System.Text.RegularExpressions.Regex LineTime();

    /// <summary>UTF-16 (with a BOM, or recognisable by its zero bytes), or null for byte-oriented text (UTF-8 / ANSI).</summary>
    private static Encoding? DetectUtf16(FileStream fs)
    {
        var probe = new byte[4096];
        fs.Position = 0;
        var n = fs.Read(probe, 0, probe.Length);
        if (n >= 2 && probe[0] == 0xFF && probe[1] == 0xFE) return Encoding.Unicode;
        if (n >= 2 && probe[0] == 0xFE && probe[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (n < 8) return null;
        int oddZeros = 0, evenZeros = 0;
        for (var i = 0; i + 1 < n; i += 2)
        {
            if (probe[i] == 0) evenZeros++;
            if (probe[i + 1] == 0) oddZeros++;
        }

        if (oddZeros > n / 8 && evenZeros < n / 64) return Encoding.Unicode;
        if (evenZeros > n / 8 && oddZeros < n / 64) return Encoding.BigEndianUnicode;
        return null;
    }

    /// <summary>
    /// Lines of a text file from a byte offset, with the offset just past each line and whether it ended in a newline.
    /// Each line is decoded on its own (strict UTF-8, else the ANSI code page), so a multi-byte character can never be
    /// cut by a buffer boundary and a file that mixes encodings still reads correctly.
    /// </summary>
    private static IEnumerable<(string text, long end, bool complete)> Lines(FileStream fs, long start, Encoding? utf16)
    {
        const int maxLine = 1 << 20; // a "line" longer than 1 MB is binary data or a minified blob: hand it out in pieces
        var strict = new UTF8Encoding(false, true);
        Encoding? ansi = null;
        var unit = utf16 != null ? 2 : 1;
        var buffer = new byte[1 << 16];
        var line = new List<byte>(256);
        fs.Position = start;
        var position = start;
        var first = start == 0;

        string Decode(List<byte> bytes)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
            string s;
            if (utf16 != null)
            {
                s = utf16.GetString(span);
            }
            else
            {
                try
                {
                    s = strict.GetString(span);
                }
                catch (DecoderFallbackException)
                {
                    if (ansi == null)
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        ansi = Encoding.GetEncoding((int)Native.Win32.GetACP());
                    }

                    s = ansi.GetString(span);
                }
            }

            if (first)
            {
                first = false;
                s = s.TrimStart('\uFEFF');
            }

            return s.TrimEnd('\r', '\n');
        }

        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length - (buffer.Length % unit))) > 0)
        {
            for (var i = 0; i + unit <= read; i += unit)
            {
                line.Add(buffer[i]);
                if (unit == 2) line.Add(buffer[i + 1]);
                position += unit;
                var newline = unit == 1
                    ? buffer[i] == (byte)'\n'
                    : (utf16 == Encoding.Unicode ? buffer[i] == 0x0A && buffer[i + 1] == 0 : buffer[i] == 0 && buffer[i + 1] == 0x0A);
                if (newline || line.Count >= maxLine)
                {
                    yield return (Decode(line), position, newline);
                    line.Clear();
                }
            }
        }

        if (line.Count > 0) yield return (Decode(line), position, false);
    }

    private static Task<int> StatAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return Task.FromResult(ctx.Fail("stat: which file?"));
        var rc = 0;
        foreach (var arg in args)
        {
            var path = ctx.Runtime.ResolvePath(arg);
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Select(f => new FileInfo(f)).ToList();
                ctx.Out.WriteLine($"{path}  folder, {files.Count} files, {Format.Bytes(files.Sum(f => f.Length))}");
                continue;
            }

            if (!File.Exists(path))
            {
                ctx.Err.WriteLine($"stat: {path} does not exist.");
                rc = 1;
                continue;
            }

            var fi = new FileInfo(path);
            ctx.Out.WriteLine(path);
            ctx.Out.WriteLine($"  size      {fi.Length} ({Format.Bytes(fi.Length)})");
            ctx.Out.WriteLine($"  modified  {Format.Time(fi.LastWriteTimeUtc)}   created {Format.Time(fi.CreationTimeUtc)}   attrs {fi.Attributes}");
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrEmpty(vi.FileVersion))
                ctx.Out.WriteLine($"  version   file {vi.FileVersion}  product {vi.ProductVersion}  \"{Format.Clip(vi.FileDescription, 60)}\"  {Format.Clip(vi.CompanyName, 40)}");
            if (DescribePe(path) is { } pe) ctx.Out.WriteLine($"  image     {pe}");
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // What the shortcut starts, as Explorer would: the three things that decide how the app comes up.
                if (Native.ShellLink.Read(path, out var why) is not { } link)
                {
                    ctx.Out.WriteLine($"  shortcut  (cannot be read: {why})");
                }
                else if (link.Target.Length == 0)
                {
                    ctx.Out.WriteLine("  shortcut  does not point to a file (a Store app, a web link or a shell folder)");
                }
                else if (link.Advertised)
                {
                    ctx.Out.WriteLine($"  shortcut  made by Windows Installer: the link holds no program path (Windows works it out when the shortcut is used), only its icon file {link.Target}");
                    ctx.Out.WriteLine($"            arguments {(link.Arguments.Length > 0 ? link.Arguments : "(none)")}");
                }
                else
                {
                    ctx.Out.WriteLine($"  shortcut  target    {link.Target}{(File.Exists(link.Target) || Directory.Exists(link.Target) ? "" : "   (DOES NOT EXIST)")}");
                    ctx.Out.WriteLine($"            arguments {(link.Arguments.Length > 0 ? link.Arguments : "(none)")}");
                    ctx.Out.WriteLine("            start in  " + (link.WorkingDirectory.Length == 0 ? "(none named)"
                        : link.WorkingDirectory + (Directory.Exists(link.WorkingDirectory) ? "" : "   (DOES NOT EXIST)")));
                }
            }

            try
            {
                using var s = fi.OpenRead();
                ctx.Out.WriteLine($"  sha256    {Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()}");
            }
            catch (IOException ex)
            {
                ctx.Out.WriteLine($"  sha256    (unreadable: {ex.Message})");
            }

            // Mark of the Web: files that came from the internet/another PC can be blocked by SmartScreen or load with reduced trust.
            try
            {
                var zone = File.ReadAllText(path + ":Zone.Identifier");
                var id = zone.Split('\n').FirstOrDefault(l => l.StartsWith("ZoneId=", StringComparison.Ordinal))?.Trim();
                ctx.Out.WriteLine($"  blocked   yes, marked as downloaded ({id}). Unblock-File removes it.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // no mark
            }
        }

        return Task.FromResult(rc);
    }

    /// <summary>"x64 native GUI" / "x86 .NET (AnyCPU, 32-bit preferred)" etc., or null when the file is not a PE image.</summary>
    public static string? DescribePe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            if (h.PEHeader == null) return null;
            var machine = h.CoffHeader.Machine switch
            {
                Machine.I386 => "x86",
                Machine.Amd64 => "x64",
                Machine.Arm64 => "arm64",
                Machine.Arm or Machine.ArmThumb2 => "arm",
                var m => m.ToString(),
            };
            var kind = h.IsDll ? "DLL" : h.PEHeader.Subsystem == Subsystem.WindowsCui ? "console EXE" : "GUI EXE";
            if (h.CorHeader is { } cor)
            {
                var flags = cor.Flags;
                var ilOnly = h.CoffHeader.Machine == Machine.I386 && (flags & CorFlags.ILOnly) != 0;

                // "Prefer 32-bit" sets the Requires32Bit bit as well, so it has to be tested first.
                var bitness = ilOnly && (flags & CorFlags.Prefers32Bit) != 0 ? "AnyCPU (32-bit preferred)"
                    : ilOnly && (flags & CorFlags.Requires32Bit) == 0 ? "AnyCPU"
                    : machine;
                return $"{bitness} .NET {kind}";
            }

            return $"{machine} native {kind}";
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Task<int> LocksAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return Task.FromResult(ctx.Fail("locks: which file?"));
        foreach (var arg in args)
        {
            var path = ctx.Runtime.ResolvePath(arg);
            var holders = WhoLocks(path, out var error);
            if (error != null) ctx.Out.WriteLine($"{path}: {error}");
            else if (holders.Count == 0) ctx.Out.WriteLine($"{path}: not held open by any process");
            else ctx.Out.WriteLine($"{path}: held by " + string.Join(", ", holders.Select(h => $"{h.name} (pid {h.pid})")));
        }

        return Task.FromResult(0);
    }

    /// <summary>Restart Manager answers "who has this file open" without touching other processes' handles.</summary>
    private static unsafe List<(int pid, string name)> WhoLocks(string path, out string? error)
    {
        var result = new List<(int, string)>();
        error = null;
        var key = stackalloc char[40];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            error = "Restart Manager unavailable";
            return result;
        }

        try
        {
            fixed (char* p = path)
            {
                var files = stackalloc char*[1];
                files[0] = p;
                if (RmRegisterResources(session, 1, files, 0, 0, 0, 0) != 0)
                {
                    error = "cannot query (does the file exist?)";
                    return result;
                }
            }

            const int infoSize = 668;
            uint count = 32;
            var buffer = new byte[infoSize * count];
            fixed (byte* b = buffer)
            {
                var rc = RmGetList(session, out _, ref count, b, out _);
                if (rc != 0)
                {
                    error = rc == 234 ? "more than 32 processes hold it" : $"Restart Manager error {rc}";
                    return result;
                }

                for (var i = 0; i < count; i++)
                {
                    var entry = b + (i * infoSize);
                    result.Add((*(int*)entry, new string((char*)(entry + 12))));
                }
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return result;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint session, int flags, char* key);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmRegisterResources(uint session, uint fileCount, char** files, uint appCount, nint apps, uint serviceCount, nint services);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmGetList(uint session, out uint needed, ref uint count, byte* infos, out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);
}
