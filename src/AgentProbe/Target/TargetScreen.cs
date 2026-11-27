using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentProbe.Controller;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Target;

/// <summary>
/// The window on the PC being shared, once a developer PC is connected. At the top, always: whose PC this is, who
/// controls it, that it is paused or not, where the log is, and buttons to pause and to open the log. Under that the
/// activity log as a list, newest last: a line too long for the window is one row ending in "…" until it is
/// clicked, then it shows in full (a command's whole text, a script included). The log file keeps every line as it
/// always did. Drawn from a list of lines on every change, like the start screen; a console that cannot do that
/// keeps the plain scrolling log.
/// </summary>
internal static partial class TargetScreen
{
    private sealed class Entry
    {
        public required string Time { get; init; }

        public required string Text { get; init; }

        /// <summary>The whole text when the log line was shortened (a command's full text), else null.</summary>
        public string? Detail { get; init; }

        public bool Open { get; set; }
    }

    /// <summary>What the header says, asked for at every redraw.</summary>
    public sealed record State(bool Connected, bool Paused, string Controller);

    private readonly record struct Row(string Text, int Indent, int Entry, int Button, string Sgr);

    private const int Margin = 2; // the first column holds + (click to see all) or - (click to fold)
    private const int ButtonPause = 1, ButtonResume = 2, ButtonLog = 3;

    private static readonly List<Entry> Entries = [];
    private static readonly ConcurrentQueue<Entry> Incoming = new();
    private static readonly object Gate = new();
    private static StreamWriter? _out;
    private static Thread? _thread;
    private static Func<State>? _state;
    private static Action<bool>? _pause;
    private static Func<string>? _title;
    private static string _logPath = "";
    private static List<Row> _header = [];
    private static List<Row> _rows = [];
    private static int _width, _height, _top, _hotEntry = -1, _hotButton, _ruleCells = 1, _pressedEntry = -1, _pressedButton;
    private static bool _follow = true, _dirty = true, _layout = true, _leftDown, _stop;
    private static State _shown = new(false, false, "");
    private static uint _inputMode, _outputMode;
    private static TextWriter? _oldOut, _oldError;
    private static volatile bool _active;

    public static bool Active => _active;

    /// <summary>Keeps a line of the activity log, for the screen when it is (or will be) shown.</summary>
    public static void Record(string time, string text, string? detail = null)
    {
        // The whole text is kept only when the line shortened it: "#3 exec ..." that fitted needs nothing more.
        if (detail != null && Flat(Prefix(text) + detail).TrimEnd() == Flat(text).TrimEnd()) detail = null;
        Incoming.Enqueue(new Entry { Time = time, Text = Clean(text), Detail = detail });
        while (!_active && Incoming.Count > 5000) Incoming.TryDequeue(out _); // a screen that never comes keeps only the newest
        _dirty = true;
    }

    /// <summary>Takes the console over. False, and nothing changed, when this console cannot show the screen.</summary>
    public static bool Start(Func<string> title, string logPath, Func<State> state, Action<bool> pause)
    {
        if (_active || Console.IsInputRedirected || Console.IsOutputRedirected) return _active;
        var input = Home.GetStdHandle(-10);
        var output = Home.GetStdHandle(-11);
        if (!Home.GetConsoleMode(input, out _inputMode) || !Home.GetConsoleMode(output, out _outputMode)) return false;
        if (!Home.SetConsoleMode(output, _outputMode | 0x0001u | 0x0004u)) return false; // escape sequences
        if (!Home.SetConsoleMode(input, 0x0080u | 0x0010u | 0x0008u))                    // mouse and resize events; no QuickEdit, no line editing
        {
            Home.SetConsoleMode(output, _outputMode);
            return false;
        }

        (_title, _logPath, _state, _pause) = (title, logPath, state, pause);
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 16384);

        // What the program still prints by itself ("waiting for ... to come back") becomes lines of the list.
        (_oldOut, _oldError) = (Console.Out, Console.Error);
        var lines = new LineWriter(line => Record(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), line.Trim()));
        Console.SetOut(lines);
        Console.SetError(lines);

        _out.Write("\u001b[?1049h\u001b[?7l"); // a screen of its own (the console's text comes back when it ends), no automatic line wrap
        _out.Write("\u001b[1;1H─");          // some East Asian console fonts draw the line two columns wide: measure it
        _out.Flush();
        _ruleCells = Math.Clamp(Console.CursorLeft, 1, 2);
        _active = true;
        _thread = new Thread(() => Loop(input)) { IsBackground = true, Name = "target screen" };
        _thread.Start();
        return true;
    }

    /// <summary>Gives the console back as it was (the window may stay open after sharing ends).</summary>
    public static void Stop()
    {
        if (!_active) return;
        _stop = true;
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(500);
        lock (Gate)
        {
            _active = false;
            try
            {
                _out?.Write("\u001b[0m\u001b[?25h\u001b[?7h\u001b[?1049l");
                _out?.Flush();
            }
            catch (IOException)
            {
                // the console is gone
            }

            Home.SetConsoleMode(Home.GetStdHandle(-10), _inputMode);
            Home.SetConsoleMode(Home.GetStdHandle(-11), _outputMode);
            if (_oldOut != null) Console.SetOut(_oldOut);
            if (_oldError != null) Console.SetError(_oldError);
        }
    }

    private static void Loop(nint input)
    {
        while (!_stop)
        {
            try
            {
                var added = false;
                while (Incoming.TryDequeue(out var e))
                {
                    Entries.Add(e);
                    added = true;
                }

                if (Entries.Count > 5000) Entries.RemoveRange(0, 1000);
                if (added) _layout = true;

                var state = _state?.Invoke() ?? _shown;
                if (state != _shown)
                {
                    _shown = state;
                    _layout = true;
                }

                var (width, height) = (Console.WindowWidth, Console.WindowHeight);
                if (width != _width || height != _height)
                {
                    (_width, _height) = (width, height);
                    lock (Gate) _out!.Write("\u001b[2J");
                    _layout = true;
                }

                if (_layout) Layout();
                if (_dirty) Render();
                if (Win32.WaitForSingleObject(input, 50) == 0) ReadInput(input);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                Thread.Sleep(200); // the window is being resized or closed
            }
        }
    }

    // ---------------------------------------------------------------- drawing

    private static int ListRows => Math.Max(1, _height - _header.Count - 3); // the header, two rules, the help line

    private static void Layout()
    {
        _layout = false;
        var cells = Math.Max(10, _width - Margin - 1);

        // The header: who, what state, where the log is, and the buttons. It never scrolls away.
        _header = [];
        var state = _shown;
        var tag = state.Paused ? Lang.T(" PAUSED: remote commands are refused ", " 일시 멈춤: 원격 명령을 받지 않음 ")
            : state.Connected ? Lang.T($" CONTROLLED by {state.Controller} ", $" 원격 제어 중: {state.Controller} ")
            : Lang.T(" not connected: waiting for the developer PC ", " 연결 끊김: 개발 PC를 기다리는 중 ");
        var title = _title?.Invoke() ?? "agentprobe";
        if (Home.Cells(title) + Home.Cells(tag) + 2 <= cells)
        {
            _header.Add(new Row(title + new string(' ', cells - Home.Cells(title) - Home.Cells(tag)) + "\u0001" + tag, 0, -1, 0, "1"));
        }
        else
        {
            // Narrow: the title wraps (whose rights these are must stay readable), the state goes under it.
            foreach (var part in Wrap(title, cells)) _header.Add(new Row(part, 0, -1, 0, "1"));
            _header.Add(new Row("\u0001" + Home.Clip(tag, cells), 0, -1, 0, ""));
        }

        foreach (var part in Wrap(Lang.T($"Everything done from the other PC is listed below and saved to {_logPath}", $"다른 PC에서 하는 모든 일이 아래에 나오고 다음 파일에 남습니다: {_logPath}"), cells))
            _header.Add(new Row(part, 0, -1, 0, "90"));
        _header.Add(state.Paused
            ? new Row(Lang.T(" [Enter] Resume ", " [Enter] 다시 받기 "), 0, -1, ButtonResume, "")
            : new Row(Lang.T(" [Space] Pause remote commands ", " [스페이스] 원격 명령 잠깐 멈추기 "), 0, -1, ButtonPause, ""));
        _header.Add(new Row(Lang.T(" [Click] Open the log file ", " [클릭] 로그 파일 열기 "), 0, -1, ButtonLog, ""));
        _header.Add(new Row(Lang.T("Close this window to stop sharing. Nothing is installed.", "공유를 끝내려면 이 창을 닫으세요. 설치되는 것은 없습니다."), 0, -1, 0, "90"));

        // The list: one row per line, or all of it for a line that was clicked open.
        _rows = [];
        for (var i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            var full = e.Detail != null ? Prefix(e.Text) + e.Detail : e.Text;
            var sgr = Color(e.Text);
            var room = cells - 10; // "HH:mm:ss  "
            if (!e.Open)
            {
                var flat = Flat(full);
                var shortened = Home.Cells(flat) > room;
                _rows.Add(new Row(e.Time + "  " + (shortened ? Home.Clip(flat, room - 3) + "..." : flat), 0, shortened || e.Detail != null ? i : -1, 0, sgr));
                continue;
            }

            var first = true;
            foreach (var line in full.Replace("\r", "").Split('\n'))
            {
                foreach (var part in Wrap(Clean(line), room))
                {
                    _rows.Add(new Row((first ? e.Time : "        ") + "  " + part, 0, i, 0, sgr));
                    first = false;
                }
            }
        }

        var last = Math.Max(0, _rows.Count - ListRows);
        _top = _follow ? last : Math.Clamp(_top, 0, last);
        _dirty = true;
    }

    /// <summary>"#12 " of "#12 exec ...": shown again in front of the command's full text.</summary>
    private static string Prefix(string text)
    {
        var space = text.IndexOf(' ');
        return text.StartsWith('#') && space > 0 ? text[..(space + 1)] : "";
    }

    /// <summary>One row's worth: the lines of a script joined with " | ".</summary>
    private static string Flat(string text) => Clean(string.Join(" | ", text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)));

    private static string Color(string text) =>
        text.Contains("PAUSED", StringComparison.Ordinal) ? "33"
        : text.StartsWith("connected to", StringComparison.Ordinal) ? "32"
        : text.StartsWith("connection lost", StringComparison.Ordinal) || text.StartsWith("refused", StringComparison.Ordinal) ? "33"
        : ExitLine().Match(text) is { Success: true } m && m.Groups[1].Value != "0" ? "33" // "#4 exit 3": a command that failed
        : "";

    [System.Text.RegularExpressions.GeneratedRegex(@"^#\d+ (?:finished while disconnected: )?exit (-?\d+)")]
    private static partial System.Text.RegularExpressions.Regex ExitLine();

    private static void Render()
    {
        _dirty = false;
        var sb = new StringBuilder(8192);
        sb.Append("\u001b[?25l\u001b[0m");
        if (_width < 30 || _height < _header.Count + 5)
        {
            sb.Append("\u001b[2J\u001b[1;1H").Append(Home.Clip(Lang.T("Make this window larger to see what is done here.", "여기서 하는 일을 보려면 창을 크게 해 주세요."), Math.Max(1, _width - 1)));
            Write(sb);
            return;
        }

        var y = 1;
        foreach (var row in _header) Line(sb, y++, row);
        Rule(sb, y++, "");
        for (var i = 0; i < ListRows; i++, y++)
        {
            // The + or - goes on the first row of a line that can be opened or folded.
            if (_top + i < _rows.Count) Line(sb, y, _rows[_top + i], _rows[_top + i].Entry >= 0 && (_top + i == 0 || _rows[_top + i - 1].Entry != _rows[_top + i].Entry));
            else sb.Append("\u001b[").Append(y).Append(";1H\u001b[0m\u001b[K");
        }

        var more = _top + ListRows < _rows.Count ? "v " + Lang.T("newer lines below: mouse wheel, PgDn or End", "아래에 새 줄이 더 있음: 마우스 휠, PgDn 또는 End")
            : _top > 0 ? "^ " + Lang.T("older lines above: mouse wheel or PgUp", "위에 이전 줄이 더 있음: 마우스 휠 또는 PgUp")
            : "";
        Rule(sb, y++, more);
        sb.Append("\u001b[").Append(y).Append(";1H\u001b[0m\u001b[90m ")
            .Append(Home.Clip(Lang.T("+ before a line: click it to see all of it, again to fold it   |   wheel, PgUp, PgDn: scroll", "줄 앞의 +: 클릭하면 전체가 보이고 다시 클릭하면 접힘   |   휠, PgUp, PgDn: 스크롤"), _width - 2)).Append("\u001b[0m\u001b[K");
        Write(sb);
    }

    private static void Line(StringBuilder sb, int y, Row row, bool first = false)
    {
        sb.Append("\u001b[").Append(y).Append(";1H\u001b[0m");
        var hot = (row.Button != 0 && row.Button == _hotButton) || (row.Entry >= 0 && row.Entry == _hotEntry);
        if (row.Entry >= 0 && first) sb.Append("\u001b[36m").Append(Entries[row.Entry].Open ? '-' : '+').Append("\u001b[0m ");
        else sb.Append(' ', Margin);
        sb.Append(' ', row.Indent);
        var tag = row.Text.IndexOf('\u0001');
        var key = row.Button != 0 && row.Text.StartsWith(" [", StringComparison.Ordinal) ? row.Text.IndexOf(']') : -1;
        if (row.Button != 0 && hot) sb.Append("\u001b[7m").Append(row.Text).Append("\u001b[0m");
        else if (key > 0) sb.Append(' ').Append("\u001b[36m").Append(row.Text, 1, key).Append("\u001b[0m").Append(row.Text, key + 1, row.Text.Length - key - 1);
        else if (row.Button != 0) sb.Append("\u001b[36m").Append(row.Text).Append("\u001b[0m");
        else if (tag >= 0)
        {
            // The state tag at the end of the title: red while controlled, yellow while paused, grey otherwise.
            var colour = _shown.Paused ? "30;43" : _shown.Connected ? "97;41" : "30;47";
            sb.Append(row.Sgr.Length > 0 ? $"\u001b[{row.Sgr}m" : "").Append(row.Text, 0, tag).Append("\u001b[0m\u001b[").Append(colour).Append('m').Append(row.Text, tag + 1, row.Text.Length - tag - 1).Append("\u001b[0m");
        }
        else
        {
            // The time in grey; under the mouse, a line that opens is underlined from where its text starts.
            var colour = row.Sgr.Length > 0 ? $"\u001b[{row.Sgr}m" : "";
            var timed = row.Text.Length > 10 && row.Text[2] == ':' && row.Text[5] == ':';
            if (timed) sb.Append("\u001b[90m").Append(row.Text, 0, 8).Append("\u001b[39m");
            var from = timed ? 8 : 0;
            var text = from;
            while (text < row.Text.Length && row.Text[text] == ' ') text++;
            sb.Append(row.Text, from, text - from).Append(colour).Append(hot ? "\u001b[4m" : "").Append(row.Text, text, row.Text.Length - text);
        }

        sb.Append("\u001b[0m\u001b[K");
    }

    private static void Rule(StringBuilder sb, int y, string note)
    {
        var cells = Math.Max(1, _width - 1);
        sb.Append("\u001b[").Append(y).Append(";1H\u001b[0m\u001b[90m");
        if (note.Length > 0)
        {
            note = Home.Clip(note, Math.Max(0, cells - (2 * _ruleCells) - 2));
            sb.Append('─', 2).Append(' ').Append(note).Append(' ');
            cells -= (2 * _ruleCells) + 2 + Home.Cells(note);
        }

        sb.Append('─', Math.Max(0, cells / _ruleCells)).Append("\u001b[0m\u001b[K");
    }

    private static void Write(StringBuilder sb)
    {
        lock (Gate)
        {
            if (!_active) return;
            _out!.Write(sb);
            _out.Flush();
        }
    }

    // ---------------------------------------------------------------- input

    private static unsafe void ReadInput(nint input)
    {
        const int Size = 20; // INPUT_RECORD
        var records = stackalloc byte[Size * 32];
        uint count;
        if (Home.GetNumberOfConsoleInputEvents(input, &count) == 0 || count == 0) return;
        if (Home.ReadConsoleInputW(input, records, 32, &count) == 0) return;
        for (var i = 0; i < count; i++)
        {
            var r = records + (i * Size);
            switch (*(ushort*)r)
            {
                case 1 when *(int*)(r + 4) != 0: // key down
                    Key(*(ushort*)(r + 10), (char)*(ushort*)(r + 14));
                    break;
                case 2: // mouse
                    Mouse(*(short*)(r + 4), *(short*)(r + 6), *(uint*)(r + 8), *(uint*)(r + 16));
                    break;
                case 0x10 when *(int*)(r + 4) == 0: // the window lost the focus: nothing is hovered
                    (_hotEntry, _hotButton, _dirty) = (-1, 0, true);
                    break;
            }
        }
    }

    /// <summary>Space pauses and Enter resumes, as before: keys without a letter, which an input method does not hold back.</summary>
    private static void Key(ushort vk, char ch)
    {
        switch (vk)
        {
            case 0x20:
                _pause?.Invoke(true);
                break;
            case 0x0D:
                _pause?.Invoke(false);
                break;
            case 0x21: // PgUp
                Scroll(-Math.Max(1, ListRows - 1));
                break;
            case 0x22: // PgDn
                Scroll(Math.Max(1, ListRows - 1));
                break;
            case 0x26: // Up
                Scroll(-1);
                break;
            case 0x28: // Down
                Scroll(1);
                break;
            case 0x24: // Home
                Scroll(-_rows.Count);
                break;
            case 0x23: // End
                Scroll(_rows.Count);
                break;
            default:
                if (ch == ' ') _pause?.Invoke(true);
                break;
        }
    }

    private static void Mouse(int x, int y, uint buttons, uint what)
    {
        if (what == 4) // wheel; the turn is in the high word
        {
            Scroll((short)(buttons >> 16) > 0 ? -3 : 3);
            return;
        }

        var (entry, button) = (-1, 0);
        if (y >= 0 && y < _header.Count) button = _header[y].Button != 0 && x >= Margin && x < Margin + Home.Cells(_header[y].Text) ? _header[y].Button : 0;
        var listY = y - _header.Count - 1;
        if (listY >= 0 && listY < ListRows && _top + listY < _rows.Count) entry = _rows[_top + listY].Entry;
        if (entry != _hotEntry || button != _hotButton)
        {
            (_hotEntry, _hotButton) = (entry, button);
            _dirty = true;
        }

        // Something happens when the button comes up over what it went down on.
        var left = (buttons & 1) != 0;
        if (left && !_leftDown) (_pressedEntry, _pressedButton) = (entry, button);
        else if (!left && _leftDown)
        {
            if (button != 0 && button == _pressedButton) Press(button);
            else if (entry >= 0 && entry == _pressedEntry && entry < Entries.Count)
            {
                Entries[entry].Open = !Entries[entry].Open;
                _follow = false;
                _layout = true;
            }

            (_pressedEntry, _pressedButton) = (-1, 0);
        }

        _leftDown = left;
    }

    private static void Press(int button)
    {
        switch (button)
        {
            case ButtonPause:
                _pause?.Invoke(true);
                break;
            case ButtonResume:
                _pause?.Invoke(false);
                break;
            case ButtonLog when File.Exists(_logPath):
                try
                {
                    Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // no program for .log files
                }

                break;
        }
    }

    private static void Scroll(int rows)
    {
        var last = Math.Max(0, _rows.Count - ListRows);
        _top = Math.Clamp(_top + rows, 0, last);
        _follow = _top == last;
        _dirty = true;
    }

    // ---------------------------------------------------------------- text

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\t') sb.Append("    ");
            else if (c == '\n' || c == '\r' || (c >= 32 && c != 127 && c != '\u0001')) sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Rows of at most this many columns, broken at a space where there is one.</summary>
    private static List<string> Wrap(string text, int cells)
    {
        cells = Math.Max(8, cells);
        var rows = new List<string>();
        var rest = text;
        while (Home.Cells(rest) > cells)
        {
            var cut = Math.Max(1, Home.Clip(rest, cells).Length);
            var space = rest.LastIndexOf(' ', cut - 1);
            if (rest[cut] == ' ') space = cut;
            if (space > cut / 2)
            {
                rows.Add(rest[..space]);
                rest = rest[(space + 1)..];
            }
            else
            {
                rows.Add(rest[..cut]);
                rest = rest[cut..];
            }
        }

        rows.Add(rest);
        return rows;
    }

    /// <summary>Text written to the console while the screen is up, as whole lines.</summary>
    private sealed class LineWriter(Action<string> line) : TextWriter
    {
        private readonly StringBuilder _partial = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_partial)
            {
                if (value == '\n')
                {
                    var text = _partial.ToString().TrimEnd('\r');
                    _partial.Clear();
                    if (text.Trim().Length > 0) line(text);
                }
                else
                {
                    _partial.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var c in value) Write(c);
        }
    }
}
