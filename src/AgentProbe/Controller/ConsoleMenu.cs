using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Controller;

/// <summary>One line of a <see cref="ConsoleMenu"/>: what it does, and a few grey words about it.</summary>
internal sealed record MenuOption(string Label, string Detail = "");

/// <summary>
/// A short question answered with a click. Each option is one line that works as a button: it highlights under the
/// mouse, and a click, its number key, or the arrow keys with Enter pick it. Like the start screen it is redrawn
/// from its lines on every change, on a separate screen that goes away with the answer.
/// </summary>
internal static partial class ConsoleMenu
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int FlushConsoleInputBuffer(nint handle);

    /// <param name="selected">The option that is highlighted at first and that Enter picks, or -1 for none.</param>
    /// <returns>The index picked, -1 for Esc, or null when this console cannot show a menu (ask in plain text then).</returns>
    public static int? Choose(string title, string question, IReadOnlyList<MenuOption> options, int selected, CancellationToken ct = default)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return null;
        var input = Home.GetStdHandle(-10);
        var output = Home.GetStdHandle(-11);
        if (!Home.GetConsoleMode(input, out var inputMode) || !Home.GetConsoleMode(output, out var outputMode)) return null;
        if (!Home.SetConsoleMode(output, outputMode | 0x0001u | 0x0004u)) return null; // escape sequences
        if (!Home.SetConsoleMode(input, 0x0080u | 0x0010u | 0x0008u))                   // mouse and resize events; no QuickEdit, no line editing
        {
            Home.SetConsoleMode(output, outputMode);
            return null;
        }

        // Keys pressed before the question was on the screen are not an answer to it.
        FlushConsoleInputBuffer(input);
        var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096);
        writer.Write("\u001b[?1049h\u001b[?7l\u001b[?25l");
        try
        {
            return new Menu(title, question, options, selected, writer).Run(input, ct);
        }
        finally
        {
            writer.Write("\u001b[0m\u001b[?25h\u001b[?7h\u001b[?1049l");
            writer.Flush();
            Home.SetConsoleMode(input, inputMode);
            Home.SetConsoleMode(output, outputMode);
        }
    }

    private sealed class Menu(string title, string question, IReadOnlyList<MenuOption> options, int selected, StreamWriter writer)
    {
        private int _selected = Math.Clamp(selected, -1, options.Count - 1);
        private int _pressed = -1;   // the option the left button went down on
        private int _firstRow;       // window row of the first option
        private int _width, _height;
        private int? _result;

        // Nobody answers a question in the instant it appears: what arrives then was typed or clicked for something else.
        private readonly long _armedAt = Environment.TickCount64 + 400;

        public int Run(nint input, CancellationToken ct)
        {
            var dirty = true;
            while (_result == null && !ct.IsCancellationRequested)
            {
                var (width, height) = (Console.WindowWidth, Console.WindowHeight);
                if (width != _width || height != _height)
                {
                    (_width, _height) = (width, height);
                    dirty = true;
                }

                if (dirty) Render();
                dirty = Win32.WaitForSingleObject(input, 50) == 0 && ReadInput(input);
            }

            return _result ?? -1;
        }

        private void Render()
        {
            var sb = new StringBuilder(1024);
            var row = 1;
            void Line(string text)
            {
                if (row <= _height) sb.Append("\u001b[").Append(row).Append(";1H\u001b[0m").Append(text).Append("\u001b[0m\u001b[K");
                row++;
            }

            var room = Math.Max(8, _width - 2);
            Line("");
            Line(" \u001b[1m" + Home.Clip(title, room));
            Line("");
            Line(" " + Home.Clip(question, room));
            Line("");
            _firstRow = row - 1;
            var labelCells = options.Max(o => Home.Cells(o.Label));
            for (var i = 0; i < options.Count; i++)
            {
                var o = options[i];
                var label = Home.Clip(o.Label, Math.Max(4, room - 9));
                var pad = new string(' ', Math.Max(0, labelCells - Home.Cells(label)));
                var button = i == _selected
                    ? $"\u001b[7m [{i + 1}] {label}{pad} \u001b[27m"
                    : $" \u001b[36m[{i + 1}]\u001b[39m {label}{pad} ";
                var left = room - 9 - Home.Cells(label) - pad.Length - 2;
                Line("   " + button + (o.Detail.Length > 0 && left > 4 ? "  \u001b[90m" + Home.Clip(o.Detail, left) : ""));
            }

            Line("");
            Line(" \u001b[90m" + Home.Clip(Lang.T("Click one, or press its number   |   Esc: close", "클릭하거나 숫자 키를 누르세요   |   Esc: 닫기"), room));
            sb.Append("\u001b[").Append(Math.Min(row, _height)).Append(";1H\u001b[0m\u001b[J");
            writer.Write(sb);
            writer.Flush();
        }

        /// <summary>Handles what arrived; true when the picture has to be drawn again.</summary>
        private unsafe bool ReadInput(nint input)
        {
            const int Size = 20; // INPUT_RECORD
            var records = stackalloc byte[Size * 32];
            uint count;
            if (Home.GetNumberOfConsoleInputEvents(input, &count) == 0 || count == 0) return false;
            if (Home.ReadConsoleInputW(input, records, 32, &count) == 0) return false;
            if (Environment.TickCount64 < _armedAt) return false;
            var before = _selected;
            for (var i = 0; i < count && _result == null; i++)
            {
                var r = records + (i * Size);
                switch (*(ushort*)r)
                {
                    case 1 when *(int*)(r + 4) != 0: // key down: virtual key, character
                        Key(*(ushort*)(r + 10), (char)*(ushort*)(r + 14));
                        break;
                    case 2 when *(uint*)(r + 16) != 4: // mouse (not the wheel): row, buttons
                        Mouse(*(short*)(r + 6), (*(uint*)(r + 8) & 1) != 0);
                        break;
                }
            }

            return _selected != before;
        }

        private void Key(ushort vk, char ch)
        {
            switch (vk)
            {
                case 0x0D when _selected >= 0:
                    _result = _selected;
                    return;
                case 0x1B:
                    _result = -1;
                    return;
                case 0x26:
                    _selected = _selected <= 0 ? options.Count - 1 : _selected - 1;
                    return;
                case 0x28:
                    _selected = _selected >= options.Count - 1 ? 0 : _selected + 1;
                    return;
            }

            if (ch == 3) _result = -1; // Ctrl+C
            else if (ch >= '1' && ch < '1' + Math.Min(9, options.Count)) _result = ch - '1';
        }

        private void Mouse(int y, bool left)
        {
            var over = y >= _firstRow && y < _firstRow + options.Count ? y - _firstRow : -1;
            if (over >= 0) _selected = over;

            // The pick happens when the button comes up again, so the screen that follows never sees it held down.
            if (left && _pressed == -1) _pressed = over >= 0 ? over : -2;
            else if (!left)
            {
                if (_pressed >= 0 && _pressed == over) _result = over;
                _pressed = -1;
            }
        }
    }
}
