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
                 Unnamed layout containers are folded away. --all also lists off-screen controls.
            ui click <name|#id> [--window W]     press a button / select an item (falls back to a mouse click on it)
            ui set <name|#id> <text> [--window W]  put text into an edit field
            ui get <name|#id> [--window W]       one control's value and state
              Works with WPF, WinForms, Win32, UWP/WinUI and most browsers/Electron apps. Read the tree once, then act
              by name; take a screenshot only when you need to judge how something looks.
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

    private static (int code, string text) OnComThread(Func<nint, (int, string)> work)
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
        return thread.Join(TimeSpan.FromSeconds(20))
            ? result
            : (1, "ui: the application did not answer within 20s (its UI thread is busy or hung). Try `windows` and `threads <pid>`.");
    }

    private static nint RootFor(nint automation, string? window, out string title, out string error)
    {
        error = "";
        title = "";
        nint hwnd;
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
        if (!n.Enabled) sb.Append(" (disabled)");
        if (n.Offscreen) sb.Append(" (off-screen)");
        else if (n.Rect.Width > 0) sb.Append(" @").Append((n.Rect.Left + n.Rect.Right) / 2).Append(',').Append((n.Rect.Top + n.Rect.Bottom) / 2);
        return sb.ToString();
    }

    private static Node? Find(List<Node> nodes, string spec)
    {
        if (spec.StartsWith('#')) return nodes.FirstOrDefault(n => n.Id.Equals(spec[1..], StringComparison.OrdinalIgnoreCase));
        var candidates = nodes.Where(n => !n.Offscreen).ToList();
        return candidates.FirstOrDefault(n => n.Name.Equals(spec, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(n => n.Id.Equals(spec, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(n => n.Name.Contains(spec, StringComparison.OrdinalIgnoreCase))
               ?? nodes.FirstOrDefault(n => n.Name.Contains(spec, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<int> UiAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all");
        var verb = a.Positionals.Count > 0 ? a.Positionals[0].ToLowerInvariant() : "";
        var isAction = verb is "click" or "set" or "get";
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

        var (code, text) = OnComThread(automation =>
        {
            var root = RootFor(automation, window, out var title, out var error);
            if (root == 0) return (2, error);
            var nodes = Walk(automation, root, a.GetInt("depth", isAction ? 40 : 12), a.GetInt("max", isAction ? 4000 : 300), a.Flag("all") || isAction, out var deeper);
            try
            {
                return isAction ? Act(ctx, verb, a.Positionals, nodes) : (0, Render(nodes, title, grep, a.Flag("all"), a.GetInt("max", 300), deeper));
            }
            finally
            {
                foreach (var n in nodes) Release(n.Element);
            }
        });

        (code == 0 ? ctx.Out : ctx.Err).WriteLine(text.TrimEnd());
        return Task.FromResult(code);
    }

    private static string Render(List<Node> nodes, string title, Regex? grep, bool all, int max, bool deeper)
    {
        var sb = new StringBuilder();
        var shown = 0;

        // Fold away unnamed layout containers: their children move up one level, which keeps the tree short.
        var indentAt = new int[64];
        foreach (var n in nodes)
        {
            var structural = n.Depth > 0 && n.Name.Length == 0 && n.Value.Length == 0 && (n.Id.Length == 0 || n.Id.All(char.IsDigit)) && n.Type is 50033 or 50026 or 50025 or 50020 or 50006 or 50038 or 50027;
            var parentIndent = n.Depth == 0 ? -1 : indentAt[Math.Min(n.Depth - 1, 63)];
            var indent = structural ? parentIndent : parentIndent + 1;
            indentAt[Math.Min(n.Depth, 63)] = indent;
            if (structural || (n.Offscreen && !all && n.Depth > 0)) continue;
            var line = Line(n, Math.Max(0, indent));
            if (grep != null && !grep.IsMatch(line)) continue;
            sb.Append(grep != null ? line.TrimStart() : line).Append('\n');
            shown++;
        }

        var note = nodes.Count >= max ? $" (stopped at {max} controls: narrow with --grep, or raise --max)" : "";
        if (deeper) note += " (more controls exist below the depth limit: raise --depth)";
        return $"window \"{Format.Clip(title, 70)}\": {shown} controls{note}; '@x,y' is each control's center on screen\n" + (shown == 0 ? "[no control matches]\n" : sb.ToString());
    }

    private static (int, string) Act(ToolContext ctx, string verb, List<string> positionals, List<Node> nodes)
    {
        var spec = positionals[1];
        var node = Find(nodes, spec);
        if (node == null) return (1, $"ui {verb}: no control named '{spec}' in that window. Run `ui --grep {spec}` to see what is there.");
        var label = $"{TypeName(node.Type)} \"{Format.Clip(node.Name.Length > 0 ? node.Name : node.Id, 50)}\"";

        switch (verb)
        {
            case "get":
                return (0, Line(node, 0));

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

            default:
            {
                if (!node.Enabled) return (1, $"ui click: {label} is disabled.");
                ctx.Runtime.Audit.Write($"ui click {label}");

                // Prefer the control's own action; it works even when the window is covered or scrolled.
                foreach (var (patternId, iid, what) in new[] { (10000, IidInvoke, "invoked"), (10010, IidSelectionItem, "selected"), (10015, IidToggle, "toggled") })
                {
                    var p = Pattern(node.Element, patternId, iid);
                    if (p == 0) continue;
                    var hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)p)[3])(p); // Invoke / Select / Toggle
                    Release(p);
                    if (hr >= 0) return (0, $"{what} {label}");
                }

                if (node.Offscreen || node.Rect.Width <= 0) return (1, $"ui click: {label} has no action of its own and is not visible on screen.");
                var x = (node.Rect.Left + node.Rect.Right) / 2;
                var y = (node.Rect.Top + node.Rect.Bottom) / 2;
                UiTools.Mouse(x, y, 0x0001);
                Thread.Sleep(40);
                UiTools.Mouse(x, y, 0x0002);
                UiTools.Mouse(x, y, 0x0004);
                return (0, $"clicked {label} at {x},{y}");
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
