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
            heap <pid|name> [--type TEXT] [--top 25] [--live] [--timeout 2m]
            heap <pid|name> --why <TYPE>
              The .NET objects on the process's heap by type, largest total first: COUNT, BYTES, TYPE.
              BYTES is the objects' own size: what they point to (a byte[] in a field) is counted under its own type.
              --type keeps the types whose name contains TEXT (case-insensitive).
              --live counts only objects that are still reachable from a root, i.e. what a garbage collection
              would keep: the number to compare before and after a fix ("20 editor windows" -> "0").
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
    }

    private static Task<int> HeapAsync(ToolContext ctx, string[] args) => Task.Run(() => Heap(ctx, args));

    private static int Heap(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "live");
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
        var live = a.Flag("live") || why != null;
        var filter = a.Get("type");
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
            long objects = 0, bytes = 0;
            var complete = true;
            void Count(ClrObject o)
            {
                var name = o.Type?.Name ?? "(unknown type)";
                if (!stats.TryGetValue(name, out var s)) stats[name] = s = new Stat();
                var size = (long)o.Size;
                s.Count++;
                s.Bytes += size;
                objects++;
                bytes += size;
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

            var shown = stats.Where(s => filter == null || s.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Value.Bytes).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
            ctx.Out.WriteLine($"{p.Name} (pid {p.Pid}, {flavor}): {objects:N0} {(live ? "reachable " : "")}objects, {Format.Bytes(bytes)}" +
                              (live ? "" : " on the managed heap, garbage not yet collected included (--live leaves it out)") +
                              $"; snapshot taken in {snapshotMs} ms" +
                              (complete ? "" : $"; INCOMPLETE: stopped after {Format.Duration(sw.Elapsed)}, the counts below are too low (raise --timeout)"));
            if (shown.Count == 0)
            {
                ctx.Out.WriteLine(filter != null ? $"no {(live ? "reachable " : "")}object of a type containing '{filter}'{(complete ? "" : " was met before it stopped")}" : "no objects");
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
