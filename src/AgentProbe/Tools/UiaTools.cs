using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// The window's controls as text, through Windows UI Automation: every button, field, list item and label with its
/// name, value and position. An agent can then act by NAME ("ui click Save") instead of guessing pixel coordinates
/// from a screenshot, which is cheaper to read and survives layout, DPI and theme changes.
/// <para>
/// COM is called through raw vtables (no runtime COM interop under NativeAOT). Slots follow UIAutomationClient.h.
/// </para>
/// </summary>
internal static unsafe partial class UiaTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("ui", "Desktop", "Read a window's controls as text and act on them by name (UI Automation)",
            """
            ui [window] [--grep REGEX] [--depth 12] [--max 300] [--all] [--class] [--pos] [--items] [--diff]
            ui --at @<x>,<y> [--depth 6]         what is at that point of the screen: the control there, the ones it
                                                 lies in (up to its window) and the ones inside it, each with its class
                 The control tree of a window (default: the foreground window; give part of its title or an HWND).
                 One line per control: type, "name", #automation-id, =value, state.
                 --pos adds each control's center on screen (@x,y), for click/touch/drag by coordinates; a
                 control with neither name nor id always has it, and so does `ui get`. You act by name, so the
                 positions are left out unless asked for: they are a quarter of the text.
                 A long run of list, tree or grid items is cut to its first 8 and any selected one, with a line
                 that says how many are not listed; --items lists them all.
                 --diff prints only what changed since the last `ui` of that window (controls that appeared,
                 went, or read differently), instead of the whole tree again.
                 State is what the control reports: (selected) on the tab, list item or radio button that is the
                 selected one, (checked) / (unchecked) for a check box or toggle button, (expanded) / (collapsed),
                 (disabled), and (maximized) / (minimized) / (normal) on the window's own line. Without such a
                 word the control is not selected, or does not report that kind of state at all.
                 What can scroll says how far it is scrolled: (scroll v=52.8%), with h= when it scrolls sideways.
                 A slider, progress bar or spinner that reports a range gives its value with it: =0.739 (0..1)
                 (WPF and most modern controls do; an old Win32 trackbar only gives a bare number, its percentage).
                 The window's own line also has its position and size, so a window that moved or resized shows.
                 --class adds each control's class name in [brackets] ([ListBoxItem], [HwndHost]: a control that
                 is a native child window inside a WPF window takes input differently from the rest).
                 Unnamed layout containers are folded away. --all also lists off-screen controls.
                 --grep searches the WHOLE tree, off-screen controls included (to depth 40 unless --depth says
                 otherwise), and prints the matching lines; --max then limits how many of them are printed, and
                 the total is stated. A search that could not cover everything says INCOMPLETE.
                 Every action below answers with what it did AND what changed on the screen in the moment after:
                 a window or menu that opened (with its text and buttons), one that closed, a control of this
                 window that reads differently, the program ending. "no change seen" is an answer too. So there
                 is no need to follow an action with `windows`, `ui` or a screenshot to see its effect; what
                 happens later than about a second is for `wait --ui` / `expect`. --no-watch leaves that out.
            ui click <name|#id> [--window W] [--nth N] [--mouse]   press a button / select an item (falls back to a mouse click on it)
            ui rclick <name|#id> [--window W]    right-click it (opens its context menu; `windows` then shows a popup)
            ui dblclick <name|#id> [--window W]  double-click it
            ui tap <name|#id> [--window W]       tap it by touch, as a finger on a touch screen would (see `touch`)
            ui dtap <name|#id> [--window W]      tap it twice by touch (a double tap)
            ui set <name|#id> <text> [--window W]  put text into an edit field
            ui get <name|#id> [--window W]       one control's line: value and state (compare before and after an action)
              Works with WPF, WinForms, Win32, UWP/WinUI and most browsers/Electron apps. Read the tree once, then act
              by name; take a screenshot only when you need to judge how something looks.
              ui click uses the control's own action where it has one (it works even when the control is
              covered or scrolled away) and the mouse otherwise; --mouse makes it the mouse in any case. A button
              that can only be toggled (a WPF ToggleButton) is clicked with the mouse: its own action would flip
              its state without the app's click handler running (when the mouse cannot reach it, it is toggled
              and the answer says so). After toggling, the answer gives the new state.
              ui set puts the text into the edit field of a combo box when the name is the combo box's, reads
              the value back, and fails when the control still reads as before; --nth counts the edit fields,
              combo boxes, documents and spinners of that name when there are any. An action that finds no such
              control names the window it looked in.
              When several controls have that name the first is taken and the answer says how many there are:
              --nth 2 takes the second. They count in the order `ui` lists them, panes and groups after the
              controls (a tab and the page it shows have the same name: the tab is the first). --nth works
              with every action.
              An action that needs the mouse or touch (rclick, dblclick, tap, dtap, and click on a control without an
              action of its own) is not done when another window covers the control: the answer names it.
            """, UiAsync) { Quiet = true },
    ];

    private static readonly Guid ClsidAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IidInvoke = new("FB377FBE-8EA6-46D5-9C73-6499642D3059");
    private static readonly Guid IidValue = new("A94CD8B1-0844-4CD6-9D2D-640537AB39E9");
    private static readonly Guid IidSelectionItem = new("A8EFA66A-0FDA-421A-9194-38021F3578EA");
    private static readonly Guid IidToggle = new("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70");

    private static readonly string[] TypeNames =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu", "MenuBar", "MenuItem",
        "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text", "ToolBar", "ToolTip", "Tree",
        "TreeItem", "Custom", "Group", "Thumb", "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem",
        "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
    ];

    private sealed record Node(nint Element, int Type, string Name, string Id, string Value, bool Enabled, bool Offscreen, RECT Rect, int Depth);

    // ---- raw COM helpers

    private static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)p)[2])(p);
    }

    private static string Bstr(nint obj, int slot)
    {
        nint bstr = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(void***)obj)[slot])(obj, &bstr) < 0 || bstr == 0) return "";
        var s = new string((char*)bstr, 0, (int)SysStringLen(bstr));
        SysFreeString(bstr);
        return s;
    }

    private static int Int(nint obj, int slot)
    {
        var value = 0;
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)(*(void***)obj)[slot])(obj, &value);
        return value;
    }

    private static nint Out(nint obj, int slot, nint arg)
    {
        nint result = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)obj)[slot])(obj, arg, &result);
        return result;
    }

    private static nint Pattern(nint element, int patternId, Guid iid)
    {
        nint result = 0;
        ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)(*(void***)element)[14])(element, patternId, &iid, &result); // GetCurrentPatternAs
        return result;
    }

    /// <summary>The control's current text (ValuePattern), e.g. what is typed in an edit box; "" also when it cannot be read.</summary>
    private static string ValueOf(nint element) => ReadValue(element) ?? "";

    /// <summary>The control's current text, or null when the control did not give one (which is not the same as an empty text).</summary>
    private static string? ReadValue(nint element)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        if (((delegate* unmanaged[Stdcall]<nint, int, int, byte*, int>)(*(void***)element)[11])(element, 30045, 1, variant) < 0) return null; // GetCurrentPropertyValueEx(Value.Value), "not supported" told apart
        var text = *(ushort*)variant == 8 ? (*(nint*)(variant + 8) != 0 ? new string((char*)*(nint*)(variant + 8)) : "") : null; // VT_BSTR
        VariantClear(variant);
        return text;
    }

    /// <summary>
    /// A property that only some controls have (is selected, toggle state...), or null when this control does not
    /// report it. Asked with "ignore the default value", so "not supported" is not mistaken for false or 0.
    /// </summary>
    private static int? StateProperty(nint element, int propertyId)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        if (((delegate* unmanaged[Stdcall]<nint, int, int, byte*, int>)(*(void***)element)[11])(element, propertyId, 1, variant) < 0) return null; // GetCurrentPropertyValueEx
        int? value = *(ushort*)variant switch
        {
            3 => *(int*)(variant + 8), // VT_I4
            11 => *(short*)(variant + 8) != 0 ? 1 : 0, // VT_BOOL
            _ => null, // VT_UNKNOWN: the "not supported" marker
        };
        VariantClear(variant);
        return value;
    }

    /// <summary>A number that only some controls have (a scroll position, a slider's value), or null when this control does not report it.</summary>
    private static double? NumberProperty(nint element, int propertyId)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        if (((delegate* unmanaged[Stdcall]<nint, int, int, byte*, int>)(*(void***)element)[11])(element, propertyId, 1, variant) < 0) return null; // GetCurrentPropertyValueEx
        double? value = *(ushort*)variant switch
        {
            5 => *(double*)(variant + 8), // VT_R8
            4 => *(float*)(variant + 8), // VT_R4
            3 => *(int*)(variant + 8), // VT_I4
            _ => null, // VT_UNKNOWN: the "not supported" marker
        };
        VariantClear(variant);
        return value is { } v && double.IsFinite(v) ? v : null;
    }

    private static string Number(double v) => v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"0.739 (0..1)" for a control with a value in a range (slider, progress bar, spinner, scroll bar); "" for one without.</summary>
    private static string RangeOf(Node n)
    {
        if (n.Type is not (50015 or 50012 or 50016 or 50014)) return "";
        if (NumberProperty(n.Element, 30047) is not { } value) return ""; // RangeValue.Value
        var (min, max) = (NumberProperty(n.Element, 30049), NumberProperty(n.Element, 30050)); // Minimum, Maximum
        return min != null && max != null ? $"{Number(value)} ({Number(min.Value)}..{Number(max.Value)})" : Number(value);
    }

    [ThreadStatic]
    private static bool _withClass; // --class, --at and `ui get`: lines carry the control's class name

    [ThreadStatic]
    private static bool _mouseOnly; // ui click --mouse: not the control's own action

    [ThreadStatic]
    private static bool _withPos; // --pos, --at and `ui get`: lines carry the control's center on screen

    [ThreadStatic]
    private static bool _walkTimedOut; // the last walk on this thread stopped at its deadline

    /// <summary>" (selected)", " (checked)", " (expanded)"... as the control reports them; "" for one that reports none.</summary>
    private static string StateOf(Node n)
    {
        var sb = new StringBuilder();

        // How far something that scrolls is scrolled, in each direction it can scroll in (asked first: a
        // direction that cannot scroll answers 0 or -1 for its position, depending on the framework).
        if (n.Type is 50008 or 50023 or 50028 or 50036 or 50033 or 50030 or 50025 or 50026 or 50004) // List, Tree, DataGrid, Table, Pane, Document, Custom, Group, Edit
        {
            var parts = new List<string>();
            if (StateProperty(n.Element, 30058) == 1 && NumberProperty(n.Element, 30055) is >= 0 and var v) parts.Add($"v={Number(v)}%"); // Scroll.VerticallyScrollable, VerticalScrollPercent
            if (StateProperty(n.Element, 30057) == 1 && NumberProperty(n.Element, 30053) is >= 0 and var h) parts.Add($"h={Number(h)}%"); // Scroll.HorizontallyScrollable, HorizontalScrollPercent
            if (parts.Count > 0) sb.Append(" (scroll ").Append(string.Join(' ', parts)).Append(')');
        }

        // Controls that never have such a state are not asked: every question is a call into the other process.
        if (n.Type is 50020 or 50006 or 50033 or 50038 or 50027 or 50014 or 50037 or 50022 or 50017 or 50012 or 50004 or 50030) return sb.ToString();
        if (n.Type == 50032)
        {
            sb.Append(StateProperty(n.Element, 30075) switch { 0 => " (normal)", 1 => " (maximized)", 2 => " (minimized)", _ => "" }); // Window.WindowVisualState
            if (n.Rect.Width > 0) sb.Append($" {n.Rect.Left},{n.Rect.Top} {n.Rect.Width}x{n.Rect.Height}"); // where it is and how big: a window that moved shows
            return sb.ToString();
        }

        if (StateProperty(n.Element, 30079) == 1) sb.Append(" (selected)"); // SelectionItem.IsSelected
        sb.Append(StateProperty(n.Element, 30086) switch { 0 => " (unchecked)", 1 => " (checked)", 2 => " (indeterminate)", _ => "" }); // Toggle.ToggleState
        sb.Append(StateProperty(n.Element, 30070) switch { 0 => " (collapsed)", 1 => " (expanded)", 2 => " (partially expanded)", _ => "" }); // ExpandCollapse state; 3 = a leaf
        return sb.ToString();
    }

    private static Node Describe(nint element, int depth)
    {
        RECT rect = default;
        ((delegate* unmanaged[Stdcall]<nint, RECT*, int>)(*(void***)element)[43])(element, &rect); // get_CurrentBoundingRectangle
        var type = Int(element, 21);
        var value = type is 50004 or 50003 or 50015 or 50016 or 50030 ? ValueOf(element) : ""; // Edit, ComboBox, Slider, Spinner, Document
        return new Node(element, type, Bstr(element, 23), Bstr(element, 29), value, Int(element, 28) != 0, Int(element, 38) != 0, rect, depth);
    }

    private static string TypeName(int type) => type is >= 50000 and < 50041 ? TypeNames[type - 50000] : "Control";

    // ---- the work, on its own COM thread with a time limit (a hung app blocks UI Automation calls)

    private static (int code, string text) OnComThread(Func<nint, (int, string)> work, int seconds = 20)
    {
        (int, string) result = (1, "ui: no result");
        var thread = new Thread(() =>
        {
            CoInitializeEx(0, 0); // multithreaded apartment
            nint automation = 0;
            try
            {
                var clsid = ClsidAutomation;
                var iid = IidAutomation;
                var hr = CoCreateInstance(&clsid, 0, 1, &iid, &automation);
                result = hr < 0 || automation == 0 ? (1, $"ui: UI Automation is not available (0x{hr:X8}).") : work(automation);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = (1, "ui: " + ex.Message);
            }
            finally
            {
                Release(automation);
                CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "uia",
        };
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(seconds))
            ? result
            : (1, $"ui: not finished within {seconds}s; whatever was read so far is not shown. Either the application's UI thread is busy or hung (`windows`, `threads <pid>`), " +
                  "or the window has more controls than can be read in that time (a smaller --depth, or the HWND of a part of it).");
    }

    private static nint RootFor(nint automation, string? window, out string title, out string error, out nint hwnd)
    {
        error = "";
        title = "";
        hwnd = 0;
        if (string.IsNullOrEmpty(window))
        {
            hwnd = Win32.GetForegroundWindow();
            if (hwnd == 0)
            {
                error = "ui: there is no foreground window. Name one: ui <part of its title>   (see `windows`).";
                return 0;
            }
        }
        else if (window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(window[2..], System.Globalization.NumberStyles.HexNumber, null, out var h))
        {
            hwnd = (nint)h;
        }
        else
        {
            var all = UiTools.EnumerateWindows().Where(w => w.Visible && w.Title.Length > 0 && w.Pid != Environment.ProcessId).ToList();
            var match = all.FirstOrDefault(w => w.Title.Equals(window, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(w => w.Title.Contains(window, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                error = $"ui: no window matches '{window}' (see `windows`).";
                return 0;
            }

            hwnd = match.Hwnd;
        }

        if (UiTools.IsOwnWindow(Win32.GetAncestor(hwnd, 2) is var top && top != 0 ? top : hwnd))
        {
            error = "ui: that is agentprobe's own window on the target; its controls are not read or driven remotely. Name the app's window: ui <part of its title>   (see `windows`).";
            return 0;
        }

        var buf = stackalloc char[300];
        title = new string(buf, 0, Math.Max(0, Win32.GetWindowText(hwnd, buf, 300)));
        var root = Out(automation, 6, hwnd); // ElementFromHandle
        if (root == 0) error = "ui: could not attach to that window (elevated window? run agentprobe elevated on the target).";
        return root;
    }

    private static nint ControlViewWalker(nint automation)
    {
        nint walker = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(void***)automation)[14])(automation, &walker); // get_ControlViewWalker
        return walker;
    }

    /// <summary>Depth-first walk of the control view. The caller releases every returned element.</summary>
    /// <param name="deadline">A <see cref="System.Diagnostics.Stopwatch"/> timestamp after which the walk stops where it is (0: none); <see cref="_walkTimedOut"/> then says so.</param>
    private static List<Node> Walk(nint automation, nint root, int maxDepth, int maxNodes, bool includeOffscreen, out bool deeper, long deadline = 0)
    {
        deeper = false;
        _walkTimedOut = false;
        var cutByDepth = false;
        var nodes = new List<Node>();
        var walker = ControlViewWalker(automation);
        if (walker == 0) return nodes;

        void Visit(nint element, int depth)
        {
            var node = Describe(element, depth);
            nodes.Add(node);
            if (nodes.Count >= maxNodes) return;
            if (deadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
            {
                _walkTimedOut = true;
                maxNodes = nodes.Count; // ends every loop above this one too
                return;
            }

            if (depth >= maxDepth)
            {
                var below = Out(walker, 4, element);
                if (below != 0)
                {
                    Release(below);
                    cutByDepth = true; // controls exist below the depth limit: the caller must be told
                }

                return;
            }

            if (node.Offscreen && !includeOffscreen && depth > 0) return; // nothing useful below an off-screen container
            var child = Out(walker, 4, element); // GetFirstChildElement
            while (child != 0 && nodes.Count < maxNodes)
            {
                Visit(child, depth + 1);
                child = Out(walker, 6, child); // GetNextSiblingElement
            }

            if (child != 0) Release(child);
        }

        Visit(root, 0);
        Release(walker);
        deeper = cutByDepth;
        return nodes;
    }

    /// <summary>What a control's line says about the control itself: type, name, id, value and state. No class, no position.</summary>
    private static string Core(Node n)
    {
        var sb = new StringBuilder();
        sb.Append(TypeName(n.Type));
        if (n.Name.Length > 0) sb.Append(" \"").Append(Format.Clip(n.Name, 70)).Append('"');
        if (n.Id.Length > 0 && !n.Id.All(char.IsDigit)) sb.Append(" #").Append(Format.Clip(n.Id, 40));
        if (RangeOf(n) is { Length: > 0 } range) sb.Append(" =").Append(range);
        else if (n.Value.Length > 0) sb.Append(" =").Append(Format.Clip(n.Value, 60));
        sb.Append(StateOf(n));
        if (!n.Enabled) sb.Append(" (disabled)");
        if (n.Offscreen) sb.Append(" (off-screen)");
        return sb.ToString();
    }

    /// <summary>A control that cannot be named in `ui click`: it has neither a name nor an id of its own.</summary>
    private static bool Nameless(Node n) => n.Name.Length == 0 && (n.Id.Length == 0 || n.Id.All(char.IsDigit));

    /// <summary>
    /// The line as it is printed: the class when asked for, and the position when asked for (--pos, `ui get`,
    /// --at) or when the position is the only handle the control has.
    /// </summary>
    private static string Line(Node n, int indent, string? core = null)
    {
        core ??= Core(n);
        var sb = new StringBuilder();
        sb.Append(' ', indent * 2);
        var type = TypeName(n.Type);
        if (_withClass && Bstr(n.Element, 30) is { Length: > 0 } cls) sb.Append(type).Append(" [").Append(Format.Clip(cls, 40)).Append(']').Append(core, type.Length, core.Length - type.Length); // get_CurrentClassName
        else sb.Append(core);
        if ((_withPos || Nameless(n)) && !n.Offscreen && n.Rect.Width > 0) sb.Append(" @").Append((n.Rect.Left + n.Rect.Right) / 2).Append(',').Append((n.Rect.Top + n.Rect.Bottom) / 2);
        return sb.ToString();
    }

    /// <summary>An unnamed layout container, text or image: folded away in a listing, its children move up a level.</summary>
    private static bool Structural(Node n) =>
        n.Depth > 0 && n.Name.Length == 0 && n.Value.Length == 0 && (n.Id.Length == 0 || n.Id.All(char.IsDigit)) && n.Type is 50033 or 50026 or 50025 or 50020 or 50006 or 50038 or 50027;

    /// <summary>
    /// Every control the name stands for, by the first rule that finds any: the exact name, the exact id, part of
    /// the name (each among the controls on screen), then part of the name anywhere. They come in tree order, but
    /// a container (pane, group, window) after the controls: a tab and the page it shows have the same name, and
    /// "click that name" means the tab.
    /// </summary>
    private static List<Node> FindAll(List<Node> nodes, string spec)
    {
        if (spec.StartsWith('#')) return nodes.Where(n => n.Id.Equals(spec[1..], StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = nodes.Where(n => !n.Offscreen).ToList();
        foreach (var rule in new Func<Node, bool>[]
                 {
                     n => n.Name.Equals(spec, StringComparison.OrdinalIgnoreCase),
                     n => n.Id.Equals(spec, StringComparison.OrdinalIgnoreCase),
                     n => n.Name.Contains(spec, StringComparison.OrdinalIgnoreCase),
                 })
        {
            var found = candidates.Where(rule).ToList();
            if (found.Count > 0) return ContainersLast(found);
        }

        return ContainersLast(nodes.Where(n => n.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList());

        static List<Node> ContainersLast(List<Node> found) => [.. found.OrderBy(n => n.Type is 50033 or 50026 or 50032 ? 1 : 0)]; // Pane, Group, Window; the sort is stable
    }

    private static Task<int> UiAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all", "class", "mouse", "pos", "items", "diff", "no-watch");
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        var isAction = verb is "click" or "rclick" or "dblclick" or "tap" or "dtap" or "set" or "get";
        var withClass = a.Flag("class") || verb == "get" || a.Has("at");
        var withPos = a.Flag("pos") || verb == "get";
        if (a.Has("at"))
        {
            var point = (a.Get("at") ?? "").TrimStart('@').Split(',');
            if (point.Length != 2 || !int.TryParse(point[0], out var ax) || !int.TryParse(point[1], out var ay))
                return Task.FromResult(ctx.Fail("ui --at: which point? Screen coordinates as `ui --pos` prints them: ui --at @342,1156"));
            var (atCode, atText) = OnComThread(automation =>
            {
                _withClass = true;
                _withPos = true;
                return At(automation, ax, ay, Math.Clamp(a.GetInt("depth", 6), 0, 40));
            });
            (atCode == 0 ? ctx.Out : ctx.Err).WriteLine(atText.TrimEnd());
            return Task.FromResult(atCode);
        }

        var window = isAction ? a.Get("window") : (a.Positionals.Count > 0 ? string.Join(' ', a.Positionals) : null);
        if (isAction && a.Positionals.Count < 2) return Task.FromResult(ctx.Fail($"ui {verb}: which control? Give its name or #id (see `ui`)."));
        if (verb == "set" && a.Positionals.Count < 3) return Task.FromResult(ctx.Fail("ui set: usage: ui set <name|#id> <text>"));

        Regex? grep = null;
        if (a.Get("grep") is { } pattern)
        {
            try
            {
                grep = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("ui: bad --grep pattern: " + ex.Message));
            }
        }

        // A search looks at the whole tree: its limit is on the lines printed, not on the controls looked at.
        const int searchLimit = 20000;
        // A listing prints at most --max lines, but looks at four times as many controls: a long list that is
        // folded into a few lines must not keep the rest of the window from being read.
        var max = Math.Max(1, a.GetInt("max", 300));
        var walkLimit = isAction ? 4000 : grep != null ? searchLimit : a.Flag("items") ? max : max * 4;
        var nth = a.Has("nth") ? a.GetInt("nth", 0) : (int?)null;
        if (nth is < 1) return Task.FromResult(ctx.Fail("ui: --nth wants a number from 1."));
        if (a.Flag("diff") && (isAction || grep != null)) return Task.FromResult(ctx.Fail("ui --diff compares two listings of a window: it goes with `ui [window]`, not with --grep or an action (an action says what changed by itself)."));
        var (code, text) = OnComThread(automation =>
        {
            _withClass = withClass;
            _withPos = withPos;
            var root = RootFor(automation, window, out var title, out var error, out var hwnd);
            if (root == 0) return (2, error);
            var nodes = Walk(automation, root, a.GetInt("depth", isAction || grep != null ? 40 : 12), walkLimit, a.Flag("all") || isAction || grep != null, out var deeper);
            try
            {
                _mouseOnly = a.Flag("mouse");
                if (isAction) return Act(ctx, verb, a.Positionals, nodes, nth, hwnd, title, nodes.Count >= walkLimit ? walkLimit : 0, automation, !a.Flag("no-watch"));
                var entries = grep == null ? new List<UiEntry>() : null;
                var listing = Render(nodes, title, grep, a.Flag("all"), max, walkLimit, deeper, a.Flag("items"), entries);
                if (entries == null) return (0, listing);
                var earlier = RememberListing(hwnd, entries);
                if (!a.Flag("diff")) return (0, listing);
                return (0, earlier == null
                    ? $"[--diff: this is the first `ui` of that window in this session, so there is nothing to compare with; the whole listing follows]\n{listing}"
                    : DiffListing(title, earlier.Value.entries, entries, DateTime.UtcNow - earlier.Value.at));
            }
            finally
            {
                foreach (var n in nodes) Release(n.Element);
            }
        }, grep != null ? 60 : isAction ? 30 : 20);

        (code == 0 ? ctx.Out : ctx.Err).WriteLine(text.TrimEnd());
        return Task.FromResult(code);
    }

    /// <summary>
    /// <c>ui --at</c>: the control at a point of the screen, the controls it lies in (outermost first, up to its
    /// window) and what is inside it. For "what did that tap land on?".
    /// </summary>
    private static (int, string) At(nint automation, int x, int y, int depth)
    {
        if (UiTools.IsOwnWindow(UiTools.RootAt(x, y)))
            return (2, $"ui --at: {x},{y} is on agentprobe's own window on the target; its controls are not read remotely.");
        nint element = 0;
        var found = ((delegate* unmanaged[Stdcall]<nint, long, nint*, int>)(*(void***)automation)[7])(automation, (uint)x | ((long)y << 32), &element); // ElementFromPoint (the POINT goes by value)
        if (found < 0) return (1, $"ui --at: UI Automation could not look at {x},{y} (0x{found:X8}); that is not the same as nothing being there.");
        if (element == 0) return (1, $"ui --at: UI Automation finds no control at {x},{y}.");

        var walker = ControlViewWalker(automation);
        var above = new List<nint>();
        var inside = new List<Node>();
        try
        {
            // Upwards: its parents. The last one is the desktop itself, which is not listed (unless the chain
            // was cut before it got there, which the answer then says).
            const int most = 60;
            var cutAbove = false;
            if (walker != 0)
            {
                var up = Out(walker, 3, element); // GetParentElement
                while (up != 0 && above.Count < most)
                {
                    above.Add(up);
                    up = Out(walker, 3, up);
                }

                cutAbove = up != 0;
                Release(up);
                if (!cutAbove && above.Count > 0)
                {
                    Release(above[^1]);
                    above.RemoveAt(above.Count - 1);
                }
            }

            above.Reverse();
            var sb = new StringBuilder();
            for (var i = 0; i < above.Count; i++) sb.Append(Line(Describe(above[i], 0), i)).Append('\n');

            inside = Walk(automation, element, depth, 400, true, out var deeper);
            for (var i = 0; i < inside.Count; i++)
            {
                var line = Line(inside[i], above.Count + inside[i].Depth);
                sb.Append(i == 0 ? "> " + line.TrimStart().PadLeft(line.TrimStart().Length + Math.Max(0, (above.Count * 2) - 2)) : line).Append('\n');
            }

            var note = (cutAbove ? $" (only the nearest {most} of what it lies in)" : "") + (inside.Count >= 400 ? " (stopped at 400 controls inside it)" : deeper ? $" (more below depth {depth}: raise --depth)" : "");
            return (0, $"at {x},{y}: '>' marks the control there; above it the {above.Count} it lies in, below it the {Math.Max(0, inside.Count - 1)} inside it{note}; [class] after each type\n" + sb);
        }
        finally
        {
            foreach (var n in inside) Release(n.Element); // the walk hands back the element it started from as its first node
            if (inside.Count == 0) Release(element);
            foreach (var e in above) Release(e);
            Release(walker);
        }
    }

    /// <param name="entries">When given, filled with what the listing says about each control it lists (for <c>--diff</c>).</param>
    private static string Render(List<Node> nodes, string title, Regex? grep, bool all, int max, int walkLimit, bool deeper, bool items, List<UiEntry>? entries)
    {
        var sb = new StringBuilder();
        var shown = 0;
        var matched = 0;
        var folded = 0;
        var cutLines = false;

        // Fold away unnamed layout containers: their children move up one level, which keeps the tree short.
        var indentAt = new int[64];

        // A long run of items (list items, tree items, grid rows) under one parent: the first few are listed and
        // the rest counted. A run is tracked per depth; `parentAt` is the node last seen at each depth.
        const int foldAbove = 12, keep = 8;
        var parentAt = new int[64];
        var runParent = new int[64];
        var runType = new int[64];
        var runSeen = new int[64];
        var runFolded = new int[64];
        var runIndent = new int[64];
        var runLong = new bool[64];
        Array.Fill(runParent, -2);
        var open = new List<int>(); // depths that have a run with folded items
        var skipBelow = -1;

        void EndRun(int depth)
        {
            if (runFolded[depth] > 0)
            {
                sb.Append(' ', runIndent[depth] * 2).Append($"… {runFolded[depth]} more {TypeName(runType[depth])} here, not listed (--items lists them, --grep finds one)\n");
                folded += runFolded[depth];
                open.Remove(depth);
            }

            runFolded[depth] = 0;
            runParent[depth] = -2;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (skipBelow >= 0)
            {
                if (n.Depth > skipBelow) continue; // inside an item that is not listed
                skipBelow = -1;
            }

            var d = Math.Min(n.Depth, 63);
            parentAt[d] = i;
            var structural = Structural(n);
            var parentIndent = n.Depth == 0 ? -1 : indentAt[Math.Min(n.Depth - 1, 63)];
            var indent = structural ? parentIndent : parentIndent + 1;
            indentAt[d] = indent;
            for (var k = open.Count - 1; k >= 0; k--)
            {
                if (open[k] > d) EndRun(open[k]); // back above it: that run is over
            }

            if (structural || (n.Offscreen && !all && grep == null && n.Depth > 0)) continue; // a search covers off-screen controls too; they say "(off-screen)"
            if (grep == null && !items && n.Type is 50007 or 50029 or 50024) // ListItem, DataItem, TreeItem
            {
                var parent = d == 0 ? -1 : parentAt[d - 1];
                if (runParent[d] != parent || runType[d] != n.Type)
                {
                    EndRun(d);
                    (runParent[d], runType[d], runSeen[d], runIndent[d]) = (parent, n.Type, 0, Math.Max(0, indent));
                    var length = 0;
                    for (var j = i; j < nodes.Count && nodes[j].Depth >= n.Depth; j++)
                    {
                        if (nodes[j].Depth > n.Depth) continue;
                        if (nodes[j].Type != n.Type) break;
                        if (!nodes[j].Offscreen || all) length++;
                    }

                    runLong[d] = length > foldAbove;
                }

                runSeen[d]++;
                if (runLong[d] && runSeen[d] > keep && StateProperty(n.Element, 30079) != 1) // SelectionItem.IsSelected: the selected one is always listed
                {
                    if (runFolded[d]++ == 0) open.Add(d);
                    skipBelow = n.Depth;
                    continue;
                }
            }
            else
            {
                EndRun(d);
            }

            if (grep == null && shown >= max)
            {
                cutLines = true; // looked at, not printed: the first line says so
                continue;
            }

            var core = Core(n);
            var line = Line(n, Math.Max(0, indent), core);
            if (grep != null && !grep.IsMatch(line)) continue;
            matched++;
            if (grep != null && shown >= max) continue; // counted, not printed
            entries?.Add(new UiEntry(KeyOf(n), core));
            sb.Append(grep != null ? line.TrimStart() : line).Append('\n');
            shown++;
        }

        for (var k = open.Count - 1; k >= 0; k--) EndRun(open[k]);
        if (folded > 0) entries?.Add(new UiEntry("(items not listed)", $"{folded} list, tree or grid items that are not listed"));

        // What was left out is said in the first line: a search that stopped early is not "no match".
        var cut = nodes.Count >= walkLimit;
        var note = grep == null
            ? (cut ? $" (stopped at {walkLimit} controls: narrow with --grep, or raise --max)" : cutLines ? $" (the first {max} lines; the window has more: narrow with --grep, or raise --max)" : "")
            : (matched > shown ? $" of {matched} that match (--max prints more)" : "") +
              (cut ? $" (INCOMPLETE: the search stopped after {walkLimit} controls, there may be more matches; name a smaller window)" : "");
        if (deeper) note += grep == null ? " (more controls exist below the depth limit: raise --depth)" : " (controls below the depth limit were not searched: raise --depth)";
        var what = grep == null ? "controls" : matched > shown ? "matching controls shown" : "controls match";
        var positions = _withPos ? "'@x,y' is each control's center on screen" : "--pos adds positions (@x,y)";
        return $"window \"{Format.Clip(title, 70)}\": {shown} {what}{note}; {positions}\n" +
               (shown > 0 ? sb.ToString() : grep != null && (cut || deeper) ? "[no match among the controls that were searched; see the first line for what was not]\n" : "[no control matches]\n");
    }

    /// <param name="cutAt">Not 0 when only the first that many controls of the window were read.</param>
    /// <param name="watch">Also say what changed on the screen in the moment after the action.</param>
    private static (int, string) Act(ToolContext ctx, string verb, List<string> positionals, List<Node> nodes, int? nth, nint hwnd, string title, int cutAt, nint automation, bool watch)
    {
        var (code, text, node, several) = ActOn(ctx, verb, positionals, nodes, nth, hwnd, title, watch && verb != "get" ? automation : 0, out var watching);
        if (code != 0 && watching != null && node != null && watching.Hwnd != 0 && !Win32.IsWindowVisible(watching.Hwnd))
        {
            // The window went away under the action (a button that closes it, or ends the program): the control
            // could not answer any more, which is not the same as the action not having run.
            code = 0;
            text = $"acted on {TypeName(node.Type)} \"{Format.Clip(node.Name.Length > 0 ? node.Name : node.Id, 50)}\": its window went away during the action, so the control gave no proper answer; the action did run";
        }

        if (code == 0 && watching != null)
        {
            if (verb == "set" && node != null) watching.SkipKey = KeyOf(node); // its new value is in the answer already
            text += "\n" + Changes(automation, watching);
        }

        if (cutAt > 0 && (code != 0 || several > 1))
            text += $"\n[only the first {cutAt} controls of the window were read: there may be more with that name]";

        // Several controls have that name: say so, and where the one that was taken is, so a wrong pick shows.
        if (several > 1 && node != null && nth == null)
        {
            var where = node.Offscreen || node.Rect.Width <= 0 ? "off-screen" : $"@{(node.Rect.Left + node.Rect.Right) / 2},{(node.Rect.Top + node.Rect.Bottom) / 2}";
            var first = $"[{several} controls have that name; this is the first ({where}). --nth 2 takes the next]";
            var lineEnd = text.IndexOf('\n'); // right after what was done, before what changed
            text = lineEnd < 0 ? text + "\n" + first : text[..lineEnd] + "\n" + first + text[lineEnd..];
        }

        return (code, text);
    }

    /// <param name="watchWith">The automation object when what the action changes is to be reported, else 0.</param>
    /// <param name="watching">What the screen looked like right before the action (null when not asked for, or when nothing was done).</param>
    private static (int code, string text, Node? node, int several) ActOn(ToolContext ctx, string verb, List<string> positionals, List<Node> nodes, int? nth, nint hwnd, string title, nint watchWith, out UiWatch? watching)
    {
        watching = null;
        var (code, text, node, several) = (0, "", (Node?)null, 0);
        var spec = positionals[1];
        var all = FindAll(nodes, spec);

        // The window that was looked in is part of the answer: without --window it is whatever was in front,
        // which after a command that started something can be the desktop itself.
        var where = title.Length > 0 ? $"window \"{Format.Clip(title, 60)}\"" : "that window (it has no title)";
        if (nodes.Count > 0 && Bstr(nodes[0].Element, 30) is "Progman" or "WorkerW") where += ", which is the desktop itself";
        if (all.Count == 0)
            return (1, $"ui {verb}: no control named '{spec}' in {where}. `ui --grep {spec}` shows what is there; name another window with --window <title> (see `windows`).", null, 0);

        // Text goes into a control that takes text: a label of the same name is not a choice for `set`.
        if (verb == "set" && all.Where(n => n.Type is 50004 or 50003 or 50030 or 50016).ToList() is { Count: > 0 } takeText) all = takeText; // Edit, ComboBox, Document, Spinner
        several = all.Count;
        if (nth > all.Count) return (1, $"ui {verb}: --nth {nth}, but only {all.Count} control{(all.Count == 1 ? " has" : "s have")} the name '{spec}' in {where}{(verb == "set" ? " (counting edit fields and combo boxes)" : "")}.", null, all.Count);
        node = all[(nth ?? 1) - 1];

        // The text of a combo box lives in the edit field inside it.
        if (verb == "set" && node.Type == 50003)
        {
            var at = nodes.IndexOf(node);
            for (var i = at + 1; at >= 0 && i < nodes.Count && nodes[i].Depth > node.Depth; i++)
            {
                if (nodes[i].Type != 50004) continue;
                node = nodes[i];
                break;
            }
        }

        if (watchWith != 0) watching = Before(watchWith, hwnd);
        (code, text) = Do(ctx, verb, positionals, node, hwnd, title);
        return (code, text, node, several);
    }

    /// <summary>
    /// The mouse or a finger at a control's centre only reaches the control when nothing lies in front of it.
    /// Null when the way is clear, else the refusal to return (nothing is done then).
    /// </summary>
    private static string? Covered(string verb, string label, int x, int y, nint hwnd, string title) =>
        UiTools.WrongTarget(x, y, hwnd, $"`focus \"{Format.Clip(title, 60)}\"` brings its window to the front.") is { } wrong
            ? $"ui {verb}: {label} is at {x},{y}, but {wrong}"
            : null;

    private static (int, string) Do(ToolContext ctx, string verb, List<string> positionals, Node node, nint hwnd, string title)
    {
        var label = $"{TypeName(node.Type)} \"{Format.Clip(node.Name.Length > 0 ? node.Name : node.Id, 50)}\"";

        switch (verb)
        {
            case "get":
                return (0, Line(node, 0));

            case "tap" or "dtap":
            {
                // Touch has no action of its own in UI Automation either: a finger does it, at the control's centre.
                if (node.Offscreen || node.Rect.Width <= 0) return (1, $"ui {verb}: {label} is not visible on screen.");
                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                if (Covered(verb, label, x, y, hwnd, title) is { } covered) return (1, covered);
                var blocked = UiTools.DisabledWindowAt(x, y);
                ctx.Runtime.Audit.Write($"ui {verb} {label} at {x},{y}");
                if (UiTools.TouchTaps(x, y, verb == "dtap" ? 2 : 1, ctx.Ct) is { } failure) return (1, $"ui {verb}: {label} at {x},{y}: {failure}");
                return (0, $"{(verb == "dtap" ? "tapped twice" : "tapped")} {label} by touch at {x},{y}" + (blocked != null ? "\n" + blocked.Replace("The click did nothing there", "The touch did nothing there") : ""));
            }

            case "set":
            {
                var text = string.Join(' ', positionals.Skip(2));
                var value = Pattern(node.Element, 10002, IidValue);
                if (value == 0) return (1, $"ui set: {label} does not accept text. Click it and use `type` instead.");
                var before = ReadValue(node.Element);
                int hr;
                fixed (char* p = text)
                {
                    var bstr = SysAllocString(p);
                    hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)value)[3])(value, bstr); // SetValue
                    SysFreeString(bstr);
                }

                Release(value);
                if (hr < 0) return (1, $"ui set: {label} refused the text (0x{hr:X8}; read-only?).");
                ctx.Runtime.Audit.Write($"ui set {label} ({text.Length} characters)");

                // A control can accept the call and keep its value (a combo box that only takes entries of its
                // list does). What it holds now is read back, for up to a second: some programs take the text in
                // a moment later. A password field does not give its value away, and some controls give none.
                if (Int(node.Element, 35) != 0) return (0, $"set {label} ({text.Length} characters; not checked: a password field does not show what it holds)"); // get_CurrentIsPassword
                static string Plain(string v) => v.Replace("\r", "").TrimEnd();
                var now = ReadValue(node.Element);
                for (var i = 0; i < 5 && now != null && Plain(now) != Plain(text) && now == before; i++)
                {
                    Thread.Sleep(200);
                    now = ReadValue(node.Element);
                }

                if (now == null) return (0, $"set {label} ({text.Length} characters); the control does not give its value back, so that it took the text is not checked");
                if (Plain(now) == Plain(text)) return (0, $"set {label}: now =\"{Format.Clip(now, 60)}\"");
                return now == before
                    ? (1, $"ui set: {label} took the call and still reads =\"{Format.Clip(now, 60)}\", as before: the text does not seem to have been taken (a control that only accepts its own entries? the wrong one of several with that name: --nth? or it shows the same value in another form). `ui get` shows it; if the text is not in, click the control and use `type`.")
                    : (0, $"set {label}; it now reads =\"{Format.Clip(now, 60)}\", which is not exactly the text given (the control changes what it is given)");
            }

            case "rclick" or "dblclick":
            {
                // These have no action of their own in UI Automation: the mouse does them, at the control's centre.
                if (node.Offscreen || node.Rect.Width <= 0) return (1, $"ui {verb}: {label} is not visible on screen.");
                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                if (Covered(verb, label, x, y, hwnd, title) is { } covered) return (1, covered);
                ctx.Runtime.Audit.Write($"ui {verb} {label} at {x},{y}");
                var blocked = UiTools.DisabledWindowAt(x, y); // these two go through the mouse, which a disabled window ignores
                UiTools.Mouse(x, y, 0x0001);
                Thread.Sleep(40);
                var (down, up, times) = verb == "rclick" ? (0x0008u, 0x0010u, 1) : (0x0002u, 0x0004u, 2);
                for (var i = 0; i < times; i++)
                {
                    UiTools.Mouse(x, y, down);
                    UiTools.Mouse(x, y, up);
                    Thread.Sleep(60);
                }

                return (0, $"{(verb == "rclick" ? "right-clicked" : "double-clicked")} {label} at {x},{y}" + (blocked != null ? "\n" + blocked : ""));
            }

            default:
            {
                if (!node.Enabled) return (1, $"ui click: {label} is disabled.");
                var logged = false;
                var refused = "";
                var why = _mouseOnly ? "--mouse" : "the control has no action of its own that worked";
                var hadInvoke = false;
                var toggleOnly = false; // a button whose only action is "toggle": the mouse is tried first

                // Toggle, and say which way it went ("toggled" alone does not).
                (int, string)? Toggle(string note)
                {
                    var t = Pattern(node.Element, 10015, IidToggle);
                    if (t == 0) return null;
                    if (!logged) ctx.Runtime.Audit.Write($"ui click {label}");
                    logged = true;
                    var result = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)t)[3])(t);
                    Release(t);
                    if (result < 0)
                    {
                        refused = $" (its own action answered 0x{result:X8} first, and may have had an effect)";
                        return null;
                    }

                    var state = StateProperty(node.Element, 30086) switch { 0 => ": now unchecked", 1 => ": now checked", 2 => ": now indeterminate", _ => "" };
                    return (0, $"toggled {label}{state}{note}");
                }

                // Prefer the control's own action; it works even when the window is covered or scrolled.
                foreach (var (patternId, iid, what) in new[] { (10000, IidInvoke, "invoked"), (10010, IidSelectionItem, "selected"), (10015, IidToggle, "toggled") })
                {
                    if (_mouseOnly) break;
                    if (patternId == 10015)
                    {
                        // A button that offers "toggle" and no "invoke" (a WPF ToggleButton): toggling flips its
                        // checked state and does NOT run the click handler the app hangs on it. A click with the
                        // mouse does both, so that comes first; toggling is what is left when the mouse cannot reach.
                        if (node.Type is 50000 or 50031 && !hadInvoke)
                        {
                            var only = Pattern(node.Element, patternId, iid);
                            if (only == 0) break;
                            Release(only);
                            toggleOnly = true;
                            why = "a button that can only be toggled: its own action would flip its state without the app's click handler running";
                            break;
                        }

                        if (Toggle("") is { } toggled) return toggled;
                        continue;
                    }

                    var p = Pattern(node.Element, patternId, iid);
                    if (p == 0) continue;
                    if (patternId == 10000) hadInvoke = true;
                    if (!logged) ctx.Runtime.Audit.Write($"ui click {label}"); // before the call: a button that opens a dialog may not return for a while
                    logged = true;
                    var hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)p)[3])(p); // Invoke / Select
                    Release(p);
                    if (hr >= 0) return (0, $"{what} {label}");
                    refused = $" (its own action answered 0x{hr:X8} first, and may have had an effect)";
                }

                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                var unreachable = node.Offscreen || node.Rect.Width <= 0 ? $"ui click: {label} is not visible on screen, and the mouse is the only way here ({why}){refused}."
                    : Covered(verb, label, x, y, hwnd, title) is { } covered ? covered + refused
                    : null;
                if (unreachable != null)
                {
                    // The mouse cannot get to it. A toggle-only button can still be toggled, and the answer says
                    // what that does not do.
                    if (toggleOnly && !_mouseOnly && Toggle(" (the mouse could not reach it, so it was only toggled: the app's click handler may not have run)") is { } instead) return instead;
                    return (1, unreachable);
                }

                // No action of its own: the mouse does it, and a disabled window ignores the mouse.
                ctx.Runtime.Audit.Write($"ui click {label} at {x},{y} (mouse)");
                var blocked = UiTools.DisabledWindowAt(x, y);
                UiTools.Mouse(x, y, 0x0001);
                Thread.Sleep(40);
                UiTools.Mouse(x, y, 0x0002);
                UiTools.Mouse(x, y, 0x0004);
                return (0, $"clicked {label} at {x},{y} with the mouse ({why}){refused}" + (blocked != null ? "\n" + blocked : ""));
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);

    [LibraryImport("oleaut32.dll")]
    private static partial uint SysStringLen(nint bstr);

    [LibraryImport("oleaut32.dll")]
    private static partial void SysFreeString(nint bstr);

    [LibraryImport("oleaut32.dll")]
    private static partial nint SysAllocString(char* text);

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(byte* variant);
}
