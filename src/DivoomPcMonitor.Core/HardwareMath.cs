using System.Globalization;
using System.Text.RegularExpressions;

namespace DivoomPcMonitor;

public sealed class GpuCard
{
    public string Vendor { get; init; } = "";
    public string Pci { get; init; } = "";
    public string Name { get; init; } = "";
    public string Luid { get; init; } = "";

    public string Label
    {
        get
        {
            var label = Name.Length > 0 ? Name : Vendor.Length > 0 ? Vendor : "none";
            if (Pci.Length > 0)
            {
                return $"{label} ({Pci})";
            }

            if (Luid.Length > 0)
            {
                return $"{label} ({Luid})";
            }

            return label;
        }
    }
}

public sealed class HardwareSelection
{
    public required string CpuVendor { get; init; }
    public required GpuCard Gpu { get; init; }
}

public sealed class NvidiaRow
{
    public int? Util { get; init; }
    public int? Temp { get; init; }
    public string Status { get; init; } = "";
    public string Pci { get; init; } = "";
}

public sealed class DiskRank
{
    public string? Name { get; init; }
    public int? Util { get; init; }
}

public sealed class DiskSample
{
    public ulong Idle { get; init; }
    public ulong Timestamp { get; init; }
}

public sealed class EngineSample
{
    public required string InstanceName { get; init; }
    public double CookedValue { get; init; }
}

public sealed class ThermalZone
{
    public required string Name { get; init; }
    public double DeciKelvin { get; init; }
}

public sealed class ThermalPick
{
    public required string Name { get; init; }
    public int Celsius { get; init; }
    public double Raw { get; init; }
}

public sealed class VendorReading
{
    public int? Util { get; init; }
    public int? Temp { get; init; }
    public int? CpuTemp { get; init; }
}

public enum SetupStep
{
    Device,
    LcdGroup,
    Lcd,
    Drive,
    Gpu,
    Review,
}

public static class HardwareMath
{
    private static readonly Regex PciPattern = new(@"^[0-9a-f]{2}:[0-9a-f]{2}\.[0-9a-f]$", RegexOptions.CultureInvariant);
    private static readonly Regex LuidPattern = new(@"^luid_0x[0-9a-f]{8}_0x[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnginePattern = new(
        @"luid_(0x[0-9a-f]{8}_0x[0-9a-f]{8})_phys_\d+_eng_\d+_engtype_([a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CpuZonePattern = new(@"cpu|pkg|soc|core|tdie|tctl|processor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GpuZonePattern = new(@"gpu|gfx|adreno|kgsl|video", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ShortenPci(string? bdf)
    {
        if (string.IsNullOrWhiteSpace(bdf))
        {
            return "";
        }

        var text = bdf.Trim().ToLowerInvariant();
        if (text.StartsWith("pci:", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        var parts = text.Split(':');
        if (parts.Length >= 3)
        {
            text = parts[^2] + ":" + parts[^1];
        }

        return text;
    }

    public static bool IsPciAddress(string? pci)
    {
        return pci is not null && PciPattern.IsMatch(pci);
    }

    public static string VendorName(uint vendorId)
    {
        return vendorId switch
        {
            0x10DE => "NVIDIA",
            0x1002 => "AMD",
            0x8086 => "Intel",
            0x4D4F4351 => "Qualcomm",
            _ => "Unknown",
        };
    }

    public static string? CpuVendorFromText(string? manufacturer, string? name)
    {
        var text = $"{manufacturer} {name}";
        if (Regex.IsMatch(text, @"AMD|AuthenticAMD", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "AMD";
        }

        if (Regex.IsMatch(text, @"Intel|GenuineIntel", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "Intel";
        }

        if (manufacturer is not null && manufacturer.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase))
        {
            return "Qualcomm";
        }

        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            return manufacturer.Trim();
        }

        return null;
    }

    public static GpuCard? SelectDefault(IReadOnlyList<GpuCard> gpus)
    {
        foreach (var gpu in gpus)
        {
            if (gpu.Vendor is "NVIDIA" or "AMD")
            {
                return gpu;
            }
        }

        return gpus.Count > 0 ? gpus[0] : null;
    }

    public static GpuCard? FromConfig(MonitorConfig config, Action<string>? warn = null)
    {
        var vendor = config.GpuVendor.Trim();
        var pci = ShortenPci(config.GpuPci);
        var name = config.GpuName.Trim();
        var luid = config.GpuLuid.Trim();
        if (vendor.Length == 0 && pci.Length == 0 && luid.Length == 0)
        {
            return null;
        }

        if (pci.Length > 0 && !IsPciAddress(pci))
        {
            warn?.Invoke($"Ignoring gpu_pci='{pci}'; expected an address like 01:00.0.");
            pci = "";
        }

        if (luid.Length > 0 && !LuidPattern.IsMatch(luid))
        {
            warn?.Invoke($"Ignoring gpu_luid='{luid}'.");
            luid = "";
        }

        return new GpuCard
        {
            Vendor = vendor,
            Pci = pci,
            Name = name,
            Luid = luid,
        };
    }

    public static HardwareSelection Resolve(MonitorConfig config, IReadOnlyList<GpuCard>? gpus, Func<string?>? cpuVendorLookup = null, Action<string>? warn = null)
    {
        var cpuVendor = config.CpuVendor.Trim();
        if (cpuVendor.Length == 0)
        {
            cpuVendor = cpuVendorLookup?.Invoke() ?? "";
        }

        if (cpuVendor.Length == 0)
        {
            cpuVendor = "Unknown";
        }

        var gpu = FromConfig(config, warn);
        if (gpu is null)
        {
            var chosen = gpus is null ? null : SelectDefault(gpus);
            if (chosen is not null)
            {
                var pin = chosen.Pci.Length > 0 ? chosen.Pci : chosen.Luid;
                warn?.Invoke($"No GPU is pinned in config.env. Using {chosen.Name} ({pin}). Open Setup to choose the card.");
                gpu = chosen;
            }
            else
            {
                warn?.Invoke("No GPU found. GPU fields will stay blank until one is detected.");
                gpu = new GpuCard();
            }
        }

        return new HardwareSelection
        {
            CpuVendor = cpuVendor,
            Gpu = gpu,
        };
    }

    public static NvidiaRow ParseNvidiaCsv(string? text, string? pci)
    {
        var rows = new List<(string Pci, int Util, int Temp)>();
        foreach (var line in (text ?? "").Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',').Select(static part => part.Trim()).ToArray();
            if (parts.Length < 3 || parts[0].Length == 0)
            {
                continue;
            }

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var util))
            {
                continue;
            }

            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
            {
                continue;
            }

            rows.Add((ShortenPci(parts[0]), (int)Math.Round(util), (int)Math.Round(temp)));
        }

        if (rows.Count == 0)
        {
            return new NvidiaRow { Status = "missing" };
        }

        var wanted = ShortenPci(pci);
        if (wanted.Length > 0)
        {
            foreach (var row in rows)
            {
                if (row.Pci == wanted)
                {
                    return new NvidiaRow { Util = row.Util, Temp = row.Temp, Status = "match", Pci = row.Pci };
                }
            }

            return new NvidiaRow { Status = "missing", Pci = wanted };
        }

        var first = rows[0];
        return new NvidiaRow
        {
            Util = first.Util,
            Temp = first.Temp,
            Status = rows.Count > 1 ? "ambiguous" : "only",
            Pci = first.Pci,
        };
    }

    public static int? CpuUsage(long[]? first, long[]? second)
    {
        if (first is null || second is null || first.Length < 3 || second.Length < 3)
        {
            return null;
        }

        var total = (second[1] + second[2]) - (first[1] + first[2]);
        if (total <= 0)
        {
            return null;
        }

        var busy = total - (second[0] - first[0]);
        var percent = 100.0 * busy / total;
        if (percent < 0)
        {
            percent = 0;
        }

        if (percent > 100)
        {
            percent = 100;
        }

        return (int)Math.Round(percent);
    }

    public static DiskRank HighestDisk(
        double beforeTime,
        IReadOnlyDictionary<string, double> beforeBusy,
        double afterTime,
        IReadOnlyDictionary<string, double> afterBusy,
        IEnumerable<string> allowed)
    {
        var elapsedMs = (afterTime - beforeTime) * 1000;
        if (elapsedMs <= 0)
        {
            return new DiskRank();
        }

        var allowedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in allowed)
        {
            if (!string.IsNullOrEmpty(name))
            {
                allowedNames.Add(name);
            }
        }

        string? bestName = null;
        var bestUtil = -1.0;
        foreach (var name in afterBusy.Keys)
        {
            if (!allowedNames.Contains(name) || !beforeBusy.ContainsKey(name))
            {
                continue;
            }

            var util = (afterBusy[name] - beforeBusy[name]) / elapsedMs * 100;
            if (util < 0)
            {
                util = 0;
            }

            if (util > 100)
            {
                util = 100;
            }

            if (util > bestUtil)
            {
                bestUtil = util;
                bestName = name;
            }
        }

        if (bestName is null)
        {
            return new DiskRank();
        }

        return new DiskRank { Name = bestName, Util = (int)Math.Round(bestUtil) };
    }

    public static DiskRank BusierLogicalDisk(
        IReadOnlyDictionary<string, DiskSample>? before,
        IReadOnlyDictionary<string, DiskSample>? after,
        IEnumerable<string> allowed)
    {
        if (before is null || after is null)
        {
            return new DiskRank();
        }

        string? bestName = null;
        var bestUtil = -1.0;
        foreach (var name in allowed)
        {
            var key = (name ?? "").Trim().ToUpperInvariant();
            if (key.Length == 0 || !before.TryGetValue(key, out var first) || !after.TryGetValue(key, out var second))
            {
                continue;
            }

            var timeDelta = (decimal)second.Timestamp - (decimal)first.Timestamp;
            if (timeDelta <= 0)
            {
                continue;
            }

            var idleDelta = (decimal)second.Idle - (decimal)first.Idle;
            if (idleDelta < 0)
            {
                idleDelta = 0;
            }

            var idlePercent = idleDelta / timeDelta * 100m;
            if (idlePercent > 100)
            {
                idlePercent = 100;
            }

            var util = (double)(100m - idlePercent);
            if (util < 0)
            {
                util = 0;
            }

            if (util > bestUtil)
            {
                bestUtil = util;
                bestName = key;
            }
        }

        if (bestName is null)
        {
            return new DiskRank();
        }

        return new DiskRank { Name = bestName, Util = (int)Math.Round(bestUtil) };
    }

    public static int? GpuUsage(IEnumerable<EngineSample>? samples, string? luid)
    {
        if (string.IsNullOrWhiteSpace(luid) || samples is null)
        {
            return null;
        }

        var wanted = luid.ToLowerInvariant();
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var match = EnginePattern.Match(sample.InstanceName ?? "");
            if (!match.Success)
            {
                continue;
            }

            var found = "luid_" + match.Groups[1].Value.ToLowerInvariant();
            if (found != wanted)
            {
                continue;
            }

            var kind = match.Groups[2].Value.ToLowerInvariant();
            sums[kind] = sums.TryGetValue(kind, out var existing) ? existing + sample.CookedValue : sample.CookedValue;
        }

        if (!sums.ContainsKey("3d") && !sums.ContainsKey("compute"))
        {
            return null;
        }

        var value = 0.0;
        foreach (var kind in new[] { "3d", "compute" })
        {
            if (sums.TryGetValue(kind, out var sum) && sum > value)
            {
                value = sum;
            }
        }

        if (value < 0)
        {
            value = 0;
        }

        if (value > 100)
        {
            value = 100;
        }

        return (int)Math.Round(value);
    }

    public static double? ThermalCelsius(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        double kelvin;
        if (number >= 1000)
        {
            kelvin = number / 10.0;
        }
        else if (number is >= 200 and <= 500)
        {
            kelvin = number;
        }
        else
        {
            return null;
        }

        var celsius = kelvin - 273.15;
        if (celsius < 0 || celsius > 125)
        {
            return null;
        }

        return celsius;
    }

    public static int? ThermalCelsiusRounded(object? value)
    {
        var celsius = ThermalCelsius(value);
        if (celsius is null)
        {
            return null;
        }

        var rounded = (int)Math.Round(celsius.Value);
        if (rounded < 0 || rounded > 125)
        {
            return null;
        }

        return rounded;
    }

    public static ThermalPick? SelectThermal(IEnumerable<ThermalZone>? zones, string role)
    {
        var gpu = string.Equals(role, "Gpu", StringComparison.OrdinalIgnoreCase);
        var pattern = gpu ? GpuZonePattern : CpuZonePattern;
        var valid = new List<ThermalPick>();
        if (zones is not null)
        {
            foreach (var zone in zones)
            {
                if (zone is null)
                {
                    continue;
                }

                var celsius = ThermalCelsius(zone.DeciKelvin);
                if (celsius is null)
                {
                    continue;
                }

                valid.Add(new ThermalPick
                {
                    Name = zone.Name ?? "",
                    Celsius = (int)Math.Round(celsius.Value),
                    Raw = celsius.Value,
                });
            }
        }

        var named = valid.Where(zone => pattern.IsMatch(zone.Name)).ToList();
        IReadOnlyList<ThermalPick> pool;
        if (named.Count > 0)
        {
            pool = named;
        }
        else if (gpu)
        {
            return null;
        }
        else
        {
            pool = valid;
        }

        ThermalPick? best = null;
        foreach (var zone in pool)
        {
            if (best is null
                || zone.Raw > best.Raw
                || (zone.Raw == best.Raw && string.Compare(zone.Name, best.Name, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = zone;
            }
        }

        return best;
    }

    public static int? VendorCelsius(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        if (value.Value < 0 || value.Value > 125)
        {
            return null;
        }

        return (int)Math.Round(value.Value);
    }

    public static (int? Util, int? Temp) MergeGpu(int? util, int? temp, int? extraUtil, int? extraTemp)
    {
        return (util ?? extraUtil, temp ?? extraTemp);
    }

    public static (int? Util, int? Temp) CombineGpu(GpuCard gpu, IEnumerable<EngineSample>? engines, VendorReading? vendor, NvidiaRow? nvidia, Action<string>? once = null)
    {
        var name = (gpu.Vendor ?? "").Trim();
        if (name.Length == 0)
        {
            once?.Invoke("gpu-missing|No GPU is configured. Open Setup to choose one.");
            return (null, null);
        }

        var vendorUtil = vendor?.Util;
        var vendorTemp = vendor?.Temp;
        var enginesUtil = GpuUsage(engines, gpu.Luid);
        (int? Util, int? Temp) reading;
        switch (name)
        {
            case "NVIDIA":
                var primary = nvidia ?? new NvidiaRow();
                reading = MergeGpu(primary.Util, primary.Temp, vendorUtil, vendorTemp);
                reading = MergeGpu(reading.Util, reading.Temp, enginesUtil, null);
                break;
            case "AMD":
            case "Intel":
                reading = MergeGpu(vendorUtil, vendorTemp, enginesUtil, null);
                break;
            default:
                reading = MergeGpu(enginesUtil, vendorTemp, vendorUtil, null);
                break;
        }

        if (name == "Qualcomm" && reading.Temp is null)
        {
            once?.Invoke("qcom-temp|Qualcomm does not publish GPU temperature on Windows. Utilization comes from the Adreno GPU Engine counters.");
        }

        return reading;
    }

    public static IReadOnlyList<SetupStep> PlanSteps(int groupCount, bool pixoo, int gpuCount)
    {
        var steps = new List<SetupStep> { SetupStep.Device };
        if (!pixoo)
        {
            if (groupCount > 1)
            {
                steps.Add(SetupStep.LcdGroup);
            }

            steps.Add(SetupStep.Lcd);
        }

        steps.Add(SetupStep.Drive);
        if (gpuCount > 1)
        {
            steps.Add(SetupStep.Gpu);
        }

        steps.Add(SetupStep.Review);
        return steps;
    }

    public static int? IntelUtil(ulong[]? first, ulong[]? second)
    {
        if (first is null || second is null || first.Length < 2 || second.Length < 2)
        {
            return null;
        }

        var elapsed = (long)second[1] - (long)first[1];
        if (elapsed <= 0)
        {
            return null;
        }

        var active = (long)second[0] - (long)first[0];
        if (active < 0)
        {
            active = 0;
        }

        var percent = 100.0 * active / elapsed;
        if (percent < 0)
        {
            percent = 0;
        }

        if (percent > 100)
        {
            percent = 100;
        }

        return (int)Math.Round(percent);
    }
}
