using System.Runtime.InteropServices;

namespace AgentProbe.Native;

/// <summary>What a shortcut (.lnk) starts: the program, its arguments and the folder it starts in ("Start in").</summary>
internal sealed record ShortcutInfo(string Target, string Arguments, string WorkingDirectory)
{
    /// <summary>
    /// A shortcut made by Windows Installer ("advertised"): the link does not hold the program's path, Windows
    /// Installer works it out when the shortcut is used, and what the link gives as its target is only the icon
    /// file it keeps under the Windows folder. Recognised by that location.
    /// </summary>
    public bool Advertised => Target.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer") + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads a .lnk file through the shell's own reader (IShellLinkW + IPersistFile), so the answer is what Explorer
/// would start. COM is called through raw vtables, as in UiaTools (no runtime COM interop under NativeAOT).
/// </summary>
internal static unsafe partial class ShellLink
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-C000-000000000046");

    /// <summary>Null, with the reason in <paramref name="error"/>, when the file cannot be read as a shortcut.</summary>
    public static ShortcutInfo? Read(string path, out string? error)
    {
        ShortcutInfo? info = null;
        string? failure = null;
        var thread = new Thread(() =>
        {
            var init = CoInitializeEx(0, 2); // apartment threaded, which is what the shell link object wants
            nint link = 0, file = 0;
            try
            {
                var clsid = ClsidShellLink;
                var iid = IidShellLinkW;
                var hr = CoCreateInstance(&clsid, 0, 1, &iid, &link);
                if (hr < 0 || link == 0)
                {
                    failure = $"the shell's shortcut reader is not available (0x{hr:X8})";
                    return;
                }

                var iidFile = IidPersistFile;
                hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)link)[0])(link, &iidFile, &file); // QueryInterface
                if (hr < 0 || file == 0)
                {
                    failure = $"the shell's shortcut reader is not available (0x{hr:X8})";
                    return;
                }

                fixed (char* p = path) hr = ((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)(*(void***)file)[5])(file, p, 0); // IPersistFile::Load, read-only
                if (hr < 0)
                {
                    failure = $"it is not a readable shortcut (0x{hr:X8})";
                    return;
                }

                var buffer = new char[4096];
                fixed (char* b = buffer)
                {
                    b[0] = '\0';
                    // A failed call is an error, not "the shortcut has none": S_FALSE with an empty text is how
                    // a shortcut that points to no file answers.
                    hr = ((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)(*(void***)link)[3])(link, b, buffer.Length, 0, 0); // GetPath
                    var target = new string(b);
                    var failed = hr < 0 ? $"its target could not be read (0x{hr:X8})" : null;
                    b[0] = '\0';
                    hr = ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(void***)link)[8])(link, b, buffer.Length); // GetWorkingDirectory
                    var folder = new string(b);
                    if (hr < 0) failed ??= $"its start folder could not be read (0x{hr:X8})";
                    b[0] = '\0';
                    hr = ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(void***)link)[10])(link, b, buffer.Length); // GetArguments
                    var arguments = new string(b);
                    if (hr < 0) failed ??= $"its arguments could not be read (0x{hr:X8})";
                    if (failed != null)
                    {
                        failure = failed;
                        return;
                    }

                    info = new ShortcutInfo(Environment.ExpandEnvironmentVariables(target), arguments, Environment.ExpandEnvironmentVariables(folder));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex.Message;
            }
            finally
            {
                if (file != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)file)[2])(file);
                if (link != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)link)[2])(link);
                if (init >= 0) CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "shell link",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            error = "the shell did not answer within 10s";
            return null;
        }

        error = failure;
        return failure == null ? info : null;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);
}
