using System.Text;

namespace AgentProbe.Controller;

/// <summary>
/// Keeps an agent's context small: prints the first lines as they arrive and the last lines at the end,
/// and writes everything to a local file the agent can grep instead of reading it all.
/// <para>
/// What is printed has a budget in characters as well as in lines, because it is characters that cost tokens:
/// three hundred lines of a minified log are a hundred times three hundred short ones. A line that comes several
/// times in a row is printed once with its count. The saved file always holds the output as it came.
/// </para>
/// </summary>
internal sealed class OutputLimiter
{
    private const int HeadLines = 200;
    private const int TailLines = 100;
    private const int HeadChars = 16_000;
    private const int TailChars = 8_000;
    private const int MaxLineChars = 2000;
    private const int QuietTail = 5;

    private readonly string _tool;
    private readonly bool _unlimited;
    private readonly bool _quietOk;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    private readonly List<(string text, bool err, int raw)> _head = []; // quiet mode only: held back until the exit code is known
    private readonly Queue<(string text, bool err, int raw)> _tail = new();
    private readonly StringBuilder _partial = new();
    private readonly StringBuilder _all = new();
    private StreamWriter? _file;
    private bool _headClosed;
    private int _headCount, _headChars, _tailChars;
    private long _lines, _headRaw, _tailRaw;

    // The line that came last, held back until a different one follows: a run of the same line is one line.
    private string? _held;
    private bool _heldErr;
    private int _heldCount;

    /// <param name="quietOk">
    /// <c>exec --quiet-ok</c>: nothing is printed until the command has ended. Exit code 0 then gives its last few
    /// lines and where the rest is; any other code gives the output as usual.
    /// </param>
    public OutputLimiter(string tool, bool unlimited, TextWriter? stdout = null, TextWriter? stderr = null, bool quietOk = false)
    {
        _tool = tool;
        _unlimited = unlimited;
        _quietOk = quietOk && !unlimited;
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
    }

    public string? FilePath { get; private set; }

    /// <summary>The same limits for text that is already complete (the MCP server collects a command's output first).</summary>
    public static string Cut(string text, string tool, bool quietOk = false, int exitCode = 0)
    {
        var sw = new StringWriter { NewLine = "\n" };
        var limiter = new OutputLimiter(tool, false, sw, sw, quietOk);
        limiter.Write(text, false);
        limiter.Finish(exitCode);
        return sw.ToString();
    }

    public void Write(string text, bool err)
    {
        if (_unlimited)
        {
            (err ? _err : _out).Write(text);
            return;
        }

        _partial.Append(text);
        while (true)
        {
            var s = _partial.ToString();
            var nl = s.IndexOf('\n');
            if (nl < 0) break;
            _partial.Remove(0, nl + 1);
            Line(s[..nl], err);
        }
    }

    private void Line(string line, bool err)
    {
        _lines++;
        if (_file != null) _file.WriteLine(line);
        else _all.Append(line).Append('\n');

        if (_held != null && line.Length > 0 && err == _heldErr && line == _held)
        {
            _heldCount++;
            return;
        }

        FlushHeld();
        (_held, _heldErr, _heldCount) = (line, err, 1);
    }

    private void FlushHeld()
    {
        if (_held == null) return;
        var shown = _held.Length > MaxLineChars ? _held[..MaxLineChars] + $"…[+{_held.Length - MaxLineChars} chars]" : _held;
        if (_heldCount >= 3) Emit($"{shown}  [x{_heldCount}: this line came {_heldCount} times in a row]", _heldErr, _heldCount);
        else for (var i = 0; i < _heldCount; i++) Emit(shown, _heldErr, 1);
        _held = null;
    }

    private void Emit(string text, bool err, int raw)
    {
        if (!_headClosed && _headCount < HeadLines && _headChars + text.Length <= HeadChars)
        {
            _headCount++;
            _headChars += text.Length;
            _headRaw += raw;
            if (_quietOk) _head.Add((text, err, raw));
            else (err ? _err : _out).WriteLine(text);
            return;
        }

        _headClosed = true;
        if (_file == null) OpenFile();
        _tail.Enqueue((text, err, raw));
        _tailChars += text.Length;
        _tailRaw += raw;
        while (_tail.Count > 1 && (_tail.Count > TailLines || _tailChars > TailChars))
        {
            var dropped = _tail.Dequeue();
            _tailChars -= dropped.text.Length;
            _tailRaw -= dropped.raw;
        }
    }

    private void OpenFile()
    {
        if (_file != null || FilePath != null) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), AppInfo.Name, "out");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{_tool}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..4]}.txt");
            _file = new StreamWriter(FilePath, false, new UTF8Encoding(false));
            _file.Write(_all.ToString());
            _all.Clear();
        }
        catch (IOException)
        {
            FilePath = null;
        }
    }

    public void Finish(int exitCode = 0)
    {
        if (_unlimited) return;
        if (_partial.Length > 0) Line(_partial.ToString(), false);
        FlushHeld();

        if (_quietOk && exitCode == 0)
        {
            // It went well: the end of the output and where the rest is. (Nothing is "omitted" when it all fits.)
            var entries = _head.Concat(_tail).ToList();
            var last = entries.TakeLast(QuietTail).ToList();
            var hidden = _lines - last.Sum(e => (long)e.raw);
            if (hidden > 0)
            {
                OpenFile();
                _file?.Dispose();
                _file = null;
                _out.WriteLine($"[exit 0; --quiet-ok shows the last lines only. {hidden} earlier lines not shown. Full output ({_lines} lines): {FilePath ?? "(could not be saved)"}]");
            }

            foreach (var (text, err, _) in last) (err ? _err : _out).WriteLine(text);
            _file?.Dispose();
            return;
        }

        _file?.Dispose();
        foreach (var (text, err, _) in _head) (err ? _err : _out).WriteLine(text);
        if (_tail.Count == 0) return;
        var omitted = _lines - _headRaw - _tailRaw;
        if (omitted > 0)
            _out.WriteLine($"[... {omitted} lines omitted. Full output ({_lines} lines): {FilePath ?? "(could not be saved)"} - search it locally, or rerun with --grep/--tail, or --full ...]");
        foreach (var (text, err, _) in _tail) (err ? _err : _out).WriteLine(text);
    }
}
