using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

internal sealed record WindowInfo(nint Hwnd, int Pid, string Title, string Class, bool Visible, bool Hung, bool Minimized, RECT Rect);

/// <summary>Seeing and driving the desktop: windows, screenshots, mouse and keyboard.</summary>
internal static partial class UiTools
{
    [ThreadStatic]
    private static List<nint>? _collect;

    // Where the last screenshot sits on the virtual desktop, so click coordinates can be given in screenshot pixels.
    private static int _shotX, _shotY;
    private static double _shotScale = 1;
    private static DateTime _lastThumbNotice;

    public static IEnumerable<Tool> Tools =>
    [
        new("windows", "Desktop", "Top-level windows: title, process, position, and whether they are hung",
            """
            windows [filter] [--all] [--class]
              Visible titled windows by default (--all includes hidden/untitled). "NOT RESPONDING" marks a hung UI thread.
              "popup" marks an open menu or drop-down (untitled, shown as [its class]): the cheap way to ask "is the
              menu up?". "console" marks a console window, whose title is often an exe path that matches the app's
              name. --class adds each window's class.
              "SYSTEM PROMPT" marks a dialog of Windows itself (firewall, UAC, credentials, SmartScreen, a packaged
              app's file picker) that the person at the PC has to answer; those are listed whatever the filter.
              Much cheaper than a screenshot for "did the dialog appear?" / "is the app frozen?".
            """, WindowsAsync) { Quiet = true },
        new("screenshot", "Desktop", "Capture the screen (or one display/window) to a PNG on the developer PC",
            """
            screenshot [-o file.png] [--display N] [--window TITLE|HWND] [--scale 0.5]
              Saved on the developer PC; open the file to look at it. --window crops to one window and --scale shrinks
              the image: both make it much cheaper to read. click/drag coordinates refer to the latest screenshot.
            """, ScreenshotAsync),
        new("click", "Desktop", "Click at a position of the latest screenshot",
            """
            click <x> <y> [--button left|right|middle] [--double] [--hover] [--screen]
            click @<x>,<y> ...               screen coordinates, exactly as `ui` prints them
              x,y are pixels in your most recent screenshot (crop and scale are undone for you). A point that
              falls outside the desktop is refused, not clicked. By name instead: ui click / ui rclick / ui dblclick.
              --hover only moves the pointer there (tooltips, hover states, menus that open on hover).
              --screen means raw desktop coordinates instead. Windows of elevated apps ignore input unless
              agentprobe itself runs elevated on the target.
            """, ClickAsync),
        new("drag", "Desktop", "Drag from one position to another", "drag <x1> <y1> <x2> <y2> [--screen]", DragAsync),
        new("scroll", "Desktop", "Turn the mouse wheel", "scroll <notches> [x y]     positive = up, negative = down", ScrollAsync),
        new("type", "Desktop", "Type text into the focused window",
            "type <text>     Types exactly what you give it (backslashes included). For Enter or Tab use: key enter / key tab", TypeAsync),
        new("key", "Desktop", "Press a key or shortcut",
            "key <combo> [more combos...]     e.g. key ctrl+s   key alt+f4   key win+r   key enter   key ctrl+shift+esc", KeyAsync),
        new("focus", "Desktop", "Bring a window to the foreground", "focus <TITLE|HWND>", FocusAsync),
    ];

    public static unsafe List<WindowInfo> EnumerateWindows()
    {
        _collect = [];
        Win32.EnumWindows(&EnumProc, 0);
        var handles = _collect;
        _collect = null;
        var buf = stackalloc char[512];
        var result = new List<WindowInfo>(handles.Count);
        foreach (var hwnd in handles)
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            var len = Win32.GetWindowText(hwnd, buf, 512);
            var title = new string(buf, 0, Math.Max(0, len));
            len = Win32.GetClassName(hwnd, buf, 256);
            var cls = new string(buf, 0, Math.Max(0, len));
            Win32.GetWindowRect(hwnd, out var rect);
            result.Add(new WindowInfo(hwnd, pid, title, cls, Win32.IsWindowVisible(hwnd), Win32.IsHungAppWindow(hwnd), Win32.IsIconic(hwnd), rect));
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumProc(nint hwnd, nint lParam)
    {
        _collect?.Add(hwnd);
        return 1;
    }

    private static WindowInfo? FindWindow(string spec)
    {
        var all = EnumerateWindows();
        if (spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(spec[2..], System.Globalization.NumberStyles.HexNumber, null, out var h))
            return all.FirstOrDefault(w => w.Hwnd == (nint)h);
        var visible = all.Where(w => w.Visible && w.Title.Length > 0).ToList();
        return visible.FirstOrDefault(w => w.Title.Equals(spec, StringComparison.OrdinalIgnoreCase))
               ?? visible.FirstOrDefault(w => w.Title.Contains(spec, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<int> WindowsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all", "class");
        var names = ProcessSnapshot.Take().ToDictionary(p => p.Pid, p => p.Name);
        var foreground = Win32.GetForegroundWindow();
        var withClass = a.Flag("class");
        var t = withClass ? new Table("HWND", ">PID", "PROCESS", "POSITION", "STATE", "CLASS", "TITLE") : new Table("HWND", ">PID", "PROCESS", "POSITION", "STATE", "TITLE");
        void Row(string hwnd, int pid, string process, string position, string state, string cls, string title)
        {
            if (withClass) t.Add(hwnd, pid, process, position, state, Format.Clip(cls, 40), title);
            else t.Add(hwnd, pid, process, position, state, title);
        }

        var windows = EnumerateWindows();

        // Prompts Windows shows in windows of its own (firewall, UAC, credentials) are listed whatever the filter:
        // their top-level windows are often untitled or empty, and they are exactly what an agent must not miss.
        var prompts = SystemPrompts.Find(windows, names);
        var promptPids = prompts.Select(p => p.Pid).ToHashSet();
        foreach (var p in prompts)
        {
            var where = p.Rect.Width > 1 ? $"{p.Rect.Left},{p.Rect.Top} {p.Rect.Width}x{p.Rect.Height}" : "";
            Row(p.Hwnd != 0 ? "0x" + ((long)p.Hwnd).ToString("X") : "-", p.Pid, p.Process, where, "SYSTEM PROMPT", "", Format.Clip(p.Title, 90));
        }

        foreach (var w in windows)
        {
            if (w.Pid == Environment.ProcessId) continue; // our own "remote control active" banner
            if (promptPids.Contains(w.Pid) && !a.Flag("all")) continue; // shown above as one line
            var popup = IsPopup(w);
            if (!a.Flag("all") && !popup && (!w.Visible || w.Title.Length == 0 || (w.Rect.Width <= 1 && !w.Minimized))) continue;
            var process = names.GetValueOrDefault(w.Pid, "?");
            if (a.Positionals.Count > 0 && !w.Title.Contains(a.Positionals[0], StringComparison.OrdinalIgnoreCase) && !process.Contains(a.Positionals[0], StringComparison.OrdinalIgnoreCase)) continue;
            var state = w.Hung ? "NOT RESPONDING" : !w.Visible ? "hidden" : w.Minimized ? "minimized" : popup ? "popup" : w.Hwnd == foreground ? "foreground" : "";
            if (w.Class is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "PseudoConsoleWindow") state = (state + " console").TrimStart();
            var pos = w.Minimized ? "" : $"{w.Rect.Left},{w.Rect.Top} {w.Rect.Width}x{w.Rect.Height}";
            Row("0x" + ((long)w.Hwnd).ToString("X"), w.Pid, process, pos, state, w.Class, Format.Clip(w.Title.Length > 0 ? w.Title : $"[{w.Class}]", 90));
        }

        ctx.Out.Write(t.Count > 0 ? t.ToString() : "no matching windows\n");
        if (prompts.Count > 0) ctx.Out.WriteLine("SYSTEM PROMPT = a dialog of Windows itself (see its title) is waiting for the person at this PC; whatever caused it waits until they answer.");
        if (SystemPrompts.SecureDesktopShowing())
            ctx.Out.WriteLine("The secure desktop is showing (a UAC prompt, the lock screen or Ctrl+Alt+Del): the windows above cannot be seen or clicked until the person there answers.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// An open menu, context menu or drop-down: a visible untitled window that belongs to another window (or is a
    /// Win32 menu). Shadows, tooltips and input-method helpers are owned and untitled too, and are left out.
    /// </summary>
    private static bool IsPopup(WindowInfo w)
    {
        if (!w.Visible || w.Minimized || w.Title.Length > 0 || w.Rect.Width < 16 || w.Rect.Height < 8) return false;
        if (w.Class == "#32768") return true; // a Win32 menu
        if (w.Class is "SysShadow" or "tooltips_class32" or "IME" or "MSCTFIME UI" or "Default IME" or "WorkerW" || w.Class.Contains("Shadow", StringComparison.OrdinalIgnoreCase)) return false;
        return Win32.GetWindow(w.Hwnd, 4) != 0; // GW_OWNER
    }

    /// <summary>
    /// A click is refused when it would land outside the desktop: that happens when screen coordinates (what `ui`
    /// prints as @x,y) are given as if they were pixels of a scaled or cropped screenshot, and it would press
    /// whatever sits at the edge of the screen instead.
    /// </summary>
    private static string? OutsideDesktop(int x, int y, bool raw)
    {
        var (vx, vy, vw, vh) = (Win32.GetSystemMetrics(76), Win32.GetSystemMetrics(77), Win32.GetSystemMetrics(78), Win32.GetSystemMetrics(79));
        if (x >= vx && y >= vy && x < vx + vw && y < vy + vh) return null;
        return $"{x},{y} is outside the desktop ({vx},{vy} {vw}x{vh}); nothing was done. " + (raw
            ? "Screen coordinates were given: check them against `windows` or `ui`."
            : $"Plain coordinates are pixels of your latest screenshot (scale {_shotScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}, taken at {_shotX},{_shotY}). " +
              "The @x,y that `ui` prints are screen coordinates: give them as @x,y (click @1876,1006) or add --screen.");
    }

    /// <summary>Turns "@x,y" (the form `ui` prints: screen coordinates) into two numbers. True when any was given.</summary>
    private static bool ExpandAt(ArgParser a)
    {
        var any = false;
        for (var i = a.Positionals.Count - 1; i >= 0; i--)
        {
            var p = a.Positionals[i];
            if (p.Length < 4 || p[0] != '@') continue;
            var parts = p[1..].Split(',');
            if (parts.Length != 2) continue;
            a.Positionals.RemoveAt(i);
            a.Positionals.InsertRange(i, parts);
            any = true;
        }

        return any;
    }

    // ------------------------------------------------------------------ screenshot

    private static unsafe List<(RECT rect, bool primary)> Monitors()
    {
        var list = new List<(RECT, bool)>();
        var handle = GCHandle.Alloc(list);
        Win32.EnumDisplayMonitors(0, 0, &MonitorProc, GCHandle.ToIntPtr(handle));
        handle.Free();
        return list.OrderByDescending(m => m.Item2).ThenBy(m => m.Item1.Left).ToList();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int MonitorProc(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var list = (List<(RECT, bool)>)GCHandle.FromIntPtr(data).Target!;
        var info = default(MONITORINFOEX);
        info.cbSize = (uint)sizeof(MONITORINFOEX);
        var primary = Win32.GetMonitorInfo(monitor, &info) && (info.dwFlags & 1) != 0;
        list.Add((*rect, primary));
        return 1;
    }

    /// <summary>Copies a desktop rectangle into top-down BGRA pixels, shrinking it when asked. Null when the desktop is not capturable.</summary>
    private static unsafe byte[]? Capture(RECT area, int width, int height, bool shrink)
    {
        var pixels = new byte[width * height * 4];
        var screen = Win32.GetDC(0);
        var mem = Win32.CreateCompatibleDC(screen);
        var bitmap = Win32.CreateCompatibleBitmap(screen, width, height);
        var old = Win32.SelectObject(mem, bitmap);
        bool ok;
        if (shrink)
        {
            SetStretchBltMode(mem, 4); // HALFTONE
            ok = StretchBlt(mem, 0, 0, width, height, screen, area.Left, area.Top, area.Width, area.Height, 0x00CC0020 | 0x40000000) != 0;
        }
        else
        {
            ok = Win32.BitBlt(mem, 0, 0, width, height, screen, area.Left, area.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        }

        Win32.SelectObject(mem, old);
        var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        fixed (byte* p = pixels) ok &= Win32.GetDIBits(mem, bitmap, 0, (uint)height, p, &header, 0) != 0;
        Win32.DeleteObject(bitmap);
        Win32.DeleteDC(mem);
        Win32.ReleaseDC(0, screen);
        return ok ? pixels : null;
    }

    private static async Task<int> ScreenshotAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var monitors = Monitors();
        RECT area;
        string what;
        if (a.Get("window") is { } spec)
        {
            var w = FindWindow(spec);
            if (w == null) return ctx.Fail($"screenshot: no window matches '{spec}' (see `windows`).");
            if (w.Minimized) return ctx.Fail($"screenshot: \"{w.Title}\" is minimized. `focus` it first.");
            area = w.Rect;
            what = $"window \"{Format.Clip(w.Title, 50)}\"";
        }
        else if (a.Has("display"))
        {
            var n = a.GetInt("display", 1);
            if (n < 1 || n > monitors.Count) return ctx.Fail($"screenshot: display {n} does not exist ({monitors.Count} display(s)).");
            area = monitors[n - 1].rect;
            what = $"display {n}";
        }
        else
        {
            area = new RECT
            {
                Left = Win32.GetSystemMetrics(76),
                Top = Win32.GetSystemMetrics(77),
            };
            area.Right = area.Left + Win32.GetSystemMetrics(78);
            area.Bottom = area.Top + Win32.GetSystemMetrics(79);
            what = monitors.Count > 1 ? $"all {monitors.Count} displays" : "the screen";
        }

        if (area.Width <= 0 || area.Height <= 0) return ctx.Fail("screenshot: nothing to capture (no interactive desktop? locked screen?).");

        // --thumb W: a small preview for the dashboard. It must not disturb an agent's work in progress.
        var thumb = a.Has("thumb");
        var scale = thumb ? Math.Clamp(a.GetInt("thumb", 480) / (double)area.Width, 0.05, 1.0) : Math.Clamp(a.GetDouble("scale", 1.0), 0.1, 1.0);
        var width = Math.Max(1, (int)(area.Width * scale));
        var height = Math.Max(1, (int)(area.Height * scale));

        var pixels = Capture(area, width, height, scale < 1);
        if (pixels == null) return ctx.Fail("screenshot: capture failed (secure desktop such as UAC or the lock screen is showing?).");

        var file = Path.Combine(Path.GetTempPath(), $"{AppInfo.Name}-shot-{Guid.NewGuid():N}.png");
        await using (var fs = File.Create(file)) PngWriter.Write(fs, pixels, width, height);
        if (thumb)
        {
            // Viewing the screen is logged, but once per stretch of watching, not once per frame.
            if (DateTime.UtcNow - _lastThumbNotice > TimeSpan.FromMinutes(10)) ctx.Runtime.Audit.Write("screen is being viewed from the developer PC's dashboard");
            _lastThumbNotice = DateTime.UtcNow;
        }
        else
        {
            (_shotX, _shotY, _shotScale) = (area.Left, area.Top, scale); // click coordinates follow the agent's own screenshots only
            ctx.Runtime.Audit.Write($"screenshot of {what}");
            ctx.Out.WriteLine($"{width}x{height} PNG of {what}" + (scale < 1 ? $" at {scale:0.##}x" : "") + "; click/drag take coordinates in this image");
        }

        await ctx.SendFile($"screenshot-{DateTime.Now:HHmmss}.png", file, false, true);
        return 0;
    }

    // ------------------------------------------------------------------ input

    private static (int x, int y) ToScreen(double x, double y, bool raw)
        => raw ? ((int)x, (int)y) : (_shotX + (int)Math.Round(x / _shotScale), _shotY + (int)Math.Round(y / _shotScale));

    internal static unsafe void Mouse(int x, int y, uint flags, uint data = 0)
    {
        var vx = Win32.GetSystemMetrics(76);
        var vy = Win32.GetSystemMetrics(77);
        var vw = Math.Max(2, Win32.GetSystemMetrics(78));
        var vh = Math.Max(2, Win32.GetSystemMetrics(79));
        var input = default(INPUT);
        input.type = 0;
        input.mi.dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        input.mi.dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        input.mi.mouseData = data;
        input.mi.dwFlags = flags | 0x8000 | 0x4000; // ABSOLUTE | VIRTUALDESK
        Win32.SendInput(1, &input, sizeof(INPUT));
    }

    private static bool Coordinates(ToolContext ctx, ArgParser a, int count, out double[] values)
    {
        values = new double[count];
        if (a.Positionals.Count < count)
        {
            ctx.Fail($"expected {count} coordinates.");
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!double.TryParse(a.Positionals[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i]))
            {
                ctx.Fail($"'{a.Positionals[i]}' is not a number.");
                return false;
            }
        }

        return true;
    }

    private static async Task<int> ClickAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "double", "screen", "hover");
        var raw = ExpandAt(a) | a.Flag("screen");
        if (!Coordinates(ctx, a, 2, out var v)) return 2;
        var (x, y) = ToScreen(v[0], v[1], raw);
        if (OutsideDesktop(x, y, raw) is { } outside) return ctx.Fail("click: " + outside);
        if (a.Flag("hover"))
        {
            // Two steps, so the window under the pointer sees a real movement and not only a jump.
            Mouse(x - 2, y - 2, 0x0001);
            await Task.Delay(40, ctx.Ct);
            Mouse(x, y, 0x0001);
            ctx.Runtime.Audit.Write($"pointer moved to {x},{y}");
            ctx.Out.WriteLine($"pointer at screen {x},{y}");
            return 0;
        }

        var (down, up) = (a.Get("button") ?? "left").ToLowerInvariant() switch
        {
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u),
        };
        Mouse(x, y, 0x0001); // move
        await Task.Delay(40, ctx.Ct);
        for (var i = 0; i < (a.Flag("double") ? 2 : 1); i++)
        {
            Mouse(x, y, down);
            Mouse(x, y, up);
            await Task.Delay(60, ctx.Ct);
        }

        ctx.Runtime.Audit.Write($"click at {x},{y}");
        ctx.Out.WriteLine($"clicked at screen {x},{y}");
        return 0;
    }

    private static async Task<int> DragAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "screen");
        var raw = ExpandAt(a) | a.Flag("screen"); // both ends in the same kind of coordinates
        if (!Coordinates(ctx, a, 4, out var v)) return 2;
        var (x1, y1) = ToScreen(v[0], v[1], raw);
        var (x2, y2) = ToScreen(v[2], v[3], raw);
        if ((OutsideDesktop(x1, y1, raw) ?? OutsideDesktop(x2, y2, raw)) is { } outside) return ctx.Fail("drag: " + outside);
        Mouse(x1, y1, 0x0001);
        await Task.Delay(50, ctx.Ct);
        Mouse(x1, y1, 0x0002);
        var (x, y) = (x1, y1);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                (x, y) = (x1 + ((x2 - x1) * i / 12), y1 + ((y2 - y1) * i / 12));
                Mouse(x, y, 0x0001);
                await Task.Delay(15, ctx.Ct);
            }
        }
        finally
        {
            Mouse(x, y, 0x0004); // the button is released even if the command is cancelled half way
        }

        ctx.Runtime.Audit.Write($"drag {x1},{y1} -> {x2},{y2}");
        ctx.Out.WriteLine($"dragged {x1},{y1} -> {x2},{y2}");
        return 0;
    }

    private static unsafe Task<int> ScrollAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "screen");
        var raw = ExpandAt(a) | a.Flag("screen");
        if (a.Positionals.Count == 0 || !int.TryParse(a.Positionals[0], out var notches)) return Task.FromResult(ctx.Fail("scroll: how many notches? (negative = down)"));
        if (a.Positionals.Count >= 3 && double.TryParse(a.Positionals[1], out var px) && double.TryParse(a.Positionals[2], out var py))
        {
            var (x, y) = ToScreen(px, py, raw);
            if (OutsideDesktop(x, y, raw) is { } outside) return Task.FromResult(ctx.Fail("scroll: " + outside));
            Mouse(x, y, 0x0001);
        }

        var input = default(INPUT);
        input.type = 0;
        input.mi.mouseData = unchecked((uint)(notches * 120));
        input.mi.dwFlags = 0x0800; // WHEEL
        Win32.SendInput(1, &input, sizeof(INPUT));
        ctx.Out.WriteLine($"scrolled {notches}");
        return Task.FromResult(0);
    }

    private static unsafe void Key(ushort vk, ushort scan, uint flags)
    {
        var input = default(INPUT);
        input.type = 1;
        input.ki.wVk = vk;
        input.ki.wScan = scan;
        input.ki.dwFlags = flags;
        Win32.SendInput(1, &input, sizeof(INPUT));
    }

    private static async Task<int> TypeAsync(ToolContext ctx, string[] args)
    {
        // Literal text only: a path like C:\temp\new.txt must arrive as typed, not as "C:<Tab>emp<Enter>ew.txt".
        var text = string.Join(' ', args);
        if (text.Length == 0) return ctx.Fail("type: no text.");
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                Key(0x0D, 0, 0);
                Key(0x0D, 0, 2);
            }
            else if (ch == '\t')
            {
                Key(0x09, 0, 0);
                Key(0x09, 0, 2);
            }
            else if (ch != '\r')
            {
                Key(0, ch, 4); // KEYEVENTF_UNICODE
                Key(0, ch, 4 | 2);
            }

            await Task.Delay(4, ctx.Ct);
        }

        ctx.Runtime.Audit.Write($"type {text.Length} characters");
        ctx.Out.WriteLine($"typed {text.Length} characters");
        return 0;
    }

    private static readonly Dictionary<string, ushort> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["apps"] = 0x5D, ["menu"] = 0x5D, ["capslock"] = 0x14,
    };

    private static async Task<int> KeyAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return ctx.Fail("key: which key? e.g. key ctrl+s");
        var extended = new HashSet<ushort> { 0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x5B, 0x5D };

        // Resolve everything first, so a typo in the third combo does not leave the first two half done.
        var combos = new List<List<ushort>>();
        foreach (var combo in args)
        {
            var keys = new List<ushort>();
            foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (KeyNames.TryGetValue(part, out var vk)) keys.Add(vk);
                else if (part.Length > 1 && (part[0] is 'f' or 'F') && int.TryParse(part[1..], out var fn) && fn is >= 1 and <= 24) keys.Add((ushort)(0x70 + fn - 1));
                else if (part.Length == 1)
                {
                    var scan = Win32.VkKeyScanW(part[0]);
                    if (scan == -1) return ctx.Fail($"key: cannot map '{part}'.");
                    keys.Add((ushort)(scan & 0xFF));
                }
                else return ctx.Fail($"key: unknown key '{part}'.");
            }

            if (keys.Count == 0) return ctx.Fail($"key: '{combo}' names no key.");
            combos.Add(keys);
        }

        foreach (var keys in combos)
        {
            var down = new List<ushort>();
            try
            {
                foreach (var k in keys)
                {
                    Key(k, 0, extended.Contains(k) ? 1u : 0u);
                    down.Add(k);
                }

                await Task.Delay(30, ctx.Ct);
            }
            finally
            {
                // Released even when the command is cancelled: a modifier left "held" would corrupt everything typed next.
                for (var i = down.Count - 1; i >= 0; i--) Key(down[i], 0, 2 | (extended.Contains(down[i]) ? 1u : 0u));
            }

            await Task.Delay(60, ctx.Ct);
        }

        ctx.Runtime.Audit.Write("key " + string.Join(' ', args));
        ctx.Out.WriteLine("pressed " + string.Join(' ', args));
        return 0;
    }

    private static async Task<int> FocusAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return ctx.Fail("focus: which window? (title or HWND from `windows`)");
        var w = FindWindow(string.Join(' ', args));
        if (w == null) return ctx.Fail($"focus: no window matches '{string.Join(' ', args)}'.");
        if (w.Minimized) Win32.ShowWindow(w.Hwnd, 9); // SW_RESTORE

        // Windows only lets the foreground process change the foreground window; a synthetic Alt tap lifts that lock.
        Key(0x12, 0, 0);
        Key(0x12, 0, 2);
        var ok = Win32.SetForegroundWindow(w.Hwnd);
        await Task.Delay(150, ctx.Ct);
        ctx.Out.WriteLine(ok || Win32.GetForegroundWindow() == w.Hwnd ? $"focused \"{Format.Clip(w.Title, 60)}\"" : $"asked Windows to focus \"{Format.Clip(w.Title, 60)}\" but it refused (elevated window?)");
        return 0;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint hdc, int mode);
}
