using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DivoomPcMonitor;

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }
}

public sealed class MonitorConfig
{
    public string DeviceId { get; init; } = "";
    public string DeviceIp { get; init; } = "";
    public string LcdIndependence { get; init; } = "";
    public string LcdIndex { get; init; } = "";
    public string LcdClockId { get; init; } = "";
    public IReadOnlyList<string> HardDrive { get; init; } = [];
    public string CpuVendor { get; init; } = "";
    public string GpuVendor { get; init; } = "";
    public string GpuPci { get; init; } = "";
    public string GpuName { get; init; } = "";
    public string GpuLuid { get; init; } = "";

    public string HardwareKey => string.Join('|', CpuVendor, GpuVendor, GpuPci, GpuName, GpuLuid);
}

public static class ConfigStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static MonitorConfig Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Configuration file not found: {path}. Open Setup in {Product.Name}.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_id"] = "",
            ["device_ip"] = "",
            ["lcd_independence"] = "",
            ["lcd_index"] = "",
            ["lcd_clock_id"] = "",
            ["hard_drive"] = "",
            ["cpu_vendor"] = "",
            ["gpu_vendor"] = "",
            ["gpu_pci"] = "",
            ["gpu_name"] = "",
            ["gpu_luid"] = "",
        };

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith('['))
            {
                continue;
            }

            var split = text.IndexOf('=');
            if (split < 1)
            {
                continue;
            }

            var key = CanonicalKey(text[..split].Trim());
            if (key is null)
            {
                continue;
            }

            values[key] = text[(split + 1)..].Trim();
        }

        var deviceIp = values["device_ip"];
        var lcdIndex = values["lcd_index"];
        if (deviceIp.Length == 0 || deviceIp == "None")
        {
            throw new ConfigException("config.env has no device_ip. Open Setup in " + Product.Name + ".");
        }

        if (lcdIndex.Length == 0 || lcdIndex == "None")
        {
            throw new ConfigException("config.env has no lcd_index. Open Setup in " + Product.Name + ".");
        }

        var drives = values["hard_drive"]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part.Length > 0)
            .ToArray();

        return new MonitorConfig
        {
            DeviceId = values["device_id"],
            DeviceIp = deviceIp,
            LcdIndependence = values["lcd_independence"],
            LcdIndex = lcdIndex,
            LcdClockId = values["lcd_clock_id"],
            HardDrive = drives,
            CpuVendor = values["cpu_vendor"],
            GpuVendor = values["gpu_vendor"],
            GpuPci = values["gpu_pci"],
            GpuName = values["gpu_name"],
            GpuLuid = values["gpu_luid"],
        };
    }

    public static void Write(
        string path,
        string? deviceId,
        string? deviceIp,
        string? lcdIndependence,
        string? lcdIndex,
        string? lcdClockId,
        object? hardDrive,
        string? cpuVendor,
        GpuCard? gpu)
    {
        var driveText = string.Join(',', EnumerateDrives(hardDrive).Select(FormatValue).Where(static text => text.Length > 0));
        var card = gpu ?? new GpuCard();
        var lines = new[]
        {
            "[DEFAULT]",
            "device_id=" + FormatValue(deviceId),
            "device_ip=" + FormatValue(deviceIp),
            "lcd_independence=" + FormatValue(lcdIndependence),
            "lcd_index=" + FormatValue(lcdIndex),
            "lcd_clock_id=" + FormatValue(lcdClockId),
            "hard_drive=" + driveText,
            "cpu_vendor=" + FormatValue(cpuVendor),
            "gpu_vendor=" + FormatValue(card.Vendor),
            "gpu_pci=" + FormatValue(card.Pci),
            "gpu_name=" + FormatValue(card.Name),
            "gpu_luid=" + FormatValue(card.Luid),
        };

        var temporary = path + ".tmp";
        var replaced = false;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, string.Join('\n', lines) + "\n", Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
            replaced = true;
        }
        finally
        {
            if (!replaced && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string FormatValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    public static string? CanonicalKey(string name)
    {
        var compact = name.ToLowerInvariant().Replace("_", "");
        return compact switch
        {
            "deviceid" => "device_id",
            "deviceip" => "device_ip",
            "lcdindependence" => "lcd_independence",
            "lcdindex" => "lcd_index",
            "lcdclockid" => "lcd_clock_id",
            "harddrive" => "hard_drive",
            "selecteddriveletters" => "hard_drive",
            "cpuvendor" => "cpu_vendor",
            "gpuvendor" => "gpu_vendor",
            "gpupci" => "gpu_pci",
            "gpuname" => "gpu_name",
            "gpuluid" => "gpu_luid",
            _ => null,
        };
    }

    private static IEnumerable<string> EnumerateDrives(object? hardDrive)
    {
        switch (hardDrive)
        {
            case null:
                yield break;
            case string one:
                yield return one;
                yield break;
            case IEnumerable<string> many:
                foreach (var drive in many)
                {
                    if (!string.IsNullOrWhiteSpace(drive))
                    {
                        yield return drive;
                    }
                }

                yield break;
            default:
                yield return Convert.ToString(hardDrive, CultureInfo.InvariantCulture) ?? "";
                yield break;
        }
    }
}

public static class ConfigLocator
{
    public static string? Find(string? startDirectory = null, string? executableDirectory = null)
    {
        startDirectory ??= Directory.GetCurrentDirectory();
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                ordered.Add(full);
            }
        }

        Consider(Path.Combine(startDirectory, "config.env"));
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var appRoot = ResolveAppDirectory(executableDirectory);
            Consider(Path.Combine(appRoot, "config.env"));
            Consider(Path.Combine(executableDirectory, "config.env"));
        }

        foreach (var directory in Ancestors(startDirectory).Take(8))
        {
            Consider(Path.Combine(directory, "config.env"));
        }

        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            foreach (var directory in Ancestors(executableDirectory).Take(8))
            {
                Consider(Path.Combine(directory, "config.env"));
            }
        }

        Consider(Path.Combine(LocalFolder(), "config.env"));
        return ordered.FirstOrDefault(File.Exists);
    }

    public static string DefaultSavePath(string executableDirectory)
    {
        var appRoot = ResolveAppDirectory(executableDirectory);
        if (CanWrite(appRoot))
        {
            return Path.Combine(appRoot, "config.env");
        }

        if (CanWrite(executableDirectory))
        {
            return Path.Combine(executableDirectory, "config.env");
        }

        var folder = LocalFolder();
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "config.env");
    }

    public static string ResolveAppDirectory(string executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return executableDirectory;
        }

        var envRoot = Environment.GetEnvironmentVariable("DIVOOM_ROOT_DIR");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        try
        {
            var trimmed = executableDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var info = new DirectoryInfo(trimmed);
            if (string.Equals(info.Name, "runtime", StringComparison.OrdinalIgnoreCase) && info.Parent is not null)
            {
                return info.Parent.FullName;
            }
        }
        catch (Exception)
        {
        }

        return executableDirectory;
    }

    public static string? ResolveLauncherPath(string? processPath)
    {
        var envLauncher = Environment.GetEnvironmentVariable("DIVOOM_LAUNCHER_PATH");
        if (!string.IsNullOrWhiteSpace(envLauncher) && File.Exists(envLauncher))
        {
            return Path.GetFullPath(envLauncher);
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        try
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(directory))
            {
                var appRoot = ResolveAppDirectory(directory);
                var rootExe = Path.Combine(appRoot, Path.GetFileName(processPath));
                if (File.Exists(rootExe))
                {
                    return Path.GetFullPath(rootExe);
                }
            }
        }
        catch (Exception)
        {
        }

        return processPath;
    }

    public static string LocalFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DivoomPCMonitor");
    }

    private static IEnumerable<string> Ancestors(string directory)
    {
        DirectoryInfo? parent;
        try
        {
            parent = new DirectoryInfo(directory).Parent;
        }
        catch (Exception)
        {
            yield break;
        }

        while (parent is not null)
        {
            yield return parent.FullName;
            parent = parent.Parent;
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".divoom-write-" + Guid.NewGuid().ToString("n"));
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int IntervalSeconds { get; set; } = 30;
    public bool DebugLog { get; set; }

    public static string FilePath => Path.Combine(ConfigLocator.LocalFolder(), "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (loaded.IntervalSeconds < 1)
            {
                loaded.IntervalSeconds = 30;
            }

            return loaded;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        if (IntervalSeconds < 1)
        {
            IntervalSeconds = 1;
        }

        Directory.CreateDirectory(ConfigLocator.LocalFolder());
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
