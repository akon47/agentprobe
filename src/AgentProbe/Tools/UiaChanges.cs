using System.Diagnostics;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>What a listing says about one control: <paramref name="Key"/> tells controls apart, <paramref name="Text"/> is its line.</summary>
internal readonly record struct UiEntry(string Key, string Text);

/// <summary>The screen right before an input action, to say afterwards what the action changed.</summary>
internal sealed class UiWatch
{
    public nint Hwnd;
    public string Title = "";
    public int Pid;
    public string ProcessName = "";
    public Process? Process; // held open, so that an exit code can still be read when the program ends
    public List<WindowInfo> Windows = [];
    public List<UiEntry>? Controls; // of the window acted on; null when they were not read
    public bool ControlsCut;
    public string WhyNoControls = "";
    public string? SkipKey;

    // For the tools that send raw input: the thread that took this look stays connected to UI Automation until
    // it has also taken the look after (see UiaTools.Watch). Null when that thread is not there.
    public ManualResetEventSlim? Go;
    public ManualResetEventSlim? Done;
    public volatile bool Abandoned;
    public volatile string? Result;
}

/// <summary>
/// "What did that do?" answered by the action itself. An agent that clicks a button otherwise spends the next
/// two or three commands finding out (did a dialog open? what does it say? did the status line change?), and each
/// of them is a turn that carries the whole conversation again. So an action looks at the screen before and after:
/// windows that opened (with what they say) or closed, controls of the window that read differently, the program
/// ending. It is a look of about a second, and the answer says so; what happens later is for `wait` and `expect`.
/// </summary>
internal static unsafe partial class UiaTools
{
    private const int WatchNodes = 600;
    private const int WatchEntries = 400;
    private const int WatchLines = 10;

    private static readonly Dictionary<nint, (DateTime at, List<UiEntry> entries)> Listings = [];

    /// <summary>Tells controls apart across two looks: the type with the id, or with the name when there is no id.</summary>
    private static string KeyOf(Node n) => TypeName(n.Type) + (n.Id.Length > 0 && !n.Id.All(char.IsDigit) ? " #" + n.Id : n.Name.Length > 0 ? " \"" + n.Name + "\"" : "");

    /// <summary>The windows a person would notice appearing or going: titled ones, and open menus or drop-downs. Not agentprobe's own.</summary>
    private static List<WindowInfo> Noticeable() => UiTools.EnumerateWindows()
        .Where(w => w.Visible && w.Rect.Width > 1 && w.Pid != Environment.ProcessId && (w.Title.Length > 0 || UiTools.IsPopup(w))
                    && w.Class is not ("Progman" or "WorkerW") && !UiTools.IsOwnWindow(w.Hwnd) && !UiTools.Cloaked(w.Hwnd)).ToList();

    private static string Signature(List<WindowInfo> windows)
    {
        var sb = new StringBuilder();
        foreach (var w in windows) sb.Append(w.Hwnd).Append(':').Append(w.Title).Append(':').Append(w.Disabled ? 'd' : 'e').Append(w.Minimized ? 'm' : 'n').Append('|');
        return sb.ToString();
    }

    /// <summary>The controls of a window as a listing has them (on screen, layout containers folded away), within a time limit.</summary>
    private static List<UiEntry>? Snapshot(nint automation, nint hwnd, int maxNodes, out bool cut)
    {
        cut = false;
        var root = Out(automation, 6, hwnd); // ElementFromHandle
        if (root == 0) return null;
        var (withClass, withPos) = (_withClass, _withPos);
        var nodes = Walk(automation, root, 30, maxNodes, false, out var deeper, Stopwatch.GetTimestamp() + (long)(2.5 * Stopwatch.Frequency));
        var timedOut = _walkTimedOut;
        try
        {
            var list = new List<UiEntry>();
            var deadline = Stopwatch.GetTimestamp() + (long)(2.5 * Stopwatch.Frequency);
            var titleBar = -1; // the depth of a title bar being skipped: its buttons are the same in every window
            foreach (var n in nodes)
            {
                if (titleBar >= 0)
                {
                    if (n.Depth > titleBar) continue;
                    titleBar = -1;
                }

                if (n.Type == 50037)
                {
                    titleBar = n.Depth;
                    continue;
                }

                if ((n.Offscreen && n.Depth > 0) || Structural(n)) continue;
                if (list.Count >= WatchEntries || Stopwatch.GetTimestamp() > deadline)
                {
                    cut = true;
                    break;
                }

                list.Add(new UiEntry(KeyOf(n), Core(n)));
            }

            cut |= timedOut || deeper || nodes.Count >= maxNodes;
            return list;
        }
        finally
        {
            foreach (var n in nodes) Release(n.Element);
            (_withClass, _withPos) = (withClass, withPos);
        }
    }

    /// <summary>The look before an action, on the automation thread the action runs on.</summary>
    private static UiWatch Before(nint automation, nint hwnd)
    {
        var top = Win32.GetAncestor(hwnd, 2) is var root && root != 0 ? root : hwnd; // GA_ROOT
        var watch = new UiWatch { Hwnd = top, Windows = Noticeable() };
        if (top == 0) return watch;
        var buf = stackalloc char[300];
        watch.Title = new string(buf, 0, Math.Max(0, Win32.GetWindowText(top, buf, 300)));
        Win32.GetWindowThreadProcessId(top, out watch.Pid);
        try
        {
            watch.Process = Process.GetProcessById(watch.Pid);
            watch.ProcessName = watch.Process.ProcessName + ".exe";
            _ = watch.Process.SafeHandle; // opened now: afterwards the process may be gone
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            watch.Process = null; // a program with higher rights: its end shows by its window closing only
        }

        if (UiTools.IsOwnWindow(top)) watch.WhyNoControls = "";
        else if (Win32.IsHungAppWindow(top)) watch.WhyNoControls = "its controls were not compared: the window is not responding";
        else if (automation == 0) watch.WhyNoControls = "its controls were not compared: UI Automation did not answer in time";
        else
        {
            watch.Controls = Snapshot(automation, top, WatchNodes, out watch.ControlsCut);
            if (watch.Controls == null) watch.WhyNoControls = "its controls were not compared: UI Automation could not attach to the window (a window of a program with higher rights?)";
        }

        return watch;
    }

    /// <summary>
    /// For the tools that send raw input (click, touch, key, type, drag, scroll): the look before. One thread
    /// connects to UI Automation, takes this look, waits while the caller sends its input, and then takes the look
    /// after (<see cref="Report"/>) before it disconnects: one connection for both looks.
    /// </summary>
    public static UiWatch Watch(nint hwnd)
    {
        UiWatch? watch = null;
        var ready = new ManualResetEventSlim();
        var go = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var gaveUp = false;
        var thread = new Thread(() =>
        {
            CoInitializeEx(0, 0); // multithreaded apartment
            nint automation = 0;
            UiWatch? mine = null;
            try
            {
                var clsid = ClsidAutomation;
                var iid = IidAutomation;
                if (CoCreateInstance(&clsid, 0, 1, &iid, &automation) < 0) automation = 0;
                mine = Before(automation, hwnd);
                (mine.Go, mine.Done) = (go, done);
                watch = mine;
                ready.Set();
                if (Volatile.Read(ref gaveUp) || !go.Wait(TimeSpan.FromMinutes(3)) || mine.Abandoned) return;
                mine.Result = Changes(automation, mine);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (mine != null) mine.Result = "what changed on the screen was not determined: " + ex.Message;
            }
            finally
            {
                done.Set();
                Release(automation);
                CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "uia-watch",
        };
        thread.Start();
        if (ready.Wait(TimeSpan.FromSeconds(6)) && watch is { } taken) return taken;

        // UI Automation did not answer in time (a busy or hung window): the windows are still compared, here.
        Volatile.Write(ref gaveUp, true);
        return Before(0, hwnd);
    }

    /// <summary>The look after, as the lines to add to the action's answer.</summary>
    public static string Report(UiWatch watch)
    {
        if (watch.Go == null || watch.Done == null) return Changes(0, watch);
        watch.Go.Set();
        return watch.Done.Wait(TimeSpan.FromSeconds(20)) && watch.Result is { } text
            ? text
            : "what changed on the screen was not determined: UI Automation did not answer in time (`windows` shows whether the window stopped responding)";
    }

    /// <summary>The input was not sent after all: lets the thread that waits for it go.</summary>
    public static void Drop(UiWatch? watch)
    {
        if (watch?.Go == null) return;
        watch.Abandoned = true;
        watch.Go.Set();
    }

    /// <summary>Waits for the screen to settle (up to 1.5 s), then says what is different from <paramref name="watch"/>.</summary>
    private static string Changes(nint automation, UiWatch watch)
    {
        var sw = Stopwatch.StartNew();
        var beforeSignature = Signature(watch.Windows);
        var now = watch.Windows;
        var signature = beforeSignature;
        var stableSince = TimeSpan.Zero;
        while (true)
        {
            Thread.Sleep(90);
            now = Noticeable();
            var current = Signature(now);
            if (current != signature) (signature, stableSince) = (current, sw.Elapsed);
            if (sw.ElapsedMilliseconds >= 450 && sw.Elapsed - stableSince >= TimeSpan.FromMilliseconds(250)) break;
            if (sw.ElapsedMilliseconds >= 1500) break;
        }

        var lines = new List<string>();
        var names = new Lazy<Dictionary<int, string>>(() => ProcessSnapshot.Take().GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name));
        string Named(WindowInfo w) => (w.Title.Length > 0 ? $"window \"{Format.Clip(w.Title, 60)}\"" : $"popup [{w.Class}]") + $" ({names.Value.GetValueOrDefault(w.Pid, "?")} pid {w.Pid})";

        // Windows that opened: each with what it says, so a message box needs no second look.
        var opened = now.Where(w => watch.Windows.All(b => b.Hwnd != w.Hwnd)).ToList();
        var inOpened = new HashSet<string>(); // what the opened windows hold: a dialog is also listed among its owner's controls
        foreach (var w in opened.Take(3))
        {
            lines.Add($"+ {Named(w)} opened, {w.Rect.Width}x{w.Rect.Height}{(w.Minimized ? " (minimized)" : "")}");
            if (automation == 0 || w.Hung || w.Minimized) continue;
            var inside = Snapshot(automation, w.Hwnd, 150, out var cut);
            if (inside == null || inside.Count <= 1) continue;
            foreach (var e in inside) inOpened.Add(e.Text);
            const int most = 12;
            foreach (var e in inside.Skip(1).Take(most)) lines.Add("    " + e.Text);
            if (inside.Count - 1 > most || cut)
                lines.Add($"    … {(inside.Count - 1 > most ? $"{inside.Count - 1 - most} more controls" : "more controls")}{(cut && inside.Count - 1 > most ? " at least" : "")}: ui {(w.Title.Length > 0 ? "\"" + Format.Clip(w.Title, 40) + "\"" : "0x" + ((long)w.Hwnd).ToString("X"))}");
        }

        if (opened.Count > 3) lines.Add($"+ {opened.Count - 3} more windows opened (`windows` lists them)");
        foreach (var w in watch.Windows.Where(b => now.All(x => x.Hwnd != b.Hwnd)).Take(4)) lines.Add($"- {Named(w)} closed");
        foreach (var w in now)
        {
            if (watch.Windows.FirstOrDefault(b => b.Hwnd == w.Hwnd) is not { } was) continue;
            var same = w.Pid == watch.Pid; // what other programs do to their own windows meanwhile is not this action's
            if (same && was.Title != w.Title) lines.Add($"~ window title: \"{Format.Clip(w.Title, 60)}\"  (was \"{Format.Clip(was.Title, 60)}\")");
            if (was.Disabled != w.Disabled) lines.Add($"~ {Named(w)} is {(w.Disabled ? "now disabled: it takes no input, usually because a dialog that belongs to it is open" : "enabled again")}");
            if (same && was.Minimized != w.Minimized) lines.Add($"~ {Named(w)} is {(w.Minimized ? "now minimized" : "no longer minimized")}");
        }

        try
        {
            if (watch.Process is { HasExited: true } ended)
                lines.Add($"- the program ended: {watch.ProcessName} pid {watch.Pid}, exit code {ProcessDetails.ExitCodeName((uint)ended.ExitCode)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // its exit code cannot be read: the closed window above is what is known
        }
        finally
        {
            watch.Process?.Dispose();
        }

        // The controls of the window that was acted on.
        var compared = false;
        var stillThere = watch.Hwnd != 0 && Win32.IsWindowVisible(watch.Hwnd);
        var note = watch.WhyNoControls;
        if (watch.Controls != null && stillThere && automation != 0)
        {
            var after = Snapshot(automation, watch.Hwnd, WatchNodes, out var cutAfter);
            if (after == null)
            {
                note = "its controls were not compared: UI Automation could not read the window afterwards";
            }
            else
            {
                compared = true;
                var partial = watch.ControlsCut || cutAfter;
                var changes = DiffEntries(watch.Controls, after, watch.SkipKey, onlyChanged: partial);

                // Said already: what an opened window holds, and that this window became disabled or enabled.
                changes.RemoveAll(c => c.StartsWith("+ ", StringComparison.Ordinal) && inOpened.Contains(c[2..]));
                if (watch.Controls.Count > 0 && now.FirstOrDefault(w => w.Hwnd == watch.Hwnd) is { } self && watch.Windows.FirstOrDefault(w => w.Hwnd == watch.Hwnd) is { } selfBefore && self.Disabled != selfBefore.Disabled)
                    changes.RemoveAll(c => c.StartsWith("~ " + watch.Controls[0].Key + " ", StringComparison.Ordinal) && c.Contains("(disabled)", StringComparison.Ordinal));
                foreach (var c in changes.Take(WatchLines)) lines.Add(c);
                if (changes.Count > WatchLines) lines.Add($"… {changes.Count - WatchLines} more controls of that window changed (`ui{(watch.Title.Length > 0 ? " \"" + Format.Clip(watch.Title, 40) + "\"" : "")}` lists them)");
                if (partial) note = $"the window has more controls than were compared (the first {WatchEntries}, read within 2.5s): controls that appeared or went are not reported, only ones that read differently";
            }
        }
        else if (watch.Controls != null && automation == 0)
        {
            note = "its controls were not compared: UI Automation did not answer in time";
        }

        var took = (sw.Elapsed.TotalSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (lines.Count == 0)
        {
            var looked = compared ? "windows, and this window's controls" : "windows";
            return $"no change seen within {took}s ({looked}){(note.Length > 0 ? "; " + note : "")}";
        }

        return $"changed within {took}s:\n  " + string.Join("\n  ", lines) + (note.Length > 0 ? $"\n  ({note})" : "");
    }

    /// <summary>
    /// The difference between two looks at the same controls, as lines: "~" reads differently, "+" appeared,
    /// "-" went. Controls are told apart by <see cref="UiEntry.Key"/>; several with one key are compared as a group.
    /// </summary>
    private static List<string> DiffEntries(List<UiEntry> before, List<UiEntry> after, string? skipKey, bool onlyChanged = false)
    {
        var result = new List<string>();
        var old = before.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Select(e => e.Text).ToList());
        var seen = new HashSet<string>();
        foreach (var g in after.GroupBy(e => e.Key))
        {
            seen.Add(g.Key);
            if (g.Key == skipKey) continue;
            var removed = old.TryGetValue(g.Key, out var was) ? new List<string>(was) : [];
            if (onlyChanged && was == null) continue;
            var added = new List<string>();
            foreach (var text in g.Select(e => e.Text))
            {
                if (!removed.Remove(text)) added.Add(text);
            }

            var pairs = Math.Min(removed.Count, added.Count);
            for (var i = 0; i < pairs; i++) result.Add("~ " + Reworded(removed[i], added[i]));
            if (onlyChanged) continue;
            for (var i = pairs; i < added.Count; i++) result.Add("+ " + added[i]);
            for (var i = pairs; i < removed.Count; i++) result.Add("- " + removed[i]);
        }

        if (onlyChanged) return result;
        foreach (var g in before.GroupBy(e => e.Key))
        {
            if (seen.Contains(g.Key) || g.Key == skipKey) continue;
            foreach (var e in g) result.Add("- " + e.Text);
        }

        return result;
    }

    /// <summary>The new line with the part of the old one that differed: <c>Text "Saved" #status  (was "Ready")</c>.</summary>
    private static string Reworded(string was, string now)
    {
        // One is the other with more at its end: a state that came or went ("(disabled)", a scroll position).
        if (now.StartsWith(was + " ", StringComparison.Ordinal)) return $"{now}  (new: {now[(was.Length + 1)..]})";
        if (was.StartsWith(now + " ", StringComparison.Ordinal)) return $"{now}  (no longer: {was[(now.Length + 1)..]})";
        var limit = Math.Min(was.Length, now.Length);
        var prefix = 0;
        while (prefix < limit && was[prefix] == now[prefix]) prefix++;
        while (prefix > 0 && was[prefix - 1] != ' ') prefix--; // back to the start of a word
        var suffix = 0;
        while (suffix < limit - prefix && was[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;
        while (suffix > 0 && was[^suffix] != ' ') suffix--; // forward to the start of a word
        var wasPart = was[prefix..(was.Length - suffix)].Trim();
        var nowPart = now[prefix..(now.Length - suffix)].Trim();
        return wasPart.Length > 0 ? $"{now}  (was {wasPart})" : $"{now}  (new: {nowPart})";
    }

    /// <summary>Keeps what a listing of a window said, and returns what the listing before it said (null: there was none).</summary>
    private static (DateTime at, List<UiEntry> entries)? RememberListing(nint hwnd, List<UiEntry> entries)
    {
        lock (Listings)
        {
            (DateTime, List<UiEntry>)? earlier = Listings.TryGetValue(hwnd, out var e) ? e : null;
            if (Listings.Count > 40)
            {
                foreach (var gone in Listings.Keys.Where(h => !Win32.IsWindowVisible(h)).ToList()) Listings.Remove(gone);
            }

            Listings[hwnd] = (DateTime.UtcNow, entries);
            return earlier;
        }
    }

    private static string DiffListing(string title, List<UiEntry> before, List<UiEntry> after, TimeSpan ago)
    {
        var changes = DiffEntries(before, after, null);
        var head = $"window \"{Format.Clip(title, 70)}\": ";
        if (changes.Count == 0) return head + $"nothing changed among its {after.Count} listed controls since the last `ui` of it, {Format.Duration(ago)} ago";
        const int most = 60;
        return head + $"{changes.Count} change{(changes.Count == 1 ? "" : "s")} since the last `ui` of it, {Format.Duration(ago)} ago (~ reads differently, + appeared, - went)\n" +
               string.Join('\n', changes.Take(most)) + (changes.Count > most ? $"\n… {changes.Count - most} more: `ui` without --diff lists the window as it is now" : "");
    }

    /// <summary>
    /// One control, looked up by name or #id as the actions do it. Status 0: found, with its line (no position)
    /// and where it is; 1: the window has no such control; 2: the window was not found or could not be read.
    /// </summary>
    public static (int status, string text, RECT rect, nint hwnd, bool offscreen) Probe(string? window, string spec, int nth = 1)
    {
        RECT rect = default;
        nint found = 0;
        var off = false;
        var (code, text) = OnComThread(automation =>
        {
            (_withClass, _withPos) = (false, false);
            var root = RootFor(automation, window, out var title, out var error, out var hwnd);
            if (root == 0) return (2, error);
            var nodes = Walk(automation, root, 40, 4000, true, out _);
            try
            {
                var all = FindAll(nodes, spec);
                if (all.Count < nth) return (1, $"no control '{spec}' in window \"{Format.Clip(title, 60)}\"" + (all.Count > 0 ? $" (--nth {nth}, and {all.Count} have that name)" : ""));
                var node = all[nth - 1];
                (rect, found, off) = (node.Rect, hwnd, node.Offscreen || node.Rect.Width <= 0);
                return (0, Core(node));
            }
            finally
            {
                foreach (var n in nodes) Release(n.Element);
            }
        }, 15);
        return (code == 0 ? 0 : code == 1 && text.StartsWith("no control", StringComparison.Ordinal) ? 1 : 2, text, rect, found, off);
    }
}
