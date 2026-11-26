using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <param name="Disabled">The window takes no mouse or keyboard input (Windows disables the owner of an open modal dialog).</param>
/// <param name="CaptureAffinity">
/// What the window asked of screen captures (SetWindowDisplayAffinity): 0 nothing, 1 show it black (WDA_MONITOR),
/// 0x11 leave it out (WDA_EXCLUDEFROMCAPTURE). The person at the PC sees such a window; a screenshot does not.
/// </param>
internal sealed record WindowInfo(nint Hwnd, int Pid, string Title, string Class, bool Visible, bool Hung, bool Minimized, RECT Rect, bool Disabled = false, uint CaptureAffinity = 0);

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
            windows --hwnd <TITLE>           only the handle of that window (0x...), for a script that needs one
              Visible titled windows by default (--all includes hidden/untitled). "NOT RESPONDING" marks a hung UI thread.
              "popup" marks an open menu or drop-down (untitled, shown as [its class]): the cheap way to ask "is the
              menu up?". "console" marks a console window, whose title is often an exe path that matches the app's
              name. --class adds each window's class.
              "covered" marks a window that is mostly behind other windows: a screenshot of the screen, or a click
              there, gets what is in front (`focus` brings it forward; `screenshot --window` shows it anyway).
              "no-capture" marks a window that keeps itself out of screen captures (a player of protected video,
              a password manager, a meeting app): the person at the PC sees it, a `screenshot` shows black or
              what is behind it there. `ui` still reads its controls.
              "SYSTEM PROMPT" marks a dialog of Windows itself (firewall, UAC, credentials, SmartScreen, a packaged
              app's file picker) that the person at the PC has to answer; those are listed whatever the filter.
              Much cheaper than a screenshot for "did the dialog appear?" / "is the app frozen?".
            """, WindowsAsync) { Quiet = true },
        new("screenshot", "Desktop", "Capture the screen (or one display/window) to a PNG on the developer PC",
            """
            screenshot [-o file.png] [--display N] [--window TITLE|HWND [--raise]] [--scale 0.5]
                       [--control NAME|#ID [--nth N]] [--region X,Y,W,H] [--diff] [--stats]
              Saved on the developer PC, in its temp folder unless -o says where (the path is printed); open the
              file to look at it. click/drag coordinates refer to the latest screenshot.
              What an image costs to read depends on how many pixels it has, not on its file size: ask for the
              part you need. --window gives one window, --scale shrinks it, and:
              --control NAME  only that control (found by name or #id as `ui click` finds it, in --window or
                              the foreground window), with a small margin
              --region X,Y,W,H  only that rectangle of the screen (screen coordinates, as `windows` and
                              `ui --pos` give them)
              --diff          only the part that changed since the previous screenshot of the same area and
                              scale. Nothing changed: one line of text and no image at all.
              --stats         no image: the average color, how much is near black or near white and how much
                              the most common color covers. Answers "is it black?", "is anything drawn?"; with
                              --diff also how many pixels changed ("is the video moving?").
              Without --window the picture is the screen as the person at the PC sees it: whatever lies in front
              is in it, agentprobe's own window included. --window asks that window to draw ITSELF, together with
              the dialogs it owns, so it comes out whole even when other windows cover it; the answer says when
              it is covered on the screen (a click there would go to what is in front). --raise brings it to the
              front first. A window that cannot be drawn this way (it draws nothing when asked, it does not
              respond, or Windows scales it for the display) is cut out of the screen instead, and the answer
              says so and why.
            """, ScreenshotAsync),
        new("click", "Desktop", "Click at a position of the latest screenshot",
            """
            click <x> <y> [--button left|right|middle] [--double] [--hover] [--screen] [--window TITLE [--raise]]
            click @<x>,<y> ...               screen coordinates, exactly as `ui` prints them
              x,y are pixels in your most recent screenshot (crop and scale are undone for you). A point that
              falls outside the desktop is refused, not clicked. By name instead: ui click / ui rclick / ui dblclick.
              The answer names the window that got the click: another window may lie in front of the one you
              mean. --window TITLE makes that a condition: when something else is in front at that point,
              nothing is clicked and the answer says what it is; --raise brings TITLE to the front first.
              A point on agentprobe's own window on the target is never clicked.
              --hover only moves the pointer there (tooltips, hover states, menus that open on hover).
              The answer also says what changed on the screen in the moment after the click (a window that
              opened, with its text; one that closed; controls of that window that read differently), so no
              `windows`, `ui` or screenshot is needed to see its effect; --no-watch leaves that out. The
              same goes for touch, drag, scroll, type and key.
              --screen means raw desktop coordinates instead. Windows of elevated apps ignore input unless
              agentprobe itself runs elevated on the target.
            """, ClickAsync),
        new("touch", "Desktop", "Touch the screen: tap, press and hold, drag (what a finger does, not the mouse)",
            """
            touch tap <x> <y> [--hold 60ms] [--screen] [--window TITLE [--raise]]
            touch dtap <x> <y> ...                       tap twice (a double tap), with the timing a finger has
            touch hold <x> <y> [--for 1200ms] ...        press and hold (the touch way to open a context menu)
            touch drag <x1> <y1> <x2> <y2> [--duration 400ms] [--screen] [--window TITLE [--raise]]
            touch tap @<x>,<y> ...                       screen coordinates, exactly as `ui` prints them
              Real touch input (pointer messages, as from a touch screen), for bugs that a mouse click does not
              show: an app that ignores touch, touch-only gestures, press and hold. It works on a PC without a
              touch screen too. Coordinates are those of `click`: pixels of your latest screenshot, or @x,y /
              --screen for screen coordinates; --window and --raise as for `click`. By name: ui tap <name>,
              ui dtap <name>. A gesture is one command because its timing matters: two `touch tap` calls are
              seconds apart and never a double tap.
              Windows shows its touch feedback at the point, and hides the mouse pointer until the mouse moves.
            """, TouchAsync),
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
            var affinity = GetWindowDisplayAffinity(hwnd, out var asked) ? asked : 0;
            result.Add(new WindowInfo(hwnd, pid, title, cls, Win32.IsWindowVisible(hwnd), Win32.IsHungAppWindow(hwnd), Win32.IsIconic(hwnd), rect, !Win32.IsWindowEnabled(hwnd), affinity));
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
        if (a.Has("hwnd"))
        {
            // One handle and nothing else, by the rule `focus` and `screenshot --window` use: the visible window
            // with exactly that title, else the first whose title contains it.
            var wanted = string.Join(' ', new[] { a.Get("hwnd") }.Concat(a.Positionals).Where(x => !string.IsNullOrEmpty(x)));
            if (wanted.Length == 0) return Task.FromResult(ctx.Fail("windows --hwnd: which window? Give its title or part of it."));
            if (FindWindow(wanted) is not { } found) return Task.FromResult(ctx.Fail($"windows --hwnd: no visible window matches '{wanted}' (see `windows`)."));
            ctx.Out.WriteLine("0x" + ((long)found.Hwnd).ToString("X"));
            return Task.FromResult(0);
        }

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
        var anyDisabled = false;
        var anyNoCapture = false;
        var anyCovered = false;

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
            if (w.Disabled && w.Visible && !popup)
            {
                state = (state + " disabled").TrimStart();
                anyDisabled = true;
            }

            if (w.CaptureAffinity != 0 && w.Visible)
            {
                state = (state + " no-capture").TrimStart();
                anyNoCapture = true;
            }

            if (w.Visible && !w.Minimized && !w.Disabled && !w.Hung && !popup && w.Title.Length > 0 && w.Rect.Width > 1
                && w.Class is not ("Progman" or "WorkerW") && !Cloaked(w.Hwnd) // the desktop is behind everything by nature; a cloaked window is not on the screen
                && CoveredBy(w, 3, [w.Hwnd, .. OwnedBy(windows, w.Hwnd).Select(x => x.Hwnd)]).share >= 0.5)
            {
                state = (state + " covered").TrimStart();
                anyCovered = true;
            }

            var pos = w.Minimized ? "" : $"{w.Rect.Left},{w.Rect.Top} {w.Rect.Width}x{w.Rect.Height}";
            Row("0x" + ((long)w.Hwnd).ToString("X"), w.Pid, process, pos, state, w.Class, Format.Clip(w.Title.Length > 0 ? w.Title : $"[{w.Class}]", 90));
        }

        ctx.Out.Write(t.Count > 0 ? t.ToString() : "no matching windows\n");
        if (prompts.Count > 0) ctx.Out.WriteLine("SYSTEM PROMPT = a dialog of Windows itself (see its title) is waiting for the person at this PC; whatever caused it waits until they answer. " + SystemPrompts.Effect);
        if (anyDisabled) ctx.Out.WriteLine("disabled = that window takes no mouse or keyboard input now, usually because a dialog that belongs to it is open: close the dialog first; `ui click` goes through UI Automation instead of the mouse where the control allows it, and may still work.");
        if (anyCovered) ctx.Out.WriteLine("covered = most of that window is behind other windows on the screen: a screenshot of the screen or a click there gets what is in front. `focus <title>` brings it forward; `screenshot --window <title>` shows the window itself all the same.");
        if (anyNoCapture) ctx.Out.WriteLine("no-capture = that window keeps itself out of screen captures: the person at the PC sees it, a `screenshot` shows black or what is behind it there. `ui` still reads its controls.");
        if (SystemPrompts.SecureDesktopShowing())
            ctx.Out.WriteLine("The secure desktop is showing (a UAC prompt, the lock screen or Ctrl+Alt+Del): the windows above cannot be seen or clicked until the person there answers.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// The windows that belong to a window: the ones whose chain of owners leads to it (its dialogs and message
    /// boxes, and theirs). Front to back, as Windows lists them.
    /// </summary>
    internal static List<WindowInfo> OwnedBy(List<WindowInfo> all, nint hwnd) => all.Where(x =>
    {
        var up = x.Hwnd;
        for (var i = 0; i < 16 && up != 0; i++)
        {
            up = Win32.GetWindow(up, 4); // GW_OWNER
            if (up == hwnd) return true;
        }

        return false;
    }).ToList();

    /// <summary>Not on the screen at all although it counts as visible: on another virtual desktop, or a suspended Store app.</summary>
    internal static bool Cloaked(nint hwnd) => Win32.DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0; // DWMWA_CLOAKED

    /// <summary>
    /// How much of a window is behind other windows, by asking Windows what is in front at a grid of points
    /// across it (the same question a click answers), and what those windows are. <paramref name="itself"/> are
    /// the windows that count as the window: it and what belongs to it. Points outside the desktop are not
    /// counted. Not for a disabled window: Windows skips one when it is asked what is at a point.
    /// </summary>
    internal static (double share, List<nint> inFront) CoveredBy(WindowInfo w, int grid, HashSet<nint> itself)
    {
        var (vx, vy, vw, vh) = (Win32.GetSystemMetrics(76), Win32.GetSystemMetrics(77), Win32.GetSystemMetrics(78), Win32.GetSystemMetrics(79));
        var inFront = new List<nint>();
        int asked = 0, covered = 0;
        for (var row = 0; row < grid; row++)
        {
            for (var column = 0; column < grid; column++)
            {
                var x = w.Rect.Left + (int)(w.Rect.Width * (column + 0.5) / grid);
                var y = w.Rect.Top + (int)(w.Rect.Height * (row + 0.5) / grid);
                if (x < vx || y < vy || x >= vx + vw || y >= vy + vh) continue;
                asked++;
                var at = RootAt(x, y);
                if (at == 0 || itself.Contains(at)) continue;
                covered++;
                if (!inFront.Contains(at)) inFront.Add(at);
            }
        }

        return (asked == 0 ? 0 : (double)covered / asked, inFront);
    }

    /// <summary>
    /// An open menu, context menu or drop-down: a visible untitled window that belongs to another window (or is a
    /// Win32 menu). Shadows, tooltips and input-method helpers are owned and untitled too, and are left out.
    /// </summary>
    internal static bool IsPopup(WindowInfo w)
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

    /// <summary>The top-level window that would get input at this point (what lies in front there), or 0.</summary>
    internal static nint RootAt(int x, int y) => Win32.GetAncestor(Win32.WindowFromPoint((uint)x | ((long)y << 32)), 2); // GA_ROOT

    /// <summary>A window as a person would name it: "its title" (process.exe pid N), or [its class] when it has no title.</summary>
    internal static unsafe string Describe(nint hwnd)
    {
        if (hwnd == 0) return "nothing";
        var buf = stackalloc char[300];
        var title = new string(buf, 0, Math.Max(0, Win32.GetWindowText(hwnd, buf, 300)));
        if (title.Length == 0) title = "[" + new string(buf, 0, Math.Max(0, Win32.GetClassName(hwnd, buf, 256))) + "]";
        else title = "\"" + Format.Clip(title, 60) + "\"";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            process = p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            process = "?";
        }

        return $"{title} ({process} pid {pid})";
    }

    /// <summary>
    /// agentprobe's own window on this PC: the console it shares the PC from, or its banner. The input tools
    /// (click, touch, drag, scroll, type, key, focus, ui) do not act on it: a click meant for the app behind it
    /// would land on the console, and what that window asks or offers is for the person at the PC.
    /// It is recognised as the console window itself, or as the terminal window that hosts the console (which
    /// Windows Terminal reports; a terminal that does not tell Windows which window is its own is not recognised).
    /// </summary>
    internal static bool IsOwnWindow(nint root)
    {
        if (root == 0) return false;
        Win32.GetWindowThreadProcessId(root, out var pid);
        if (pid == Environment.ProcessId) return true;
        var console = GetConsoleWindow();
        return console != 0 && (root == console || root == Win32.GetAncestor(console, 3)); // GA_ROOTOWNER: the terminal that hosts the console
    }

    /// <summary>
    /// Is input at this point going where it is meant to go? Null when it is, else why not (nothing is done then).
    /// <paramref name="meant"/> is the window the caller has in mind (0: none named). A menu, drop-down or flyout
    /// that belongs to it counts as that window; a dialog or any other window in front of it does not.
    /// </summary>
    internal static unsafe string? WrongTarget(int x, int y, nint meant, string hint)
    {
        var root = RootAt(x, y);
        if (IsOwnWindow(root))
            return $"{x},{y} is on agentprobe's own window on the target, which lies in front of whatever else is there; nothing was done. {hint}".TrimEnd();
        if (meant == 0) return null;
        var top = Win32.GetAncestor(meant, 2);
        if (top != 0) meant = top;
        if (Win32.IsIconic(meant)) return $"{Describe(meant)} is minimized; nothing was done. {hint}".TrimEnd();

        // Windows skips a disabled window when it looks for what is at a point, so "what is in front" cannot be
        // told for one; and input does nothing there anyway.
        if (!Win32.IsWindowEnabled(meant))
            return $"{Describe(meant)} is disabled: it takes no mouse or touch input now, usually because a dialog that belongs to it is open (`windows` shows what is); nothing was done.";
        if (root == meant) return null;

        Win32.GetWindowRect(meant, out var rect);
        var inside = x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        if (root != 0)
        {
            // A menu, drop-down or flyout: a window without a title bar that the meant window owns (it may
            // belong to another process, as a browser control's drop-down does, and may reach beyond the window).
            // Failing that, an untitled window of the same program over the window itself.
            var none = stackalloc char[2];
            var untitled = Win32.GetWindowText(root, none, 2) == 0;
            var captioned = ((long)Win32.GetWindowLongPtrW(root, -16) & 0x00C00000) == 0x00C00000; // GWL_STYLE, WS_CAPTION
            Win32.GetWindowThreadProcessId(root, out var pidAt);
            Win32.GetWindowThreadProcessId(meant, out var pidMeant);
            if (Win32.GetAncestor(root, 3) == Win32.GetAncestor(meant, 3) && (untitled || !captioned)) return null;
            if (untitled && pidAt == pidMeant && inside) return null;
        }

        return inside
            ? $"{x},{y} is not on {Describe(meant)}: {Describe(root)} is in front of it there; nothing was done. {hint}".TrimEnd()
            : $"{x},{y} is outside {Describe(meant)}, which is at {rect.Left},{rect.Top} {rect.Width}x{rect.Height}; nothing was done.";
    }

    /// <summary>Asks a window to close, as its close button does (the program may ask about unsaved work, or refuse).</summary>
    /// <returns>False when Windows did not deliver the request (a window of a program with higher rights).</returns>
    internal static bool Close(nint hwnd) => PostMessageW(hwnd, 0x0010, 0, 0) != 0; // WM_CLOSE

    /// <summary>Brings a window to the foreground (restoring it when minimized). False when Windows refused.</summary>
    internal static bool BringToFront(nint hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, 9); // SW_RESTORE

        // Windows only lets the foreground process change the foreground window; a synthetic Alt tap lifts that lock.
        Key(0x12, 0, 0);
        Key(0x12, 0, 2);
        var ok = Win32.SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        return ok || Win32.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// May input be sent to this point? Checks agentprobe's own window, and with --window TITLE that TITLE is what
    /// is in front there; --raise brings it forward first, but only once the point is known to lie on it.
    /// False after saying why: nothing has been sent then.
    /// </summary>
    private static bool TargetOk(ToolContext ctx, string tool, ArgParser a, int x, int y)
    {
        var raise = a.Flag("raise");
        if (a.Has("window") && string.IsNullOrWhiteSpace(a.Get("window")))
        {
            ctx.Fail($"{tool}: --window needs a title; nothing was done.");
            return false;
        }

        if (a.Get("window") is not { } spec)
        {
            if (raise) ctx.Err.WriteLine($"[{tool}: --raise needs --window <title> to know which window to bring to the front; ignored]");
            if (WrongTarget(x, y, 0, "Bring the app to the front first (`focus <its title>`).") is not { } onOwn) return true;
            ctx.Fail($"{tool}: {onOwn}");
            return false;
        }

        if (FindWindow(spec) is not { } w)
        {
            ctx.Fail($"{tool}: no window matches '{spec}' (see `windows`); nothing was done.");
            return false;
        }

        if (IsOwnWindow(w.Hwnd))
        {
            ctx.Fail($"{tool}: '{spec}' is agentprobe's own window on the target, which takes no remote input; nothing was done.");
            return false;
        }

        var raised = false;
        if (raise)
        {
            // Bringing a window forward is not undone by a refusal, so the point is checked against it first.
            if (!w.Minimized && (x < w.Rect.Left || x >= w.Rect.Right || y < w.Rect.Top || y >= w.Rect.Bottom))
            {
                ctx.Fail($"{tool}: {x},{y} is outside \"{Format.Clip(w.Title, 60)}\", which is at {w.Rect.Left},{w.Rect.Top} {w.Rect.Width}x{w.Rect.Height}; nothing was done.");
                return false;
            }

            raised = BringToFront(w.Hwnd);
            if (!raised) ctx.Err.WriteLine($"[{tool}: Windows refused to bring \"{Format.Clip(w.Title, 60)}\" to the front (an elevated window?)]");
        }

        if (WrongTarget(x, y, w.Hwnd, raise ? "" : "Add --raise to bring it to the front first.") is not { } wrong) return true;
        ctx.Fail($"{tool}: " + (raised ? wrong.Replace("nothing was done.", "the window was brought to the front, but no input was sent.") : wrong));
        return false;
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

    /// <summary>
    /// True when Windows scales the window for its display: a program that does not know about display scaling
    /// (or about the scaling of this display) is drawn at one size and shown at another.
    /// </summary>
    private static bool ScaledByWindows(nint hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            return monitor != 0 && Win32.GetDpiForMonitor(monitor, 0, out var shown, out _) == 0 && GetDpiForWindow(hwnd) is var own && own != 0 && own != shown;
        }
        catch (EntryPointNotFoundException)
        {
            return false; // before Windows 10 1607: no per-window scaling to ask about
        }
    }

    /// <summary>
    /// A picture of windows as they draw themselves (PrintWindow with the full-content flag), put together at
    /// their places inside <paramref name="area"/>: first the window asked for, then the ones that belong to it,
    /// back to front. Nothing on the screen can cover them. Null when the first window drew nothing (or only
    /// black); a later one that draws nothing is left out, and <paramref name="drawn"/> says how many are in.
    /// </summary>
    private static unsafe byte[]? CaptureWindows(RECT area, IReadOnlyList<WindowInfo> backToFront, int width, int height, out int drawn)
    {
        drawn = 0;
        var screen = Win32.GetDC(0);
        var canvasDc = Win32.CreateCompatibleDC(screen);
        var canvas = Win32.CreateCompatibleBitmap(screen, area.Width, area.Height);
        var outDc = Win32.CreateCompatibleDC(screen);
        var outBitmap = Win32.CreateCompatibleBitmap(screen, width, height);
        var oldCanvas = canvas != 0 ? Win32.SelectObject(canvasDc, canvas) : 0;
        try
        {
            if (screen == 0 || canvasDc == 0 || canvas == 0 || outDc == 0 || outBitmap == 0) return null;
            for (var i = 0; i < backToFront.Count; i++)
            {
                var w = backToFront[i];
                if (w.Rect.Width <= 0 || w.Rect.Height <= 0) continue;
                var dc = Win32.CreateCompatibleDC(screen);
                var bitmap = Win32.CreateCompatibleBitmap(screen, w.Rect.Width, w.Rect.Height);
                var ok = dc != 0 && bitmap != 0;
                if (ok)
                {
                    var old = Win32.SelectObject(dc, bitmap);
                    ok = PrintWindow(w.Hwnd, dc, 2) != 0; // PW_RENDERFULLCONTENT: also what the window draws with the GPU

                    // A window that answers without drawing leaves its picture black. For the window asked for
                    // that is "no picture"; one that belongs to it is then simply not in the picture.
                    if (ok)
                    {
                        Win32.SelectObject(dc, old);
                        var own = new byte[w.Rect.Width * w.Rect.Height * 4];
                        var info = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w.Rect.Width, biHeight = -w.Rect.Height, biPlanes = 1, biBitCount = 32 };
                        fixed (byte* q = own) ok = Win32.GetDIBits(dc, bitmap, 0, (uint)w.Rect.Height, q, &info, 0) != 0;
                        if (ok)
                        {
                            ok = false;
                            for (var k = 0; k + 2 < own.Length; k += 4)
                            {
                                if (own[k] == 0 && own[k + 1] == 0 && own[k + 2] == 0) continue;
                                ok = true;
                                break;
                            }
                        }

                        old = Win32.SelectObject(dc, bitmap);
                        if (ok) Win32.BitBlt(canvasDc, w.Rect.Left - area.Left, w.Rect.Top - area.Top, w.Rect.Width, w.Rect.Height, dc, 0, 0, 0x00CC0020); // SRCCOPY
                    }

                    Win32.SelectObject(dc, old);
                }

                if (bitmap != 0) Win32.DeleteObject(bitmap);
                if (dc != 0) Win32.DeleteDC(dc);
                if (ok) drawn++;
                else if (i == 0) return null;
            }

            var oldOut = Win32.SelectObject(outDc, outBitmap);
            if (width == area.Width && height == area.Height)
            {
                Win32.BitBlt(outDc, 0, 0, width, height, canvasDc, 0, 0, 0x00CC0020);
            }
            else
            {
                SetStretchBltMode(outDc, 4); // HALFTONE
                StretchBlt(outDc, 0, 0, width, height, canvasDc, 0, 0, area.Width, area.Height, 0x00CC0020);
            }

            Win32.SelectObject(outDc, oldOut);
            var pixels = new byte[width * height * 4];
            var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            fixed (byte* p = pixels)
            {
                if (Win32.GetDIBits(outDc, outBitmap, 0, (uint)height, p, &header, 0) == 0) return null;
            }

            return pixels;
        }
        finally
        {
            if (canvas != 0) Win32.SelectObject(canvasDc, oldCanvas);
            if (canvas != 0) Win32.DeleteObject(canvas);
            if (canvasDc != 0) Win32.DeleteDC(canvasDc);
            if (outBitmap != 0) Win32.DeleteObject(outBitmap);
            if (outDc != 0) Win32.DeleteDC(outDc);
            if (screen != 0) Win32.ReleaseDC(0, screen);
        }
    }

    private static async Task<int> ScreenshotAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "raise", "diff", "stats");
        var monitors = Monitors();
        RECT area;
        string what;
        List<WindowInfo>? itself = null; // --window: the window and the dialogs it owns, back to front
        var windowSpec = a.Get("window");
        RECT? region = null;
        var regionName = "";
        if (a.Has("thumb") && (a.Has("control") || a.Has("region") || a.Flag("diff") || a.Flag("stats"))) return ctx.Fail("screenshot: --control, --region, --diff and --stats are not for the dashboard's preview.");
        if (a.Get("control") is { } controlSpec)
        {
            // The control's own rectangle, in the window it is in: found as `ui click` finds it.
            var probe = await Task.Run(() => UiaTools.Probe(windowSpec, controlSpec, Math.Max(1, a.GetInt("nth", 1))));
            if (probe.status != 0) return ctx.Fail("screenshot --control: " + probe.text + (probe.status == 1 ? " (`ui --grep` shows what is there)." : ""));
            if (probe.offscreen) return ctx.Fail($"screenshot --control: {probe.text} is not visible on screen (scrolled away, or on a page that is not shown): there is nothing to take a picture of.");
            region = new RECT { Left = probe.rect.Left - 6, Top = probe.rect.Top - 6, Right = probe.rect.Right + 6, Bottom = probe.rect.Bottom + 6 };
            regionName = probe.text;
            windowSpec = "0x" + ((long)probe.hwnd).ToString("X");
        }
        else if (a.Get("region") is { } regionText)
        {
            var n = regionText.Split([',', ' ', 'x'], StringSplitOptions.RemoveEmptyEntries);
            if (n.Length != 4 || !int.TryParse(n[0], out var rx) || !int.TryParse(n[1], out var ry) || !int.TryParse(n[2], out var rw) || !int.TryParse(n[3], out var rh) || rw <= 0 || rh <= 0)
                return ctx.Fail("screenshot: --region wants X,Y,W,H in screen coordinates, for example --region 160,160,440,375.");
            region = new RECT { Left = rx, Top = ry, Right = rx + rw, Bottom = ry + rh };
            regionName = $"the region {rx},{ry} {rw}x{rh}";
        }

        if (windowSpec is { } spec)
        {
            var w = FindWindow(spec);
            if (w == null) return ctx.Fail($"screenshot: no window matches '{spec}' (see `windows`).");
            var raise = a.Flag("raise");
            if (raise && a.Has("thumb")) return ctx.Fail("screenshot: --raise is not for the dashboard's preview, which only looks.");
            if (raise)
            {
                if (IsOwnWindow(w.Hwnd)) return ctx.Fail("screenshot: --raise does not bring agentprobe's own window on the target to the front.");
                ctx.Runtime.Audit.Write($"screenshot --raise: bringing \"{Format.Clip(w.Title, 60)}\" to the front");
                if (!BringToFront(w.Hwnd)) ctx.Err.WriteLine($"[screenshot: Windows refused to bring \"{Format.Clip(w.Title, 60)}\" to the front (an elevated window?)]");
                w = EnumerateWindows().FirstOrDefault(x => x.Hwnd == w.Hwnd) ?? w; // it may have been restored: its place is new
            }

            if (w.Minimized) return ctx.Fail($"screenshot: \"{w.Title}\" is minimized{(raise ? " and Windows did not restore it" : ". `focus` it first, or add --raise")}.");

            // The window with what belongs to it: its dialogs and message boxes are windows of their own, owned by
            // it. Only the ones with a title (a shadow or a glow frame has none), on the desktop, and not so far
            // away that the picture would be mostly empty.
            area = w.Rect;
            var desk = new RECT { Left = Win32.GetSystemMetrics(76), Top = Win32.GetSystemMetrics(77) };
            (desk.Right, desk.Bottom) = (desk.Left + Win32.GetSystemMetrics(78), desk.Top + Win32.GetSystemMetrics(79));
            var owned = OwnedBy(EnumerateWindows(), w.Hwnd)
                .Where(x => x.Visible && !x.Minimized && x.Title.Length > 0 && x.Rect.Width > 1 && !Cloaked(x.Hwnd)
                            && x.Rect.Left >= desk.Left - 100 && x.Rect.Top >= desk.Top - 100 && x.Rect.Right <= desk.Right + 100 && x.Rect.Bottom <= desk.Bottom + 100)
                .Reverse().ToList();
            itself = [w, .. owned];
            foreach (var x in owned)
            {
                area = new RECT { Left = Math.Min(area.Left, x.Rect.Left), Top = Math.Min(area.Top, x.Rect.Top), Right = Math.Max(area.Right, x.Rect.Right), Bottom = Math.Max(area.Bottom, x.Rect.Bottom) };
            }

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

        // A window is asked to draw itself, so that it comes out whole whatever lies in front of it. When it
        // cannot, it is cut out of the screen, and the answer says which and why:
        //  - it drew nothing, or only black (some windows do not draw this way);
        //  - it is not responding (asking it would wait for it);
        //  - Windows scales it for the display (an app that does not know about display scaling draws itself
        //    smaller than it is shown, so the picture and the click coordinates would not fit the screen).
        var fromScreen = itself == null;
        var whyScreen = "";
        var drawn = 0;
        byte[]? pixels = null;
        if (itself != null)
        {
            if (itself[0].Hung) whyScreen = "it is not responding, so it was not asked to draw itself";
            else if (ScaledByWindows(itself[0].Hwnd)) whyScreen = "Windows scales that window for the display, and it would draw itself at another size than it is shown";
            else if ((long)area.Width * area.Height > 80_000_000) whyScreen = "it is too large to be drawn off the screen";
            else pixels = CaptureWindows(area, itself, width, height, out drawn);
            if (pixels == null && whyScreen.Length == 0) whyScreen = "it drew nothing, or only black, when asked to draw itself";
        }

        if (pixels == null)
        {
            if (itself != null) area = itself[0].Rect; // the window's own place on the screen, nothing more
            (width, height) = (Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
            fromScreen = true;
            pixels = Capture(area, width, height, scale < 1);
        }
        else if (drawn > 1)
        {
            what += $" with {drawn - 1} window{(drawn == 2 ? "" : "s")} that belong{(drawn == 2 ? "s" : "")} to it ({string.Join(", ", itself!.Skip(1).Take(3).Select(x => "\"" + Format.Clip(x.Title, 30) + "\""))})";
        }

        if (pixels == null) return ctx.Fail("screenshot: capture failed (secure desktop such as UAC or the lock screen is showing?).");

        // --control / --region: that part of the picture only. The place it has on the screen stays known, so
        // click coordinates in the smaller image still land where they should.
        if (region is { } wanted)
        {
            var clip = new RECT { Left = Math.Max(wanted.Left, area.Left), Top = Math.Max(wanted.Top, area.Top), Right = Math.Min(wanted.Right, area.Right), Bottom = Math.Min(wanted.Bottom, area.Bottom) };
            if (clip.Width <= 0 || clip.Height <= 0)
                return ctx.Fail($"screenshot: {regionName} (at {wanted.Left},{wanted.Top} {wanted.Width}x{wanted.Height}) lies outside {what}, which is at {area.Left},{area.Top} {area.Width}x{area.Height}; no picture was taken.");
            var cx = Math.Clamp((int)((clip.Left - area.Left) * scale), 0, width - 1);
            var cy = Math.Clamp((int)((clip.Top - area.Top) * scale), 0, height - 1);
            var cw = Math.Clamp((int)Math.Ceiling(clip.Width * scale), 1, width - cx);
            var ch = Math.Clamp((int)Math.Ceiling(clip.Height * scale), 1, height - cy);
            pixels = Crop(pixels, width, cx, cy, cw, ch);
            (width, height, area) = (cw, ch, clip);
            what = $"{regionName} in {what}";
        }

        // --diff: against the previous screenshot of exactly this area and scale, kept on this PC.
        var diffNote = "";
        var changedShare = -1.0;
        if (!thumb)
        {
            var key = $"{what}|{area.Left},{area.Top},{area.Width},{area.Height}|{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var previous = _lastShot;
            _lastShot = (key, pixels, width, height, DateTime.UtcNow);
            if (a.Flag("diff"))
            {
                if (previous is { } p && p.key == key && p.width == width && p.height == height)
                {
                    var ago = Format.Duration(DateTime.UtcNow - p.at);
                    int minX = width, minY = height, maxX = -1, maxY = -1;
                    long changed = 0;
                    for (var y = 0; y < height; y++)
                    {
                        var row = y * width * 4;
                        for (var x = 0; x < width; x++)
                        {
                            var i = row + (x * 4);
                            if (pixels[i] == p.pixels[i] && pixels[i + 1] == p.pixels[i + 1] && pixels[i + 2] == p.pixels[i + 2]) continue;
                            changed++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }

                    if (changed == 0)
                    {
                        ctx.Runtime.Audit.Write($"screenshot of {what} (compared with the one before: no change)");
                        ctx.Out.WriteLine($"no change: {what} looks exactly as in the screenshot taken {ago} ago ({width}x{height} pixels compared). No image was sent.");
                        return 0;
                    }

                    changedShare = 100.0 * changed / ((long)width * height);
                    var share = changedShare < 0.1 ? "under 0.1" : Format.Pct(changedShare);
                    var (bx, by) = (Math.Max(0, minX - 8), Math.Max(0, minY - 8));
                    var (bw, bh) = (Math.Min(width, maxX + 9) - bx, Math.Min(height, maxY + 9) - by);
                    if (a.Flag("stats"))
                    {
                        diffNote = $"{share}% of the pixels differ from the screenshot taken {ago} ago, all of them within {bx},{by} {bw}x{bh} of the image";
                    }
                    else if ((long)bw * bh * 100 >= (long)width * height * 70)
                    {
                        diffNote = $"--diff: {share}% of the pixels differ from the screenshot taken {ago} ago, spread over most of it: this is the whole picture.";
                    }
                    else
                    {
                        diffNote = $"--diff: this is only the part that changed since the screenshot taken {ago} ago: {bx},{by} {bw}x{bh} of that {width}x{height} image ({share}% of its pixels differ). Everything outside it is as it was.";
                        pixels = Crop(pixels, width, bx, by, bw, bh);
                        area = new RECT { Left = area.Left + (int)Math.Round(bx / scale), Top = area.Top + (int)Math.Round(by / scale), Right = area.Left + (int)Math.Round((bx + bw) / scale), Bottom = area.Top + (int)Math.Round((by + bh) / scale) };
                        (width, height) = (bw, bh);
                    }
                }
                else
                {
                    diffNote = a.Flag("stats")
                        ? "--diff: no earlier screenshot of exactly this area and scale in this session to compare with"
                        : "--diff: there is no earlier screenshot of exactly this area and scale in this session to compare with, so this is the whole picture.";
                }
            }
        }

        if (a.Flag("stats"))
        {
            // Numbers instead of a picture: enough for "is it black?", "is anything drawn there?".
            long sumR = 0, sumG = 0, sumB = 0, black = 0, white = 0;
            var buckets = new int[4096];
            for (var i = 0; i + 2 < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                sumR += r;
                sumG += g;
                sumB += b;
                if (r < 24 && g < 24 && b < 24) black++;
                else if (r > 231 && g > 231 && b > 231) white++;
                buckets[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]++;
            }

            long count = Math.Max(1, pixels.Length / 4);
            var top = Array.IndexOf(buckets, buckets.Max());
            static string Share(long part, long whole) => (100.0 * part / whole).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            ctx.Runtime.Audit.Write($"screenshot of {what} (statistics only, no image)");
            ctx.Out.WriteLine($"{width}x{height} pixels of {what}{(scale < 1 ? $" at {scale:0.##}x" : "")}, no image sent: average color #{sumR / count:X2}{sumG / count:X2}{sumB / count:X2}; " +
                              $"{Share(black, count)}% near black, {Share(white, count)}% near white; the most common color, about #{((top >> 8) << 4) + 8:X2}{(((top >> 4) & 15) << 4) + 8:X2}{((top & 15) << 4) + 8:X2}, covers {Share(buckets[top], count)}%" +
                              (diffNote.Length > 0 ? "; " + diffNote : ""));
            return 0;
        }

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
            if (diffNote.Length > 0) ctx.Out.WriteLine(diffNote);
            if (itself != null)
            {
                // What is in front of it on the screen. A disabled window cannot be asked about (Windows skips
                // it at a point), and one that is cloaked is not on the screen in the first place.
                var main = itself[0];
                var known = !main.Disabled && !Cloaked(main.Hwnd);
                var (share, inFront) = known ? CoveredBy(main, 6, [.. itself.Select(x => x.Hwnd)]) : (0, []);
                if (fromScreen)
                {
                    ctx.Out.WriteLine($"This is that part of the SCREEN, not the window drawn by itself: {whyScreen}." +
                                      (inFront.Count > 0 ? $" {string.Join(", ", inFront.Take(3).Select(Describe))} lie{(inFront.Count == 1 ? "s" : "")} in front of it there and {(inFront.Count == 1 ? "is" : "are")} in the picture (`focus`, or --raise, brings the window forward)." : ""));
                }
                else if (Cloaked(main.Hwnd))
                {
                    ctx.Out.WriteLine("The picture is the window as it draws itself. It is NOT ON THE SCREEN now (another virtual desktop, or an app Windows has suspended): the person at the PC does not see it.");
                }
                else if (main.Disabled)
                {
                    ctx.Out.WriteLine("The picture is the window as it draws itself. The window is disabled now (a dialog that belongs to it is open?), so what lies in front of it on the screen could not be asked: `windows` shows what is open.");
                }
                else if (inFront.Count > 0)
                {
                    ctx.Out.WriteLine($"The picture is the window as it draws itself. ON THE SCREEN about {Math.Round(share * 100)}% of it is behind {string.Join(", ", inFront.Take(3).Select(Describe))}" +
                                      (inFront.Count > 3 ? $" and {inFront.Count - 3} more" : "") +
                                      ": the person at the PC does not see all of this, and a click there goes to what is in front (`click --window <title> --raise`).");
                }

                if (!fromScreen && SystemPrompts.SecureDesktopShowing())
                    ctx.Out.WriteLine("The target shows its secure desktop now (a UAC prompt, the lock screen or Ctrl+Alt+Del): the person there does not see this window at all, and nothing on it can be clicked.");
            }

            // A window may keep itself out of captures: the image then differs from what the person at the PC
            // sees, and nothing in the image says so.
            var unseen = EnumerateWindows().Where(w => w.CaptureAffinity != 0 && w.Visible && !w.Minimized && w.Pid != Environment.ProcessId
                                                       && (fromScreen || itself!.Any(x => x.Hwnd == w.Hwnd)) // a window that drew itself is not hidden by another one's wish
                                                       && w.Rect.Width > 1 && w.Rect.Right > area.Left && w.Rect.Left < area.Right && w.Rect.Bottom > area.Top && w.Rect.Top < area.Bottom
                                                       && !(Win32.DwmGetWindowAttribute(w.Hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0)).ToList(); // DWMWA_CLOAKED: on another desktop
            if (unseen.Count > 0)
            {
                ctx.Out.WriteLine("NOT in this image as the person at the PC sees it: " + string.Join("; ", unseen.Take(6).Select(w =>
                                      $"{Describe(w.Hwnd)} at {w.Rect.Left},{w.Rect.Top} {w.Rect.Width}x{w.Rect.Height} ({(w.CaptureAffinity == 1 || !fromScreen ? "shown black" : "left out: what is behind it shows")})")) +
                                  (unseen.Count > 6 ? $" (+{unseen.Count - 6} more)" : "") +
                                  ". Those windows keep themselves out of screen captures; `ui <title>` reads their controls.");
            }
        }

        await ctx.SendFile($"screenshot-{DateTime.Now:HHmmss}.png", file, false, true);
        return 0;
    }

    /// <summary>The last screenshot an agent took, for <c>--diff</c>: which area it was of, and its pixels.</summary>
    private static (string key, byte[] pixels, int width, int height, DateTime at)? _lastShot;

    /// <summary>A rectangle out of top-down BGRA pixels.</summary>
    private static byte[] Crop(byte[] pixels, int width, int x, int y, int w, int h)
    {
        var result = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Buffer.BlockCopy(pixels, (((y + row) * width) + x) * 4, result, row * w * 4, w * 4);
        return result;
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
        var a = ArgParser.Parse(args, "double", "screen", "hover", "raise", "no-watch");
        var raw = ExpandAt(a) | a.Flag("screen");
        if (!Coordinates(ctx, a, 2, out var v)) return 2;
        var (x, y) = ToScreen(v[0], v[1], raw);
        if (OutsideDesktop(x, y, raw) is { } outside) return ctx.Fail("click: " + outside);
        if ((!a.Flag("hover") || a.Has("window")) && !TargetOk(ctx, "click", a, x, y)) return 2; // moving the pointer alone presses nothing
        var onWhat = Describe(RootAt(x, y)); // before the click: the click itself may close or open windows
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
        var blocked = DisabledWindowAt(x, y); // looked at before the click: the click itself may change what is open
        var watch = await WatchAsync(a.Flag("no-watch"), RootAt(x, y));
        Mouse(x, y, 0x0001); // move
        await Task.Delay(40, ctx.Ct);
        for (var i = 0; i < (a.Flag("double") ? 2 : 1); i++)
        {
            Mouse(x, y, down);
            Mouse(x, y, up);
            await Task.Delay(60, ctx.Ct);
        }

        ctx.Runtime.Audit.Write($"click at {x},{y}");
        ctx.Out.WriteLine($"clicked at screen {x},{y} on {onWhat}");
        await ReportAsync(ctx, watch);
        if (blocked != null) ctx.Out.WriteLine(blocked);
        SystemPrompts.Warn(ctx.Err);
        return 0;
    }

    /// <summary>The screen right before an input action (see <see cref="UiaTools.Watch"/>); null when not asked for.</summary>
    private static Task<UiWatch?> WatchAsync(bool off, nint window) => off ? Task.FromResult<UiWatch?>(null) : Task.Run(() => (UiWatch?)UiaTools.Watch(window));

    /// <summary>Adds what changed on the screen since <paramref name="watch"/> to the answer.</summary>
    private static async Task ReportAsync(ToolContext ctx, UiWatch? watch)
    {
        if (watch != null) ctx.Out.WriteLine(await Task.Run(() => UiaTools.Report(watch)));
    }

    /// <summary>
    /// A click on a disabled window does nothing, and nothing says so: the app just "does not react". When the
    /// point is on such a window, name it. The disabled window is either the one Windows names for that point
    /// (a control inside it is not disabled itself) or one that lies in front of it (Windows skips a disabled
    /// window when the point is on its bare surface). Nothing is said when that cannot be told: when the window
    /// that gets the click is not in the list of top-level windows (Start, shell flyouts), or when the window in
    /// front lets clicks through at that point.
    /// </summary>
    internal static string? DisabledWindowAt(int x, int y)
    {
        var windows = EnumerateWindows(); // front to back
        var target = Win32.GetAncestor(Win32.WindowFromPoint((uint)x | ((long)y << 32)), 2); // GA_ROOT
        var targetAt = target == 0 ? -1 : windows.FindIndex(w => w.Hwnd == target);
        if (targetAt < 0) return null;
        for (var i = 0; i <= targetAt; i++)
        {
            var w = windows[i];
            if (!w.Visible || !w.Disabled || w.Minimized || w.Title.Length == 0) continue;
            if (x < w.Rect.Left || x >= w.Rect.Right || y < w.Rect.Top || y >= w.Rect.Bottom) continue;
            if (Win32.DwmGetWindowAttribute(w.Hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0) continue; // DWMWA_CLOAKED: not on screen
            if (i < targetAt)
            {
                // In front of what gets the click: was it skipped because it is disabled, or because it is
                // see-through there? Ask the window itself what is at that point.
                if (((long)Win32.GetWindowLongPtrW(w.Hwnd, -20) & 0x20) != 0) continue; // WS_EX_TRANSPARENT
                var point = (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
                if (Win32.SendMessageTimeoutW(w.Hwnd, 0x0084, 0, point, 0x0002, 200, out var hit) == 0) continue; // WM_NCHITTEST, abort if hung
                if (hit is 0 or -1) continue; // HTNOWHERE, HTTRANSPARENT
            }

            string process;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(w.Pid);
                process = p.ProcessName + ".exe";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                process = "?";
            }

            return $"note: that point is on \"{Format.Clip(w.Title, 60)}\" ({process} pid {w.Pid}), which is disabled: it takes no mouse or keyboard input, " +
                   "usually because a dialog that belongs to it is open (`windows` shows what is). The click did nothing there; `ui click` goes through UI Automation instead of the mouse where the control allows it, and may still work.";
        }

        return null;
    }

    // ------------------------------------------------------------------ touch

    private const uint TouchDown = 0x00010000 | 0x0002 | 0x0004; // POINTER_FLAG_DOWN | INRANGE | INCONTACT
    private const uint TouchMove = 0x00020000 | 0x0002 | 0x0004; // POINTER_FLAG_UPDATE | INRANGE | INCONTACT
    private const uint TouchUp = 0x00040000; // POINTER_FLAG_UP

    private static unsafe bool TouchFrame(int x, int y, uint flags)
    {
        var contact = default(POINTER_TOUCH_INFO);
        contact.pointerInfo.pointerType = 2; // PT_TOUCH
        contact.pointerInfo.pointerFlags = flags;
        contact.pointerInfo.ptPixelLocationX = x;
        contact.pointerInfo.ptPixelLocationY = y;
        contact.touchMask = 0x1 | 0x2 | 0x4; // contact area, orientation, pressure
        contact.rcContact = new RECT { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 };
        contact.orientation = 90;
        contact.pressure = 32000;
        return InjectTouchInput(1, &contact);
    }

    /// <summary>
    /// One finger: down at the first point, along the others, up at the last; <paramref name="duration"/> is the
    /// time between down and up. Everything happens on the calling thread, which Windows ties the contact to.
    /// Null when the whole gesture was accepted, else why Windows refused it. The finger is always lifted again.
    /// </summary>
    internal static string? TouchGesture(IReadOnlyList<(int x, int y)> path, TimeSpan duration, CancellationToken ct)
    {
        static string Refused(string step)
        {
            var error = Marshal.GetLastPInvokeError();
            var meaning = error switch
            {
                5 => ": access denied, the window there belongs to an elevated program",
                87 => ": invalid parameter",
                21 => ": touch injection is not ready",
                _ => "",
            };
            return $"Windows refused the touch input ({step}, error {error}{meaning}).";
        }

        if (!InitializeTouchInjection(1, 1)) return Refused("InitializeTouchInjection"); // one contact, Windows' own touch feedback
        var (x, y) = path[0];
        if (!TouchFrame(x, y, TouchDown)) return Refused("touch down");
        var (liftX, liftY) = (x, y); // the last point Windows accepted
        string? failure = null;
        try
        {
            // A contact that is not updated for a while is dropped by Windows, so it is refreshed every few ms.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                Thread.Sleep(16);
                if (path.Count > 1)
                {
                    var along = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / Math.Max(1, duration.TotalMilliseconds)) * (path.Count - 1);
                    var segment = Math.Min(path.Count - 2, (int)along);
                    var within = along - segment;
                    x = (int)Math.Round(path[segment].x + ((path[segment + 1].x - path[segment].x) * within));
                    y = (int)Math.Round(path[segment].y + ((path[segment + 1].y - path[segment].y) * within));
                }

                if (!TouchFrame(x, y, TouchMove))
                {
                    failure = Refused("touch move");
                    break;
                }

                (liftX, liftY) = (x, y);
            }

            if (failure == null && ct.IsCancellationRequested)
            {
                failure = $"cancelled after {(int)sw.ElapsedMilliseconds} ms of {(int)duration.TotalMilliseconds}; the finger was lifted at {liftX},{liftY}.";
            }
            else if (failure == null && path.Count > 1)
            {
                (x, y) = path[^1];
                if (TouchFrame(x, y, TouchMove)) (liftX, liftY) = (x, y);
                else failure = Refused("touch move");
            }
        }
        finally
        {
            if (!TouchFrame(liftX, liftY, TouchUp)) failure ??= Refused("touch up");
        }

        return failure;
    }

    /// <summary>
    /// One tap, or several in a row with the pause a finger makes between them (two = a double tap), all on the
    /// calling thread. Null when Windows accepted them all, else why not.
    /// </summary>
    internal static string? TouchTaps(int x, int y, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0) Thread.Sleep(90);
            if (TouchGesture([(x, y)], TimeSpan.FromMilliseconds(50), ct) is { } failure) return count > 1 ? $"tap {i + 1} of {count}: {failure}" : failure;
        }

        return null;
    }

    private static async Task<int> TouchAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "screen", "raise", "no-watch");
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        if (verb is not ("tap" or "dtap" or "hold" or "drag")) return ctx.Fail("touch: tap, dtap, hold or drag? e.g. touch tap 640 400   (see `help touch`)");
        a.Positionals.RemoveAt(0);
        var raw = ExpandAt(a) | a.Flag("screen");
        var points = verb == "drag" ? 2 : 1;
        if (!Coordinates(ctx, a, points * 2, out var v)) return 2;
        var path = new List<(int x, int y)>();
        for (var i = 0; i < points; i++)
        {
            var (x, y) = ToScreen(v[i * 2], v[(i * 2) + 1], raw);
            if (OutsideDesktop(x, y, raw) is { } outside) return ctx.Fail("touch: " + outside);
            path.Add((x, y));
        }

        var duration = verb switch
        {
            "hold" => a.GetDuration("for") ?? a.GetDuration("hold") ?? TimeSpan.FromMilliseconds(1200),
            "drag" => a.GetDuration("duration") ?? TimeSpan.FromMilliseconds(400),
            _ => a.GetDuration("hold") ?? TimeSpan.FromMilliseconds(60),
        };
        duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 20, 60_000));

        if (!TargetOk(ctx, "touch", a, path[0].x, path[0].y)) return 2;
        if (path.Count > 1 && IsOwnWindow(RootAt(path[^1].x, path[^1].y))) return ctx.Fail($"touch: {path[^1].x},{path[^1].y}, where the drag would end, is on agentprobe's own window on the target; nothing was done.");
        var onWhat = Describe(RootAt(path[0].x, path[0].y));
        var blocked = DisabledWindowAt(path[0].x, path[0].y);
        var watch = await WatchAsync(a.Flag("no-watch"), RootAt(path[0].x, path[0].y));
        var failure = await Task.Run(() => verb == "dtap" ? TouchTaps(path[0].x, path[0].y, 2, ctx.Ct) : TouchGesture(path, duration, ctx.Ct), ctx.Ct);
        if (failure != null)
        {
            UiaTools.Drop(watch);
            return ctx.Fail("touch: " + failure);
        }


        var where = $"{path[0].x},{path[0].y}";
        ctx.Runtime.Audit.Write(verb switch
        {
            "drag" => $"touch drag {where} -> {path[1].x},{path[1].y}",
            "hold" => $"touch press and hold at {where}",
            "dtap" => $"touch double tap at {where}",
            _ => $"touch tap at {where}",
        });
        ctx.Out.WriteLine(verb switch
        {
            "drag" => $"dragged by touch from screen {where} to {path[1].x},{path[1].y} in {Format.Duration(duration)}, starting on {onWhat}",
            "hold" => $"pressed and held by touch at screen {where} for {Format.Duration(duration)} on {onWhat}",
            "dtap" => $"tapped twice by touch at screen {where} on {onWhat}",
            _ => $"tapped by touch at screen {where} on {onWhat}",
        });
        await ReportAsync(ctx, watch);
        if (blocked != null) ctx.Out.WriteLine(blocked.Replace("The click did nothing there", "The touch did nothing there"));
        SystemPrompts.Warn(ctx.Err);
        return 0;
    }

    private static async Task<int> DragAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "screen", "no-watch");
        var raw = ExpandAt(a) | a.Flag("screen"); // both ends in the same kind of coordinates
        if (!Coordinates(ctx, a, 4, out var v)) return 2;
        var (x1, y1) = ToScreen(v[0], v[1], raw);
        var (x2, y2) = ToScreen(v[2], v[3], raw);
        if ((OutsideDesktop(x1, y1, raw) ?? OutsideDesktop(x2, y2, raw)) is { } outside) return ctx.Fail("drag: " + outside);
        if (WrongTarget(x1, y1, 0, "Bring the app to the front first (`focus <its title>`).") is { } wrong) return ctx.Fail("drag: " + wrong);
        if (IsOwnWindow(RootAt(x2, y2))) return ctx.Fail($"drag: {x2},{y2}, where the drag would end, is on agentprobe's own window on the target; nothing was done.");
        var watch = await WatchAsync(a.Flag("no-watch"), RootAt(x1, y1));
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
        await ReportAsync(ctx, watch);
        return 0;
    }

    private static unsafe Task<int> ScrollAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "screen", "no-watch");
        var raw = ExpandAt(a) | a.Flag("screen");
        if (a.Positionals.Count == 0 || !int.TryParse(a.Positionals[0], out var notches)) return Task.FromResult(ctx.Fail("scroll: how many notches? (negative = down)"));
        if (a.Positionals.Count >= 3 && double.TryParse(a.Positionals[1], out var px) && double.TryParse(a.Positionals[2], out var py))
        {
            var (x, y) = ToScreen(px, py, raw);
            if (OutsideDesktop(x, y, raw) is { } outside) return Task.FromResult(ctx.Fail("scroll: " + outside));
            if (WrongTarget(x, y, 0, "Bring the app to the front first (`focus <its title>`).") is { } wrong) return Task.FromResult(ctx.Fail("scroll: " + wrong));
            Mouse(x, y, 0x0001);
        }
        else if (GetCursorPos(out var at) && IsOwnWindow(RootAt(at.X, at.Y)))
        {
            // The wheel turns for the window under the pointer.
            return Task.FromResult(ctx.Fail("scroll: the pointer is on agentprobe's own window on the target; nothing was done. Give a position: scroll <notches> <x> <y>."));
        }

        var watch = !a.Flag("no-watch") && GetCursorPos(out var pointer) ? UiaTools.Watch(RootAt(pointer.X, pointer.Y)) : null;
        var input = default(INPUT);
        input.type = 0;
        input.mi.mouseData = unchecked((uint)(notches * 120));
        input.mi.dwFlags = 0x0800; // WHEEL
        Win32.SendInput(1, &input, sizeof(INPUT));
        ctx.Out.WriteLine($"scrolled {notches}");
        if (watch != null) ctx.Out.WriteLine(UiaTools.Report(watch));
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

    /// <summary>Keys go to the foreground window. When that is agentprobe's own, nothing is typed: why, else null.</summary>
    private static string? KeyboardOnOwnWindow() => IsOwnWindow(Win32.GetAncestor(Win32.GetForegroundWindow(), 2))
        ? "the keyboard is on agentprobe's own window on the target (it is the foreground window); nothing was typed. Bring the app to the front first: `focus <its title>`."
        : null;

    /// <summary>Why typing must not go on, asked before every key: the person paused, or agentprobe's own window came to the front. Null to go on.</summary>
    private static string? StopTyping(ToolContext ctx) => ctx.Runtime.Paused
        ? "the person at the target paused remote commands."
        : IsOwnWindow(Win32.GetAncestor(Win32.GetForegroundWindow(), 2)) ? "agentprobe's own window on the target came to the front." : null;

    private static async Task<int> TypeAsync(ToolContext ctx, string[] args)
    {
        // Literal text only: a path like C:\temp\new.txt must arrive as typed, not as "C:<Tab>emp<Enter>ew.txt".
        var text = string.Join(' ', args);
        if (text.Length == 0) return ctx.Fail("type: no text.");
        if (KeyboardOnOwnWindow() is { } own) return ctx.Fail("type: " + own);
        var watch = await WatchAsync(false, Win32.GetForegroundWindow());
        var typed = 0;
        foreach (var ch in text)
        {
            // Before every character: the person at the PC may bring agentprobe's window forward (to pause) while
            // this is typing, and none of it may land there.
            if (StopTyping(ctx) is { } stop)
            {
                UiaTools.Drop(watch);
                ctx.Runtime.Audit.Write($"type stopped after {typed} of {text.Length} characters");
                return ctx.Fail($"type: stopped after {typed} of {text.Length} characters: {stop}");
            }

            typed++;
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
        await ReportAsync(ctx, watch);
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
        var noWatch = args.Contains("--no-watch");
        args = [.. args.Where(x => x != "--no-watch")];
        if (args.Length == 0) return ctx.Fail("key: which key? e.g. key ctrl+s");
        if (KeyboardOnOwnWindow() is { } own) return ctx.Fail("key: " + own);
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

        var watch = await WatchAsync(noWatch, Win32.GetForegroundWindow());
        var pressed = 0;
        foreach (var keys in combos)
        {
            if (StopTyping(ctx) is { } stop)
            {
                UiaTools.Drop(watch);
                ctx.Runtime.Audit.Write("key " + string.Join(' ', args.Take(pressed)) + $" (stopped after {pressed} of {combos.Count})");
                return ctx.Fail($"key: stopped after {pressed} of {combos.Count}: {stop}");
            }

            pressed++;
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
        await ReportAsync(ctx, watch);
        return 0;
    }

    private static async Task<int> FocusAsync(ToolContext ctx, string[] args)
    {
        if (args.Length == 0) return ctx.Fail("focus: which window? (title or HWND from `windows`)");
        var w = FindWindow(string.Join(' ', args));
        if (w == null) return ctx.Fail($"focus: no window matches '{string.Join(' ', args)}'.");
        if (IsOwnWindow(w.Hwnd)) return ctx.Fail("focus: that is agentprobe's own window on the target; it is not brought to the front remotely.");
        await Task.Yield();
        ctx.Out.WriteLine(BringToFront(w.Hwnd) ? $"focused \"{Format.Clip(w.Title, 60)}\"" : $"asked Windows to focus \"{Format.Clip(w.Title, 60)}\" but it refused (elevated window?)");
        return 0;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    private static partial int PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeTouchInjection(uint maxCount, uint feedbackMode);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InjectTouchInput(uint count, POINTER_TOUCH_INFO* contacts);
}

#pragma warning disable SA1300, SA1307, IDE1006 // Win32 names

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>POINTER_INFO of winuser.h (x64 layout, 96 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint pointerType;
    public uint pointerId;
    public uint frameId;
    public uint pointerFlags;
    public nint sourceDevice;
    public nint hwndTarget;
    public int ptPixelLocationX;
    public int ptPixelLocationY;
    public int ptHimetricLocationX;
    public int ptHimetricLocationY;
    public int ptPixelLocationRawX;
    public int ptPixelLocationRawY;
    public int ptHimetricLocationRawX;
    public int ptHimetricLocationRawY;
    public uint dwTime;
    public uint historyCount;
    public int inputData;
    public uint dwKeyStates;
    public ulong performanceCount;
    public int buttonChangeType;
}

/// <summary>POINTER_TOUCH_INFO of winuser.h (x64 layout, 144 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public uint touchFlags;
    public uint touchMask;
    public RECT rcContact;
    public RECT rcContactRaw;
    public uint orientation;
    public uint pressure;
}
