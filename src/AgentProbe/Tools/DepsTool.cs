using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using AgentProbe.Native;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// "Why does it not start on that PC": walks the import tables like Dependencies/Dependency Walker and reports only
/// what is wrong: missing DLLs, x86/x64 mismatches, functions a DLL does not export, a missing .NET runtime.
/// DLLs are searched the way the loader would for THAT image's bitness (SysWOW64 for 32-bit apps).
/// </summary>
internal static partial class DepsTool
{
    public static IEnumerable<Tool> Tools =>
    [
        new("deps", "Files", "Check an EXE/DLL's dependencies: missing DLLs, x86/x64 mismatch, missing exports, .NET runtime",
            """
            deps <file> [--tree] [--deep]
            deps <exe> --run [--cwd DIR] [--wait 6s] [--env NAME=VALUE]... [-- arguments for the program]
              Prints problems only (plus a one-line summary). --tree prints the whole tree, --deep also walks into
              Windows system DLLs. Typical findings: a VC++ runtime that is not installed (0xC0000135), an x64 DLL
              next to an x86 app (0xC000007B), an API this Windows build lacks (0xC0000139), no matching .NET runtime.
              A .NET app is also looked at the way its host (apphost/hostfxr) sees it: the app's DLL, its
              .runtimeconfig.json and .deps.json, the files the .deps.json lists, the runtime it asks for. An app
              that fails there shows no window, writes no log and leaves nothing in the event log.
              --run answers "what does the .NET host do with it?" by starting the app once with the host's own
              tracing switched on (COREHOST_TRACE) and reading the trace: the mode the host took, the runtime it
              chose, what it wrote when it gave up, the exit code. No administrator rights are needed. The app is
              started for real, as a job, in --cwd (default: its own folder, as a double-click does); when the
              host succeeds the app keeps running as that job (`kill <job>`). --wait is how long to wait for the
              host's verdict.
            """, DepsAsync) { Quiet = true, LoudWhen = args => ArgParser.Parse(args, "tree", "deep", "run").Has("run") },
    ];

    private sealed class Image
    {
        public string Path = "";
        public Machine Machine;
        public bool Managed;
        public string Description = "";
        public List<(string dll, List<string> functions)> Imports = [];
        public List<string> DelayImports = [];
        public HashSet<string>? Exports;
    }

    private static Image? Load(string path, bool wantExports)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            if (h.PEHeader == null) return null;
            var image = new Image { Path = path, Machine = h.CoffHeader.Machine, Managed = h.CorHeader != null, Description = FileTools.DescribePe(path) ?? "" };
            var is64 = h.PEHeader.Magic == PEMagic.PE32Plus;

            var imports = h.PEHeader.ImportTableDirectory;
            if (imports.Size > 0 && imports.RelativeVirtualAddress > 0)
            {
                var r = pe.GetSectionData(imports.RelativeVirtualAddress).GetReader();
                while (r.RemainingBytes >= 20)
                {
                    var originalThunk = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt32();
                    var nameRva = r.ReadInt32();
                    var firstThunk = r.ReadInt32();
                    if (nameRva == 0 && firstThunk == 0) break;
                    var functions = new List<string>();
                    var thunkRva = originalThunk != 0 ? originalThunk : firstThunk;
                    if (thunkRva != 0)
                    {
                        var t = pe.GetSectionData(thunkRva).GetReader();
                        while (t.RemainingBytes >= (is64 ? 8 : 4) && functions.Count < 20000)
                        {
                            var value = is64 ? t.ReadUInt64() : t.ReadUInt32();
                            if (value == 0) break;
                            var byOrdinal = (value & (is64 ? 0x8000000000000000UL : 0x80000000UL)) != 0;
                            if (!byOrdinal) functions.Add(ReadString(pe, (int)(value & 0x7FFFFFFF) + 2));
                        }
                    }

                    image.Imports.Add((ReadString(pe, nameRva), functions));
                }
            }

            var delay = h.PEHeader.DelayImportTableDirectory;
            if (delay.Size > 0 && delay.RelativeVirtualAddress > 0)
            {
                var r = pe.GetSectionData(delay.RelativeVirtualAddress).GetReader();
                while (r.RemainingBytes >= 32)
                {
                    var attributes = r.ReadUInt32();
                    var nameRva = r.ReadUInt32();
                    r.ReadBytes(24);
                    if (nameRva == 0) break;
                    if ((attributes & 1) == 0) nameRva -= (uint)h.PEHeader.ImageBase; // old format stores virtual addresses
                    var name = ReadString(pe, (int)nameRva);
                    if (name.Length > 0) image.DelayImports.Add(name);
                }
            }

            if (wantExports)
            {
                image.Exports = new HashSet<string>(StringComparer.Ordinal);
                var exports = h.PEHeader.ExportTableDirectory;
                if (exports.Size > 0 && exports.RelativeVirtualAddress > 0)
                {
                    var r = pe.GetSectionData(exports.RelativeVirtualAddress).GetReader();
                    r.ReadBytes(24);
                    var nameCount = r.ReadInt32();
                    r.ReadInt32();
                    var namesRva = r.ReadInt32();
                    if (namesRva != 0)
                    {
                        var n = pe.GetSectionData(namesRva).GetReader();
                        for (var i = 0; i < nameCount && n.RemainingBytes >= 4; i++) image.Exports.Add(ReadString(pe, n.ReadInt32()));
                    }
                }
            }

            return image;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReadString(PEReader pe, int rva)
    {
        if (rva <= 0) return "";
        try
        {
            var r = pe.GetSectionData(rva).GetReader();
            var sb = new StringBuilder();
            while (r.RemainingBytes > 0 && sb.Length < 512)
            {
                var b = r.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }

            return sb.ToString();
        }
        catch (BadImageFormatException)
        {
            return "";
        }
    }

    private static bool SideBySide(string dll)
        => Regex.IsMatch(dll, @"^(comctl32|gdiplus|msvc[rpm](80|90)|mfc(80|90)u?|atl(80|90))\.dll$", RegexOptions.IgnoreCase);

    private static string? Hint(string dll, Machine machine)
    {
        var arch = machine == Machine.I386 ? "x86" : machine == Machine.Arm64 ? "arm64" : "x64";
        var n = dll.ToLowerInvariant();
        if (Regex.IsMatch(n, @"^(vcruntime140|msvcp140|concrt140|vccorlib140|vcomp140|mfc140)")) return $"install the Microsoft Visual C++ 2015-2022 Redistributable ({arch})";
        if (Regex.IsMatch(n, @"^(msvcr120|msvcp120|mfc120)")) return $"install the Visual C++ 2013 Redistributable ({arch})";
        if (Regex.IsMatch(n, @"^(msvcr110|msvcp110|mfc110)")) return $"install the Visual C++ 2012 Redistributable ({arch})";
        if (Regex.IsMatch(n, @"^(msvcr100|msvcp100|mfc100)")) return $"install the Visual C++ 2010 Redistributable ({arch})";
        if (Regex.IsMatch(n, @"^(msvcr90|msvcp90|msvcr80|msvcp80|mfc90|mfc80)")) return $"Visual C++ 2005/2008 runtime ({arch}); normally loaded from WinSxS through the app manifest";
        if (Regex.IsMatch(n, @"^(d3dx9_|d3dx10_|d3dx11_|d3dcompiler_4[0-3]|xinput1_[1-3]|xaudio2_[0-7])")) return "install the legacy DirectX End-User Runtime (June 2010)";
        if (n.StartsWith("ucrtbase", StringComparison.Ordinal)) return "Universal C Runtime missing (Windows update KB2999226 on old systems)";
        if (n is "mscoree.dll") return ".NET Framework is not installed";
        return null;
    }

    private static Task<int> DepsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "tree", "deep", "run");
        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("deps: which file?"));
        var path = ctx.Runtime.ResolvePath(a.Positionals[0]);
        if (!File.Exists(path)) return Task.FromResult(ctx.Fail($"deps: {path} does not exist."));
        var root = Load(path, false);
        if (root == null) return Task.FromResult(ctx.Fail($"deps: {path} is not a Windows executable image."));
        if (a.Flag("run")) return RunWithHostTraceAsync(ctx, a, path);

        var o = ctx.Out;
        o.WriteLine($"{path}  ({root.Description})");
        var problems = new List<string>();
        var tree = new StringBuilder();

        // A framework-dependent .NET app starts through a small native host (MyApp.exe) next to MyApp.runtimeconfig.json.
        if (root.Managed || File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json"))) CheckDotnet(path, root, problems, o);

        // Loader search order for this image's bitness. This tool is a 64-bit process, so System32 is the real one.
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var is32 = root.Machine == Machine.I386;
        var systemDir = Path.Combine(windir, is32 && Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32");
        var searchDirs = new List<string> { Path.GetDirectoryName(path)!, systemDir, windir };
        searchDirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !(is32 && p.StartsWith(Path.Combine(windir, "System32"), StringComparison.OrdinalIgnoreCase)))); // 32-bit apps see SysWOW64 there

        var cache = new Dictionary<string, Image?>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deep = a.Flag("deep");
        int resolved = 0, apiSets = 0;

        string? Resolve(string dll)
        {
            foreach (var dir in searchDirs)
            {
                var candidate = Path.Combine(dir, dll);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        void Walk(Image image, string chain, int depth)
        {
            foreach (var (dll, functions) in image.Imports)
            {
                var indent = new string(' ', (depth + 1) * 2);
                if (dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase))
                {
                    apiSets++;
                    continue; // API sets are resolved by Windows itself, not by a file on disk
                }

                var found = Resolve(dll);
                if (found == null)
                {
                    var hint = Hint(dll, root.Machine);
                    problems.Add($"MISSING     {dll}  needed by {chain}{(hint != null ? $"\n            -> {hint}" : "")}");
                    tree.Append(indent).Append(dll).Append("  MISSING\n");
                    continue;
                }

                resolved++;
                if (!cache.TryGetValue(found, out var dep)) cache[found] = dep = Load(found, true);
                var system = found.StartsWith(windir, StringComparison.OrdinalIgnoreCase);
                tree.Append(indent).Append(dll).Append("  ").Append(found).Append('\n');
                if (dep == null)
                {
                    problems.Add($"UNREADABLE  {found}  needed by {chain} (not a valid image, or access denied)");
                    continue;
                }

                if (dep.Machine != root.Machine && !(dep.Managed && dep.Machine == Machine.I386))
                    problems.Add($"WRONG ARCH  {found} is {dep.Description.Split(' ')[0]} but {Path.GetFileName(root.Path)} is {root.Description.Split(' ')[0]} (fails with 0xC000007B)  needed by {chain}");

                // Common controls, GDI+ and the VC++ 2005/2008 runtimes are picked from WinSxS by the app's manifest,
                // not from the folder searched here, so their export lists cannot be judged from this copy.
                if (dep.Exports != null && dep.Exports.Count > 0 && !SideBySide(dll))
                {
                    var missing = functions.Where(f => f.Length > 0 && !dep.Exports.Contains(f)).ToList();
                    if (missing.Count > 0)
                        problems.Add($"NO EXPORT   {dll} lacks {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? $" (+{missing.Count - 6} more)" : "")}  needed by {chain}\n" +
                                     $"            -> {(system ? "this Windows build does not provide it (app needs a newer Windows?)" : "a different version of that DLL is being picked up")} (0xC0000139)");
                }

                if (visited.Add(found) && depth < 12 && (!system || deep)) Walk(dep, $"{dll} <- {chain}", depth + 1);
            }
        }

        Walk(root, Path.GetFileName(path), 0);

        var optionalMissing = root.DelayImports.Where(d => !d.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) && !d.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase) && Resolve(d) == null).ToList();

        if (a.Flag("tree"))
        {
            o.WriteLine(Path.GetFileName(path));
            o.Write(tree.ToString());
        }

        foreach (var p in problems.Distinct()) o.WriteLine(p);
        if (optionalMissing.Count > 0) o.WriteLine($"delay-loaded, missing (fails only when that feature is used): {string.Join(", ", optionalMissing)}");
        o.WriteLine(problems.Count == 0
            ? $"ok: {resolved} imports resolved{(apiSets > 0 ? $", {apiSets} API-set imports left to Windows" : "")}; searched {Path.GetDirectoryName(path)}, {systemDir}, PATH"
            : $"{problems.Distinct().Count()} problem(s). DLLs searched in: app folder, {systemDir}, {windir}, PATH");
        if (root.Imports.Count == 0 && !root.Managed) o.WriteLine("(no import table: packed/protected executable, or a resource-only DLL)");
        return Task.FromResult(problems.Count == 0 ? 0 : 1);
    }

    /// <summary>Managed apps fail before any DLL import matters: check that the runtime they ask for exists.</summary>
    private static void CheckDotnet(string path, Image root, List<string> problems, TextWriter o)
    {
        var config = Path.ChangeExtension(path, ".runtimeconfig.json");
        if (!File.Exists(config))
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new PEReader(fs);
                var md = pe.GetMetadataReader();
                var targets = md.AssemblyReferences.Select(h => md.GetAssemblyReference(h)).Select(r => $"{md.GetString(r.Name)} {r.Version}").Where(n => n.StartsWith("mscorlib", StringComparison.Ordinal) || n.StartsWith("System.Runtime ", StringComparison.Ordinal) || n.StartsWith("netstandard", StringComparison.Ordinal)).ToList();
                o.WriteLine($".NET assembly, metadata {md.MetadataVersion}, references {string.Join(", ", targets)}" + (targets.Any(t => t.StartsWith("mscorlib", StringComparison.Ordinal)) ? " (.NET Framework app)" : " (no runtimeconfig.json next to it: a library, or a framework-dependent app missing its config)"));
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
            {
                // metadata unreadable: nothing more to say
            }

            return;
        }

        var appDir = Path.GetDirectoryName(path)!;
        var appName = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config), HostJson);
            var wanted = new List<(string name, string version)>();
            if (doc.RootElement.TryGetProperty("runtimeOptions", out var options))
            {
                if (options.TryGetProperty("framework", out var single)) wanted.Add((single.GetProperty("name").GetString()!, single.GetProperty("version").GetString()!));
                if (options.TryGetProperty("frameworks", out var many))
                    foreach (var f in many.EnumerateArray()) wanted.Add((f.GetProperty("name").GetString()!, f.GetProperty("version").GetString()!));
            }

            // The app's own DLL. The exe is a small native host with the DLL's name written into it; only when
            // that name is found there is its absence called a problem.
            var appDll = Path.Combine(appDir, appName + ".dll");
            var appDllMissing = !File.Exists(appDll) && HostIsBoundTo(path, appName + ".dll");
            if (appDllMissing)
                problems.Add($"NO APP DLL  {appName}.dll is not next to {appName}.exe: the exe is only the .NET host for that DLL, and ends with 0x8000809A without it");

            // A runtime of its own in the folder (hostfxr + coreclr): the host uses that one, whatever else is installed.
            var ownHost = Path.Combine(appDir, "hostfxr.dll");
            var ownRuntime = File.Exists(ownHost) && File.Exists(Path.Combine(appDir, "coreclr.dll"));
            var hostVersion = File.Exists(ownHost) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(ownHost).ProductVersion?.Split('+', ' ')[0] : null;
            var depsFile = Path.Combine(appDir, appName + ".deps.json");
            var hasDeps = File.Exists(depsFile);
            var depsNote = hasDeps ? ListedFiles(depsFile, appDir, appName, problems, appDllMissing ? appName + ".dll" : null) : $"no {appName}.deps.json";

            if (ownRuntime && !hasDeps && !appDllMissing)
            {
                // fx_muxer.cpp, detect_operating_mode: with coreclr next to the host and no <app>.deps.json there, an
                // <app>.runtimeconfig.json in the CURRENT folder makes the host run in "split/FX" mode, in which
                // the exe is not an app. Reproduced with the .NET 8 and 10 hosts; the same code is in the 9 host.
                // The .NET 11 host no longer does this: it was seen to start such an app from its own folder.
                var major = int.TryParse(hostVersion?.Split('.')[0], out var m) ? m : 0;
                var known = major is >= 8 and <= 10 ? "" : $" (seen with the .NET 8, 9 and 10 hosts; this folder has host {hostVersion ?? "of unknown version"}: `deps <exe> --run` shows what it does)";
                if (major >= 11)
                    depsNote += $" (the .NET {major} host starts the app without it; hosts up to .NET 10 do not, from the app's own folder)";
                else problems.Add($"HOST TRAP   {appName}.deps.json is missing while the folder has its own runtime (coreclr.dll) and {appName}.runtimeconfig.json{known}.\n" +
                             $"            The .NET host then starts the app only when the working folder is NOT the app's folder. Started from its own\n" +
                             $"            folder (a double-click, a shortcut that starts in that folder, `start --exe` without --cwd) it ends with\n" +
                             $"            0x80008081: no window, no log, nothing in the event log.\n" +
                             $"            -> put {appName}.deps.json from the build's publish output next to the exe");
            }

            if (wanted.Count == 0)
            {
                o.WriteLine($".NET app, self-contained (brings its own runtime{(hostVersion != null ? ", host " + hostVersion : "")}); {depsNote}");
                if (!ownRuntime)
                {
                    var missing = new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" }.Where(f => !File.Exists(Path.Combine(appDir, f))).ToList();
                    problems.Add($"NO RUNTIME  {appName}.runtimeconfig.json names no framework, so the app has to bring its own runtime, but {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not in {appDir}");
                }

                return;
            }

            o.WriteLine($".NET app, framework-dependent; {depsNote}" + (ownRuntime ? $"; the folder also has hostfxr.dll and coreclr.dll of its own (host {hostVersion})" : ""));
            var is32 = root.Description.StartsWith("x86", StringComparison.Ordinal) || root.Description.Contains("32-bit preferred", StringComparison.Ordinal);
            var programFiles = Environment.GetEnvironmentVariable(is32 ? "ProgramFiles(x86)" : "ProgramFiles") ?? @"C:\Program Files";
            foreach (var (name, version) in wanted)
            {
                var dir = Path.Combine(programFiles, "dotnet", "shared", name);
                var installed = Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).ToList() : [];
                var major = version.Split('.')[0];

                // Default roll-forward: same major, equal or newer minor/patch.
                var usable = installed.Where(v => v!.Split('.')[0] == major && System.Version.TryParse(v.Split('-')[0], out var have) && System.Version.TryParse(version.Split('-')[0], out var need) && have >= need).ToList();
                o.WriteLine($".NET app needs {name} {version} ({(is32 ? "x86" : "x64")}); installed: {(installed.Count > 0 ? string.Join(", ", installed) : "none")}");
                if (usable.Count == 0)
                    problems.Add($"NO RUNTIME  {name} {major}.x ({(is32 ? "x86" : "x64")}) >= {version} is not installed in {dir}\n            -> install the .NET {major} {(name.Contains("WindowsDesktop", StringComparison.Ordinal) ? "Desktop " : name.Contains("AspNetCore", StringComparison.Ordinal) ? "ASP.NET Core " : "")}Runtime ({(is32 ? "x86" : "x64")})");
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            o.WriteLine($".NET app; could not read {Path.GetFileName(config)}: {ex.Message}");
            if (ex is System.Text.Json.JsonException)
                problems.Add($"BAD CONFIG  {Path.GetFileName(config)} is not valid JSON ({Format.Clip(ex.Message, 120)}): the .NET host cannot read it either");
        }
    }

    /// <summary>The .NET host reads its JSON files with comments allowed; a file is only called unreadable when the host would say so too.</summary>
    private static readonly System.Text.Json.JsonDocumentOptions HostJson = new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip };

    /// <summary>True when the exe is a .NET app host that has <paramref name="dllName"/> written into it as the DLL to run.</summary>
    private static bool HostIsBoundTo(string exe, string dllName)
    {
        try
        {
            using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 64 * 1024 * 1024) return false; // an app host is small; nothing is claimed about anything else
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            var needle = new byte[Encoding.UTF8.GetByteCount(dllName) + 1]; // the name and the zero that ends it
            Encoding.UTF8.GetBytes(dllName, needle);
            return bytes.AsSpan().IndexOf(needle) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The assemblies and native libraries the app's .deps.json lists as part of the app, checked against the
    /// folder. The host looks for each by its file name in the app's folder; one that is not there does not stop
    /// the .NET 8 host in its default setup (checked), the app fails later, when it first needs that file.
    /// Returns the half sentence for the summary line.
    /// </summary>
    /// <param name="alreadyReported">A file whose absence has been reported on its own (the app's DLL), not to be listed again.</param>
    private static string ListedFiles(string depsFile, string dir, string name, List<string> problems, string? alreadyReported)
    {
        var file = Path.GetFileName(depsFile);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile), HostJson);
            var rootElement = doc.RootElement;
            if (!rootElement.TryGetProperty("runtimeTarget", out var rt) || !rt.TryGetProperty("name", out var targetName) ||
                !rootElement.TryGetProperty("targets", out var targets) || !targets.TryGetProperty(targetName.GetString() ?? "", out var target))
                return $"{file} has no runtime target (not checked)";

            // A build output (not a published app) may take package files from the NuGet cache instead of the folder.
            var buildOutput = File.Exists(Path.Combine(dir, name + ".runtimeconfig.dev.json"));
            int listed = 0, missing = 0;
            var examples = new List<string>();
            foreach (var library in target.EnumerateObject())
            {
                foreach (var section in new[] { "runtime", "native" })
                {
                    if (!library.Value.TryGetProperty(section, out var assets) || assets.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    foreach (var asset in assets.EnumerateObject())
                    {
                        var leaf = Path.GetFileName(asset.Name.Replace('/', '\\'));
                        if (leaf.Length == 0 || leaf == "_._") continue; // a placeholder for "nothing"
                        listed++;
                        if (leaf.Equals(alreadyReported, StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(dir, leaf)) || File.Exists(Path.Combine(dir, asset.Name.Replace('/', '\\')))) continue;
                        missing++;
                        if (examples.Count < 8) examples.Add($"{leaf} ({library.Name})");
                    }
                }
            }

            var files = listed == 1 ? "1 file" : $"{listed} files";
            if (missing == 0) return alreadyReported == null ? $"{file} lists {files}, all in the folder" : $"{file} lists {files}, the others all in the folder";
            if (buildOutput) return $"{file} lists {files}, {missing} not in the folder (a build output: those may come from the NuGet cache)";
            problems.Add($"MISSING     {missing} of the {listed} files that {file} lists {(missing == 1 ? "is" : "are")} not in the app's folder: {string.Join(", ", examples)}{(missing > examples.Count ? $" (+{missing - examples.Count} more)" : "")}\n" +
                         "            -> the .NET 8 host starts the app all the same (checked), and the app fails when it first needs one of them (FileNotFoundException,\n" +
                         "               or a silent exit). Other host versions, or a PC with extra probing paths, may refuse to start it instead (0x8000808C).");
            return $"{file} lists {files}, {missing} MISSING";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (ex is System.Text.Json.JsonException)
                problems.Add($"BAD DEPS    {file} is not valid JSON ({Format.Clip(ex.Message, 120)}): the .NET host cannot read it either");
            return $"{file} could not be read ({Format.Clip(ex.Message, 80)})";
        }
    }

    /// <summary>
    /// <c>deps --run</c>: start the program once with the .NET host's tracing on and report what the host decided.
    /// The trace is the host's own account (apphost, hostfxr, hostpolicy), written to a file in the workspace.
    /// </summary>
    private static async Task<int> RunWithHostTraceAsync(ToolContext ctx, ArgParser a, string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return ctx.Fail($"deps --run: {path} is not a program (.exe).");
        var cwd = a.Get("cwd") is { } c ? ctx.Runtime.ResolvePath(c) : Path.GetDirectoryName(path)!;
        if (!Directory.Exists(cwd)) return ctx.Fail($"deps --run: the folder {cwd} does not exist on the target.");
        var wait = a.GetDuration("wait") ?? TimeSpan.FromSeconds(6);
        wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 300));
        var env = ExecTools.ParseEnv(ctx, "deps --run", a);
        if (env == null) return 2;

        var trace = Path.Combine(ctx.Runtime.WorkDir, $"hosttrace-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log");
        env.Add(("COREHOST_TRACE", "1"));
        env.Add(("COREHOST_TRACEFILE", trace));
        env.Add(("DOTNET_DISABLE_GUI_ERRORS", "1")); // the host writes its error instead of showing a dialog nobody can answer
        Job job;
        try
        {
            job = ctx.Runtime.Jobs.StartExe(path, [.. a.Positionals.Skip(1)], cwd, a.Get("name") ?? "hosttrace", env);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ctx.Fail($"deps --run: cannot start {path}: {ex.Message}");
        }

        ctx.Runtime.Audit.Write($"deps --run: started {job.Command} in {cwd} with .NET host tracing (job {job.Id}, pid {job.Pid})");
        var o = ctx.Out;
        var given = a.Positionals.Skip(1).ToList();
        o.WriteLine($"{path} started with .NET host tracing, cwd={cwd}  (job {job.Id}, pid {job.Pid})" +
                    (given.Count > 0 ? "  args: " + Format.Clip(string.Join(' ', given.Select(JobManager.QuoteArgument)), 200) : ""));

        // Until the process ends, the host hands over to the runtime ("Launch host:" in its trace), or time is up.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string[] lines = [];
        var launched = false;
        while (true)
        {
            var exited = job.Process.HasExited;
            lines = ReadTrace(trace);
            launched = lines.Any(l => l.StartsWith("Launch host:", StringComparison.Ordinal));
            if (exited || launched || sw.Elapsed >= wait) break;
            await Task.Delay(200, ctx.Ct);
        }

        // A moment more: an app that dies right after the hand-over should be reported as dead, not as running.
        if (launched && !job.Process.HasExited) await Task.WhenAny(job.Process.WaitForExitAsync(ctx.Ct), Task.Delay(1200, ctx.Ct));
        var ended = job.Process.HasExited;
        var took = sw.Elapsed;
        if (ended) lines = ReadTrace(trace);

        string? Version(string component) => lines.Select(l => Regex.Match(l, @"^--- Invoked " + component + @"\w* \[version: ([^ \]]+)")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
        if (lines.Length == 0)
        {
            o.WriteLine(File.Exists(trace)
                ? $"host     a host trace file was started but nothing could be read from it within {Format.Duration(took)}: nothing is said about the host"
                : $"host     no host trace was written within {Format.Duration(took)}: no .NET host that writes one ran in this process (so: a native, .NET Framework or NativeAOT program, or a host older than .NET Core 3). Nothing is said about a host.");
        }
        else
        {
            var versions = new[] { ("apphost", Version("apphost")), ("hostfxr", Version("hostfxr")), ("hostpolicy", Version("hostpolicy")) }.Where(v => v.Item2 != null).Select(v => $"{v.Item1} {v.Item2}");
            o.WriteLine("host     " + string.Join(", ", versions));
            var mode = lines.Select(l => Regex.Match(l, @"^--- Executing in (.+?) mode")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
            if (mode != null)
            {
                o.WriteLine("mode     " + mode switch
                {
                    "a native executable" => "app (the host runs the DLL this exe belongs to)",
                    "split/FX" => "split/FX: NOT how an app runs. The host took its own exe for the `dotnet` command, which it does when the folder has a runtime " +
                                  "of its own, no <app>.deps.json, and the working folder holds <app>.runtimeconfig.json (see `deps <exe>`)",
                    "muxer" => "muxer (the `dotnet` command: its first argument is the app or the SDK command to run)",
                    _ => mode,
                });
            }

            var kind = lines.Select(l => Regex.Match(l, @"^Executing as an? (self-contained|framework-dependent) app")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
            var coreclr = lines.Select(l => Regex.Match(l, @"^CoreCLR path = '([^']+)'")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
            if (kind != null || coreclr != null) o.WriteLine("app      " + string.Join(", ", new[] { kind, coreclr != null ? "runtime " + coreclr : null }.Where(x => x != null)));
        }

        var code = ended ? ProcessDetails.ExitCodeName((uint)job.Process.ExitCode) : "";
        var failed = false;
        if (launched && !ended)
        {
            o.WriteLine($"result   the host started the runtime and the app is running (job {job.Id}, pid {job.Pid}): `kill {job.Id}` closes it. What happens from here is the app's own start-up, not the host's.");
        }
        else if (launched)
        {
            o.WriteLine($"result   the host started the runtime; the app ended after {Format.Duration(took)} with exit {code}. That is the app's own doing, not the host's: `crashes`, `events`, `logs {job.Id}`.");
        }
        else if (ended)
        {
            // The trace file is shared with whatever the program starts, and only the "run an app" path writes
            // the hand-over line: its absence alone does not mean the host failed. The host's own exit codes do.
            failed = (uint)job.Process.ExitCode is >= 0x80008081 and <= 0x800080FF;
            o.WriteLine(failed
                ? $"result   the host gave up before it started the runtime: exit {code} after {Format.Duration(took)}"
                : lines.Length > 0
                    ? $"result   the program ended after {Format.Duration(took)} with exit {code}. The trace has no hand-over to the runtime for it, and the exit code is not one of the host's: " +
                      "a launcher that started the real app and left, or a program that hosts .NET in its own way"
                    : $"result   the program ended after {Format.Duration(took)} with exit {code}");
        }
        else
        {
            o.WriteLine(lines.Length > 0
                ? $"result   after {Format.Duration(took)} the trace shows no hand-over to the runtime, and the program is still running (job {job.Id}, pid {job.Pid}): " +
                  $"a host that is stuck, or a program that hosts .NET in its own way. The last lines of the trace show where it is; `kill {job.Id}` closes it"
                : $"result   the program is running (job {job.Id}, pid {job.Pid}): `kill {job.Id}` closes it");
        }

        if (ended && !launched)
        {
            // What it wrote (the host explains itself on stderr), else where the trace stops.
            await Task.Delay(300, ctx.Ct); // the last of its output
            var written = job.Read(true).Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (written.Count > 0)
            {
                o.WriteLine("it wrote:");
                foreach (var l in written.Take(25)) o.WriteLine("  " + Format.Clip(l, 240));
                if (written.Count > 25) o.WriteLine($"  ... {written.Count - 25} more lines: `logs {job.Id} --all`");
            }
        }

        if (lines.Length > 0 && !launched)
        {
            o.WriteLine("the host trace ends with:");
            foreach (var l in lines.TakeLast(6)) o.WriteLine("  " + Format.Clip(l, 240));
        }

        if (lines.Length > 0) o.WriteLine($"host trace: {trace} ({lines.Length} lines; `read` it with --grep for more)");
        if (!ended) o.WriteLine("Host tracing stays on for that process and for what it starts.");
        SystemPrompts.Warn(ctx.Err);
        return failed ? 1 : 0;
    }

    /// <summary>The host's trace file so far (it is still being written), or nothing when there is none.</summary>
    private static string[] ReadTrace(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[Math.Min(fs.Length, 32 * 1024 * 1024)];
            fs.ReadExactly(bytes);
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding((int)Win32.GetACP()).GetString(bytes);
            }

            return text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }
}
