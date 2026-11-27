using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentProbe.Util;

namespace AgentProbe.Tools;

/// <summary>
/// The temperatures and limits Windows itself can read without a vendor driver: disks (their own sensors), ACPI
/// thermal zones (a firmware value), how far the CPU is held back, power. What it cannot read is said, not guessed.
/// </summary>
internal static partial class SensorTools
{
    public static IEnumerable<Tool> Tools =>
    [
        new("sensors", "System", "Temperatures and limits Windows can read: disks, thermal zones, CPU throttling, power",
            """
            sensors [--since 7d]
              Disk temperatures from the disks themselves (with their warning and critical limits), ACPI thermal
              zones (a value from the firmware, often not the CPU's), how much the CPU is held back now and how
              often the firmware limited its speed (Kernel-Processor-Power 37), mains or battery and the power plan.
              Voltages, fan speeds and the CPU's own temperature need the board maker's driver: the answer says so.
            """, SensorsAsync) { Quiet = true },
    ];

    private static async Task<int> SensorsAsync(ToolContext ctx, string[] args)
    {
        var a = ArgParser.Parse(args);
        var since = a.GetDuration("since") ?? TimeSpan.FromDays(7);
        var o = ctx.Out;

        var disks = Disks();
        if (disks.Count == 0) o.WriteLine("disk     no disk could be opened");
        foreach (var d in disks) o.WriteLine("disk     " + d);

        var samples = await Counters(ctx.Ct);
        if (samples.Zones.Count == 0) o.WriteLine("thermal  no ACPI thermal zone reported" + (samples.Error != null ? $" ({samples.Error})" : ""));
        foreach (var z in samples.Zones) o.WriteLine("thermal  " + z);
        o.WriteLine("cpu      " + (samples.PerformanceLimit is { } limit
            ? $"running at up to {Format.Pct(limit)}% of what it may (% Performance Limit; under 100 means held back by heat, power or the power plan)"
            : "how much it is held back could not be read") + (samples.Frequency is { } f ? $", now at {Format.Pct(f)}% of its base frequency" : ""));

        var ms = (long)since.TotalMilliseconds;
        var limited = EventTools.Query("System", $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37 and TimeCreated[timediff(@SystemTime) <= {ms}]]]", 1000, out var error);
        o.WriteLine("throttle " + (error != null ? $"could not be read: {error}"
            : limited.Count == 0 ? $"the firmware did not limit the processor's speed in the last {Format.Duration(since)} (no Kernel-Processor-Power 37)"
            : $"the firmware limited the processor's speed {limited.Count}{(limited.Count >= 1000 ? "+" : "")} times in the last {Format.Duration(since)}, last {Format.Time(limited[0].TimeUtc)}: {Format.Clip(limited[0].Message, 160)}"));

        o.WriteLine("power    " + Power());
        o.WriteLine("not readable here: voltages (+12V, +5V, Vcore), fan speeds and the CPU's own temperature: they need the board maker's driver or a tool that loads one");
        return 0;
    }

    // ------------------------------------------------------------------ disks

    private static List<string> Disks()
    {
        var result = new List<string>();
        for (var n = 0; n < 32; n++)
        {
            var h = CreateFileW($@"\\.\PhysicalDrive{n}", 0, 3, 0, 3, 0, 0); // no access needed for these queries
            if (h == -1) continue;
            try
            {
                var name = Model(h);
                var temp = Temperature(h, out var why);
                result.Add($"PhysicalDrive{n} {name}: {temp ?? why}");
            }
            finally
            {
                CloseHandle(h);
            }
        }

        return result;
    }

    private const uint IoctlStorageQueryProperty = 0x2D1400;

    private static unsafe byte[]? Query(nint h, int property, int size)
    {
        var query = stackalloc int[3];
        query[0] = property; // PropertyId
        query[1] = 0;        // PropertyStandardQuery
        query[2] = 0;
        var buffer = new byte[size];
        fixed (byte* b = buffer)
        {
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, 12, b, (uint)size, out var got, 0) || got < 8) return null;
        }

        return buffer;
    }

    private static string Model(nint h)
    {
        var b = Query(h, 0, 1024); // StorageDeviceProperty: STORAGE_DEVICE_DESCRIPTOR
        if (b == null) return "(model unknown)";
        string At(int offsetField)
        {
            var offset = BitConverter.ToInt32(b, offsetField);
            if (offset <= 0 || offset >= b.Length) return "";
            var end = Array.IndexOf(b, (byte)0, offset);
            return Encoding.ASCII.GetString(b, offset, (end < 0 ? b.Length : end) - offset).Trim();
        }

        var bus = BitConverter.ToInt32(b, 28) switch
        {
            1 => "SCSI",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            11 => "SATA",
            17 => "NVMe",
            _ => "",
        };
        var model = $"{At(12)} {At(16)}".Trim();
        return (model.Length > 0 ? model : "(no model)") + (bus.Length > 0 ? $" ({bus})" : "");
    }

    private static string? Temperature(nint h, out string why)
    {
        why = "no temperature reported (the disk or its driver does not give one to Windows)";
        var b = Query(h, 52, 512); // StorageDeviceTemperatureProperty: STORAGE_TEMPERATURE_DATA_DESCRIPTOR
        if (b == null) return null;
        var critical = BitConverter.ToInt16(b, 8);
        var warning = BitConverter.ToInt16(b, 10);
        var count = BitConverter.ToUInt16(b, 12);
        var temps = new List<string>();
        for (var i = 0; i < count && 24 + (i * 16) + 4 <= b.Length; i++)
        {
            var t = BitConverter.ToInt16(b, 24 + (i * 16) + 2);
            if (t == short.MinValue) continue; // STORAGE_TEMPERATURE_VALUE_NOT_REPORTED
            temps.Add($"{t}°C");
        }

        if (temps.Count == 0) return null;
        string Limit(string what, short v) => v == short.MinValue || v == 0 ? $"{what} not reported" : $"{what} at {v}°C";
        return $"{string.Join(", ", temps)}{(temps.Count > 1 ? " (its sensors; the first may be a composite of them)" : "")}, {Limit("warning", warning)}, {Limit("critical", critical)}";
    }

    // ------------------------------------------------------------------ performance counters

    private sealed class Sample
    {
        public List<string> Zones { get; } = [];

        public double? PerformanceLimit { get; set; }

        public double? Frequency { get; set; }

        public string? Error { get; set; }
    }

    private static async Task<Sample> Counters(CancellationToken ct)
    {
        var s = new Sample();
        if (PdhOpenQueryW(null, 0, out var query) != 0)
        {
            s.Error = "performance counters are not available";
            return s;
        }

        try
        {
            var temperature = Add(query, @"\Thermal Zone Information(*)\Temperature");
            var passive = Add(query, @"\Thermal Zone Information(*)\% Passive Limit");
            var reasons = Add(query, @"\Thermal Zone Information(*)\Throttle Reasons");
            var limit = Add(query, @"\Processor Information(_Total)\% Performance Limit");
            var frequency = Add(query, @"\Processor Information(_Total)\% Processor Performance");
            PdhCollectQueryData(query);
            await Task.Delay(1000, ct); // rates need two samples
            PdhCollectQueryData(query);

            var temps = Values(temperature);
            var passives = Values(passive).ToDictionary(v => v.name, v => v.value);
            var why = Values(reasons).ToDictionary(v => v.name, v => v.value);
            foreach (var (name, kelvin) in temps)
            {
                var celsius = kelvin - 273.15;
                var extra = passives.TryGetValue(name, out var p) && p < 100 ? $", passive cooling holds the CPU at {Format.Pct(p)}%" : "";
                if (why.TryGetValue(name, out var r) && r > 0) extra += $", throttle reasons {r:0}";
                s.Zones.Add($"ACPI {name}: {celsius.ToString("0.0", CultureInfo.InvariantCulture)}°C (a value from the firmware: it need not be the CPU's temperature){extra}");
            }

            if (temperature == 0) s.Error = "the thermal zone counters are not on this PC";
            var l = Values(limit);
            if (l.Count > 0) s.PerformanceLimit = l[0].value;
            var f = Values(frequency);
            if (f.Count > 0) s.Frequency = f[0].value;
        }
        finally
        {
            PdhCloseQuery(query);
        }

        return s;
    }

    private static nint Add(nint query, string path) => PdhAddEnglishCounterW(query, path, 0, out var counter) == 0 ? counter : 0;

    private static unsafe List<(string name, double value)> Values(nint counter)
    {
        var list = new List<(string, double)>();
        if (counter == 0) return list;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref size, out var count, null); // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
        if (status != 0x800007D2 || size == 0) return list; // PDH_MORE_DATA
        var buffer = new byte[size];
        fixed (byte* b = buffer)
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref size, out count, b) != 0) return list;
            for (var i = 0; i < count; i++)
            {
                var item = b + (i * 24); // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, CStatus, padding, double
                if (*(uint*)(item + 8) > 1) continue; // PDH_CSTATUS_VALID_DATA (0) or NEW_DATA (1)
                list.Add((new string(*(char**)item), *(double*)(item + 16)));
            }
        }

        return list;
    }

    // ------------------------------------------------------------------ power

    private static unsafe string Power()
    {
        var parts = new List<string>();
        var status = stackalloc byte[12];
        if (GetSystemPowerStatus(status))
        {
            parts.Add(status[0] switch
            {
                0 => "on battery",
                1 => "on mains power",
                _ => "mains or battery not known",
            });
            if ((status[1] & 128) == 0 && status[1] != 255 && status[2] != 255) parts.Add($"battery {status[2]}%");
        }

        if (PowerGetActiveScheme(0, out var scheme) == 0)
        {
            try
            {
                uint size = 0;
                PowerReadFriendlyName(0, scheme, null, null, null, ref size);
                if (size > 0)
                {
                    var name = new byte[size];
                    fixed (byte* n = name)
                    {
                        if (PowerReadFriendlyName(0, scheme, null, null, n, ref size) == 0) parts.Add($"power plan \"{Encoding.Unicode.GetString(name).TrimEnd('\0')}\"");
                    }
                }
            }
            finally
            {
                LocalFree((nint)scheme);
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "could not be read";
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(nint device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetSystemPowerStatus(byte* status);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial uint PowerGetActiveScheme(nint root, out Guid* scheme);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial uint PowerReadFriendlyName(nint root, Guid* scheme, Guid* subGroup, Guid* setting, byte* buffer, ref uint size);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static unsafe partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, byte* buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
