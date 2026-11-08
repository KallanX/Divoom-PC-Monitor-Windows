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

    public static int? MemoryLoad()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        return status.MemoryLoad < 0 ? null : (int)status.MemoryLoad;
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
            }).FirstOrDefault();
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
                var name = ReadDeviceProperty(deviceSet, ref info, 0x0C);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = ReadDeviceProperty(deviceSet, ref info, 0x00);
                }

                var location = ReadDeviceProperty(deviceSet, ref info, 0x0D);
                var match = PciLocation.Match(location ?? "");
                if (!match.Success || string.IsNullOrWhiteSpace(name) || map.ContainsKey(name))
                {
                    continue;
                }

                var bus = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var device = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var function = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                map[name] = $"{bus:x2}:{device:x2}.{function:x}";
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }

        return map;
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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

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

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
