using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Transport;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>
/// What a person sees when they start agentprobe on the developer PC: where the dashboard is and exactly what to give
/// an AI agent. In a real console it is a small full-screen view: the items react to the mouse (hover, click) and to
/// their number keys, and a command line stays pinned at the bottom. Piped, or in a console that cannot do that, the
/// same information is printed as plain text.
/// </summary>
internal static partial class Home
{
    /// <param name="openBrowser">Also open the dashboard in the default browser.</param>
    /// <param name="interactive">Show the full-screen view when the console allows it.</param>
    /// <param name="ownWindow">The console window belongs to this process (a double-click).</param>
    public static async Task<int> ShowAsync(bool openBrowser, bool interactive, bool ownWindow = false)
    {
        var facts = await FetchAsync();
        if (facts == null)
        {
            Console.Error.WriteLine(Lang.T("Could not reach the agentprobe background process.", "agentprobe 백그라운드 프로세스에 연결하지 못했습니다."));
            return 1;
        }

        var content = new Content(facts);
        if (openBrowser) OpenBrowser(content.Url);
        var console = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (console) Console.OutputEncoding = new UTF8Encoding(false);
        if (console && ownWindow) Console.Title = Lang.T("agentprobe - developer PC", "agentprobe - 개발 PC");

        if (console && interactive)
        {
            switch (new Screen(content).Run())
            {
                case Closed.ByUser when ownWindow:
                    return 0; // the window goes away with us
                case Closed.ByUser:
                    break;    // back in the person's shell: leave the information there as text
                case Closed.Stopped:
                    Console.WriteLine(Lang.T("agentprobe stopped.", "agentprobe를 종료했습니다."));
                    return 0;
                case Closed.NotSupported:
                    break;
            }
        }

        if (console && ownWindow) Console.Clear();
        PrintPlain(content, Console.Out);
        if (console && ownWindow)
        {
            Console.WriteLine();
            Console.Write(Lang.T(
                "Press Enter to close this window. agentprobe keeps running in the background (stop it with `agentprobe stop`).",
                "Enter를 누르면 이 창이 닫힙니다. agentprobe는 백그라운드에서 계속 실행됩니다 (`agentprobe stop`으로 종료)."));
            Console.ReadLine();
        }

        return 0;
    }

    private sealed record Facts(string Url, string Key, string Addresses, string Targets);

    private static async Task<Facts?> FetchAsync()
    {
        var sw = new StringWriter();
        var code = await Cli.DaemonAsync(new PipeMsg { Op = "home" }, "home", true, sw);
        var values = new Dictionary<string, string>();
        foreach (var line in sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0) values[line[..i]] = line[(i + 1)..].Trim();
        }

        if (code != 0 || !values.TryGetValue("url", out var url)) return null;
        return new Facts(url, values.GetValueOrDefault("key", "?"), values.GetValueOrDefault("addr", ""), values.GetValueOrDefault("targets", ""));
    }

    /// <summary>The texts of the start screen, built once and used by both the full-screen and the plain form.</summary>
    private sealed class Content
    {
        public Content(Facts facts)
        {
            var exe = AppInfo.ExePath;
            OnPath = UserPath.Contains(Path.GetDirectoryName(exe)!);
            var command = OnPath ? "agentprobe" : $"\"{exe}\"";
            Url = facts.Url;
            Targets = facts.Targets;
            Title = $"agentprobe {AppInfo.Version}  -  " + Lang.T("developer PC", "개발 PC") + $" \"{Environment.MachineName}\"";
            Instructions = $"To debug or test on the remote Windows PC, use the agentprobe CLI: {command}. Run `{command} guide` first.";
            ClaudeMcp = $"claude mcp add agentprobe -- \"{exe}\" mcp";
            CodexMcp = $"codex mcp add agentprobe -- \"{exe}\" mcp";
            Chat = Lang.T(
                $"A Windows PC is shared with this machine through agentprobe. Run `{command} guide` and `{command} list`, then use that CLI to investigate: ",
                $"agentprobe로 이 PC에 Windows PC 한 대가 공유되어 있어. `{command} guide`와 `{command} list`를 실행한 다음, 그 명령으로 다음 문제를 조사해 줘: ");
            Give = Lang.T(
                "Give your AI agent ONE of these. It is the same for Claude Code, Codex and others.",
                "아래 중 하나를 AI 에이전트에게 주세요. Claude Code, Codex 등 어떤 에이전트든 같습니다.");
            InstructionsLabel = Lang.T("A line for its instructions file (CLAUDE.md, AGENTS.md, ...)", "지침 파일(CLAUDE.md, AGENTS.md 등)에 넣을 한 줄");
            ClaudeLabel = Lang.T("Register as an MCP server in Claude Code (then restart the agent)", "Claude Code에 MCP 서버로 등록 (등록 후 에이전트 다시 시작)");
            CodexLabel = Lang.T("Register as an MCP server in Codex (then restart the agent)", "Codex에 MCP 서버로 등록 (등록 후 에이전트 다시 시작)");
            ChatLabel = Lang.T("Or just say it in the chat", "또는 대화에서 바로 말하기");
            TargetHow = Lang.T(
                $"PC to test: copy this exe there and run it, then approve \"{Environment.MachineName}\" there (key {facts.Key}).",
                $"테스트할 PC: 이 exe를 그 PC에 복사해 실행하고, 거기서 \"{Environment.MachineName}\"(키 {facts.Key})을(를) 승인하세요.");
            TargetManual = Lang.T(
                $"Not found automatically (other subnet, VPN)? Start it there as: agentprobe share <one of: {facts.Addresses}>",
                $"자동으로 찾지 못하면 (다른 서브넷, VPN) 그 PC에서: agentprobe share <다음 중 하나: {facts.Addresses}>");
            Tip = OnPath
                ? ""
                : Lang.T(
                    $"Tip: run `\"{exe}\" setup` once (PATH and firewall port); after that `agentprobe` alone works everywhere.",
                    $"참고: `\"{exe}\" setup`을 한 번 실행하면 (PATH 등록, 방화벽 포트) 어디서나 `agentprobe`만 입력하면 됩니다.");
        }

        public bool OnPath { get; }

        public string Url { get; }

        /// <summary>Names of the connected PCs; refreshed while the screen is open.</summary>
        public volatile string Targets;

        public string Title { get; }

        public string Instructions { get; }

        public string ClaudeMcp { get; }

        public string CodexMcp { get; }

        public string Chat { get; }

        public string Give { get; }

        public string InstructionsLabel { get; }

        public string ClaudeLabel { get; }

        public string CodexLabel { get; }

        public string ChatLabel { get; }

        public string TargetHow { get; }

        public string TargetManual { get; }

        public string Tip { get; }

        public string Connected => Lang.T("Connected now: ", "지금 연결된 PC: ") + (Targets.Length == 0 ? Lang.T("none yet", "아직 없음") : Targets);
    }

    private static void PrintPlain(Content c, TextWriter o)
    {
        o.WriteLine(c.Title);
        o.WriteLine();
        o.WriteLine("  " + Lang.T("Dashboard (open in a browser on this PC)", "대시보드 (이 PC의 브라우저에서 열기)"));
        o.WriteLine("    " + c.Url);
        o.WriteLine();
        o.WriteLine("  " + c.Give);
        o.WriteLine();
        o.WriteLine("  1. " + c.InstructionsLabel);
        o.WriteLine("    " + c.Instructions);
        o.WriteLine();
        o.WriteLine("  2. " + c.ClaudeLabel);
        o.WriteLine("    " + c.ClaudeMcp);
        o.WriteLine();
        o.WriteLine("  3. " + c.CodexLabel);
        o.WriteLine("    " + c.CodexMcp);
        o.WriteLine();
        o.WriteLine("  4. " + c.ChatLabel);
        o.WriteLine("    " + c.Chat);
        o.WriteLine();
        o.WriteLine("  " + c.TargetHow);
        o.WriteLine("  " + c.TargetManual);
        if (c.Tip.Length > 0) o.WriteLine("  " + c.Tip);
        o.WriteLine();
        o.WriteLine("  " + c.Connected);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no default browser: the printed link still works
        }
    }

    private enum Closed
    {
        NotSupported,
        ByUser,
        Stopped,
    }

    /// <summary>One logical line of the view; it is wrapped to the window width when shown.</summary>
    /// <param name="Item">Index of the menu item this line belongs to, or -1.</param>
    /// <param name="Label">The line of a menu item that is highlighted under the mouse.</param>
    /// <param name="Sgr">Colour, as the parameters of an SGR escape sequence.</param>
    /// <param name="Live">Text that can change while the screen is open (overrides Text).</param>
    private sealed record Block(string Text, int Indent = 0, int Item = -1, bool Label = false, string Sgr = "", Func<string>? Live = null);

    private readonly record struct Row(string Text, int Indent, int Item, bool Label, string Sgr);

    /// <summary>
    /// The full-screen view. Everything is drawn from a list of lines, so a resize, a scroll or new output is just
    /// another redraw: the lines above, and at the bottom a fixed command line between two rules (the only place the
    /// caret ever is) with a status line under it.
    /// </summary>
    private sealed class Screen
    {
        private const int FixedRows = 4; // rule, command line, rule, status
        private const int Margin = 1;    // empty columns at the left edge

        private readonly Content _c;
        private readonly List<(string label, Func<string> action)> _items = [];
        private readonly List<Row> _rows = [];
        private readonly StringBuilder _input = new();
        private readonly List<string> _history = [];
        private readonly ConcurrentQueue<(string? text, bool error, int code)> _output = new();
        private readonly StreamWriter _out = new(Console.OpenStandardOutput(), new UTF8Encoding(false), 16384);
        private readonly CancellationTokenSource _refreshStop = new();
        private List<Block> _blocks = [];
        private Task? _refreshTask;
        private Process? _process;
        private int _width, _height, _top, _caret, _historyAt, _hot = -1, _ruleCells = 1, _inputStart;
        private bool _follow, _dirty = true, _leftDown, _exit, _stopped, _exitWhenDone;
        private volatile bool _layout = true;
        private string _status = "";
        private long _statusUntil;

        public Screen(Content content) => _c = content;

        private int ViewRows => Math.Max(1, _height - FixedRows);

        public Closed Run()
        {
            var input = GetStdHandle(-10);
            var output = GetStdHandle(-11);
            if (!GetConsoleMode(input, out var inputMode) || !GetConsoleMode(output, out var outputMode)) return Closed.NotSupported;
            if (!SetConsoleMode(output, outputMode | 0x0001u | 0x0004u)) return Closed.NotSupported; // escape sequences
            if (!SetConsoleMode(input, 0x0080u | 0x0010u | 0x0008u))                                // mouse and resize events; no QuickEdit, no line editing
            {
                SetConsoleMode(output, outputMode);
                return Closed.NotSupported;
            }

            // Nothing else may write into the picture (a failed background refresh would print an error).
            var (oldOut, oldError) = (Console.Out, Console.Error);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            _out.Write("\u001b[?1049h\u001b[?7l"); // a separate screen (the shell's own text comes back when we leave), no automatic line wrap
            try
            {
                // Some East Asian console fonts draw the line character two columns wide: measure, do not assume.
                _out.Write("\u001b[1;1H─");
                _out.Flush();
                _ruleCells = Math.Clamp(Console.CursorLeft, 1, 2);

                _blocks = HomeBlocks();
                _refreshTask = Task.Run(RefreshAsync);
                Loop(input);
            }
            finally
            {
                StopRefresh();
                StopCommand();
                _out.Write("\u001b[0m\u001b[?25h\u001b[?7h\u001b[?1049l");
                _out.Flush();
                SetConsoleMode(input, inputMode);
                SetConsoleMode(output, outputMode);
                Console.SetOut(oldOut);
                Console.SetError(oldError);
            }

            return _stopped ? Closed.Stopped : Closed.ByUser;
        }

        private List<Block> HomeBlocks()
        {
            _items.Clear();
            var blocks = new List<Block> { new(_c.Title, 0, Sgr: "1"), new(""), new("", 2, Live: () => _c.Connected), new(""), new(_c.Give, 2) };

            void Item(string label, string detail, Func<string> action)
            {
                var index = _items.Count;
                _items.Add((label, action));
                blocks.Add(new Block($" [{index + 1}] {label} ", 3, index, true));
                if (detail.Length > 0) blocks.Add(new Block(detail, 8, index, Sgr: "90"));
            }

            Item(_c.InstructionsLabel, _c.Instructions, () => Copy(_c.Instructions));
            Item(_c.ClaudeLabel, _c.ClaudeMcp, () => Copy(_c.ClaudeMcp));
            Item(_c.CodexLabel, _c.CodexMcp, () => Copy(_c.CodexMcp));
            Item(_c.ChatLabel, _c.Chat, () => Copy(_c.Chat));
            blocks.Add(new Block(""));
            blocks.Add(new Block(Lang.T("Dashboard", "대시보드") + "  " + _c.Url, 2));
            Item(Lang.T("Open it in the browser", "브라우저에서 열기"), "", () =>
            {
                OpenBrowser(_c.Url);
                return Lang.T("opened in the browser", "브라우저에서 열었습니다");
            });
            Item(Lang.T("Copy the link", "주소 복사"), "", () => Copy(_c.Url));
            blocks.Add(new Block(""));
            blocks.Add(new Block(_c.TargetHow, 2));
            blocks.Add(new Block(_c.TargetManual, 2));
            if (_c.Tip.Length > 0) blocks.Add(new Block(_c.Tip, 2));
            return blocks;
        }

        private void Loop(nint input)
        {
            while (!_exit)
            {
                while (_output.TryDequeue(out var item))
                {
                    if (item.text != null)
                    {
                        Append(new Block(Clean(item.text), 0, Sgr: item.error ? "33" : ""));
                        continue;
                    }

                    // The command has ended and all of its output has been read.
                    _process?.Dispose();
                    _process = null;
                    if (item.code != 0) Append(new Block(Lang.T($"(exit code {item.code})", $"(종료 코드 {item.code})"), 0, Sgr: "90"));
                    _dirty = true;
                    if (_exitWhenDone)
                    {
                        _stopped = item.code == 0;
                        _exit = true;
                    }
                }

                var (width, height) = (Console.WindowWidth, Console.WindowHeight);
                if (width != _width || height != _height)
                {
                    (_width, _height) = (width, height);
                    _out.Write("\u001b[2J");
                    _layout = true;
                }

                if (_statusUntil != 0 && Environment.TickCount64 > _statusUntil)
                {
                    (_status, _statusUntil) = ("", 0);
                    _dirty = true;
                }

                if (_layout) Layout();
                if (_dirty) Render();
                if (Win32.WaitForSingleObject(input, 30) == 0) ReadInput(input);
            }
        }

        // ---------------------------------------------------------------- drawing

        private void Layout()
        {
            _layout = false;
            _rows.Clear();
            foreach (var b in _blocks)
            {
                var text = b.Live?.Invoke() ?? b.Text;
                var hang = b.Label ? text.IndexOf(']') + 2 : 0; // " [1] "
                var first = true;
                foreach (var part in Wrap(text, _width - b.Indent - Margin - 1, hang))
                {
                    _rows.Add(new Row(part, b.Indent + (first ? 0 : hang), b.Item, b.Label, b.Sgr));
                    first = false;
                }
            }

            var last = Math.Max(0, _rows.Count - ViewRows);
            _top = _follow ? last : Math.Clamp(_top, 0, last);
            _dirty = true;
        }

        private void Render()
        {
            _dirty = false;
            var sb = new StringBuilder(4096);
            sb.Append("\u001b[?25l"); // no caret while painting
            if (_width < 24 || _height < 7)
            {
                sb.Append("\u001b[0m\u001b[2J\u001b[1;1H").Append(Clip(Lang.T("Make this window larger.", "창을 더 크게 해 주세요."), Math.Max(1, _width - 1)));
                _out.Write(sb);
                _out.Flush();
                return;
            }

            for (var y = 0; y < ViewRows; y++)
            {
                sb.Append("\u001b[").Append(y + 1).Append(";1H\u001b[0m");
                if (_top + y < _rows.Count)
                {
                    var row = _rows[_top + y];
                    sb.Append(' ', row.Indent + Margin);
                    var key = row.Label && row.Text.StartsWith(" [", StringComparison.Ordinal) ? row.Text.IndexOf(']') : -1;
                    if (row.Label && row.Item == _hot) sb.Append("\u001b[7m").Append(row.Text).Append("\u001b[27m");
                    else if (key > 0) sb.Append(' ').Append("\u001b[36m").Append(row.Text, 1, key).Append("\u001b[39m").Append(row.Text, key + 1, row.Text.Length - key - 1);
                    else if (row.Sgr.Length > 0) sb.Append("\u001b[").Append(row.Sgr).Append('m').Append(row.Text);
                    else sb.Append(row.Text);
                }

                sb.Append("\u001b[0m\u001b[K");
            }

            // The command line sits between two rules. The upper one says so when part of the text is outside
            // the window.
            var hint = _top + ViewRows < _rows.Count ? "v " + Lang.T("more below: mouse wheel or PgDn", "아래에 더 있음: 마우스 휠 또는 PgDn")
                : _top > 0 ? "^ " + Lang.T("more above: mouse wheel or PgUp", "위에 더 있음: 마우스 휠 또는 PgUp")
                : "";
            sb.Append("\u001b[").Append(_height - 3).Append(";1H\u001b[0m\u001b[90m").Append(Rule(hint)).Append("\u001b[0m\u001b[K");
            sb.Append("\u001b[").Append(_height - 1).Append(";1H\u001b[0m\u001b[90m").Append(Rule("")).Append("\u001b[0m\u001b[K");

            // The caret lives on the command line and nowhere else.
            var room = Math.Max(4, _width - 4);
            var start = 0;
            while (Cells(_input.ToString(start, _caret - start)) > room - 1) start++;
            _inputStart = start;
            var shown = Clip(_input.ToString(start, _input.Length - start), room);
            sb.Append("\u001b[").Append(_height - 2).Append(";1H\u001b[0m\u001b[1m > \u001b[0m");
            if (_input.Length == 0) sb.Append("\u001b[90m").Append(Clip(Lang.T("Type a command: list, help, -t <name> triage ...", "명령 입력: list, help, -t <이름> triage ..."), room));
            else sb.Append(shown);
            sb.Append("\u001b[0m\u001b[K");

            var status = _status.Length > 0 ? _status
                : _process != null ? Lang.T("Running...   Esc: stop it", "실행 중...   Esc: 중단")
                : Lang.T("Number key or click: run an item above   |   Enter: run the command   |   Esc: close", "숫자 키 또는 클릭: 위 항목 실행   |   Enter: 명령 실행   |   Esc: 닫기");
            sb.Append("\u001b[0m\u001b[").Append(_height).Append(";1H")
                .Append(_status.Length > 0 ? "\u001b[32m" : "\u001b[90m").Append(' ').Append(Clip(status, _width - 2)).Append("\u001b[0m\u001b[K");

            sb.Append("\u001b[").Append(_height - 2).Append(';').Append(4 + Cells(_input.ToString(start, _caret - start))).Append("H\u001b[?25h");
            _out.Write(sb);
            _out.Flush();
        }

        /// <summary>A horizontal line over the width of the window, with an optional note near its start.</summary>
        private string Rule(string note)
        {
            var cells = Math.Max(1, _width - 1);
            var sb = new StringBuilder();
            if (note.Length > 0)
            {
                note = Clip(note, Math.Max(0, cells - (2 * _ruleCells) - 2));
                sb.Append('─', 2).Append(' ').Append(note).Append(' ');
                cells -= (2 * _ruleCells) + 2 + Cells(note);
            }

            return sb.Append('─', Math.Max(0, cells / _ruleCells)).ToString();
        }

        private void Append(Block block)
        {
            _blocks.Add(block);
            if (_blocks.Count > 4000) _blocks.RemoveRange(0, 1000);
            _follow = true;
            _layout = true;
        }

        private void Say(string text)
        {
            (_status, _statusUntil) = (text, Environment.TickCount64 + 5000);
            _dirty = true;
        }

        private void Scroll(int rows)
        {
            var last = Math.Max(0, _rows.Count - ViewRows);
            _top = Math.Clamp(_top + rows, 0, last);
            _follow = _top == last && last > 0;
            _dirty = true;
        }

        // ---------------------------------------------------------------- input

        private unsafe void ReadInput(nint input)
        {
            const int Size = 20; // INPUT_RECORD
            var records = stackalloc byte[Size * 32];
            uint count;
            if (GetNumberOfConsoleInputEvents(input, &count) == 0 || count == 0) return;
            if (ReadConsoleInputW(input, records, 32, &count) == 0) return;
            for (var i = 0; i < count; i++)
            {
                var r = records + (i * Size);
                switch (*(ushort*)r)
                {
                    case 1 when *(int*)(r + 4) != 0: // key down: repeat count, virtual key, character
                    {
                        // Pasted text arrives as a burst of keys; a digit in it is text, not a shortcut.
                        var alone = true;
                        for (var j = i + 1; j < count && alone; j++)
                        {
                            var next = records + (j * Size);
                            if (*(ushort*)next == 1 && *(int*)(next + 4) != 0 && *(ushort*)(next + 14) >= 32) alone = false;
                        }

                        for (var n = Math.Clamp((int)*(ushort*)(r + 8), 1, 50); n > 0 && !_exit; n--) Key(*(ushort*)(r + 10), (char)*(ushort*)(r + 14), alone && n == 1);
                        break;
                    }

                    case 2: // mouse: position, buttons, what happened
                        Mouse(*(short*)(r + 4), *(short*)(r + 6), *(uint*)(r + 8), *(uint*)(r + 16));
                        break;
                    case 0x10 when *(int*)(r + 4) == 0 && _hot >= 0: // the window lost the focus: nothing is hovered
                        _hot = -1;
                        _dirty = true;
                        break;
                }
            }
        }

        private void Mouse(int x, int y, uint buttons, uint what)
        {
            var item = y >= 0 && y < ViewRows && _top + y < _rows.Count ? _rows[_top + y].Item : -1;
            if (what == 4) // wheel; the turn is in the high word
            {
                Scroll((short)(buttons >> 16) > 0 ? -3 : 3);
                return;
            }

            if (item != _hot)
            {
                _hot = item;
                _dirty = true;
            }

            var left = (buttons & 1) != 0;
            if (left && !_leftDown)
            {
                if (item >= 0)
                {
                    RunItem(item);
                }
                else if (y == _height - 3 && _input.Length > 0) // the command line: put the caret where the click was
                {
                    _caret = Math.Min(_input.Length, _inputStart + Clip(_input.ToString(_inputStart, _input.Length - _inputStart), Math.Max(0, x - 3)).Length);
                    _dirty = true;
                }
            }

            _leftDown = left;
        }

        private void Key(ushort vk, char ch, bool alone)
        {
            _dirty = true;
            switch (vk)
            {
                case 0x0D:
                    Enter();
                    return;
                case 0x1B: // Esc: stop the command, else empty the line, else leave
                    if (_process != null) StopCommand();
                    else if (_input.Length > 0) (_caret, _) = (0, _input.Clear());
                    else _exit = true;
                    return;
                case 0x08:
                    if (_caret > 0) Remove(_caret - 1);
                    return;
                case 0x2E:
                    if (_caret < _input.Length) Remove(_caret);
                    return;
                case 0x25:
                    if (_caret > 0) _caret -= _caret > 1 && char.IsLowSurrogate(_input[_caret - 1]) ? 2 : 1;
                    return;
                case 0x27:
                    if (_caret < _input.Length) _caret += _caret + 1 < _input.Length && char.IsHighSurrogate(_input[_caret]) ? 2 : 1;
                    return;
                case 0x24:
                    _caret = 0;
                    return;
                case 0x23:
                    _caret = _input.Length;
                    return;
                case 0x26 or 0x28: // earlier / later commands
                    if (_history.Count == 0) return;
                    _historyAt = Math.Clamp(_historyAt + (vk == 0x26 ? -1 : 1), 0, _history.Count);
                    _input.Clear().Append(_historyAt < _history.Count ? _history[_historyAt] : "");
                    _caret = _input.Length;
                    return;
                case 0x21:
                    Scroll(-(ViewRows - 1));
                    return;
                case 0x22:
                    Scroll(ViewRows - 1);
                    return;
            }

            if (ch == 3) // Ctrl+C
            {
                if (_process != null) StopCommand();
                else _exit = true;
                return;
            }

            if (ch == 0x16) // Ctrl+V, when the terminal does not paste by itself
            {
                var text = Clean(Paste().Split('\n')[0]);
                _input.Insert(_caret, text);
                _caret += text.Length;
                return;
            }

            if (ch < 32 || ch == 127) return;
            if (alone && _input.Length == 0 && ch >= '1' && ch < '1' + _items.Count)
            {
                _hot = ch - '1'; // show which line the key meant
                RunItem(ch - '1');
                return;
            }

            _input.Insert(_caret++, ch);
        }

        private void Remove(int index)
        {
            // A character outside the basic plane is two chars: remove both.
            if (char.IsLowSurrogate(_input[index]) && index > 0) index--;
            var length = char.IsHighSurrogate(_input[index]) && index + 1 < _input.Length ? 2 : 1;
            _input.Remove(index, length);
            _caret = index;
        }

        private void RunItem(int index) => Say($"[{index + 1}] {_items[index].action()}");

        // ---------------------------------------------------------------- commands

        private void Enter()
        {
            var line = _input.ToString().Trim();
            if (line.Length == 0) return;
            if (_process != null)
            {
                Say(Lang.T("A command is still running. Esc stops it.", "명령이 아직 실행 중입니다. Esc로 중단할 수 있습니다."));
                return;
            }

            _input.Clear();
            _caret = 0;
            if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
            _historyAt = _history.Count;

            var words = CommandLine.Tokenize(line).Select(t => t.text).ToList();
            if (words.Count > 0 && (words[0].Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase) || words[0].Equals(AppInfo.Name + ".exe", StringComparison.OrdinalIgnoreCase))) words.RemoveAt(0);
            if (words.Count == 0) return;
            var name = words[0].ToLowerInvariant();
            if (name is "clear" or "cls" or "home")
            {
                _blocks = HomeBlocks();
                (_follow, _top, _layout) = (false, 0, true);
                return;
            }

            Append(new Block(""));
            Append(new Block("> " + line, 0, Sgr: "1"));
            switch (name)
            {
                case "exit" or "quit":
                    _exit = true;
                    return;
                case "dashboard":
                    OpenBrowser(_c.Url);
                    Append(new Block(Lang.T("opened in the browser: ", "브라우저에서 열었습니다: ") + _c.Url));
                    return;
                case "share" or "daemon" or "mcp":
                    Append(new Block(Lang.T($"`{name}` needs a window of its own: run it from a terminal.", $"`{name}`은(는) 별도의 창이 필요합니다. 터미널에서 실행하세요."), 0, Sgr: "33"));
                    return;
                case "stop":
                    // Ends the background process, so this screen has nothing left to show: leave when it is done,
                    // and make sure our own refresh does not start it again.
                    StopRefresh();
                    _exitWhenDone = true;
                    break;
            }

            try
            {
                var start = new ProcessStartInfo(AppInfo.ExePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var word in words) start.ArgumentList.Add(word);
                var process = Process.Start(start)!;
                process.StandardInput.Close();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) _output.Enqueue((e.Data, false, 0));
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) _output.Enqueue((e.Data, true, 0));
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                _ = Task.Run(() =>
                {
                    process.WaitForExit(); // returns after both streams have been read to their end
                    _output.Enqueue((null, false, process.ExitCode));
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _exitWhenDone = false;
                Append(new Block(Lang.T("could not start the command: ", "명령을 시작하지 못했습니다: ") + ex.Message, 0, Sgr: "33"));
            }
        }

        private void StopCommand()
        {
            var process = _process;
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill(true);
                Append(new Block(Lang.T("(stopped)", "(중단함)"), 0, Sgr: "90"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
            {
                // it ended by itself in the meantime
            }

            _exitWhenDone = false;
        }

        // ---------------------------------------------------------------- connected PCs

        /// <summary>Keeps the "connected now" line current, so a PC that has just been approved shows up by itself.</summary>
        private async Task RefreshAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(3000, _refreshStop.Token);
                    var facts = await FetchAsync();
                    if (facts == null || _refreshStop.IsCancellationRequested || facts.Targets == _c.Targets) continue;
                    _c.Targets = facts.Targets;
                    _layout = true;
                }
            }
            catch (OperationCanceledException)
            {
                // the screen is closing
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // the line simply stops updating
            }
        }

        private void StopRefresh()
        {
            _refreshStop.Cancel();
            try
            {
                _refreshTask?.Wait(3000);
            }
            catch (AggregateException)
            {
                // nothing to report: see RefreshAsync
            }
        }
    }

    // ---------------------------------------------------------------- text helpers

    /// <summary>Columns a character takes in a console: Korean and other wide characters take two.</summary>
    private static int Cells(char c) =>
        char.IsLowSurrogate(c) ? 0
        : char.IsHighSurrogate(c) ? 2
        : c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6)) ? 2
        : 1;

    private static int Cells(string text)
    {
        var n = 0;
        foreach (var c in text) n += Cells(c);
        return n;
    }

    /// <summary>The longest beginning of the text that fits into this many columns.</summary>
    private static string Clip(string text, int cells)
    {
        var used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            used += Cells(text[i]);
            if (used > cells) return text[..(i > 0 && char.IsHighSurrogate(text[i - 1]) ? i - 1 : i)];
        }

        return text;
    }

    /// <summary>
    /// Splits a line into rows of at most this many columns, at a space where there is one. Rows after the first
    /// are shorter by <paramref name="hang"/> columns, for a hanging indent.
    /// </summary>
    private static List<string> Wrap(string text, int cells, int hang = 0)
    {
        cells = Math.Max(8 + hang, cells);
        var rows = new List<string>();
        var rest = text;
        while (Cells(rest) > cells)
        {
            var cut = Math.Max(1, Clip(rest, cells).Length);
            if (rest[cut] == ' ')
            {
                rows.Add(rest[..cut]);
                rest = rest[(cut + 1)..];
                if (rows.Count == 1) cells -= hang;
                continue;
            }

            var space = rest.LastIndexOf(' ', cut - 1);
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

            if (rows.Count == 1) cells -= hang;
        }

        rows.Add(rest);
        return rows;
    }

    /// <summary>Output of a command, made safe to draw: no control characters, tabs as spaces.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\t') sb.Append("    ");
            else if (c >= 32 && c != 127) sb.Append(c);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- clipboard

    /// <summary>Puts text on the Windows clipboard and says how that went.</summary>
    private static unsafe string Copy(string text)
    {
        var done = false;
        for (var attempt = 0; attempt < 5 && !done; attempt++)
        {
            if (!OpenClipboard(0))
            {
                Thread.Sleep(40); // another program has it open for a moment
                continue;
            }

            try
            {
                EmptyClipboard();
                var memory = GlobalAlloc(0x0002, (nuint)((text.Length + 1) * 2)); // GMEM_MOVEABLE
                if (memory == 0) break;
                var target = (char*)GlobalLock(memory);
                if (target == null)
                {
                    GlobalFree(memory);
                    break;
                }

                text.AsSpan().CopyTo(new Span<char>(target, text.Length));
                target[text.Length] = '\0';
                GlobalUnlock(memory);
                done = SetClipboardData(13, memory) != 0; // CF_UNICODETEXT; the clipboard owns the memory from here
                if (!done) GlobalFree(memory);
            }
            finally
            {
                CloseClipboard();
            }
        }

        return done
            ? Lang.T("copied to the clipboard", "클립보드에 복사했습니다")
            : Lang.T("could not open the clipboard; nothing was copied", "클립보드를 열 수 없어 복사하지 못했습니다");
    }

    private static unsafe string Paste()
    {
        if (!OpenClipboard(0)) return "";
        try
        {
            var memory = GetClipboardData(13);
            if (memory == 0) return "";
            var text = (char*)GlobalLock(memory);
            if (text == null) return "";
            try
            {
                return new string(text);
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetNumberOfConsoleInputEvents(nint handle, uint* count);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int ReadConsoleInputW(nint handle, byte* records, uint capacity, uint* read);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
