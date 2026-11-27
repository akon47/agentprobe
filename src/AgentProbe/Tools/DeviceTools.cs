using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AgentProbe.Util;
using Microsoft.Win32;

namespace AgentProbe.Tools;

/// <summary>A Plug and Play device as the configuration manager reports it (what Device Manager shows).</summary>
internal sealed class Device
{
    public string Id { get; init; } = "";

    public string Name { get; set; } = "";

    public string Class { get; set; } = "";

    public string Manufacturer { get; set; } = "";

    public uint Status { get; set; }

    public uint Problem { get; set; }

    public string Service { get; set; } = "";

    public string DriverVersion { get; set; } = "";

    public DateTime? DriverDate { get; set; }

    public string DriverProvider { get; set; } = "";

    public string DriverInf { get; set; } = "";

    public string Location { get; set; } = "";

    public string[] LocationPaths { get; set; } = [];

    public string Parent { get; set; } = "";

    public string[] HardwareIds { get; set; } = [];

    public DateTime? Installed { get; set; }

    public DateTime? FirstInstalled { get; set; }

    public DateTime? LastArrival { get; set; }

    public DateTime? LastRemoval { get; set; }

    public uint? LinkSpeed { get; set; }

    public uint? LinkWidth { get; set; }

    public uint? MaxLinkSpeed { get; set; }

    public uint? MaxLinkWidth { get; set; }

    public bool Started => (Status & 0x8) != 0; // DN_STARTED

    public bool HasProblem => (Status & 0x400) != 0 && Problem != 0; // DN_HAS_PROBLEM
}

/// <summary>Kernel drivers and Plug and Play devices: what a broken card, a missing driver or a 2016 driver looks like.</summary>
internal static unsafe partial class DeviceTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("drivers", "System", "Kernel drivers that are running, with version, company and date (default: not from Microsoft)",
            """
            drivers [--all] [--grep REGEX]
              The kernel and file system drivers whose service is running, with the file's version, company and
              modified date, the INF date of the devices that use it, and its path. Without --all only drivers whose
              company is not Microsoft (by the version info of the file), and how many were left out.
            """, DriversAsync) { Quiet = true },
        new("devices", "System", "Plug and Play devices: problem codes, driver, PCIe link speed, when they came and went",
            """
            devices [--grep REGEX] [--class NAME] [--long] [--all]
              Without options: the devices that have a problem (Device Manager's yellow mark), with its code
              explained, and how many devices are present.
              --grep   devices whose name, instance id, hardware id, class, driver or service matches (VEN_10EE, capture)
              --long   per device also: hardware ids, location paths, parent, the PCIe link (now and the most it can
                       do), the driver's INF, when it was installed, first installed, last arrived and last removed,
                       and its events in Kernel-PnP/Configuration
              --all    every present device, one line each
            """, DevicesAsync) { Quiet = true },
    ];

    // ------------------------------------------------------------------ devices

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;

        public DevPropKey(string guid, uint pid)
        {
            Fmtid = Guid.Parse(guid);
            Pid = pid;
        }
    }

    private const string General = "a45c254e-df1c-4efd-8020-67d146a850e0";
    private const string Node = "4340a6c5-93fa-4706-972c-7b648008a5a7";
    private const string DriverKeys = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";
    private const string Dates = "83da6326-97a6-4088-9453-a1923f573b29";
    private const string Pci = "3ab22e31-8264-4b4e-9af5-a8d2d8e33e62";

    /// <summary>Every device that is present now.</summary>
    public static List<Device> Present(out string? error)
    {
        error = null;
        var list = new List<Device>();
        if (CM_Get_Device_ID_List_Size(out var length, null, 0x100) != 0 || length == 0) // CM_GETIDLIST_FILTER_PRESENT
        {
            error = "the device list could not be read";
            return list;
        }

        var buffer = new char[length + 1024];
        fixed (char* b = buffer)
        {
            var cr = CM_Get_Device_ID_List(null, b, (uint)buffer.Length, 0x100);
            if (cr != 0)
            {
                error = $"the device list could not be read (CONFIGRET 0x{cr:X})";
                return list;
            }
        }

        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            uint node;
            fixed (char* p = id)
            {
                if (CM_Locate_DevNode(out node, p, 0) != 0) continue;
            }

            var d = new Device { Id = id };
            if (CM_Get_DevNode_Status(out var status, out var problem, node, 0) == 0)
            {
                d.Status = status;
                d.Problem = problem;
            }

            d.Name = Text(node, General, 14) is { Length: > 0 } friendly ? friendly : Text(node, General, 2);
            d.Class = Text(node, General, 9);
            d.Manufacturer = Text(node, General, 13);
            d.Service = Text(node, General, 6);
            d.Location = Text(node, General, 15);
            d.LocationPaths = Strings(node, General, 37);
            d.HardwareIds = Strings(node, General, 3);
            d.Parent = Text(node, Node, 8);
            d.DriverVersion = Text(node, DriverKeys, 3);
            d.DriverDate = Time(node, DriverKeys, 2);
            d.DriverInf = Text(node, DriverKeys, 5);
            d.DriverProvider = Text(node, DriverKeys, 9);
            d.Installed = Time(node, Dates, 100);
            d.FirstInstalled = Time(node, Dates, 101);
            d.LastArrival = Time(node, Dates, 102);
            d.LastRemoval = Time(node, Dates, 103);
            d.LinkSpeed = Number(node, Pci, 9);
            d.LinkWidth = Number(node, Pci, 10);
            d.MaxLinkSpeed = Number(node, Pci, 11);
            d.MaxLinkWidth = Number(node, Pci, 12);
            list.Add(d);
        }

        return list;
    }

    private static byte[]? Property(uint node, string guid, uint pid, out uint type)
    {
        var key = new DevPropKey(guid, pid);
        uint size = 0;
        type = 0;
        var cr = CM_Get_DevNode_Property(node, &key, out type, null, ref size, 0);
        if (cr != 0x1A || size == 0) return null; // CR_BUFFER_SMALL is the answer that says how big
        var buffer = new byte[size];
        fixed (byte* b = buffer) cr = CM_Get_DevNode_Property(node, &key, out type, b, ref size, 0);
        return cr == 0 ? buffer : null;
    }

    private static string Text(uint node, string guid, uint pid)
    {
        var b = Property(node, guid, pid, out var type);
        return b == null || type != 0x12 ? "" : Encoding.Unicode.GetString(b).TrimEnd('\0'); // DEVPROP_TYPE_STRING
    }

    private static string[] Strings(uint node, string guid, uint pid)
    {
        var b = Property(node, guid, pid, out var type);
        return b == null || type != 0x2012 ? [] : Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries); // DEVPROP_TYPE_STRING_LIST
    }

    private static DateTime? Time(uint node, string guid, uint pid)
    {
        var b = Property(node, guid, pid, out var type);
        return b == null || type != 0x10 || b.Length < 8 ? null : DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 0)); // DEVPROP_TYPE_FILETIME
    }

    private static uint? Number(uint node, string guid, uint pid)
    {
        var b = Property(node, guid, pid, out var type);
        return b == null || type != 7 || b.Length < 4 ? null : BitConverter.ToUInt32(b, 0); // DEVPROP_TYPE_UINT32
    }

    /// <summary>What Device Manager says for a problem code (CM_PROB_*).</summary>
    public static string ProblemText(uint code) => code switch
    {
        1 => "not configured correctly",
        3 => "the driver may be corrupted, or the system is low on memory",
        10 => "the device cannot start",
        12 => "not enough free resources for it",
        14 => "needs a restart to work",
        18 => "its drivers need to be reinstalled",
        19 => "its configuration in the registry is damaged",
        21 => "Windows is removing it",
        22 => "disabled",
        24 => "not present, not working properly, or not all its drivers are installed",
        28 => "no driver is installed for it",
        29 => "disabled by the firmware",
        31 => "Windows cannot load its drivers",
        32 => "its driver's service is disabled",
        37 => "its driver failed to initialize (DriverEntry)",
        38 => "the previous instance of its driver is still loaded",
        39 => "its driver is corrupted or missing",
        40 => "its driver's service key in the registry is damaged",
        41 => "its driver loaded but found no hardware",
        43 => "its driver reported that the device failed",
        45 => "not connected now",
        47 => "prepared for safe removal",
        48 => "its driver is blocked because it is known to cause problems",
        49 => "the system registry is too large",
        50 => "Windows cannot apply all its properties",
        51 => "waiting for another device to start",
        52 => "its driver's signature cannot be verified",
        53 => "reserved by the kernel debugger",
        54 => "it is being reset",
        _ => "problem code " + code,
    };

    private static string Gen(uint? speed) => speed switch
    {
        null => "?",
        >= 1 and <= 6 => $"Gen{speed}",
        _ => $"speed code {speed}",
    };

    private static string? Link(Device d)
    {
        if (d.LinkSpeed == null && d.LinkWidth == null) return null;
        var now = $"PCIe {Gen(d.LinkSpeed)} x{d.LinkWidth?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        if (d.MaxLinkSpeed == null && d.MaxLinkWidth == null) return now;
        var max = $"{Gen(d.MaxLinkSpeed)} x{d.MaxLinkWidth?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        var slower = (d.LinkSpeed < d.MaxLinkSpeed) || (d.LinkWidth < d.MaxLinkWidth);
        return slower ? $"{now}, below the {max} the device can do" : $"{now}, the most the device can do";
    }

    private static string State(Device d) => d.HasProblem ? $"PROBLEM {d.Problem}: {ProblemText(d.Problem)}" : d.Started ? "ok" : "not started";

    private static string Day(DateTime? t) => t is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?";

    private static string DriverText(Device d)
    {
        if (d.DriverVersion.Length == 0 && d.Service.Length == 0) return "no driver";
        var parts = new List<string>();
        if (d.Service.Length > 0) parts.Add(d.Service);
        if (d.DriverProvider.Length > 0) parts.Add(d.DriverProvider);
        if (d.DriverVersion.Length > 0) parts.Add(d.DriverVersion);
        if (d.DriverDate != null) parts.Add(Day(d.DriverDate));
        return "driver " + string.Join(" ", parts);
    }

    private static Task<int> DevicesAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "long", "all");
        var devices = Present(out var error);
        if (error != null) return Task.FromResult(ctx.Fail("devices: " + error));
        Regex? re = null;
        if (a.Get("grep") is { } grep)
        {
            try
            {
                re = new Regex(grep, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("devices: bad --grep pattern: " + ex.Message));
            }
        }

        var cls = a.Get("class");
        var picked = devices.Where(d => cls == null || d.Class.Equals(cls, StringComparison.OrdinalIgnoreCase))
            .Where(d => re == null || re.IsMatch(d.Name) || re.IsMatch(d.Id) || re.IsMatch(d.Class) || re.IsMatch(d.Service) || re.IsMatch(d.DriverProvider) || d.HardwareIds.Any(h => re.IsMatch(h)))
            .ToList();
        var filtered = re != null || cls != null;
        if (!filtered && !a.Flag("all")) picked = picked.Where(d => d.HasProblem).ToList();

        var o = ctx.Out;
        var problems = devices.Count(d => d.HasProblem);
        o.WriteLine($"{devices.Count} devices present, {problems} with a problem" + (filtered ? $"; {picked.Count} match" : !a.Flag("all") && problems > 0 ? ":" : ""));
        if (picked.Count == 0)
        {
            if (filtered) o.WriteLine("no present device matches (a device that is unplugged or disabled in the firmware is not present)");
            return Task.FromResult(0);
        }

        var max = a.GetInt("max", a.Flag("long") ? 10 : 60);
        foreach (var d in picked.OrderBy(d => d.HasProblem ? 0 : 1).ThenBy(d => d.Class, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(max))
        {
            if (a.Flag("long"))
            {
                Long(o, d);
                continue;
            }

            var link = Link(d);
            o.WriteLine($"{d.Name}  [{d.Class}]  {State(d)}  {DriverText(d)}" + (link != null ? $"  {link}" : ""));
            if (filtered || d.HasProblem) o.WriteLine($"  {d.Id}" + (d.Location.Length > 0 ? $"  at {d.Location}" : ""));
        }

        if (picked.Count > max) o.WriteLine($"… {picked.Count - max} more (--max {picked.Count}, or narrow with --grep / --class)");
        return Task.FromResult(0);
    }

    private static void Long(TextWriter o, Device d)
    {
        o.WriteLine($"{d.Name}  [{d.Class}]  {State(d)}");
        o.WriteLine($"  id        {d.Id}");
        if (d.HardwareIds.Length > 0) o.WriteLine($"  hardware  {Format.Clip(string.Join("; ", d.HardwareIds), 300)}");
        if (d.Location.Length > 0 || d.LocationPaths.Length > 0) o.WriteLine($"  location  {d.Location}{(d.LocationPaths.Length > 0 ? $"; paths {string.Join(", ", d.LocationPaths)}" : "")}");
        if (d.Parent.Length > 0) o.WriteLine($"  parent    {d.Parent}");
        if (Link(d) is { } link) o.WriteLine($"  link      {link}");
        o.WriteLine($"  driver    {(d.Service.Length > 0 ? $"service {d.Service}, " : "")}{(d.DriverProvider.Length > 0 ? d.DriverProvider + " " : "")}{(d.DriverVersion.Length > 0 ? d.DriverVersion : "no driver installed")}" +
                    (d.DriverDate != null ? $" dated {Day(d.DriverDate)}" : "") + (d.DriverInf.Length > 0 ? $", {d.DriverInf}" : "") + (d.Manufacturer.Length > 0 ? $"; made by {d.Manufacturer}" : ""));
        o.WriteLine($"  dates     installed {Day(d.Installed)}, first installed {Day(d.FirstInstalled)}, last arrived {(d.LastArrival is { } la ? Format.Time(la) : "?")}, last removed {(d.LastRemoval is { } lr ? Format.Time(lr) : "never recorded")}");

        // Its own events: configured, started, removed, problems while starting.
        var id = d.Id.Replace("'", "");
        var events = EventTools.Query("Microsoft-Windows-Kernel-PnP/Configuration", $"*[EventData[Data[@Name='DeviceInstanceId']='{id}']]", 200, out var err);
        if (err != null)
        {
            o.WriteLine($"  pnp log   could not be read: {err}");
            return;
        }

        o.WriteLine("  pnp log   " + (events.Count == 0 ? "no events for it in Kernel-PnP/Configuration"
            : string.Join("; ", events.GroupBy(e => e.Id).OrderByDescending(g => g.Max(e => e.TimeUtc))
                .Select(g => $"#{g.Key} x{g.Count()} (last {Format.Time(g.Max(e => e.TimeUtc))}: {Format.Clip(g.First().Message, 80)})")) + (events.Count >= 200 ? " (the newest 200 events)" : "")));
    }

    // ------------------------------------------------------------------ drivers

    private static Task<int> DriversAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args, "all", "third-party");
        Regex? re = null;
        if (a.Get("grep") is { } grep)
        {
            try
            {
                re = new Regex(grep, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ctx.Fail("drivers: bad --grep pattern: " + ex.Message));
            }
        }

        var running = SystemTools.Services(0x0B, 1); // SERVICE_DRIVER, SERVICE_ACTIVE
        if (running.Count == 0) return Task.FromResult(ctx.Fail("drivers: the service manager listed no running driver (it could not be asked)."));
        var devices = Present(out _);
        var byService = devices.Where(d => d.Service.Length > 0).GroupBy(d => d.Service, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<(string name, string version, string modified, string company, string inf, string path, bool microsoft)>();
        foreach (var (name, display, _, _) in running)
        {
            var path = ImagePath(name);
            string version = "", company = "", modified = "?";
            if (path != null && File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                // The numbers, not the text: drivers write "10, 12, 0, 1" or "2024, 6" there.
                version = vi.FileMajorPart + vi.FileMinorPart + vi.FileBuildPart + vi.FilePrivatePart > 0
                    ? $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}"
                    : vi.FileVersion?.Split(' ')[0] ?? "";
                company = vi.CompanyName?.Trim() ?? "";
                modified = File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            var inf = byService.TryGetValue(name, out var used) && used.FirstOrDefault(d => d.DriverDate != null) is { } dev ? Day(dev.DriverDate) + (used.Count > 1 ? $" ({used.Count} devices)" : "") : "";
            var microsoft = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
            var row = (name, version.Length > 0 ? version : "-", modified, company.Length > 0 ? company : "(no version info)", inf.Length > 0 ? inf : "-", path != null && File.Exists(path) ? path : $"{path ?? "?"} (file not found)", microsoft);
            if (re != null && !re.IsMatch(name) && !re.IsMatch(display) && !re.IsMatch(company) && !re.IsMatch(path ?? "")) continue;
            rows.Add(row);
        }

        var shown = a.Flag("all") || re != null ? rows : rows.Where(r => !r.microsoft).ToList();
        var o = ctx.Out;
        o.WriteLine($"{running.Count} kernel drivers running as services" + (a.Flag("all") || re != null ? $"; {shown.Count} shown" : $"; {shown.Count} not from Microsoft (by the company in the file's version info), {rows.Count - shown.Count} from Microsoft left out (--all)"));
        if (shown.Count == 0) return Task.FromResult(0);
        var t = new Table("NAME", "VERSION", "MODIFIED", "INF DATE", "COMPANY", "PATH");
        foreach (var r in shown.OrderBy(r => r.microsoft).ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase)) t.Add(r.name, r.version, r.modified, r.inf, Format.Clip(r.company, 32), r.path);
        o.Write(t.ToString());
        o.WriteLine("(MODIFIED: the file's date; INF DATE: the driver date of the devices that use it, what Device Manager shows. Drivers loaded without a service of their own are not listed.)");
        return Task.FromResult(0);
    }

    /// <summary>A driver service's file, as the service entry names it.</summary>
    private static string? ImagePath(string service)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var raw = (k?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string)?.Trim().Trim('"');
        if (string.IsNullOrEmpty(raw)) return Path.Combine(windows, "System32", "drivers", service + ".sys");
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, raw[12..]);
        if (raw.StartsWith(@"\??\", StringComparison.Ordinal)) return raw[4..];
        if (!Path.IsPathRooted(raw)) return Path.Combine(windows, raw);
        return raw;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static partial uint CM_Get_Device_ID_List_Size(out uint length, char* filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW")]
    private static partial uint CM_Get_Device_ID_List(char* filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW")]
    private static partial uint CM_Locate_DevNode(out uint node, char* id, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint node, DevPropKey* key, out uint type, byte* buffer, ref uint size, uint flags);
}
