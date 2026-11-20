using System.Diagnostics;
using AgentProbe.Native;
using AgentProbe.Util;
using Microsoft.Diagnostics.Runtime;

namespace AgentProbe.Tools;

/// <summary>
/// The managed heap of a .NET process, read on the target: how many objects of each type there are, how many of
/// them are still reachable, and what keeps one alive. This answers "do the closed windows go away?" in one line,
/// where the alternative is a multi-gigabyte dump carried to the developer PC.
/// The heap is read from a snapshot of the process (a copy-on-write clone made by Windows), so the app itself is
/// held up only for the moment the snapshot is taken.
/// </summary>
internal static class HeapTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("heap", "Processes", "Managed (.NET) objects of a process by type, and what keeps one alive",
            """
            heap <pid|name> [--type TEXT] [--ns TEXT] [--top 25] [--live] [--timeout 2m]
            heap <pid|name> --windows [--type TEXT] [--ns TEXT]
            heap <pid|name> --why <TYPE>
              The .NET objects on the process's heap by type, largest total first: COUNT, BYTES, TYPE.
              BYTES is the objects' own size: what they point to (a byte[] in a field) is counted under its own type.
              --type keeps the types whose OWN name contains TEXT (case-insensitive): the part after the namespace,
              without generic arguments, so `--type Window` finds MyApp.SettingsWindow and not everything in
              System.Windows. TEXT with a dot is looked for in the whole name (`--type MyApp.Views`), and TEXT with
              * or ? is a pattern the whole name has to match (`--type "MyApp.*Window"`, `--type "*Cache*"`).
              --ns keeps the types whose namespace contains TEXT; it combines with --type.
              --live counts only objects that are still reachable from a root, i.e. what a garbage collection
              would keep: the number to compare before and after a fix ("20 editor windows" -> "0").
              --windows answers "how many windows are still alive?": the reachable objects of WPF Window,
              WinForms Form and every type derived from them, with SHOWN = on screen now (open, even if
              minimised) and CLOSED = closed, yet still referenced: the usual window leak. What is in neither
              column was created and is hidden or was never shown. Follow up with --why <that type>.
              --why takes the nearest reachable object of that type (its name, with or without namespace; if there
              is none, the first type whose name contains the text) and prints the chain of references that keeps it
              alive, from its root down to it.
              The heap is read from a snapshot, so the app is paused only while the snapshot is taken. --live and
              --why walk every reference and take longer on a large heap; when --timeout ends them early the
              output says INCOMPLETE. For .NET Framework and .NET (Core) processes that are 64-bit like
              agentprobe: a 32-bit process cannot be read this way (see `dump --full --keep-remote`).
            """, HeapAsync),
    ];

    private sealed class Stat
    {
        public long Count;
        public long Bytes;

        /// <summary>--windows: how many are on screen, how many were closed, and for how many that could not be read.</summary>
        public long Shown, Closed, StateUnknown;
    }

    private enum WindowState
    {
        Unknown,
        Shown,
        Closed,
        NotShown, // created and never shown, or hidden
    }

    private enum WindowKind
    {
        None,
        Wpf,
        WinForms,
    }

    /// <summary>Is this type a WPF Window or a WinForms Form, or derived from one?</summary>
    private static WindowKind KindOf(ClrType type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            switch (t.Name)
            {
                case "System.Windows.Window":
                    return WindowKind.Wpf;
                case "System.Windows.Forms.Form":
                    return WindowKind.WinForms;
            }
        }

        return WindowKind.None;
    }

    /// <summary>
    /// What the window object itself records about its life, read from the private fields of the framework class;
    /// Unknown when a field is not where this version of the framework is known to keep it (then nothing is
    /// claimed). A field is looked up by the class that declares it: a derived class may well have a field of the
    /// same name (WinForms' own ContainerControl has a `state` too).
    /// "Visible" alone is not enough for WPF: a Window that was closed keeps its visible flag set, and only its
    /// disposed flag tells that it is gone from the screen.
    /// </summary>
    private static WindowState StateOf(ClrObject o, WindowKind kind)
    {
        try
        {
            if (o.Type == null) return WindowState.Unknown;
            if (kind == WindowKind.Wpf)
            {
                var visible = o.Type.Fields.FirstOrDefault(f => f.Name == "_isVisible" && f.ContainingType?.Name == "System.Windows.Window");
                var disposed = o.Type.Fields.FirstOrDefault(f => f.Name == "_disposed" && f.ContainingType?.Name == "System.Windows.Window");
                if (visible == null || disposed == null) return WindowState.Unknown;
                return disposed.Read<bool>(o.Address, false) ? WindowState.Closed
                    : visible.Read<bool>(o.Address, false) ? WindowState.Shown
                    : WindowState.NotShown;
            }

            // Control keeps its state bits in one int: `state` in .NET Framework, `_state` in .NET.
            // 0x800 = disposed (what closing a form does), 0x2 = visible.
            var state = o.Type.Fields.FirstOrDefault(f => f.Name is "state" or "_state" && f.ContainingType?.Name == "System.Windows.Forms.Control");
            if (state == null) return WindowState.Unknown;
            var bits = state.Read<int>(o.Address, false);
            return (bits & 0x800) != 0 ? WindowState.Closed : (bits & 0x2) != 0 ? WindowState.Shown : WindowState.NotShown;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ClrDiagnosticsException)
        {
            return WindowState.Unknown;
        }
    }

    /// <summary>
    /// A type name in its parts: what encloses it (namespace and outer types), its own name, and the two joined,
    /// all without the type's own generic arguments. "MyApp.Views.Cache&lt;System.Int32&gt;+Node" is
    /// ("MyApp.Views.Cache&lt;System.Int32&gt;", "Node"); "System.Collections.Generic.List&lt;MyApp.Item&gt;[]" is
    /// ("System.Collections.Generic", "List[]"). A '&lt;' is not always the start of generic arguments: compiler-made
    /// names begin with one ("&lt;&gt;c__DisplayClass1_0", "Foo+&lt;Run&gt;d__5"), so only a group at the END of the own name
    /// is taken as arguments, and separators are only looked for outside of angle brackets.
    /// </summary>
    private static (string full, string ns, string own) NameParts(string typeName)
    {
        var split = -1;
        var depth = 0;
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (c == '<') depth++;
            else if (c == '>') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c is '.' or '+' && depth == 0) split = i;
        }

        var ns = split < 0 ? "" : typeName[..split];
        var own = typeName[(split + 1)..];

        // "[]", "[,]" at the end stay; generic arguments in front of them go.
        var end = own.Length;
        while (end > 0 && own[end - 1] is '[' or ']' or ',') end--;
        var suffix = own[end..];
        if (end > 1 && own[end - 1] == '>')
        {
            depth = 0;
            for (var i = end - 1; i > 0; i--) // i > 0: a name that starts with '<' is compiler-made, not arguments
            {
                if (own[i] == '>') depth++;
                else if (own[i] == '<' && --depth == 0)
                {
                    own = own[..i] + suffix;
                    break;
                }
            }
        }

        return (ns.Length == 0 ? own : ns + typeName[split] + own, ns, own);
    }

    /// <summary>
    /// --type and --ns as one test. Plain text is looked for in the type's own name, text with a dot in the whole
    /// name, and text with * or ? is a pattern that the whole name has to match, with or without the type's own
    /// generic arguments.
    /// </summary>
    private static Func<string, bool> TypeFilter(string? type, string? ns)
    {
        Func<string, bool> byType = _ => true;
        if (type != null && (type.Contains('*') || type.Contains('?')))
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(type).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            byType = name => pattern.IsMatch(name) || pattern.IsMatch(NameParts(name).full);
        }
        else if (type != null && type.Contains('.'))
        {
            byType = name => name.Contains(type, StringComparison.OrdinalIgnoreCase);
        }
        else if (type != null)
        {
            byType = name => NameParts(name).own.Contains(type, StringComparison.OrdinalIgnoreCase);
        }

        return ns == null ? byType : name => NameParts(name).ns.Contains(ns, StringComparison.OrdinalIgnoreCase) && byType(name);
    }

    private static Task<int> HeapAsync(ToolContext ctx, string[] args) => Task.Run(() => Heap(ctx, args));

    private static int Heap(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "live", "windows");
        if (a.Positionals.Count == 0) return ctx.Fail("heap: which process? pid or name.");
        var matches = ProcessTools.Find(ProcessSnapshot.Take(), a.Positionals[0]);
        if (matches.Count == 0) return ctx.Fail($"heap: no process matches '{a.Positionals[0]}'.");
        var p = matches.OrderByDescending(m => m.PrivateBytes).First();
        if (matches.Count > 1) ctx.Err.WriteLine($"[{matches.Count} processes match; reading the largest, pid {p.Pid}]");

        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, p.Pid);
        if (h == 0) return ctx.Fail(ProcessTools.WhyNotOpen("heap", p.Name, p.Pid));
        var arch = ProcessDetails.Architecture(h);
        Win32.CloseHandle(h);
        if (arch == "x86")
        {
            return ctx.Fail($"heap: {p.Name} (pid {p.Pid}) is a 32-bit process. agentprobe is 64-bit, and a .NET heap can only be read by a tool of the same bitness as the process. " +
                            "Nothing was read. Take `dump --full --keep-remote` and analyse it on the target with a 32-bit dotnet-dump, or pull it.");
        }

        var why = a.Get("why");
        var windowsOnly = a.Flag("windows");
        if (windowsOnly && why != null) return ctx.Fail("heap: --windows and --why are two different questions; ask them one at a time.");
        var live = a.Flag("live") || why != null || windowsOnly;
        var filter = a.Get("type");
        var ns = a.Get("ns");
        Func<string, bool> wanted;
        try
        {
            wanted = TypeFilter(filter, ns);
        }
        catch (ArgumentException ex)
        {
            return ctx.Fail($"heap: --type '{filter}' is not a usable pattern: {ex.Message}");
        }

        var top = Math.Clamp(a.GetInt("top", 25), 1, 500);
        var limit = a.GetDuration("timeout") ?? TimeSpan.FromMinutes(2);
        var sw = Stopwatch.StartNew();
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(p.Pid);
            var snapshotMs = sw.ElapsedMilliseconds;
            var clr = target.ClrVersions.FirstOrDefault();
            if (clr == null) return ctx.Fail($"heap: no .NET runtime is loaded in {p.Name} (pid {p.Pid}): it is not a managed process (or has not started managed code yet).");
            using var runtime = clr.CreateRuntime();
            var heap = runtime.Heap;
            if (!heap.CanWalkHeap) return ctx.Fail("heap: the snapshot caught the process in the middle of a garbage collection, when its heap cannot be read. Run it again.");
            ctx.Runtime.Audit.Write($"heap of {p.Name} ({p.Pid}) read from a snapshot{(live ? " (reachable objects)" : "")}");

            var stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
            var windowKinds = new Dictionary<ulong, WindowKind>();
            long objects = 0, bytes = 0;
            var complete = true;
            void Count(ClrObject o)
            {
                var size = (long)o.Size;
                objects++;
                bytes += size;
                var kind = WindowKind.None;
                if (windowsOnly)
                {
                    // Only windows are tallied; everything else is just walked through.
                    if (o.Type == null) return;
                    if (!windowKinds.TryGetValue(o.Type.MethodTable, out kind)) windowKinds[o.Type.MethodTable] = kind = KindOf(o.Type);
                    if (kind == WindowKind.None) return;
                }

                var name = o.Type?.Name ?? "(unknown type)";
                if (!stats.TryGetValue(name, out var s)) stats[name] = s = new Stat();
                s.Count++;
                s.Bytes += size;
                if (kind == WindowKind.None) return;
                switch (StateOf(o, kind))
                {
                    case WindowState.Shown:
                        s.Shown++;
                        break;
                    case WindowState.Closed:
                        s.Closed++;
                        break;
                    case WindowState.Unknown:
                        s.StateUnknown++;
                        break;
                }
            }

            string? chain = null;
            if (!live)
            {
                foreach (var o in heap.EnumerateObjects())
                {
                    if (!o.IsValid || o.IsFree) continue;
                    Count(o);
                    if ((objects & 0xFFFF) == 0 && (ctx.Ct.IsCancellationRequested || sw.Elapsed > limit))
                    {
                        complete = false;
                        break;
                    }
                }
            }
            else
            {
                // Breadth-first from the roots: everything reached is what a collection would keep, and the first
                // time an object is reached is along a shortest chain, which is the one worth showing for --why.
                var parent = why != null ? new Dictionary<ulong, ulong>() : null;
                var rootKind = why != null ? new Dictionary<ulong, ClrRootKind>() : null;
                var seen = new HashSet<ulong>();
                var queue = new Queue<ulong>();
                foreach (var root in heap.EnumerateRoots())
                {
                    var o = root.Object;
                    if (!o.IsValid || o.IsFree || !seen.Add(o.Address)) continue;
                    rootKind?.TryAdd(o.Address, root.RootKind);
                    queue.Enqueue(o.Address);
                }

                // --why means the type itself first (its full name, or its own name without namespace and generic
                // arguments). Only when no such object is reachable does "the name contains the text" count, so that
                // asking for Editor does not stop at a List<Editor> or a delegate that merely mentions it.
                ulong found = 0, loose = 0;
                while (queue.Count > 0)
                {
                    var o = heap.GetObject(queue.Dequeue());
                    if (!o.IsValid) continue;
                    Count(o);
                    if (why != null)
                    {
                        var name = o.Type?.Name ?? "";
                        if (IsType(name, why))
                        {
                            found = o.Address;
                            break; // the question is answered; counting on would only keep the caller waiting
                        }

                        if (loose == 0 && name.Contains(why, StringComparison.OrdinalIgnoreCase)) loose = o.Address;
                    }

                    foreach (var r in o.EnumerateReferences(carefully: false, considerDependantHandles: true))
                    {
                        if (!r.IsValid || r.IsFree || !seen.Add(r.Address)) continue;
                        parent?.TryAdd(r.Address, o.Address);
                        queue.Enqueue(r.Address);
                    }

                    if ((objects & 0x3FFF) == 0 && (ctx.Ct.IsCancellationRequested || sw.Elapsed > limit))
                    {
                        complete = false;
                        break;
                    }
                }

                if (why != null)
                {
                    if (found == 0 && complete) found = loose; // nothing of exactly that type: the nearest by name
                    if (found == 0)
                    {
                        ctx.Out.WriteLine(complete
                            ? $"{p.Name} (pid {p.Pid}): no reachable object whose type contains '{why}' ({objects:N0} reachable objects looked at). If `heap --type {why}` still counts some, they are garbage waiting for a collection."
                            : $"{p.Name} (pid {p.Pid}): INCOMPLETE, stopped after {Format.Duration(sw.Elapsed)} and {objects:N0} objects without meeting a type that contains '{why}'. That is not a 'no': raise --timeout.");
                        return complete ? 0 : 124;
                    }

                    chain = Chain(heap, found, parent!, rootKind!);
                }
            }

            var flavor = $".NET {(clr.Flavor == ClrFlavor.Desktop ? "Framework " : "")}{clr.Version}";
            if (chain != null)
            {
                ctx.Out.WriteLine($"{p.Name} (pid {p.Pid}, {flavor}): what keeps the first reachable '{why}' object alive (root first; snapshot taken in {snapshotMs} ms)");
                ctx.Out.Write(chain);
                return 0;
            }

            var shown = stats.Where(s => wanted(s.Key)).OrderByDescending(s => windowsOnly ? s.Value.Count : s.Value.Bytes).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
            var asked = string.Join(" and ", new[] { filter != null ? $"--type {filter}" : null, ns != null ? $"--ns {ns}" : null }.Where(x => x != null));
            var incomplete = complete ? "" : $"; INCOMPLETE: stopped after {Format.Duration(sw.Elapsed)}, the counts below are too low (raise --timeout)";
            if (windowsOnly)
            {
                ctx.Out.WriteLine($"{p.Name} (pid {p.Pid}, {flavor}): {shown.Sum(s => s.Value.Count):N0} reachable window objects of {shown.Count:N0} types" +
                                  (asked.Length > 0 ? $" matching {asked}" : "") +
                                  $" (WPF Window, WinForms Form and types derived from them; {objects:N0} reachable objects looked at); snapshot taken in {snapshotMs} ms{incomplete}");
                if (shown.Count == 0) return complete ? 0 : 124;
                var w = new Table(">COUNT", ">SHOWN", ">CLOSED", "TYPE");
                foreach (var s in shown.Take(top))
                {
                    var known = s.Value.StateUnknown == 0;
                    w.Add(s.Value.Count.ToString("N0"), known ? s.Value.Shown.ToString("N0") : "?", known ? s.Value.Closed.ToString("N0") : "?", Format.Clip(s.Key, 150));
                }

                ctx.Out.Write(w.ToString());
                if (shown.Count > top) ctx.Out.WriteLine($"... and {shown.Count - top:N0} more window types ({shown.Skip(top).Sum(s => s.Value.Count):N0} objects): raise --top or narrow with --type");
                if (shown.Any(s => s.Value.StateUnknown > 0))
                    ctx.Out.WriteLine("?: this framework version keeps a window's state somewhere this tool does not know; COUNT is still exact.");
                if (shown.Any(s => s.Value.Closed > 0))
                    ctx.Out.WriteLine("CLOSED: windows that were closed (disposed) and are still referenced, the usual window leak. `heap <pid> --why <TYPE>` shows what holds one.");
                if (shown.Any(s => s.Value.StateUnknown == 0 && s.Value.Count > s.Value.Shown + s.Value.Closed))
                    ctx.Out.WriteLine("COUNT above SHOWN + CLOSED: the rest were created and are hidden or were never shown (a WinForms dialog that was closed after ShowDialog is hidden, not disposed).");
                return complete ? 0 : 124;
            }

            ctx.Out.WriteLine($"{p.Name} (pid {p.Pid}, {flavor}): {objects:N0} {(live ? "reachable " : "")}objects, {Format.Bytes(bytes)}" +
                              (live ? "" : " on the managed heap, garbage not yet collected included (--live leaves it out)") +
                              $"; snapshot taken in {snapshotMs} ms" + incomplete);
            if (shown.Count == 0)
            {
                if (asked.Length == 0)
                {
                    ctx.Out.WriteLine("no objects");
                    return complete ? 0 : 124;
                }

                ctx.Out.WriteLine($"no {(live ? "reachable " : "")}object of a type matching {asked}{(complete ? "" : " was met before it stopped")}");

                // --type looks at the type's own name. Say so when the text is there, just not in that part.
                if (filter != null && !filter.Contains('*') && !filter.Contains('?') && !filter.Contains('.'))
                {
                    var elsewhere = stats.Keys.Count(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase) && (ns == null || NameParts(k).ns.Contains(ns, StringComparison.OrdinalIgnoreCase)));
                    if (elsewhere > 0)
                        ctx.Out.WriteLine($"'{filter}' is in {elsewhere:N0} type name{(elsewhere == 1 ? "" : "s")}, but not in the type's own name: in a namespace, an enclosing type or generic arguments. --type \"*{filter}*\" lists them; --ns {filter} if it is a namespace.");
                }

                return complete ? 0 : 124;
            }

            var t = new Table(">COUNT", ">BYTES", "TYPE");
            foreach (var s in shown.Take(top)) t.Add(s.Value.Count.ToString("N0"), Format.Bytes(s.Value.Bytes), Format.Clip(s.Key, 150));
            ctx.Out.Write(t.ToString());
            if (shown.Count > top)
            {
                var rest = shown.Skip(top).ToList();
                ctx.Out.WriteLine($"... and {rest.Count:N0} more types ({rest.Sum(s => s.Value.Count):N0} objects, {Format.Bytes(rest.Sum(s => s.Value.Bytes))}): raise --top or narrow with --type");
            }

            return complete ? 0 : 124;
        }
        catch (Exception ex) when (ex is ClrDiagnosticsException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or IOException or NotSupportedException or PlatformNotSupportedException or ArgumentException or DllNotFoundException or BadImageFormatException)
        {
            return ctx.Fail($"heap: could not read the .NET heap of {p.Name} (pid {p.Pid}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="typeName"/> is the type asked for: its full name, or its own name without namespace and generic arguments.</summary>
    private static bool IsType(string typeName, string asked)
    {
        if (typeName.Equals(asked, StringComparison.OrdinalIgnoreCase)) return true;
        var generic = typeName.IndexOf('<');
        var bare = generic < 0 ? typeName : typeName[..generic];
        if (bare.Equals(asked, StringComparison.OrdinalIgnoreCase)) return true;
        var dot = bare.LastIndexOfAny(['.', '+']);
        return dot >= 0 && bare[(dot + 1)..].Equals(asked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The references from a root down to <paramref name="found"/>, one object per line, with the field that holds each next one.</summary>
    private static string Chain(ClrHeap heap, ulong found, Dictionary<ulong, ulong> parent, Dictionary<ulong, ClrRootKind> rootKind)
    {
        var path = new List<ulong> { found };
        while (parent.TryGetValue(path[^1], out var up) && path.Count < 10000) path.Add(up);
        path.Reverse();

        var sb = new System.Text.StringBuilder();
        var kind = rootKind.TryGetValue(path[0], out var k) ? k : ClrRootKind.None;
        sb.Append("root: ").Append(kind switch
        {
            ClrRootKind.Stack => "a local variable or argument on a thread's stack",
            ClrRootKind.StaticVar => "a static field",
            ClrRootKind.ThreadStaticVar => "a thread-static field",
            ClrRootKind.FinalizerQueue => "the finalizer queue (waiting for its finalizer to run)",
            ClrRootKind.StrongHandle => "a strong GC handle",
            ClrRootKind.PinnedHandle or ClrRootKind.AsyncPinnedHandle => "a pinned GC handle (static fields are kept in pinned arrays)",
            ClrRootKind.RefCountedHandle => "a COM reference count",
            ClrRootKind.SizedRefHandle => "a sized-ref GC handle",
            _ => "a root of unknown kind",
        }).Append('\n');

        for (var i = 0; i < path.Count; i++)
        {
            var o = heap.GetObject(path[i]);
            var via = "";
            if (i + 1 < path.Count)
            {
                // Which of this object's references is the next step: name the field, or say it is an array element.
                foreach (var r in o.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
                {
                    if (r.Object.Address != path[i + 1]) continue;
                    via = r.IsDependentHandle ? "  -> (dependent handle, e.g. ConditionalWeakTable)"
                        : r.IsArrayElement ? "  -> [element]"
                        : r.Field?.Name is { Length: > 0 } f ? $"  -> .{f}" : "  -> (reference)";
                    break;
                }
            }

            sb.Append(' ', Math.Min(i, 40)).Append(Format.Clip(o.Type?.Name ?? "(unknown type)", 140)).Append($" ({Format.Bytes((long)o.Size)})").Append(via).Append('\n');
        }

        return sb.ToString();
    }
}
