using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Native;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Tools;

/// <summary>The machine as a whole: what it is, how loaded it is, and a first-look health summary.</summary>
internal static partial class SystemTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("triage", "System", "START HERE: one-screen health summary (load, hung windows, recent crashes and errors, low disk)",
            """
            triage [process-name]
              Answers "what is wrong with this PC right now?" in about 30 lines, so you know which tool to use next.
              With a process name it also summarizes that process.
            """, TriageAsync) { Quiet = true },
        new("sys", "System", "What this PC is: OS build, hardware, locale, displays, network, installed runtimes",
            "sys", (ctx, _) => Task.FromResult(Sys(ctx))) { Quiet = true },
        new("mem", "System", "Memory: physical, commit, kernel pools, standby lists, biggest consumers (RAMMap-style)",
            """
            mem [--top 12]
              Commit near its limit means allocations start failing even with free RAM. Rising nonpaged pool is a driver leak.
              Standby/modified page lists need agentprobe to run elevated on the target.
            """, MemAsync) { Quiet = true },
        new("reg", "System", "Read a registry key compactly",
            """
            reg <key> [--depth 1] [--grep REGEX]
              e.g. reg HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full
              Use --view 32 for the 32-bit (WOW6432Node) view.
            """, RegAsync) { Quiet = true },
        new("svc", "System", "Windows services: state, start type, process",
            "svc [filter] [--running] [--stopped]", SvcAsync) { Quiet = true },
        new("envsnap", "System", "Machine fingerprint as key=value lines; diff two PCs, or one PC before/after",
            """
            envsnap [--dir FOLDER] [--reg KEY] [--save NAME | --diff NAME] [--grep REGEX]
              OS, locale, hardware, drivers, runtimes, environment variables, PATH, installed programs, services.
              --dir adds every file of a folder (size + version), --reg adds a registry subtree.
              --save NAME remembers the snapshot on the target; --diff NAME prints only what changed since
              (run it before and after an installer, an update or a reboot).
              To compare two PCs use: agentprobe compare <target-a> <target-b|local>
            """, EnvSnapAsync) { Quiet = true },
    ];

    // ------------------------------------------------------------------ shared facts

    public static (string product, string build, string displayVersion) OsInfo()
    {
        using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = os?.GetValue("CurrentBuild") as string ?? "?";
        var product = (os?.GetValue("ProductName") as string ?? "Windows").Replace("Microsoft ", "");
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return (product, $"{build}.{os?.GetValue("UBR")}", os?.GetValue("DisplayVersion") as string ?? "");
    }

    public static unsafe MEMORYSTATUSEX Memory()
    {
        var m = default(MEMORYSTATUSEX);
        m.dwLength = (uint)sizeof(MEMORYSTATUSEX);
        Win32.GlobalMemoryStatusEx(ref m);
        return m;
    }

    public static unsafe PERFORMANCE_INFORMATION Performance()
    {
        Win32.GetPerformanceInfo(out var pi, (uint)sizeof(PERFORMANCE_INFORMATION));
        return pi;
    }

    /// <summary>"utc+09:00" / "utc-05:00", sign included.</summary>
    private static string UtcOffset()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        return $"utc{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
    }

    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Win32.GetTickCount64());

    public static bool PendingReboot()
    {
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return cbs != null || wu != null || sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
    }

    private static List<(string name, string version)> DotnetRuntimes()
    {
        var result = new List<(string, string)>();
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (root == null) continue;
            var shared = Path.Combine(root, "dotnet", "shared");
            if (!Directory.Exists(shared)) continue;
            var x86 = root.Contains("(x86)", StringComparison.Ordinal) ? " x86" : "";
            foreach (var fw in Directory.GetDirectories(shared))
                foreach (var v in Directory.GetDirectories(fw))
                    result.Add((Path.GetFileName(fw).Replace("Microsoft.", "") + x86, Path.GetFileName(v)));
        }

        return result;
    }

    /// <summary>8.0.6, 8.0.11, 9.0.2 -> 8.0.11, 9.0.2 (only the newest patch of each major can be chosen by an app).</summary>
    private static IEnumerable<string> LatestPerMajor(IEnumerable<string> versions)
        => versions.Select(v => (text: v, parsed: Version.TryParse(v.Split('-')[0], out var p) ? p : new Version(0, 0)))
            .GroupBy(v => v.parsed.Major).OrderBy(g => g.Key).Select(g => g.OrderByDescending(v => v.parsed).First().text);

    private static string NetFramework()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        if (k?.GetValue("Release") is not int release) return "not installed";
        var name = release switch
        {
            >= 533320 => "4.8.1",
            >= 528040 => "4.8",
            >= 461808 => "4.7.2",
            >= 461308 => "4.7.1",
            >= 460798 => "4.7",
            >= 394802 => "4.6.2",
            _ => "4.x",
        };
        return $"{name} (release {release})";
    }

    private static List<string> VcRedists()
    {
        var list = new List<string>();
        foreach (var (arch, view) in new[] { ("x64", RegistryView.Registry64), ("x86", RegistryView.Registry32) })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = hklm.OpenSubKey($@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}");
            if (k?.GetValue("Installed") is 1) list.Add($"{arch} {k.GetValue("Version") as string ?? "?"}".Replace(" v", " "));
        }

        return list;
    }

    private static List<(string name, string driver, string date)> Gpus()
    {
        var list = new List<(string, string, string)>();
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc) list.Add((desc, k.GetValue("DriverVersion") as string ?? "?", k.GetValue("DriverDate") as string ?? ""));
            }
            catch (System.Security.SecurityException)
            {
                // protected subkey
            }
        }

        return list;
    }

    private static unsafe List<string> Displays()
    {
        var result = new List<string>();
        var handle = GCHandle.Alloc(result);
        Win32.EnumDisplayMonitors(0, 0, &DisplayProc, GCHandle.ToIntPtr(handle));
        handle.Free();
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe int DisplayProc(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var list = (List<string>)GCHandle.FromIntPtr(data).Target!;
        var info = default(MONITORINFOEX);
        info.cbSize = (uint)sizeof(MONITORINFOEX);
        Win32.GetMonitorInfo(monitor, &info);
        var scale = Win32.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi * 100 / 96 : 100;
        list.Add($"{rect->Width}x{rect->Height}@{scale}%{((info.dwFlags & 1) != 0 ? " primary" : "")} at {rect->Left},{rect->Top}");
        return 1;
    }

    private static List<string> Networks()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var ips = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => $"{u.Address}/{u.PrefixLength}").ToList();
                if (ips.Count == 0) continue;
                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                var dns = string.Join(",", props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Take(2));
                list.Add($"{nic.Name}: {string.Join(" ", ips)}{(gw != null ? $" gw {gw}" : "")}{(dns.Length > 0 ? $" dns {dns}" : "")}");
            }
        }
        catch (NetworkInformationException)
        {
            // adapter list unavailable
        }

        return list;
    }

    // ------------------------------------------------------------------ sys

    private static int Sys(ToolContext ctx)
    {
        var o = ctx.Out;
        var (product, build, display) = OsInfo();
        var m = Memory();
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var up = Uptime();
        o.WriteLine($"host     {Environment.MachineName}  user {Environment.UserDomainName}\\{Environment.UserName}  agentprobe runs {(Environment.IsPrivilegedProcess ? "elevated (administrator)" : "as standard user")}");
        o.WriteLine($"os       {product} {display} build {build} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}  up {Format.Duration(up)} (boot {Format.Time(DateTime.UtcNow - up)}){(PendingReboot() ? "  ! reboot pending" : "")}");
        o.WriteLine($"machine  {bios?.GetValue("SystemManufacturer")} {bios?.GetValue("SystemProductName")}  bios {bios?.GetValue("BIOSVersion")}");
        o.WriteLine($"cpu      {(cpu?.GetValue("ProcessorNameString") as string)?.Trim()}  {Environment.ProcessorCount} logical");
        o.WriteLine($"ram      {Format.Bytes((long)m.ullTotalPhys)} total, {Format.Bytes((long)m.ullAvailPhys)} available ({m.dwMemoryLoad}% used)");
        var ci = CultureInfo.CurrentCulture;
        o.WriteLine($"locale   {ci.Name}  ui {CultureInfo.CurrentUICulture.Name}  ansi cp{Win32.GetACP()} oem cp{Win32.GetOEMCP()}  tz {TimeZoneInfo.Local.Id} ({UtcOffset()})  now {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var low = d.DriveType == DriveType.Fixed && d.AvailableFreeSpace < Math.Max(2L << 30, d.TotalSize / 20) ? "  ! low" : "";
                o.WriteLine($"disk     {d.Name} {Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)} {d.DriveFormat} {d.DriveType.ToString().ToLowerInvariant()}{low}");
            }
            catch (IOException)
            {
                // drive vanished
            }
        }

        foreach (var (name, driver, date) in Gpus()) o.WriteLine($"gpu      {name}  driver {driver} ({date})");
        var displays = Displays();
        o.WriteLine($"display  {string.Join("; ", displays)}");
        foreach (var n in Networks()) o.WriteLine($"net      {n}");
        var runtimes = DotnetRuntimes();
        o.WriteLine("dotnet   " + (runtimes.Count == 0 ? "no .NET (Core) runtime installed" : string.Join("; ", runtimes.GroupBy(r => r.name).Select(g => $"{g.Key} {string.Join(",", LatestPerMajor(g.Select(r => r.version)))}"))));
        o.WriteLine($"netfx    .NET Framework {NetFramework()}");
        var vc = VcRedists();
        o.WriteLine("vcredist " + (vc.Count == 0 ? "VC++ 2015-2022 runtime NOT installed" : string.Join("; ", vc)));
        using var fs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
        using var pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        o.WriteLine($"flags    long paths {(fs?.GetValue("LongPathsEnabled") is 1 ? "on" : "off")}  UAC {(pol?.GetValue("EnableLUA") is 0 ? "OFF" : "on")}  session {(Environment.UserInteractive ? "interactive" : "non-interactive")}");
        return 0;
    }

    // ------------------------------------------------------------------ mem

    private static unsafe Task<int> MemAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var o = ctx.Out;
        var m = Memory();
        Win32.GetPerformanceInfo(out var pi, (uint)sizeof(PERFORMANCE_INFORMATION));
        long page = (long)pi.PageSize;
        long commit = (long)pi.CommitTotal * page, limit = (long)pi.CommitLimit * page;
        var commitPct = limit > 0 ? 100.0 * commit / limit : 0;
        o.WriteLine($"physical  {Format.Bytes((long)m.ullTotalPhys)} total  {Format.Bytes((long)m.ullAvailPhys)} available  {m.dwMemoryLoad}% in use{(m.dwMemoryLoad >= 90 ? "  ! memory pressure" : "")}");
        o.WriteLine($"commit    {Format.Bytes(commit)} of {Format.Bytes(limit)} limit ({Format.Pct(commitPct)}%)  peak {Format.Bytes((long)pi.CommitPeak * page)}{(commitPct >= 90 ? "  ! near the commit limit: allocations will start failing" : "")}");
        o.WriteLine($"kernel    paged pool {Format.Bytes((long)pi.KernelPaged * page)}  nonpaged pool {Format.Bytes((long)pi.KernelNonpaged * page)}  file cache {Format.Bytes((long)pi.SystemCache * page)}");
        o.WriteLine($"objects   {pi.ProcessCount} processes  {pi.ThreadCount} threads  {pi.HandleCount} handles");

        // Page lists (what RAMMap shows) need SeProfileSingleProcessPrivilege, i.e. an elevated agentprobe.
        if (Win32.EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            var lists = stackalloc long[22];
            if (Win32.NtQuerySystemInformation(80, lists, 176, out _) >= 0)
            {
                long standby = 0;
                for (var i = 5; i < 13; i++) standby += lists[i];
                o.WriteLine($"pages     zeroed {Format.Bytes(lists[0] * page)}  free {Format.Bytes(lists[1] * page)}  standby {Format.Bytes(standby * page)}  modified {Format.Bytes((lists[2] + lists[3]) * page)}  bad {Format.Bytes(lists[4] * page)}");
            }
        }
        else
        {
            o.WriteLine("pages     (standby/modified lists need agentprobe elevated on the target)");
        }

        var all = ProcessSnapshot.Take().Where(p => p.Pid != 0).ToList();
        var top = a.GetInt("top", 12);
        o.WriteLine();
        o.WriteLine("by program (private bytes, instances summed):");
        var t = new Table("NAME", ">COUNT", ">PRIVATE", ">WORKING SET", ">HANDLES");
        foreach (var g in all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(p => p.PrivateBytes)).Take(top))
            t.Add(g.Key, g.Count(), Format.Bytes(g.Sum(p => p.PrivateBytes)), Format.Bytes(g.Sum(p => p.WorkingSet)), g.Sum(p => p.Handles));
        o.Write(t.ToString());
        o.WriteLine($"all processes: private {Format.Bytes(all.Sum(p => p.PrivateBytes))}  working set {Format.Bytes(all.Sum(p => p.WorkingSet))}");
        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ triage

    private static async Task<int> TriageAsync(ToolContext ctx, string[] args)
    {
        var o = ctx.Out;
        var (product, build, display) = OsInfo();
        var before = ProcessSnapshot.Take().ToDictionary(p => (p.Pid, p.CreateTime));
        Win32.GetSystemTimes(out var idle0, out var k0, out var u0);
        var sampling = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(700, ctx.Ct);
        var procs = ProcessSnapshot.Take();
        var sampled = sampling.Elapsed; // the real interval: on a loaded PC it is longer than the delay asked for
        Win32.GetSystemTimes(out var idle1, out var k1, out var u1);
        var total = (k1 - k0) + (u1 - u0);
        var cpu = total > 0 ? 100.0 * (total - (idle1 - idle0)) / total : 0;
        var m = Memory();
        var pi = Performance();
        var commitPct = pi.CommitLimit > 0 ? 100.0 * pi.CommitTotal / pi.CommitLimit : 0;
        var findings = new List<string>();

        o.WriteLine($"{Environment.MachineName}  {product} {display} ({build})  up {Format.Duration(Uptime())}  user {Environment.UserName}{(Environment.IsPrivilegedProcess ? " (elevated)" : "")}");
        o.WriteLine($"load     cpu {Format.Pct(cpu)}% of {Environment.ProcessorCount} cores   ram {m.dwMemoryLoad}% ({Format.Bytes((long)m.ullAvailPhys)} free)   commit {Format.Pct(commitPct)}%");
        if (cpu > 85) findings.Add("CPU is saturated: `top` shows who, `threads <pid>` shows which thread");
        if (m.dwMemoryLoad >= 90) findings.Add("RAM is nearly full: `mem`");
        if (commitPct >= 90) findings.Add("commit charge near its limit (allocations will fail): `mem`");

        var capacity = sampled.Ticks * (double)Environment.ProcessorCount;
        var hot = procs.Where(p => p.Pid != 0).Select(p => (p, pct: before.TryGetValue((p.Pid, p.CreateTime), out var b) ? 100.0 * (p.CpuTime - b.CpuTime) / capacity : 0))
            .OrderByDescending(x => x.pct).Take(3).Where(x => x.pct >= 1).ToList();
        if (hot.Count > 0) o.WriteLine("cpu top  " + string.Join("  ", hot.Select(x => $"{x.p.Name}({x.p.Pid}) {Format.Pct(x.pct)}%")));
        o.WriteLine("mem top  " + string.Join("  ", procs.OrderByDescending(p => p.PrivateBytes).Take(4).Select(p => $"{p.Name}({p.Pid}) {Format.Bytes(p.PrivateBytes)}")));
        var leaky = procs.Where(p => p.Handles > 20000 && p.Pid > 4).OrderByDescending(p => p.Handles).Take(3).ToList();
        if (leaky.Count > 0)
        {
            o.WriteLine("handles  " + string.Join("  ", leaky.Select(p => $"{p.Name}({p.Pid}) {p.Handles}")));
            findings.Add("very high handle counts (leak?): `handles <pid> --interval 30s` shows which type grows");
        }

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType == DriveType.Fixed && d.AvailableFreeSpace < Math.Max(2L << 30, d.TotalSize / 20))
                {
                    o.WriteLine($"disk     {d.Name} only {Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}");
                    findings.Add($"drive {d.Name} is almost full");
                }
            }
            catch (IOException)
            {
                // ignore
            }
        }

        var hung = UiTools.EnumerateWindows().Where(w => w.Visible && w.Hung && w.Title.Length > 0).ToList();
        foreach (var w in hung.Take(5))
        {
            var name = procs.FirstOrDefault(p => p.Pid == w.Pid)?.Name ?? "?";
            o.WriteLine($"hung     {name}({w.Pid}) \"{Format.Clip(w.Title, 60)}\" is NOT RESPONDING");
        }

        if (hung.Count > 0) findings.Add("a window is not responding: `threads <pid>` (busy vs blocked), then `dump <pid>`");
        if (PendingReboot())
        {
            o.WriteLine("reboot   a restart is pending (updates/installers)");
            findings.Add("pending reboot can explain odd installer/driver behavior");
        }

        var crashes = EventTools.Crashes(TimeSpan.FromHours(24), 5);
        if (crashes.Count > 0)
        {
            o.WriteLine($"crashes  in the last 24h (newest first):");
            foreach (var c in crashes) o.WriteLine("  " + c);
            findings.Add("recent crashes: `crashes` for details and dump files, `events --level error` for context");
        }

        var errors = EventTools.ErrorSummary(TimeSpan.FromHours(1));
        if (errors.Count > 0) o.WriteLine("errors   last hour by source: " + string.Join(", ", errors.Take(6).Select(e => $"{e.provider} x{e.count}")));

        if (args.Length > 0)
        {
            var matches = ProcessTools.Find(procs, args[0]);
            o.WriteLine();
            if (matches.Count == 0) o.WriteLine($"process  '{args[0]}' is NOT running");
            foreach (var p in matches.OrderByDescending(p => p.PrivateBytes).Take(5))
            {
                var h = ProcessDetails.Open(p.Pid);
                var gui = h != 0 ? ProcessDetails.GuiObjects(h) : (0u, 0u);
                if (h != 0) Win32.CloseHandle(h);
                o.WriteLine($"process  {p.Name}({p.Pid}) up {(p.StartTimeUtc is { } s ? Format.Duration(DateTime.UtcNow - s) : "?")}  private {Format.Bytes(p.PrivateBytes)} (peak {Format.Bytes(p.PeakPrivateBytes)})  handles {p.Handles}  gdi {gui.Item1}  user {gui.Item2}  threads {p.Threads}");
            }
        }

        o.WriteLine();
        o.WriteLine(findings.Count == 0 ? "no obvious system-level problem. Next: `proc <name>`, `events`, `crashes`, or start a `watch`." : "next:\n  - " + string.Join("\n  - ", findings));
        return 0;
    }

    // ------------------------------------------------------------------ reg

    private static (RegistryKey? key, string path) OpenKey(string spec, RegistryView view)
    {
        spec = spec.Replace('/', '\\').Trim('\\');
        var i = spec.IndexOf('\\');
        var hiveName = (i < 0 ? spec : spec[..i]).ToUpperInvariant().TrimEnd(':');
        var sub = i < 0 ? "" : spec[(i + 1)..];
        var hive = hiveName switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => (RegistryHive)0,
        };
        if (hive == 0) return (null, spec);
        var root = RegistryKey.OpenBaseKey(hive, view);
        return (sub.Length == 0 ? root : root.OpenSubKey(sub), spec);
    }

    private static string RegValue(object? v) => v switch
    {
        null => "",
        string[] arr => string.Join(" | ", arr),
        byte[] b => b.Length <= 32 ? Convert.ToHexString(b) : $"{Convert.ToHexString(b, 0, 32)}… ({b.Length} bytes)",
        int i => $"{(uint)i} (0x{(uint)i:X})", // REG_DWORD is unsigned
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static Task<int> RegAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        if (a.Positionals.Count == 0) return Task.FromResult(ctx.Fail("reg: which key? e.g. reg HKCU\\Software\\MyApp"));
        var view = a.Get("view") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
        var (key, path) = OpenKey(string.Join(' ', a.Positionals), view);
        if (key == null) return Task.FromResult(ctx.Fail($"reg: {path} does not exist (or the hive name is unknown)."));
        var sb = new StringBuilder();
        DumpKey(key, path, a.GetInt("depth", 1), sb, 0);
        ctx.Out.Write(TextFilter.Apply(sb.ToString(), a.Get("grep"), null, null, ctx.Err));
        return Task.FromResult(0);
    }

    private static void DumpKey(RegistryKey key, string path, int depth, StringBuilder sb, int level)
    {
        var pad = new string(' ', level * 2);
        sb.Append(pad).Append('[').Append(level == 0 ? path : Path.GetFileName(path)).Append("]\n");
        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            sb.Append(pad).Append("  ").Append(name.Length == 0 ? "(default)" : name).Append(" = ").Append(Format.Clip(RegValue(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)), 400)).Append('\n');
        var subs = key.GetSubKeyNames();
        if (depth <= 0)
        {
            if (subs.Length > 0) sb.Append(pad).Append($"  ({subs.Length} subkeys: {Format.Clip(string.Join(", ", subs), 300)})\n");
            return;
        }

        foreach (var s in subs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var sub = key.OpenSubKey(s);
                if (sub != null) DumpKey(sub, path + "\\" + s, depth - 1, sb, level + 1);
            }
            catch (System.Security.SecurityException)
            {
                sb.Append(pad).Append("  [").Append(s).Append("] (access denied)\n");
            }
        }
    }

    // ------------------------------------------------------------------ svc

    private static unsafe List<(string name, string display, uint state, uint pid)> Services()
    {
        var list = new List<(string, string, uint, uint)>();
        var scm = Win32.OpenSCManager(null, null, 0x0004); // ENUMERATE_SERVICE
        if (scm == 0) return list;
        try
        {
            uint resume = 0;
            Win32.EnumServicesStatusEx(scm, 0, 0x30, 3, null, 0, out var needed, out _, ref resume, null);
            var buffer = new byte[needed + 4096];
            fixed (byte* b = buffer)
            {
                if (!Win32.EnumServicesStatusEx(scm, 0, 0x30, 3, b, (uint)buffer.Length, out _, out var count, ref resume, null)) return list;
                for (var i = 0; i < count; i++)
                {
                    var e = b + (i * 56); // ENUM_SERVICE_STATUS_PROCESSW on x64
                    list.Add((new string(*(char**)e), new string(*(char**)(e + 8)), *(uint*)(e + 20), *(uint*)(e + 44)));
                }
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }

        return list;
    }

    private static string StartType(string service)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        return (k?.GetValue("Start") as int?) switch
        {
            2 => k!.GetValue("DelayedAutostart") is 1 ? "auto(delayed)" : "auto",
            3 => "manual",
            4 => "disabled",
            0 or 1 => "boot",
            _ => "?",
        };
    }

    private static string ServiceState(uint s) => s switch
    {
        1 => "stopped",
        2 => "starting",
        3 => "stopping",
        4 => "running",
        7 => "paused",
        _ => "pending",
    };

    private static Task<int> SvcAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "running", "stopped");
        var t = new Table("SERVICE", "STATE", "START", ">PID", "DISPLAY NAME");
        var total = 0;
        foreach (var (name, display, state, pid) in Services().OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase))
        {
            total++;
            if (a.Flag("running") && state != 4) continue;
            if (a.Flag("stopped") && state != 1) continue;
            if (a.Positionals.Count > 0 && !name.Contains(a.Positionals[0], StringComparison.OrdinalIgnoreCase) && !display.Contains(a.Positionals[0], StringComparison.OrdinalIgnoreCase)) continue;
            t.Add(name, ServiceState(state), StartType(name), pid == 0 ? "" : pid.ToString(), Format.Clip(display, 60));
        }

        ctx.Out.WriteLine($"{t.Count} of {total} services");
        ctx.Out.Write(t.ToString());
        return Task.FromResult(0);
    }

    // ------------------------------------------------------------------ envsnap

    private static readonly Dictionary<string, SortedDictionary<string, string>> Saved = new(StringComparer.OrdinalIgnoreCase);

    public static SortedDictionary<string, string> Snapshot(string? dir, string? regKey)
    {
        var s = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (product, build, display) = OsInfo();
        s["os/product"] = $"{product} {display}";
        s["os/build"] = build;
        s["os/arch"] = RuntimeInformation.OSArchitecture.ToString();
        s["locale/culture"] = CultureInfo.CurrentCulture.Name;
        s["locale/ui"] = CultureInfo.CurrentUICulture.Name;
        s["locale/codepages"] = $"ansi {Win32.GetACP()} oem {Win32.GetOEMCP()}";
        s["locale/timezone"] = TimeZoneInfo.Local.Id;
        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            s["hw/cpu"] = $"{(cpu?.GetValue("ProcessorNameString") as string)?.Trim()} x{Environment.ProcessorCount}";
        s["hw/ram"] = Format.Bytes((long)Memory().ullTotalPhys);
        var gi = 0;
        foreach (var (name, driver, date) in Gpus()) s[$"hw/gpu{gi++}"] = $"{name} driver {driver} ({date})";
        var di = 0;
        foreach (var d in Displays()) s[$"display/{di++}"] = d;
        foreach (var g in DotnetRuntimes().GroupBy(r => r.name)) s[$"dotnet/{g.Key}"] = string.Join(",", g.Select(r => r.version));
        s["dotnet/framework"] = NetFramework();
        s["vcredist"] = string.Join("; ", VcRedists());
        s["flags/pending-reboot"] = PendingReboot() ? "yes" : "no";
        s["flags/elevated"] = Environment.IsPrivilegedProcess ? "yes" : "no";
        using (var fs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
            s["flags/long-paths"] = fs?.GetValue("LongPathsEnabled") is 1 ? "on" : "off";

        foreach (var (scope, target) in new[] { ("machine", EnvironmentVariableTarget.Machine), ("user", EnvironmentVariableTarget.User) })
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(target))
            {
                var name = (string)e.Key;
                var value = (string?)e.Value ?? "";
                if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                {
                    var n = 0;
                    foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries)) s[$"path/{scope}/{n++:00}"] = part;
                }
                else
                {
                    s[$"env/{scope}/{name}"] = Format.Clip(value, 300);
                }
            }
        }

        foreach (var (hive, view, tag) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64, ""), (RegistryHive.LocalMachine, RegistryView.Registry32, " (x86)"), (RegistryHive.CurrentUser, RegistryView.Default, " (user)") })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || k.GetValue("SystemComponent") is 1) continue;
                s[$"program/{name}{tag}"] = k.GetValue("DisplayVersion") as string ?? "?";
            }
        }

        foreach (var (name, _, state, _) in Services()) s[$"service/{name}"] = $"{ServiceState(state)}, {StartType(name)}";
        foreach (var (hive, tag) in new[] { (Registry.LocalMachine, "machine"), (Registry.CurrentUser, "user") })
        {
            using var run = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            if (run == null) continue;
            foreach (var v in run.GetValueNames()) s[$"autorun/{tag}/{v}"] = Format.Clip(run.GetValue(v) as string, 200);
        }

        if (dir != null && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(20000))
            {
                var fi = new FileInfo(f);
                var version = "";
                if (fi.Extension.ToLowerInvariant() is ".dll" or ".exe" or ".sys" or ".ocx")
                {
                    try
                    {
                        version = " v" + System.Diagnostics.FileVersionInfo.GetVersionInfo(f).FileVersion;
                    }
                    catch (FileNotFoundException)
                    {
                        // raced with deletion
                    }
                }

                s[$"file/{Path.GetRelativePath(dir, f)}"] = $"{fi.Length} bytes{version}";
            }
        }

        if (regKey != null)
        {
            var (key, path) = OpenKey(regKey, RegistryView.Registry64);
            if (key != null) SnapKey(key, "reg/" + path, s, 0);
        }

        return s;
    }

    private static void SnapKey(RegistryKey key, string path, SortedDictionary<string, string> s, int depth)
    {
        if (depth > 8 || s.Count > 60000) return;
        foreach (var name in key.GetValueNames()) s[$"{path}\\{(name.Length == 0 ? "(default)" : name)}"] = Format.Clip(RegValue(key.GetValue(name)), 300);
        foreach (var sub in key.GetSubKeyNames())
        {
            try
            {
                using var k = key.OpenSubKey(sub);
                if (k != null) SnapKey(k, path + "\\" + sub, s, depth + 1);
            }
            catch (System.Security.SecurityException)
            {
                // skip protected subkeys
            }
        }
    }

    /// <summary>Lines describing how <paramref name="b"/> differs from <paramref name="a"/>.</summary>
    public static List<string> Diff(IDictionary<string, string> a, IDictionary<string, string> b, string nameA, string nameB)
    {
        var lines = new List<string>();
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var hasA = a.TryGetValue(key, out var va);
            var hasB = b.TryGetValue(key, out var vb);
            if (hasA && hasB && va == vb) continue;
            if (hasA && hasB) lines.Add($"~ {key}: {nameA}={va} | {nameB}={vb}");
            else if (hasA) lines.Add($"- {key}: only {nameA} ({va})");
            else lines.Add($"+ {key}: only {nameB} ({vb})");
        }

        return lines;
    }

    private static Task<int> EnvSnapAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var dir = a.Get("dir") is { } d ? ctx.Runtime.ResolvePath(d) : null;
        var snap = Snapshot(dir, a.Get("reg"));
        if (a.Get("save") is { } saveName)
        {
            lock (Saved) Saved[saveName] = snap;
            ctx.Out.WriteLine($"saved snapshot '{saveName}' ({snap.Count} facts). Later: envsnap --diff {saveName}{(dir != null ? $" --dir \"{dir}\"" : "")}{(a.Get("reg") is { } r ? $" --reg \"{r}\"" : "")}");
            return Task.FromResult(0);
        }

        if (a.Get("diff") is { } diffName)
        {
            SortedDictionary<string, string>? old;
            lock (Saved) Saved.TryGetValue(diffName, out old);
            if (old == null) return Task.FromResult(ctx.Fail($"envsnap: no saved snapshot '{diffName}' (snapshots do not survive a reboot of the target)."));
            var lines = Diff(old, snap, "before", "now");
            ctx.Out.WriteLine(lines.Count == 0 ? "no differences" : $"{lines.Count} differences since '{diffName}':");
            ctx.Out.Write(TextFilter.Apply(string.Join('\n', lines) + (lines.Count > 0 ? "\n" : ""), a.Get("grep"), null, null, ctx.Err));
            return Task.FromResult(0);
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in snap) sb.Append(k).Append('=').Append(v).Append('\n');
        ctx.Out.Write(TextFilter.Apply(sb.ToString(), a.Get("grep"), null, null, ctx.Err));
        return Task.FromResult(0);
    }
}
