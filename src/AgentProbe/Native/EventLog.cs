using System.Runtime.InteropServices;

namespace AgentProbe.Native;

/// <summary>
/// The Windows event log API (wevtapi.dll). Events come as their XML, and their message is formatted only when the
/// caller asks for it (that is the slow part). Everything is UTF-16: wevtutil writes in the console code page, which
/// turns every character that code page lacks (the direction marks Windows puts around dates, other scripts) into '?'.
/// </summary>
internal static unsafe partial class EventLog
{
    private const uint EvtQueryChannelPath = 0x1;
    private const uint EvtQueryForwardDirection = 0x100;
    private const uint EvtQueryReverseDirection = 0x200;
    private const uint EvtRenderEventXml = 1;
    private const uint EvtFormatMessageEvent = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;

    private static readonly Dictionary<string, nint> Publishers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the events of <paramref name="log"/> that match <paramref name="xpath"/>, newest first unless told
    /// otherwise. <paramref name="onEvent"/> gets each event's XML and a function that formats its message (valid
    /// during the call only, null when the provider's messages cannot be found); it returns false to stop.
    /// <paramref name="error"/> says why the log could not be read, or why reading stopped early.
    /// </summary>
    public static void Read(string log, string xpath, int max, Func<string, Func<string?>, bool> onEvent, out string? error, bool newestFirst = true, TimeSpan? limit = null)
    {
        error = null;
        nint results;
        fixed (char* path = log, query = xpath) results = EvtQuery(0, path, query, EvtQueryChannelPath | (newestFirst ? EvtQueryReverseDirection : EvtQueryForwardDirection));
        if (results == 0 && newestFirst && Marshal.GetLastPInvokeError() == 15022)
        {
            // ERROR_EVT_INVALID_OPERATION_OVER_ENABLED_DIRECT_CHANNEL: an analytic or debug log reads forward only.
            fixed (char* path = log, query = xpath) results = EvtQuery(0, path, query, EvtQueryChannelPath | EvtQueryForwardDirection);
        }

        if (results == 0)
        {
            error = Describe(Marshal.GetLastPInvokeError());
            return;
        }

        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(60));
        var handles = stackalloc nint[64];
        var buffer = new char[16 * 1024];
        var read = 0;
        try
        {
            while (read < max)
            {
                if (!EvtNext(results, (uint)Math.Min(64, max - read), handles, 30000, 0, out var count))
                {
                    var code = Marshal.GetLastPInvokeError();
                    if (code != ErrorNoMoreItems) error = code == 1460 ? "reading took too long and was stopped" : Describe(code);
                    return;
                }

                var stop = false;
                for (var i = 0; i < count; i++)
                {
                    var handle = handles[i];
                    try
                    {
                        if (stop) continue;
                        read++;
                        var xml = Render(handle, ref buffer);
                        if (xml == null) continue;
                        stop = !onEvent(xml, () => FormatMessage(handle, xml));
                    }
                    finally
                    {
                        EvtClose(handle);
                    }
                }

                if (stop) return;
                if (DateTime.UtcNow > deadline)
                {
                    error = $"reading was stopped after {(limit ?? TimeSpan.FromSeconds(60)).TotalSeconds:0} s, with {read} events read";
                    return;
                }
            }
        }
        finally
        {
            EvtClose(results);
        }
    }

    private static string? Render(nint handle, ref char[] buffer)
    {
        uint used;
        bool ok;
        fixed (char* b = buffer) ok = EvtRender(0, handle, EvtRenderEventXml, (uint)(buffer.Length * 2), b, out used, out _);
        if (!ok && Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer)
        {
            buffer = new char[(used / 2) + 16];
            fixed (char* b = buffer) ok = EvtRender(0, handle, EvtRenderEventXml, (uint)(buffer.Length * 2), b, out used, out _);
        }

        return ok && used >= 2 ? new string(buffer, 0, (int)(used / 2)).TrimEnd('\0') : null;
    }

    private static string? FormatMessage(nint handle, string xml)
    {
        var provider = ProviderName(xml);
        if (provider.Length == 0) return null;

        // In English where Windows has the English text (output for an agent, and the same words to search for
        // on every PC), otherwise in the language Windows shows it in.
        return FormatMessage(handle, Publisher(provider, English)) ?? FormatMessage(handle, Publisher(provider, UiLanguage));
    }

    private const uint English = 0x409;

    private static readonly uint UiLanguage = GetUserDefaultUILanguage();

    private static string? FormatMessage(nint handle, nint metadata)
    {
        if (metadata == 0) return null;
        var buffer = new char[4096];
        uint used;
        bool ok;
        fixed (char* b = buffer) ok = EvtFormatMessage(metadata, handle, 0, 0, null, EvtFormatMessageEvent, (uint)buffer.Length, b, out used);
        if (!ok && Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer && used > buffer.Length)
        {
            buffer = new char[used];
            fixed (char* b = buffer) ok = EvtFormatMessage(metadata, handle, 0, 0, null, EvtFormatMessageEvent, (uint)buffer.Length, b, out used);
        }

        return ok && used > 0 ? new string(buffer, 0, (int)used).TrimEnd('\0') : null;
    }

    /// <summary>The Name attribute of the event's Provider element, without parsing the whole event.</summary>
    private static string ProviderName(string xml)
    {
        var i = xml.IndexOf("<Provider Name=", StringComparison.Ordinal);
        if (i < 0) return "";
        i += 15;
        if (i >= xml.Length) return "";
        var quote = xml[i];
        var end = xml.IndexOf(quote, i + 1);
        return end > i ? System.Net.WebUtility.HtmlDecode(xml[(i + 1)..end]) : "";
    }

    /// <summary>The provider's message tables, opened once per process and language (0 when it has none on this PC).</summary>
    private static nint Publisher(string name, uint locale)
    {
        var key = name + "|" + locale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (Publishers)
        {
            if (Publishers.TryGetValue(key, out var h)) return h;
            fixed (char* n = name) h = EvtOpenPublisherMetadata(0, n, null, locale, 0);
            Publishers[key] = h;
            return h;
        }
    }

    /// <summary>Is there an event provider (publisher) of this name on this PC?</summary>
    public static bool PublisherExists(string name) => Publisher(name, 0) != 0;

    /// <summary>The names of every event provider registered on this PC.</summary>
    public static List<string> PublisherNames()
    {
        var list = new List<string>();
        var e = EvtOpenPublisherEnum(0, 0);
        if (e == 0) return list;
        try
        {
            var buffer = new char[512];
            while (true)
            {
                uint used;
                bool ok;
                fixed (char* b = buffer) ok = EvtNextPublisherId(e, (uint)buffer.Length, b, out used);
                if (!ok && Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer)
                {
                    buffer = new char[used + 16];
                    continue;
                }

                if (!ok) break;
                list.Add(new string(buffer, 0, (int)used).TrimEnd('\0'));
            }
        }
        finally
        {
            EvtClose(e);
        }

        return list;
    }

    /// <summary>Every log (channel) on this PC.</summary>
    public static List<string> LogNames()
    {
        var list = new List<string>();
        var e = EvtOpenChannelEnum(0, 0);
        if (e == 0) return list;
        try
        {
            var buffer = new char[512];
            while (true)
            {
                uint used;
                bool ok;
                fixed (char* b = buffer) ok = EvtNextChannelPath(e, (uint)buffer.Length, b, out used);
                if (!ok && Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer)
                {
                    buffer = new char[used + 16];
                    continue;
                }

                if (!ok) break;
                list.Add(new string(buffer, 0, (int)used).TrimEnd('\0'));
            }
        }
        finally
        {
            EvtClose(e);
        }

        return list;
    }

    /// <summary>How many events a log holds now, or null with the reason when that cannot be read.</summary>
    public static long? RecordCount(string log, out string? error)
    {
        error = null;
        nint h;
        fixed (char* p = log) h = EvtOpenLog(0, p, 1); // EvtOpenChannelPath
        if (h == 0)
        {
            error = Describe(Marshal.GetLastPInvokeError());
            return null;
        }

        try
        {
            var variant = stackalloc byte[16 + 64];
            if (!EvtGetLogInfo(h, 5, 16 + 64, variant, out _)) // EvtLogNumberOfLogRecords
            {
                error = Describe(Marshal.GetLastPInvokeError());
                return null;
            }

            var type = *(uint*)(variant + 12);
            return type == 0 ? 0 : (long)*(ulong*)variant; // EvtVarTypeNull for a log that was never written
        }
        finally
        {
            EvtClose(h);
        }
    }

    public static string Describe(int code) => code switch
    {
        2 or 15007 => "no such log on this PC",
        5 => "access denied (this log needs agentprobe elevated on the target)",
        15001 => "the query is not valid for the event log (XPath subset)",
        15002 => "no such event provider",
        15011 or 15012 => "the log is not available",
        15022 => "an analytic or debug log, which cannot be read this way",
        15026 => "the query is too complex for the event log",
        1460 => "the query took too long",
        1717 or 1722 or 1753 => "the Windows Event Log service is not running",
        _ => $"error {code} ({new System.ComponentModel.Win32Exception(code).Message})",
    };

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtQuery(nint session, char* path, char* query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint resultSet, uint size, nint* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferSize, char* buffer, out uint bufferUsed, out uint propertyCount);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtOpenPublisherMetadata(nint session, char* publisherId, char* logFilePath, uint locale, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtFormatMessage(nint publisherMetadata, nint evt, uint messageId, uint valueCount, void* values, uint flags, uint bufferSize, char* buffer, out uint bufferUsed);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtOpenPublisherEnum(nint session, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNextPublisherId(nint publisherEnum, uint bufferSize, char* buffer, out uint bufferUsed);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtOpenChannelEnum(nint session, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNextChannelPath(nint channelEnum, uint bufferSize, char* buffer, out uint bufferUsed);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    private static partial nint EvtOpenLog(nint session, char* path, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtGetLogInfo(nint log, int propertyId, uint bufferSize, byte* buffer, out uint bufferUsed);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
