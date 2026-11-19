using System.Text;

namespace AgentProbe.Controller;

/// <summary>
/// Keeps an agent's context small: prints the first lines as they arrive and the last lines at the end,
/// and writes everything to a local file the agent can grep instead of reading it all.
/// </summary>
internal sealed class OutputLimiter(string tool, bool unlimited, int headLines = 200, int tailLines = 100, int maxLineChars = 2000)
{
    private readonly Queue<string> _tail = new();
    private readonly StringBuilder _partial = new();
    private readonly StringBuilder _all = new();
    private StreamWriter? _file;
    private int _lines;
    private long _omitted;

    public string? FilePath { get; private set; }

    public void Write(string text, bool err)
    {
        var stream = err ? Console.Error : Console.Out;
        if (unlimited)
        {
            stream.Write(text);
            return;
        }

        _partial.Append(text);
        while (true)
        {
            var s = _partial.ToString();
            var nl = s.IndexOf('\n');
            if (nl < 0) break;
            _partial.Remove(0, nl + 1);
            Line(s[..nl], stream);
        }
    }

    private void Line(string line, TextWriter stream)
    {
        _lines++;
        if (_file != null) _file.WriteLine(line);
        else _all.Append(line).Append('\n');

        var shown = line.Length > maxLineChars ? line[..maxLineChars] + $"…[+{line.Length - maxLineChars} chars]" : line;
        if (_lines <= headLines)
        {
            stream.WriteLine(shown);
            return;
        }

        if (_file == null) OpenFile();
        _tail.Enqueue(shown);
        if (_tail.Count > tailLines)
        {
            _tail.Dequeue();
            _omitted++;
        }
    }

    private void OpenFile()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), AppInfo.Name, "out");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{tool}-{Environment.ProcessId}.txt");
            _file = new StreamWriter(FilePath, false, new UTF8Encoding(false));
            _file.Write(_all.ToString());
            _all.Clear();
        }
        catch (IOException)
        {
            FilePath = null;
        }
    }

    public void Finish()
    {
        if (unlimited) return;
        if (_partial.Length > 0) Line(_partial.ToString(), Console.Out);
        _file?.Dispose();
        if (_tail.Count == 0) return;
        if (_omitted > 0)
            Console.Out.WriteLine($"[... {_omitted} lines omitted. Full output ({_lines} lines): {FilePath ?? "(could not be saved)"} - search it locally, or rerun with --grep/--tail, or --full ...]");
        foreach (var l in _tail) Console.Out.WriteLine(l);
    }
}
