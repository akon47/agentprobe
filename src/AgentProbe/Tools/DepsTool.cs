using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
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
              Prints problems only (plus a one-line summary). --tree prints the whole tree, --deep also walks into
              Windows system DLLs. Typical findings: a VC++ runtime that is not installed (0xC0000135), an x64 DLL
              next to an x86 app (0xC000007B), an API this Windows build lacks (0xC0000139), no matching .NET runtime.
            """, DepsAsync) { Quiet = true },
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
        var a = ArgParser.Parse(args, "tree", "deep");
        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("deps: which file?"));
        var path = ctx.Runtime.ResolvePath(a.Positionals[0]);
        if (!File.Exists(path)) return Task.FromResult(ctx.Fail($"deps: {path} does not exist."));
        var root = Load(path, false);
        if (root == null) return Task.FromResult(ctx.Fail($"deps: {path} is not a Windows executable image."));

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

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)) return;
            var wanted = new List<(string name, string version)>();
            if (options.TryGetProperty("framework", out var single)) wanted.Add((single.GetProperty("name").GetString()!, single.GetProperty("version").GetString()!));
            if (options.TryGetProperty("frameworks", out var many))
                foreach (var f in many.EnumerateArray()) wanted.Add((f.GetProperty("name").GetString()!, f.GetProperty("version").GetString()!));
            if (wanted.Count == 0)
            {
                o.WriteLine(".NET app, self-contained (brings its own runtime)");
                return;
            }

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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            o.WriteLine($".NET app; could not read {Path.GetFileName(config)}: {ex.Message}");
        }
    }
}
