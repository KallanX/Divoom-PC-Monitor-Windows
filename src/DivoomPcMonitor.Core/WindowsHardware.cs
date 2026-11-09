using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DivoomPcMonitor;

public sealed class FixedDrive
{
    public string DeviceId { get; init; } = "";
    public string VolumeName { get; init; } = "";
    public long SizeBytes { get; init; }

    public string SizeLabel => SizeBytes <= 0 ? "" : $"{Math.Round(SizeBytes / 1073741824d).ToString(CultureInfo.InvariantCulture)} GB";
}

public static class WindowsHardware
{
    private static readonly Guid DxgiFactoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly Regex PciLocation = new(@"PCI bus (\d+), device (\d+), function (\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex DriveLetter = new(@"^[A-Za-z]:$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<GpuCard> ListGpus()
    {
        var pciByName = ReadDisplayPci();
        var cards = new List<GpuCard>();
        foreach (var adapter in EnumerateAdapters())
        {
            if ((adapter.Flags & 2) != 0 || adapter.VendorId == 0x1414)
            {
                continue;
            }

            var name = (adapter.Name ?? "").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            cards.Add(new GpuCard
            {
                Vendor = HardwareMath.VendorName(adapter.VendorId),
                Pci = pciByName.TryGetValue(name, out var pci) ? pci : "",
                Name = name,
                Luid = adapter.Luid,
            });
        }

        return cards;
    }

    public static string ReadCpuVendor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Name FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var vendor = HardwareMath.CpuVendorFromText(item["Manufacturer"]?.ToString(), item["Name"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(vendor))
                    {
                        return vendor;
                    }
                }
            }
        }
        catch (Exception)
        {
            return "";
        }

        return "";
    }

    public static IReadOnlyList<FixedDrive> ListFixedDrives()
    {
        var drives = new List<FixedDrive>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, VolumeName, Size, DriveType FROM Win32_LogicalDisk");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var type = ToInt(item["DriveType"]);
                var id = item["DeviceID"]?.ToString() ?? "";
                if ((type != 2 && type != 3) || id.Length == 0)
                {
                    continue;
                }

                drives.Add(new FixedDrive
                {
                    DeviceId = id,
                    VolumeName = item["VolumeName"]?.ToString() ?? "",
                    SizeBytes = ToInt64(item["Size"]),
                });
            }
        }

        drives.Sort(static (left, right) => string.Compare(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase));
        return drives;
    }

    public static Dictionary<string, DiskSample> DiskSnapshot()
    {
        var snapshot = new Dictionary<string, DiskSample>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT Name, PercentIdleTime, Timestamp_Sys100NS FROM Win32_PerfRawData_PerfDisk_LogicalDisk");
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var name = item["Name"]?.ToString() ?? "";
                if (!DriveLetter.IsMatch(name))
                {
                    continue;
                }

                snapshot[name.ToUpperInvariant()] = new DiskSample
                {
                    Idle = ToUInt64(item["PercentIdleTime"]),
                    Timestamp = ToUInt64(item["Timestamp_Sys100NS"]),
                };
            }
        }

        return snapshot;
    }

    private static string? _cachedNvidiaSmi;
    private static IReadOnlyDictionary<string, string>? _cachedThermalHardwareIds;

    public static int? MemoryLoad()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        return status.MemoryLoad > 100 ? 100 : (int)status.MemoryLoad;
    }

    public static long[]? SystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return [idle, kernel, user];
    }

    public static string? FindNvidiaSmi()
    {
        var cached = Volatile.Read(ref _cachedNvidiaSmi);
        if (cached is not null && File.Exists(cached))
        {
            return cached;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), "nvidia-smi.exe");
            if (File.Exists(candidate))
            {
                Volatile.Write(ref _cachedNvidiaSmi, candidate);
                return candidate;
            }
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var candidate in new[]
        {
            Path.Combine(windows, "System32", "nvidia-smi.exe"),
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        })
        {
            if (File.Exists(candidate))
            {
                Volatile.Write(ref _cachedNvidiaSmi, candidate);
                return candidate;
            }
        }

        return null;
    }

    public static string? FindVendorFile(IEnumerable<string> candidates, string fileName, string? searchRoot)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(searchRoot, fileName, new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            })
            .OrderByDescending(static file =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(file);
                }
                catch (Exception)
                {
                    return DateTime.MinValue;
                }
            })
            .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadDisplayPci()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var classGuid = DisplayClass;
        var deviceSet = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 0x02);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(deviceSet, index, ref info); index++)
            {
                var friendlyName = ReadDeviceProperty(deviceSet, ref info, 0x0C);
                var deviceDesc = ReadDeviceProperty(deviceSet, ref info, 0x00);
                if (string.IsNullOrWhiteSpace(friendlyName) && string.IsNullOrWhiteSpace(deviceDesc))
                {
                    continue;
                }

                string? pci = null;
                if (ReadDeviceDwordProperty(deviceSet, ref info, 0x15, out var busNum)
                    && ReadDeviceDwordProperty(deviceSet, ref info, 0x1C, out var address)
                    && busNum <= 255)
                {
                    var devNum = (address >> 16) & 0xFFFF;
                    var funcNum = address & 0xFFFF;
                    if (devNum <= 31 && funcNum <= 7)
                    {
                        pci = $"{busNum:x2}:{devNum:x2}.{funcNum:x}";
                    }
                }

                if (pci is null)
                {
                    var location = ReadDeviceProperty(deviceSet, ref info, 0x0D);
                    var match = PciLocation.Match(location ?? "");
                    if (match.Success)
                    {
                        var bus = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        var device = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                        var function = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                        pci = $"{bus:x2}:{device:x2}.{function:x}";
                    }
                }

                if (pci is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    map.TryAdd(friendlyName, pci);
                }

                if (!string.IsNullOrWhiteSpace(deviceDesc))
                {
                    map.TryAdd(deviceDesc, pci);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }

        return map;
    }

    private static bool ReadDeviceDwordProperty(IntPtr deviceSet, ref SpDevInfoData info, uint property, out uint value)
    {
        var buffer = new byte[4];
        if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out var regType, buffer, (uint)buffer.Length, out _)
            || regType != 4)
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    private static string ReadDeviceProperty(IntPtr deviceSet, ref SpDevInfoData info, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer, (uint)buffer.Length, out var required))
        {
            if (required is 0 or > 8192)
            {
                return "";
            }

            buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer, (uint)buffer.Length, out _))
            {
                return "";
            }
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    public static IReadOnlyDictionary<string, string> ReadThermalZoneHardwareIds()
    {
        var cached = Volatile.Read(ref _cachedThermalHardwareIds);
        if (cached is not null)
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deviceSet = SetupDiGetClassDevsAll(IntPtr.Zero, "ACPI", IntPtr.Zero, 0x06);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var biosKey = new DevPropKey
            {
                Fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
                Pid = 10,
            };
            var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(deviceSet, index, ref info); index++)
            {
                var biosName = ReadDeviceStringProperty(deviceSet, ref info, ref biosKey);
                if (string.IsNullOrWhiteSpace(biosName))
                {
                    continue;
                }

                var hardwareId = ReadDeviceMultiStringProperty(deviceSet, ref info, 0x01);
                if (!string.IsNullOrWhiteSpace(hardwareId))
                {
                    map[biosName] = hardwareId;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }

        if (map.Count > 0)
        {
            Volatile.Write(ref _cachedThermalHardwareIds, map);
        }

        return map;
    }

    private static string ReadDeviceMultiStringProperty(IntPtr deviceSet, ref SpDevInfoData info, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer, (uint)buffer.Length, out var required))
        {
            if (required is 0 or > 8192)
            {
                return "";
            }

            buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryProperty(deviceSet, ref info, property, out _, buffer, (uint)buffer.Length, out required))
            {
                return "";
            }
        }

        var byteLen = required > 0 && required <= (uint)buffer.Length ? (int)required : buffer.Length;
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, byteLen).TrimEnd('\0');
        return text.Replace('\0', ' ').Trim();
    }

    private static string ReadDeviceStringProperty(IntPtr deviceSet, ref SpDevInfoData info, ref DevPropKey key)
    {
        var buffer = new byte[256];
        if (!SetupDiGetDeviceProperty(deviceSet, ref info, ref key, out var propType, buffer, (uint)buffer.Length, out var required, 0))
        {
            if (required is 0 or > 4096)
            {
                return "";
            }

            buffer = new byte[required];
            if (!SetupDiGetDeviceProperty(deviceSet, ref info, ref key, out propType, buffer, (uint)buffer.Length, out _, 0))
            {
                return "";
            }
        }

        if (propType != 18)
        {
            return "";
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    public static int? ReadD3dkmtGpuTemp(string? luid)
    {
        var clean = (luid ?? "").Trim().ToLowerInvariant();
        if (clean.Length != 26 || !clean.StartsWith("luid_0x", StringComparison.Ordinal) || clean[15..18] != "_0x")
        {
            return null;
        }

        if (!uint.TryParse(clean.AsSpan(7, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
            || !uint.TryParse(clean.AsSpan(18, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
        {
            return null;
        }

        var open = new D3dkmtOpenAdapterFromLuid
        {
            AdapterLuidLow = low,
            AdapterLuidHigh = unchecked((int)high),
        };

        try
        {
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.AdapterHandle == 0)
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        const int perfDataSize = 64;
        var perfBuffer = Marshal.AllocHGlobal(perfDataSize);
        try
        {
            for (var i = 0; i < perfDataSize; i++)
            {
                Marshal.WriteByte(perfBuffer, i, 0);
            }

            var query = new D3dkmtQueryAdapterInfo
            {
                AdapterHandle = open.AdapterHandle,
                Type = 62,
                PrivateDriverData = perfBuffer,
                PrivateDriverDataSize = perfDataSize,
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0)
            {
                return null;
            }

            var rawTemp = unchecked((uint)Marshal.ReadInt32(perfBuffer, 52));
            var celsius = rawTemp switch
            {
                >= 150 and <= 1250 => rawTemp / 10.0,
                >= 15 and <= 125 => (double)rawTemp,
                _ => -1.0,
            };
            return celsius < 0 ? null : HardwareMath.VendorCelsius(celsius);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(perfBuffer);
            var close = new D3dkmtCloseAdapter { AdapterHandle = open.AdapterHandle };
            try
            {
                D3DKMTCloseAdapter(ref close);
            }
            catch (Exception)
            {
            }
        }
    }

    private static List<AdapterRow> EnumerateAdapters()
    {
        var found = new List<AdapterRow>();
        var iid = DxgiFactoryIid;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero)
        {
            return found;
        }

        try
        {
            var enumerate = Vtable<EnumAdapters1>(factory, 12);
            for (uint index = 0; index < 32; index++)
            {
                var hr = enumerate(factory, index, out var adapter);
                if (hr < 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var desc = new AdapterDesc();
                    var getDesc = Vtable<GetDesc1>(adapter, 10);
                    if (getDesc(adapter, ref desc) < 0)
                    {
                        continue;
                    }

                    var high = unchecked((uint)desc.LuidHigh);
                    found.Add(new AdapterRow(
                        desc.Description?.Trim() ?? "",
                        desc.VendorId,
                        desc.Flags,
                        "luid_0x" + high.ToString("x8", CultureInfo.InvariantCulture) + "_0x" + desc.LuidLow.ToString("x8", CultureInfo.InvariantCulture)));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return found;
    }

    private static T Vtable<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(comObject);
        var pointer = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static int ToInt(object? value)
    {
        if (value is null)
        {
            return -1;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static long ToInt64(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static ulong ToUInt64(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private readonly record struct AdapterRow(string Name, uint VendorId, uint Flags, string Luid);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtOpenAdapterFromLuid
    {
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint AdapterHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint AdapterHandle;
        public uint Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr self, ref AdapterDesc desc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter close);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
