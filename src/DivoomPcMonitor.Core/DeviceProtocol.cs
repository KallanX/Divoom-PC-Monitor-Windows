using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DivoomPcMonitor;

public interface IDevicePoster
{
    Task<bool> SendSystemAsync(MonitorConfig config, DisplayReading reading, bool verbose, CancellationToken cancellationToken);

    Task<bool> SelectClockAsync(
        string deviceId,
        string deviceIp,
        string lcdIndependence,
        string lcdIndex,
        string lcdClockId,
        bool verbose,
        CancellationToken cancellationToken);
}

public sealed class LanDevice
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Hardware { get; init; } = "";
}

public sealed class LcdFace
{
    public string ClockId { get; init; } = "";
    public string ImagePixelId { get; init; } = "";
    public bool IsPcMonitor => ClockId == "625";
}

public sealed class LcdGroupInfo
{
    public string Independence { get; init; } = "0";
    public IReadOnlyList<LcdFace> Faces { get; init; } = [];
}

public sealed class DivoomDeviceClient : IDevicePoster, IDisposable
{
    public const string DeviceListUrl = "https://app.divoom-gz.com/Device/ReturnSameLANDevice";
    public const string LcdListUrl = "https://app.divoom-gz.com/Channel/Get5LcdInfoV2?DeviceType=LCD&DeviceId=";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _device = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _cloud = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Action<string>? _log;

    public DivoomDeviceClient(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task<bool> SendSystemAsync(MonitorConfig config, DisplayReading reading, bool verbose, CancellationToken cancellationToken)
    {
        SystemPayload payload;
        try
        {
            payload = SystemPayload.Create(
                config.LcdIndex,
                reading.CpuUtil,
                reading.CpuTemp,
                reading.GpuUtil,
                reading.GpuTemp,
                reading.MemoryUtil,
                reading.DiskUtil);
        }
        catch (FormatException ex)
        {
            _log?.Invoke("HTTP Post - ERROR! Invalid LCD index: " + ex.Message);
            return false;
        }

        var json = payload.ToJson();
        if (verbose)
        {
            _log?.Invoke("Data to be sent: " + json);
        }

        return await PostAsync(config.DeviceIp, json, verbose, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SelectClockAsync(
        string deviceId,
        string deviceIp,
        string lcdIndependence,
        string lcdIndex,
        string lcdClockId,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var json = ClockJson(deviceId, lcdIndependence, lcdIndex, lcdClockId);
        if (verbose)
        {
            _log?.Invoke("Select Clock Data to be sent: " + json);
        }

        return await PostAsync(deviceIp, json, verbose, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var body = await _cloud.GetStringAsync(DeviceListUrl, cancellationToken).ConfigureAwait(false);
            return ParseDevices(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Error getting device list: " + ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<LcdGroupInfo>> ListLcdGroupsAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var body = await _cloud.GetStringAsync(LcdListUrl + Uri.EscapeDataString(deviceId), cancellationToken).ConfigureAwait(false);
            return ParseLcdGroups(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Error getting LCD list: " + ex.Message, ex);
        }
    }

    public static IReadOnlyList<LanDevice> ParseDevices(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("DeviceList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var devices = new List<LanDevice>();
        foreach (var item in list.EnumerateArray())
        {
            devices.Add(new LanDevice
            {
                Name = ReadString(item, "DeviceName"),
                Id = ReadId(item, "DeviceId"),
                Ip = ReadString(item, "DevicePrivateIP"),
                Mac = ReadString(item, "DeviceMac"),
                Hardware = ReadId(item, "Hardware"),
            });
        }

        return devices;
    }

    public static IReadOnlyList<LcdGroupInfo> ParseLcdGroups(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("LcdIndependenceList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var groups = new List<LcdGroupInfo>();
        foreach (var item in list.EnumerateArray())
        {
            var faces = new List<LcdFace>();
            if (item.TryGetProperty("LcdList", out var facesElement) && facesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var face in facesElement.EnumerateArray())
                {
                    faces.Add(new LcdFace
                    {
                        ClockId = ReadId(face, "LcdClockId"),
                        ImagePixelId = ReadId(face, "ClockImagePixelId"),
                    });
                }
            }

            groups.Add(new LcdGroupInfo
            {
                Independence = item.TryGetProperty("LcdIndependence", out _) ? ReadId(item, "LcdIndependence") : "0",
                Faces = faces,
            });
        }

        return groups;
    }

    public static JsonNode NumberOrString(string? value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return value ?? "";
    }

    public static string ClockJson(string deviceId, string lcdIndependence, string lcdIndex, string lcdClockId)
    {
        _ = lcdClockId;
        var payload = new JsonObject
        {
            ["Command"] = "Channel/SetClockSelectId",
            ["LcdIndependence"] = NumberOrString(lcdIndependence),
            ["DeviceId"] = NumberOrString(deviceId),
            ["LcdIndex"] = NumberOrString(lcdIndex),
            ["ClockId"] = 625,
        };
        return payload.ToJsonString(JsonOptions);
    }

    public void Dispose()
    {
        _device.Dispose();
        _cloud.Dispose();
    }

    private async Task<bool> PostAsync(string deviceIp, string json, bool verbose, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.ContentType!.CharSet = "utf-8";
            using var response = await _device.PostAsync($"http://{deviceIp}:80/post", content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 400)
            {
                _log?.Invoke($"HTTP Post - ERROR! (HTTP {(int)response.StatusCode})");
                if (verbose)
                {
                    _log?.Invoke("System Information Response: " + body);
                }

                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                _log?.Invoke("RESPONSE - ERROR! (Invalid JSON)");
                if (verbose)
                {
                    _log?.Invoke("System Information Response: " + body);
                }

                return false;
            }

            using (document)
            {
                var ok = document.RootElement.TryGetProperty("error_code", out var code)
                    && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out var number)
                    && number == 0;
                _log?.Invoke(ok ? "HTTP Post - OKAY!" : "HTTP Post - ERROR!");
                if (verbose)
                {
                    _log?.Invoke("System Information Response: " + body);
                }

                return ok;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke("HTTP Post - ERROR! " + ex.Message);
            return false;
        }
    }

    private static string ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string ReadId(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null => "",
            _ => value.ToString(),
        };
    }
}
