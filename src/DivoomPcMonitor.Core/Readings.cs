using System.Text.Encodings.Web;
using System.Text.Json;

namespace DivoomPcMonitor;

public sealed class RawSample
{
    public int? CpuUtil { get; init; }
    public int? CpuTemp { get; init; }
    public int? GpuUtil { get; init; }
    public int? GpuTemp { get; init; }
    public int? MemoryUtil { get; init; }
    public int? DiskUtil { get; init; }
    public string? DiskName { get; init; }
    public string? CpuTempSource { get; init; }
}

public sealed class DisplayReading
{
    public int? CpuUtil { get; init; }
    public int? CpuTemp { get; init; }
    public int? GpuUtil { get; init; }
    public int? GpuTemp { get; init; }
    public int? MemoryUtil { get; init; }
    public int? DiskUtil { get; init; }
    public string? DiskName { get; init; }

    public string CpuUtilText => DisplayText.Percent(CpuUtil);
    public string CpuTempText => DisplayText.Temperature(CpuTemp);
    public string GpuUtilText => DisplayText.Percent(GpuUtil);
    public string GpuTempText => DisplayText.Temperature(GpuTemp);
    public string MemoryText => DisplayText.Percent(MemoryUtil);
    public string DiskText => DisplayText.Percent(DiskUtil);
}

/// <summary>
/// Keeps the last good reading. A real zero is kept. A later failure does not clear it.
/// </summary>
public sealed class ReadingHold
{
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);
    private string? _diskName;

    public int? Update(string key, int? value)
    {
        if (value is int number)
        {
            _values[key] = number;
        }

        return _values.TryGetValue(key, out var kept) ? kept : null;
    }

    public DisplayReading Apply(RawSample sample)
    {
        string? diskName;
        if (sample.DiskUtil is not null)
        {
            _diskName = sample.DiskName;
            diskName = sample.DiskName;
        }
        else
        {
            diskName = _diskName;
        }

        return new DisplayReading
        {
            CpuUtil = Update("cpu_util", sample.CpuUtil),
            CpuTemp = Update("cpu_temp", sample.CpuTemp),
            GpuUtil = Update("gpu_util", sample.GpuUtil),
            GpuTemp = Update("gpu_temp", sample.GpuTemp),
            MemoryUtil = Update("mem_util", sample.MemoryUtil),
            DiskUtil = Update("disk_util", sample.DiskUtil),
            DiskName = diskName,
        };
    }
}

public sealed class SystemPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public required string Command { get; init; }
    public required ScreenEntry[] ScreenList { get; init; }

    public sealed class ScreenEntry
    {
        public int LcdId { get; init; }
        public required string[] DispData { get; init; }
    }

    public static SystemPayload Create(
        string lcdIndex,
        int? cpuUtil,
        int? cpuTemp,
        int? gpuUtil,
        int? gpuTemp,
        int? memoryUtil,
        int? diskUtil)
    {
        if (!int.TryParse(lcdIndex, out var lcd))
        {
            throw new FormatException($"Invalid LCD index: {lcdIndex}");
        }

        return new SystemPayload
        {
            Command = "Device/UpdatePCParaInfo",
            ScreenList =
            [
                new ScreenEntry
                {
                    LcdId = lcd,
                    DispData =
                    [
                        DisplayText.Percent(cpuUtil),
                        DisplayText.Percent(gpuUtil),
                        DisplayText.Temperature(cpuTemp),
                        DisplayText.Temperature(gpuTemp),
                        DisplayText.Percent(memoryUtil),
                        DisplayText.Percent(diskUtil),
                    ],
                },
            ],
        };
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }
}
