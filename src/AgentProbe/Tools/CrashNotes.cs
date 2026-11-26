using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Target;

namespace AgentProbe.Tools;

/// <summary>
/// "Did anything crash?" answered without being asked. After each command the Application log is looked at for
/// crashes and hangs that Windows recorded since the last look, and each is added to that command's answer as one
/// line. An agent then has no reason to run `crashes` after every step just in case, and a crash that happens
/// between two commands (the app died while the agent was thinking) is not missed.
/// <para>
/// The log is read through the event log API, not by starting wevtutil: this runs after every command, and a
/// process started each time would itself show up in what is being watched.
/// </para>
/// </summary>
internal static unsafe partial class CrashNotes
{
    public static void Report(TargetRuntime runtime, TextWriter err)
    {
        try
        {
            var since = runtime.CrashesSeenUtc;
            var xpath = $"*[System[(EventID=1000 or EventID=1002 or EventID=1026) and TimeCreated[@SystemTime>'{since.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture)}']]]";
            var xml = Query("Application", xpath, 8);
            if (xml.Length == 0) return;
            var events = EventTools.Parse(xml, "Application").Where(e => e.TimeUtc > since).OrderBy(e => e.TimeUtc).ToList();
            if (events.Count == 0) return;
            runtime.CrashesSeenUtc = events[^1].TimeUtc;
            foreach (var e in events)
            {
                if (EventTools.CrashLine(e) is { } line)
                    err.WriteLine($"[Windows logged this on the target since the last command: {Util.Format.Time(e.TimeUtc)} {line}. `crashes` has the details]");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Xml.XmlException or InvalidOperationException)
        {
            // a courtesy: it must never fail the command it follows
        }
    }

    /// <summary>The newest matching events of a log as XML, one &lt;Event&gt; after the other; "" when there are none or the log cannot be read.</summary>
    private static string Query(string log, string xpath, int max)
    {
        nint results;
        fixed (char* path = log, query = xpath) results = EvtQuery(0, path, query, 0x1 | 0x200); // EvtQueryChannelPath | EvtQueryReverseDirection
        if (results == 0) return "";
        var sb = new StringBuilder();
        try
        {
            var handles = stackalloc nint[max];
            if (!EvtNext(results, (uint)max, handles, 2000, 0, out var count)) return "";
            var buffer = new byte[32 * 1024];
            for (var i = 0; i < count; i++)
            {
                try
                {
                    uint used;
                    bool ok;
                    fixed (byte* b = buffer) ok = EvtRender(0, handles[i], 1, (uint)buffer.Length, b, out used, out _); // EvtRenderEventXml
                    if (!ok && Marshal.GetLastPInvokeError() == 122 && used > buffer.Length) // ERROR_INSUFFICIENT_BUFFER
                    {
                        buffer = new byte[used];
                        fixed (byte* b = buffer) ok = EvtRender(0, handles[i], 1, (uint)buffer.Length, b, out used, out _);
                    }

                    if (ok && used >= 2) sb.Append(Encoding.Unicode.GetString(buffer, 0, (int)used).TrimEnd('\0')).Append('\n');
                }
                finally
                {
                    EvtClose(handles[i]);
                }
            }
        }
        finally
        {
            EvtClose(results);
        }

        return sb.ToString();
    }

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtQuery(nint session, char* path, char* query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, uint size, nint* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferSize, byte* buffer, out uint bufferUsed, out uint propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
