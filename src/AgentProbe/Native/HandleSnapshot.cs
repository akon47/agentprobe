using System.Runtime.InteropServices;

namespace AgentProbe.Native;

/// <summary>
/// System-wide handle table grouped by object type (File, Event, Key, Thread, Section...).
/// Knowing WHICH kind of handle keeps growing is usually the fastest way to find a handle leak.
/// </summary>
internal static unsafe partial class HandleSnapshot
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypesInformation = 3;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static string[]? _typeNames;

    /// <summary>Handle counts per type name for one process (or for every process when <paramref name="pid"/> is null).</summary>
    public static Dictionary<int, Dictionary<string, int>> CountByType(int? pid)
    {
        var names = TypeNames();
        var result = new Dictionary<int, Dictionary<string, int>>();
        var size = 4u << 20;
        while (true)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                var status = Win32.NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (1u << 20));
                    continue;
                }

                if (status < 0) throw new InvalidOperationException($"handle query failed: 0x{status:X8}");
                var count = *(long*)buffer;
                var entry = buffer + 16;
                for (long i = 0; i < count; i++, entry += 40)
                {
                    var owner = (int)*(long*)(entry + 8);
                    if (pid != null && owner != pid) continue;
                    var typeIndex = *(ushort*)(entry + 30);
                    var name = typeIndex < names.Length && names[typeIndex] != null ? names[typeIndex] : "type" + typeIndex;
                    if (!result.TryGetValue(owner, out var perType)) result[owner] = perType = new Dictionary<string, int>();
                    perType[name] = perType.GetValueOrDefault(name) + 1;
                }

                return result;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
    }

    /// <summary>Object type names indexed by the type index used in the handle table.</summary>
    private static string[] TypeNames()
    {
        if (_typeNames != null) return _typeNames;
        var names = new string[256];
        var size = 64u << 10;
        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            var status = NtQueryObject(0, ObjectTypesInformation, buffer, size, out var needed);
            if (status == StatusInfoLengthMismatch)
            {
                NativeMemory.Free(buffer);
                size = needed + 4096;
                buffer = (byte*)NativeMemory.Alloc(size);
                status = NtQueryObject(0, ObjectTypesInformation, buffer, size, out _);
            }

            if (status >= 0)
            {
                var count = *(uint*)buffer;
                var p = buffer + 8; // entries start pointer-aligned after the count
                for (var i = 0; i < count && p + 104 <= buffer + size; i++)
                {
                    var length = *(ushort*)p;
                    var maxLength = *(ushort*)(p + 2);
                    var text = *(char**)(p + 8);
                    var index = p[90]; // TypeIndex (Windows 8.1+); older systems use position + 2
                    if (index == 0) index = (byte)(i + 2);
                    if (text != null) names[index] = new string(text, 0, length / 2);
                    p += 104 + ((maxLength + 7) & ~7);
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return _typeNames = names;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(nint handle, int infoClass, void* buffer, uint length, out uint returnLength);
}
