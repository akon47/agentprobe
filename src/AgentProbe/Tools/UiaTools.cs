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
            ui [window] [--grep REGEX] [--depth 12] [--max 300] [--all]
                 The control tree of a window (default: the foreground window; give part of its title or an HWND).
                 One line per control: type, "name", #automation-id, =value, state, and its center point on screen.
                 State is what the control reports: (selected) on the tab, list item or radio button that is the
                 selected one, (checked) / (unchecked) for a check box or toggle button, (expanded) / (collapsed),
                 (disabled), and (maximized) / (minimized) / (normal) on the window's own line. Without such a
                 word the control is not selected, or does not report that kind of state at all.
                 Unnamed layout containers are folded away. --all also lists off-screen controls.
                 --grep searches the WHOLE tree, off-screen controls included (to depth 40 unless --depth says
                 otherwise), and prints the matching lines; --max then limits how many of them are printed, and
                 the total is stated. A search that could not cover everything says INCOMPLETE.
            ui click <name|#id> [--window W] [--nth N]   press a button / select an item (falls back to a mouse click on it)
            ui rclick <name|#id> [--window W]    right-click it (opens its context menu; `windows` then shows a popup)
            ui dblclick <name|#id> [--window W]  double-click it
            ui tap <name|#id> [--window W]       tap it by touch, as a finger on a touch screen would (see `touch`)
            ui set <name|#id> <text> [--window W]  put text into an edit field
            ui get <name|#id> [--window W]       one control's line: value and state (compare before and after an action)
              Works with WPF, WinForms, Win32, UWP/WinUI and most browsers/Electron apps. Read the tree once, then act
              by name; take a screenshot only when you need to judge how something looks.
              When several controls have that name the first is taken and the answer says how many there are:
              --nth 2 takes the second. They count in the order `ui` lists them, panes and groups after the
              controls (a tab and the page it shows have the same name: the tab is the first). --nth works
              with every action.
              An action that needs the mouse or touch (rclick, dblclick, tap, and click on a control without an
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

    /// <summary>The control's current text (ValuePattern), e.g. what is typed in an edit box.</summary>
    private static string ValueOf(nint element)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        if (((delegate* unmanaged[Stdcall]<nint, int, byte*, int>)(*(void***)element)[10])(element, 30045, variant) < 0) return ""; // GetCurrentPropertyValue(Value.Value)
        var text = *(ushort*)variant == 8 && *(nint*)(variant + 8) != 0 ? new string((char*)*(nint*)(variant + 8)) : "";
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

    /// <summary>" (selected)", " (checked)", " (expanded)"... as the control reports them; "" for one that reports none.</summary>
    private static string StateOf(Node n)
    {
        // Controls that never have such a state are not asked: every question is a call into the other process.
        if (n.Type is 50020 or 50006 or 50033 or 50038 or 50027 or 50014 or 50037 or 50022 or 50017 or 50012 or 50004 or 50030) return "";
        var sb = new StringBuilder();
        if (n.Type == 50032)
        {
            sb.Append(StateProperty(n.Element, 30075) switch { 0 => " (normal)", 1 => " (maximized)", 2 => " (minimized)", _ => "" }); // Window.WindowVisualState
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
    private static List<Node> Walk(nint automation, nint root, int maxDepth, int maxNodes, bool includeOffscreen, out bool deeper)
    {
        deeper = false;
        var cutByDepth = false;
        var nodes = new List<Node>();
        var walker = ControlViewWalker(automation);
        if (walker == 0) return nodes;

        void Visit(nint element, int depth)
        {
            var node = Describe(element, depth);
            nodes.Add(node);
            if (nodes.Count >= maxNodes) return;
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

    private static string Line(Node n, int indent)
    {
        var sb = new StringBuilder();
        sb.Append(' ', indent * 2).Append(TypeName(n.Type));
        if (n.Name.Length > 0) sb.Append(" \"").Append(Format.Clip(n.Name, 70)).Append('"');
        if (n.Id.Length > 0 && !n.Id.All(char.IsDigit)) sb.Append(" #").Append(Format.Clip(n.Id, 40));
        if (n.Value.Length > 0) sb.Append(" =").Append(Format.Clip(n.Value, 60));
        sb.Append(StateOf(n));
        if (!n.Enabled) sb.Append(" (disabled)");
        if (n.Offscreen) sb.Append(" (off-screen)");
        else if (n.Rect.Width > 0) sb.Append(" @").Append((n.Rect.Left + n.Rect.Right) / 2).Append(',').Append((n.Rect.Top + n.Rect.Bottom) / 2);
        return sb.ToString();
    }

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
        var a = ArgParser.Parse(args, "all");
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        var isAction = verb is "click" or "rclick" or "dblclick" or "tap" or "set" or "get";
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
        var walkLimit = isAction ? 4000 : grep != null ? searchLimit : Math.Max(1, a.GetInt("max", 300));
        var nth = a.Has("nth") ? a.GetInt("nth", 0) : (int?)null;
        if (nth is < 1) return Task.FromResult(ctx.Fail("ui: --nth wants a number from 1."));
        var (code, text) = OnComThread(automation =>
        {
            var root = RootFor(automation, window, out var title, out var error, out var hwnd);
            if (root == 0) return (2, error);
            var nodes = Walk(automation, root, a.GetInt("depth", isAction || grep != null ? 40 : 12), walkLimit, a.Flag("all") || isAction || grep != null, out var deeper);
            try
            {
                return isAction ? Act(ctx, verb, a.Positionals, nodes, nth, hwnd, title, nodes.Count >= walkLimit ? walkLimit : 0) : (0, Render(nodes, title, grep, a.Flag("all"), Math.Max(1, a.GetInt("max", 300)), walkLimit, deeper));
            }
            finally
            {
                foreach (var n in nodes) Release(n.Element);
            }
        }, grep != null ? 60 : 20);

        (code == 0 ? ctx.Out : ctx.Err).WriteLine(text.TrimEnd());
        return Task.FromResult(code);
    }

    private static string Render(List<Node> nodes, string title, Regex? grep, bool all, int max, int walkLimit, bool deeper)
    {
        var sb = new StringBuilder();
        var shown = 0;
        var matched = 0;

        // Fold away unnamed layout containers: their children move up one level, which keeps the tree short.
        var indentAt = new int[64];
        foreach (var n in nodes)
        {
            var structural = n.Depth > 0 && n.Name.Length == 0 && n.Value.Length == 0 && (n.Id.Length == 0 || n.Id.All(char.IsDigit)) && n.Type is 50033 or 50026 or 50025 or 50020 or 50006 or 50038 or 50027;
            var parentIndent = n.Depth == 0 ? -1 : indentAt[Math.Min(n.Depth - 1, 63)];
            var indent = structural ? parentIndent : parentIndent + 1;
            indentAt[Math.Min(n.Depth, 63)] = indent;
            if (structural || (n.Offscreen && !all && grep == null && n.Depth > 0)) continue; // a search covers off-screen controls too; they say "(off-screen)"
            var line = Line(n, Math.Max(0, indent));
            if (grep != null && !grep.IsMatch(line)) continue;
            matched++;
            if (grep != null && shown >= max) continue; // counted, not printed
            sb.Append(grep != null ? line.TrimStart() : line).Append('\n');
            shown++;
        }

        // What was left out is said in the first line: a search that stopped early is not "no match".
        var cut = nodes.Count >= walkLimit;
        var note = grep == null
            ? (cut ? $" (stopped at {walkLimit} controls: narrow with --grep, or raise --max)" : "")
            : (matched > shown ? $" of {matched} that match (--max prints more)" : "") +
              (cut ? $" (INCOMPLETE: the search stopped after {walkLimit} controls, there may be more matches; name a smaller window)" : "");
        if (deeper) note += grep == null ? " (more controls exist below the depth limit: raise --depth)" : " (controls below the depth limit were not searched: raise --depth)";
        var what = grep == null ? "controls" : matched > shown ? "matching controls shown" : "controls match";
        return $"window \"{Format.Clip(title, 70)}\": {shown} {what}{note}; '@x,y' is each control's center on screen\n" +
               (shown > 0 ? sb.ToString() : grep != null && (cut || deeper) ? "[no match among the controls that were searched; see the first line for what was not]\n" : "[no control matches]\n");
    }

    /// <param name="cutAt">Not 0 when only the first that many controls of the window were read.</param>
    private static (int, string) Act(ToolContext ctx, string verb, List<string> positionals, List<Node> nodes, int? nth, nint hwnd, string title, int cutAt)
    {
        var (code, text, node, several) = ActOn(ctx, verb, positionals, nodes, nth, hwnd, title);
        if (cutAt > 0 && (code != 0 || several > 1))
            text += $"\n[only the first {cutAt} controls of the window were read: there may be more with that name]";

        // Several controls have that name: say so, and where the one that was taken is, so a wrong pick shows.
        if (several > 1 && node != null && nth == null)
        {
            var where = node.Offscreen || node.Rect.Width <= 0 ? "off-screen" : $"@{(node.Rect.Left + node.Rect.Right) / 2},{(node.Rect.Top + node.Rect.Bottom) / 2}";
            text += $"\n[{several} controls have that name; this is the first ({where}). --nth 2 takes the next]";
        }

        return (code, text);
    }

    private static (int code, string text, Node? node, int several) ActOn(ToolContext ctx, string verb, List<string> positionals, List<Node> nodes, int? nth, nint hwnd, string title)
    {
        var (code, text, node, several) = (0, "", (Node?)null, 0);
        var spec = positionals[1];
        var all = FindAll(nodes, spec);
        several = all.Count;
        if (all.Count == 0) return (1, $"ui {verb}: no control named '{spec}' in that window. Run `ui --grep {spec}` to see what is there.", null, 0);
        if (nth > all.Count) return (1, $"ui {verb}: --nth {nth}, but only {all.Count} control{(all.Count == 1 ? " has" : "s have")} the name '{spec}' in that window.", null, all.Count);
        node = all[(nth ?? 1) - 1];
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

            case "tap":
            {
                // Touch has no action of its own in UI Automation either: a finger does it, at the control's centre.
                if (node.Offscreen || node.Rect.Width <= 0) return (1, $"ui tap: {label} is not visible on screen.");
                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                if (Covered(verb, label, x, y, hwnd, title) is { } covered) return (1, covered);
                var blocked = UiTools.DisabledWindowAt(x, y);
                ctx.Runtime.Audit.Write($"ui tap {label} at {x},{y}");
                if (UiTools.TouchGesture([(x, y)], TimeSpan.FromMilliseconds(60), ctx.Ct) is { } failure) return (1, $"ui tap: {label} at {x},{y}: {failure}");
                return (0, $"tapped {label} by touch at {x},{y}" + (blocked != null ? "\n" + blocked.Replace("The click did nothing there", "The touch did nothing there") : ""));
            }

            case "set":
            {
                var text = string.Join(' ', positionals.Skip(2));
                var value = Pattern(node.Element, 10002, IidValue);
                if (value == 0) return (1, $"ui set: {label} does not accept text. Click it and use `type` instead.");
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
                return (0, $"set {label} to \"{Format.Clip(text, 60)}\"");
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

                // Prefer the control's own action; it works even when the window is covered or scrolled.
                foreach (var (patternId, iid, what) in new[] { (10000, IidInvoke, "invoked"), (10010, IidSelectionItem, "selected"), (10015, IidToggle, "toggled") })
                {
                    var p = Pattern(node.Element, patternId, iid);
                    if (p == 0) continue;
                    if (!logged) ctx.Runtime.Audit.Write($"ui click {label}"); // before the call: a button that opens a dialog may not return for a while
                    logged = true;
                    var hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)p)[3])(p); // Invoke / Select / Toggle
                    Release(p);
                    if (hr >= 0) return (0, $"{what} {label}");
                    refused = $" (its own action answered 0x{hr:X8} first, and may have had an effect)";
                }

                if (node.Offscreen || node.Rect.Width <= 0) return (1, $"ui click: {label} has no action of its own that worked and is not visible on screen{refused}.");
                // No action of its own: the mouse does it, and a disabled window ignores the mouse.
                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                if (Covered(verb, label, x, y, hwnd, title) is { } covered) return (1, covered + refused);
                ctx.Runtime.Audit.Write($"ui click {label} at {x},{y} (mouse)");
                var blocked = UiTools.DisabledWindowAt(x, y);
                UiTools.Mouse(x, y, 0x0001);
                Thread.Sleep(40);
                UiTools.Mouse(x, y, 0x0002);
                UiTools.Mouse(x, y, 0x0004);
                return (0, $"clicked {label} at {x},{y} with the mouse (the control has no action of its own that worked){refused}" + (blocked != null ? "\n" + blocked : ""));
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
