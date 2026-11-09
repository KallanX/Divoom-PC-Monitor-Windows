using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DivoomPcMonitor;

public sealed class WindowsSensorSource : ISensorSource
{
    private readonly MonitorLog _log;
    private readonly PdhSession _pdh = new();
    private readonly AmdLibrary _amd = new();
    private readonly IntelLibrary _intel = new();
    private bool _disposed;

    public WindowsSensorSource(MonitorLog? log = null)
    {
        _log = log ?? new MonitorLog();
    }

    public async Task<RawSample> MeasureAsync(GpuCard gpu, IReadOnlyList<string> drives, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hasDrives = drives.Count > 0;
        Dictionary<string, DiskSample>? diskBefore = null;
        if (hasDrives)
        {
            try
            {
                diskBefore = WindowsHardware.DiskSnapshot();
            }
            catch (Exception ex)
            {
                _log.WriteOnce("disk-sample", "Disk counters are unavailable: " + ex.Message);
            }
        }

        long[]? cpuBefore = null;
        try
        {
            cpuBefore = WindowsHardware.SystemTimes();
        }
        catch (Exception ex)
        {
            _log.WriteOnce("cpu-util", "CPU usage unavailable: " + ex.Message);
        }

        _pdh.EnsureOpen(_log);
        _pdh.Collect();
        IReadOnlyList<(IntPtr Engine, ulong[] Stats)>? intelBaseline = null;
        var vendorName = gpu.Vendor.Trim();
        if (string.Equals(vendorName, "Intel", StringComparison.OrdinalIgnoreCase))
        {
            intelBaseline = _intel.Begin(gpu, _log);
        }

        Task<NvidiaRow>? nvidiaTask = null;
        if (string.Equals(vendorName, "NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            nvidiaTask = QueryNvidiaAsync(gpu, cancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

        _pdh.Collect();
        var engines = _pdh.ReadEngines();
        var zones = _pdh.ReadZones();
        var vendor = ReadVendor(gpu, intelBaseline);
        long[]? cpuAfter = null;
        try
        {
            cpuAfter = WindowsHardware.SystemTimes();
        }
        catch (Exception ex)
        {
            _log.WriteOnce("cpu-util", "CPU usage unavailable: " + ex.Message);
        }

        var cpuUtil = HardwareMath.CpuUsage(cpuBefore, cpuAfter);
        if (cpuUtil is null)
        {
            _log.WriteOnce("cpu-util", "CPU usage unavailable.");
        }
        else
        {
            _log.Clear("cpu-util");
        }

        var cpuTemp = vendor.CpuTemp;
        string? cpuSource = cpuTemp is null ? null : "the AMD display library";
        if (cpuTemp is null && !string.Equals(vendorName, "AMD", StringComparison.OrdinalIgnoreCase))
        {
            cpuTemp = _amd.ReadCpuTemp();
            if (cpuTemp is not null)
            {
                cpuSource = "the AMD display library";
            }
        }

        if (cpuTemp is null)
        {
            var cpuZone = HardwareMath.SelectThermal(zones, "Cpu");
            if (cpuZone is not null)
            {
                cpuTemp = cpuZone.Celsius;
                cpuSource = "ACPI thermal zone " + cpuZone.Name;
            }
        }

        Dictionary<string, DiskSample>? diskAfter = null;
        if (hasDrives && diskBefore is not null)
        {
            try
            {
                diskAfter = WindowsHardware.DiskSnapshot();
                _log.Clear("disk-sample");
            }
            catch (Exception ex)
            {
                _log.WriteOnce("disk-sample", "Disk counters are unavailable: " + ex.Message);
            }
        }

        var disk = hasDrives ? HardwareMath.BusierLogicalDisk(diskBefore, diskAfter, drives) : new DiskRank();
        int? memory = null;
        try
        {
            memory = WindowsHardware.MemoryLoad();
        }
        catch (Exception ex)
        {
            _log.WriteOnce("mem", "Memory usage unavailable: " + ex.Message);
        }

        if (memory is null)
        {
            _log.WriteOnce("mem", "Memory usage unavailable.");
        }
        else
        {
            _log.Clear("mem");
        }

        NvidiaRow? nvidia = null;
        if (nvidiaTask is not null)
        {
            nvidia = await nvidiaTask.ConfigureAwait(false);
        }

        var vendorTemp = vendor.Temp;
        if (vendorTemp is null && nvidia?.Temp is null)
        {
            vendorTemp = WindowsHardware.ReadD3dkmtGpuTemp(gpu.Luid);
        }

        if (vendorTemp is null && nvidia?.Temp is null)
        {
            var gpuZone = HardwareMath.SelectThermal(zones, "Gpu");
            if (gpuZone is not null)
            {
                vendorTemp = gpuZone.Celsius;
                _log.WriteOnce("gpu-acpi", "GPU temperature from ACPI thermal zone " + gpuZone.Name + ".");
            }
            else
            {
                _log.Clear("gpu-acpi");
            }
        }
        else
        {
            _log.Clear("gpu-acpi");
        }

        if (vendorName.Length > 0)
        {
            _log.Clear("gpu-missing");
        }

        var combined = HardwareMath.CombineGpu(
            gpu,
            engines,
            new VendorReading { Util = vendor.Util, Temp = vendorTemp, CpuTemp = vendor.CpuTemp },
            nvidia,
            _log.OnceToken);
        if (combined.Temp is not null)
        {
            _log.Clear("qcom-temp");
        }

        return new RawSample
        {
            CpuUtil = cpuUtil,
            CpuTemp = cpuTemp,
            GpuUtil = combined.Util,
            GpuTemp = combined.Temp,
            MemoryUtil = memory,
            DiskUtil = disk.Util,
            DiskName = disk.Name,
            CpuTempSource = cpuSource,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pdh.Dispose();
        _amd.Dispose();
        _intel.Dispose();
    }

    private VendorReading ReadVendor(GpuCard gpu, IReadOnlyList<(IntPtr Engine, ulong[] Stats)>? intelBaseline)
    {
        var vendor = gpu.Vendor.Trim();
        try
        {
            return vendor switch
            {
                "AMD" => _amd.Read(gpu, _log),
                "Intel" => _intel.Read(gpu, intelBaseline, _log),
                _ => new VendorReading(),
            };
        }
        catch (Exception ex)
        {
            _log.WriteOnce("vendor-gpu", "GPU vendor library failed: " + ex.Message);
            return new VendorReading();
        }
    }

    private async Task<NvidiaRow> QueryNvidiaAsync(GpuCard gpu, CancellationToken cancellationToken)
    {
        var executable = WindowsHardware.FindNvidiaSmi();
        if (executable is null)
        {
            _log.WriteOnce("missing-nvidia-smi", "nvidia-smi was not found.");
            return new NvidiaRow { Status = "missing" };
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--query-gpu=pci.bus_id,utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillProcessSafe(process);
                _log.WriteOnce("timeout-nvidia-smi", "nvidia-smi timed out after 10s.");
                return new NvidiaRow { Status = "missing" };
            }
            catch (OperationCanceledException)
            {
                KillProcessSafe(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : "exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture);
                _log.WriteOnce("fail-nvidia-smi", "nvidia-smi failed: " + detail);
                return new NvidiaRow { Status = "missing" };
            }

            _log.Clear("missing-nvidia-smi");
            _log.Clear("fail-nvidia-smi");
            _log.Clear("timeout-nvidia-smi");
            var parsed = HardwareMath.ParseNvidiaCsv(stdout, gpu.Pci);
            if (parsed.Status == "missing" && HardwareMath.ShortenPci(gpu.Pci).Length > 0)
            {
                _log.WriteOnce("nvidia-pci", "nvidia-smi has no GPU at " + HardwareMath.ShortenPci(gpu.Pci) + ".");
            }
            else if (parsed.Status == "ambiguous")
            {
                _log.WriteOnce("nvidia-multi", "More than one NVIDIA GPU is present. Open Setup to pin one. Showing " + parsed.Pci + " for now.");
            }
            else
            {
                _log.Clear("nvidia-pci");
                _log.Clear("nvidia-multi");
            }

            return parsed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.WriteOnce("fail-nvidia-smi", "nvidia-smi failed: " + ex.Message);
            return new NvidiaRow { Status = "missing" };
        }
    }

    private static void KillProcessSafe(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
            }
        }
    }
}

internal sealed class RetryGate
{
    private long _next;

    public bool TryBegin(int cooldownMs = 30_000)
    {
        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _next))
        {
            return false;
        }

        Volatile.Write(ref _next, now + cooldownMs);
        return true;
    }
}

internal sealed class PdhSession : IDisposable
{
    private const uint FmtDouble = 0x00000200;
    private readonly RetryGate _retry = new();
    private IntPtr _query;
    private IntPtr _gpuAll;
    private IntPtr _gpu3d;
    private IntPtr _gpuCompute;
    private IntPtr _thermal;
    private IntPtr _thermalFallback;
    private bool _ready;

    public void EnsureOpen(MonitorLog log)
    {
        if (_ready || !_retry.TryBegin())
        {
            return;
        }
        var status = PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _query);
        if (status != 0 || _query == IntPtr.Zero)
        {
            log.WriteOnce("pdh-open", "Windows performance counters could not be opened (status " + status.ToString(CultureInfo.InvariantCulture) + ").");
            Dispose();
            return;
        }

        _gpuAll = Add(@"\GPU Engine(*)\Utilization Percentage");
        if (_gpuAll == IntPtr.Zero)
        {
            _gpu3d = Add(@"\GPU Engine(*engtype_3d)\Utilization Percentage");
            _gpuCompute = Add(@"\GPU Engine(*engtype_compute)\Utilization Percentage");
        }

        _thermal = Add(@"\Thermal Zone Information(*)\High Precision Temperature");
        _thermalFallback = Add(@"\Thermal Zone Information(*)\Temperature");
        if (_gpuAll == IntPtr.Zero && _gpu3d == IntPtr.Zero && _gpuCompute == IntPtr.Zero && _thermal == IntPtr.Zero && _thermalFallback == IntPtr.Zero)
        {
            log.WriteOnce("pdh-open", "Windows performance counters have no GPU engine or thermal zone data.");
            Dispose();
            return;
        }

        _ready = true;
        log.Clear("pdh-open");
    }

    public void Collect()
    {
        if (_ready && _query != IntPtr.Zero)
        {
            PdhCollectQueryData(_query);
        }
    }

    public List<EngineSample> ReadEngines()
    {
        var items = new List<EngineSample>();
        if (!_ready)
        {
            return items;
        }

        if (_gpuAll != IntPtr.Zero)
        {
            items.AddRange(Read(_gpuAll));
            if (items.Count > 0)
            {
                return items;
            }
        }

        foreach (var sample in Read(_gpu3d))
        {
            items.Add(sample);
        }

        foreach (var sample in Read(_gpuCompute))
        {
            items.Add(sample);
        }

        return items;
    }

    public List<ThermalZone> ReadZones()
    {
        var zones = new List<ThermalZone>();
        if (!_ready)
        {
            return zones;
        }

        var hardwareIds = WindowsHardware.ReadThermalZoneHardwareIds();
        var precise = ReadUsable(_thermal, hardwareIds);
        if (precise.Count > 0)
        {
            return precise;
        }

        return ReadUsable(_thermalFallback, hardwareIds);
    }

    private static List<ThermalZone> ReadUsable(IntPtr counter, IReadOnlyDictionary<string, string> hardwareIds)
    {
        var zones = new List<ThermalZone>();
        foreach (var sample in Read(counter))
        {
            if (HardwareMath.ThermalCelsius(sample.CookedValue) is null)
            {
                continue;
            }

            zones.Add(new ThermalZone
            {
                Name = sample.InstanceName,
                DeciKelvin = sample.CookedValue,
                HardwareId = hardwareIds.TryGetValue(sample.InstanceName, out var hwid) ? hwid : "",
            });
        }

        return zones;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        _gpuAll = IntPtr.Zero;
        _gpu3d = IntPtr.Zero;
        _gpuCompute = IntPtr.Zero;
        _thermal = IntPtr.Zero;
        _thermalFallback = IntPtr.Zero;
        _ready = false;
    }

    private IntPtr Add(string path)
    {
        var status = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter);
        return status == 0 ? counter : IntPtr.Zero;
    }

    private static List<EngineSample> Read(IntPtr counter)
    {
        var items = new List<EngineSample>();
        if (counter == IntPtr.Zero)
        {
            return items;
        }

        const int pdhMoreData = unchecked((int)0x800007D2);
        const int stride = 24;
        uint size = 0;
        uint count = 0;
        PdhGetFormattedCounterArray(counter, FmtDouble, ref size, out count, IntPtr.Zero);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < stride || size > 8_000_000)
            {
                return items;
            }

            var allocated = size + (uint)(stride * 16);
            var buffer = Marshal.AllocHGlobal((int)allocated);
            try
            {
                size = allocated;
                var status = PdhGetFormattedCounterArray(counter, FmtDouble, ref size, out count, buffer);
                if (status == pdhMoreData)
                {
                    continue;
                }

                if (status != 0 || count == 0)
                {
                    return items;
                }

                var available = (int)Math.Min(count, allocated / stride);
                for (var i = 0; i < available; i++)
                {
                    var item = IntPtr.Add(buffer, i * stride);
                    var cstatus = Marshal.ReadInt32(item, 8);
                    if (cstatus is not (0 or 1))
                    {
                        continue;
                    }

                    var namePtr = Marshal.ReadIntPtr(item, 0);
                    var name = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr) ?? "";
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    items.Add(new EngineSample { InstanceName = name, CookedValue = value });
                }

                return items;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return items;
    }

    // A null string marshals as "", and PdhOpenQueryW then returns PDH_INVALID_ARGUMENT.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern int PdhOpenQuery(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static extern int PdhCloseQuery(IntPtr query);
}

internal sealed class AmdLibrary : IDisposable
{
    private const int Activity = 19;
    private const int Edge = 8;
    private const int Gfx = 28;
    private const int Hotspot = 27;
    private const int Cpu = 32;

    private static readonly AdlAlloc Alloc = static size => size <= 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);

    private readonly RetryGate _retry = new();
    private IntPtr _module;
    private IntPtr _context;
    private AdlDestroy? _destroy;
    private AdlCount? _count;
    private AdlInfo? _info;
    private AdlPmLog? _pmLog;
    private bool _ready;

    public VendorReading Read(GpuCard gpu, MonitorLog log)
    {
        EnsureOpen(log);
        if (!_ready || _count is null || _info is null || _pmLog is null)
        {
            return new VendorReading();
        }

        var adapters = ReadAdapters();
        var adapter = Select(adapters, gpu, log);
        if (adapter is null)
        {
            return new VendorReading();
        }

        var packed = ReadPmLog(adapter.Value.Index);
        if (packed is null)
        {
            log.WriteOnce("amd-pmlog", "AMD display library did not return performance data.");
            return new VendorReading();
        }

        log.Clear("amd-pmlog");
        int? util = null;
        var activity = Sensor(packed, Activity);
        if (activity is not null)
        {
            var percent = (double)activity.Value;
            if (percent < 0)
            {
                percent = 0;
            }

            if (percent > 100)
            {
                percent = 100;
            }

            util = (int)Math.Round(percent);
        }

        int? temp = null;
        foreach (var index in new[] { Edge, Gfx, Hotspot })
        {
            temp = HardwareMath.VendorCelsius(Sensor(packed, index));
            if (temp is not null)
            {
                break;
            }
        }

        return new VendorReading
        {
            Util = util,
            Temp = temp,
            CpuTemp = HardwareMath.VendorCelsius(Sensor(packed, Cpu)),
        };
    }

    public int? ReadCpuTemp()
    {
        try
        {
            EnsureOpen(null);
            if (!_ready || _count is null || _info is null || _pmLog is null)
            {
                return null;
            }

            foreach (var adapter in ReadAdapters())
            {
                var packed = ReadPmLog(adapter.Index);
                if (packed is null)
                {
                    continue;
                }

                var temp = HardwareMath.VendorCelsius(Sensor(packed, Cpu));
                if (temp is not null)
                {
                    return temp;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_destroy is not null && _context != IntPtr.Zero)
        {
            try
            {
                _destroy(_context);
            }
            catch (Exception)
            {
            }
        }

        _context = IntPtr.Zero;
        _ready = false;
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }

    private void EnsureOpen(MonitorLog? log)
    {
        if (_ready || !_retry.TryBegin())
        {
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var path = WindowsHardware.FindVendorFile(
            [
                Path.Combine(windows, "System32", "atiadlxx.dll"),
                Path.Combine(programFiles, "AMD", "CNext", "CNext", "atiadlxx.dll"),
            ],
            "atiadlxx.dll",
            null);
        if (path is null)
        {
            log?.WriteOnce("amd-missing", "AMD display library (atiadlxx.dll) was not found. GPU utilization uses the Windows GPU Engine counters.");
            return;
        }

        _module = LoadLibrary(path);
        if (_module == IntPtr.Zero)
        {
            log?.WriteOnce("amd-missing", "AMD display library could not be loaded from " + path + ".");
            return;
        }

        var create = Bind<AdlCreate>(_module, "ADL2_Main_Control_Create");
        _destroy = Bind<AdlDestroy>(_module, "ADL2_Main_Control_Destroy");
        _count = Bind<AdlCount>(_module, "ADL2_Adapter_NumberOfAdapters_Get");
        _info = Bind<AdlInfo>(_module, "ADL2_Adapter_AdapterInfo_Get");
        _pmLog = Bind<AdlPmLog>(_module, "ADL2_New_QueryPMLogData_Get");
        if (create is null || _destroy is null || _count is null || _info is null || _pmLog is null)
        {
            log?.WriteOnce("amd-missing", "AMD display library does not export the performance-log functions.");
            Dispose();
            return;
        }

        var status = create(Alloc, 1, out _context);
        if (status != 0 || _context == IntPtr.Zero)
        {
            log?.WriteOnce("amd-missing", "AMD display library failed to open (status " + status.ToString(CultureInfo.InvariantCulture) + ").");
            Dispose();
            return;
        }

        _ready = true;
        log?.Clear("amd-missing");
    }

    private List<AmdAdapter> ReadAdapters()
    {
        var list = new List<AmdAdapter>();
        if (_count is null || _info is null || _count(_context, out var count) != 0 || count is <= 0 or > 64)
        {
            return list;
        }

        const int stride = 4096;
        var buffer = Marshal.AllocHGlobal(stride * count);
        try
        {
            for (var i = 0; i < stride * count; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            if (_info(_context, buffer, stride * count) != 0)
            {
                return list;
            }

            var seenPci = new HashSet<(int Bus, int Device, int Function)>();
            var reported = Marshal.ReadInt32(buffer, 0);
            var step = reported is >= 280 and <= stride ? reported : stride;
            for (var i = 0; i < count; i++)
            {
                var item = IntPtr.Add(buffer, i * step);
                var bus = Marshal.ReadInt32(item, 264);
                var device = Marshal.ReadInt32(item, 268);
                var function = Marshal.ReadInt32(item, 272);
                if (bus is < 0 or > 255 || device is < 0 or > 31 || function is < 0 or > 7)
                {
                    continue;
                }

                if (seenPci.Add((bus, device, function)))
                {
                    list.Add(new AmdAdapter(Marshal.ReadInt32(item, 4), bus, device, function));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return list;
    }

    private static AmdAdapter? Select(IReadOnlyList<AmdAdapter> adapters, GpuCard gpu, MonitorLog log)
    {
        if (adapters.Count == 0)
        {
            return null;
        }

        var wanted = HardwareMath.ShortenPci(gpu.Pci);
        if (wanted.Length > 0)
        {
            foreach (var adapter in adapters)
            {
                if (adapter.Bus < 0)
                {
                    continue;
                }

                var pci = $"{adapter.Bus:x2}:{adapter.Device:x2}.{adapter.Function:x}";
                if (pci == wanted)
                {
                    log.Clear("amd-pci");
                    return adapter;
                }
            }

            log.WriteOnce("amd-pci", "AMD display library has no GPU at " + wanted + ".");
            return null;
        }

        if (adapters.Count > 1)
        {
            log.WriteOnce("amd-multi", "More than one AMD GPU is present. Open Setup to pin one. Showing adapter " + adapters[0].Index.ToString(CultureInfo.InvariantCulture) + " for now.");
        }
        else
        {
            log.Clear("amd-multi");
        }

        log.Clear("amd-pci");
        return adapters[0];
    }

    private int[]? ReadPmLog(int adapter)
    {
        if (_pmLog is null)
        {
            return null;
        }

        const int sensors = 256;
        var bytes = 4 + sensors * 8;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            for (var i = 0; i < bytes; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, bytes);
            if (_pmLog(_context, adapter, buffer) != 0)
            {
                return null;
            }

            var packed = new int[sensors * 2];
            for (var i = 0; i < sensors; i++)
            {
                var offset = 4 + i * 8;
                packed[i * 2] = Marshal.ReadInt32(buffer, offset);
                packed[i * 2 + 1] = Marshal.ReadInt32(buffer, offset + 4);
            }

            return packed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? Sensor(int[] packed, int index)
    {
        if (packed[index * 2] == 0)
        {
            return null;
        }

        return packed[index * 2 + 1];
    }

    private readonly record struct AmdAdapter(int Index, int Bus, int Device, int Function);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlAlloc(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlCreate(AdlAlloc callback, int connected, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlDestroy(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlCount(IntPtr context, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlInfo(IntPtr context, IntPtr buffer, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdlPmLog(IntPtr context, int adapter, IntPtr output);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32")]
    private static extern bool FreeLibrary(IntPtr module);

    private static T? Bind<T>(IntPtr module, string name) where T : Delegate
    {
        var address = GetProcAddress(module, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}

internal sealed class IntelLibrary : IDisposable
{
    private readonly RetryGate _retry = new();
    private IntPtr _module;
    private IntPtr _api;
    private List<IntelDevice> _devices = [];
    private CtlClose? _close;
    private CtlDeviceProps? _deviceProps;
    private CtlPciProps? _pciProps;
    private CtlEnum? _enumTemps;
    private CtlTempProps? _tempProps;
    private CtlTempState? _tempState;
    private CtlEnum? _enumEngines;
    private CtlEngineProps? _engineProps;
    private CtlEngineActivity? _activity;
    private bool _ready;

    public IReadOnlyList<(IntPtr Engine, ulong[] Stats)>? Begin(GpuCard gpu, MonitorLog log)
    {
        EnsureOpen(log);
        if (!_ready || _activity is null)
        {
            return null;
        }

        var selected = SelectDevice(gpu, log);
        if (selected is null)
        {
            return null;
        }

        var list = new List<(IntPtr Engine, ulong[] Stats)>();
        foreach (var engine in selected.Engines)
        {
            var stats = ReadEngine(_activity, engine);
            if (stats is not null)
            {
                list.Add((engine, stats));
            }
        }

        return list.Count > 0 ? list : null;
    }

    public VendorReading Read(GpuCard gpu, IReadOnlyList<(IntPtr Engine, ulong[] Stats)>? baseline, MonitorLog log)
    {
        EnsureOpen(log);
        if (!_ready)
        {
            return new VendorReading();
        }

        var selected = SelectDevice(gpu, log);
        if (selected is null)
        {
            return new VendorReading();
        }

        int? temp = null;
        if (_tempState is not null)
        {
            foreach (var sensor in selected.Sensors)
            {
                if (_tempState(sensor, out var celsius) == 0)
                {
                    temp = HardwareMath.VendorCelsius(celsius);
                    if (temp is not null)
                    {
                        break;
                    }
                }
            }
        }

        int? util = null;
        if (_activity is not null && baseline is not null)
        {
            foreach (var (engine, first) in baseline)
            {
                var current = ReadEngine(_activity, engine);
                if (current is not null)
                {
                    util = HardwareMath.IntelUtil(first, current);
                    if (util is not null)
                    {
                        break;
                    }
                }
            }
        }

        return new VendorReading { Util = util, Temp = temp };
    }

    public void Dispose()
    {
        if (_close is not null && _api != IntPtr.Zero)
        {
            try
            {
                _close(_api);
            }
            catch (Exception)
            {
            }
        }

        _api = IntPtr.Zero;
        _devices = [];
        _ready = false;
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }

    private void EnsureOpen(MonitorLog log)
    {
        if (_ready || !_retry.TryBegin())
        {
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = WindowsHardware.FindVendorFile(
            [Path.Combine(windows, "System32", "ControlLib.dll")],
            "ControlLib.dll",
            Path.Combine(windows, "System32", "DriverStore", "FileRepository"));
        if (path is null)
        {
            log.WriteOnce("intel-missing", "Intel Graphics Control Library (ControlLib.dll) was not found. GPU utilization uses the Windows GPU Engine counters.");
            return;
        }

        _module = LoadLibrary(path);
        if (_module == IntPtr.Zero)
        {
            log.WriteOnce("intel-missing", "Intel Graphics Control Library could not be loaded from " + path + ".");
            return;
        }

        var init = Bind<CtlInit>(_module, "ctlInit");
        _close = Bind<CtlClose>(_module, "ctlClose");
        var enumDevices = Bind<CtlEnum>(_module, "ctlEnumerateDevices");
        _deviceProps = Bind<CtlDeviceProps>(_module, "ctlGetDeviceProperties");
        _pciProps = Bind<CtlPciProps>(_module, "ctlPciGetProperties");
        _enumTemps = Bind<CtlEnum>(_module, "ctlEnumTemperatureSensors");
        _tempProps = Bind<CtlTempProps>(_module, "ctlTemperatureGetProperties");
        _tempState = Bind<CtlTempState>(_module, "ctlTemperatureGetState");
        _enumEngines = Bind<CtlEnum>(_module, "ctlEnumEngineGroups");
        _engineProps = Bind<CtlEngineProps>(_module, "ctlEngineGetProperties");
        _activity = Bind<CtlEngineActivity>(_module, "ctlEngineGetActivity");
        if (init is null || enumDevices is null)
        {
            log.WriteOnce("intel-missing", "Intel Graphics Control Library does not export ctlInit.");
            Dispose();
            return;
        }

        var args = new CtlInitArgs
        {
            Size = 36,
            Version = 0,
            AppVersion = 0x00010001,
            Flags = 1,
        };
        var status = init(ref args, out _api);
        if (status != 0 || _api == IntPtr.Zero)
        {
            log.WriteOnce("intel-missing", "Intel Graphics Control Library failed to open (status " + status.ToString(CultureInfo.InvariantCulture) + ").");
            Dispose();
            return;
        }

        var handles = Enumerate(enumDevices, _api);
        if (handles.Count == 0)
        {
            log.WriteOnce("intel-missing", "Intel Graphics Control Library did not list a GPU.");
            Dispose();
            return;
        }

        var list = new List<IntelDevice>();
        foreach (var handle in handles)
        {
            list.Add(BuildDevice(handle));
        }

        _devices = list;
        _ready = true;
        log.Clear("intel-missing");
    }

    private IntelDevice BuildDevice(IntPtr handle)
    {
        var (name, luid, isIntegrated) = ReadAdapterIdentity(handle);
        var pci = ReadAdapterPci(handle);

        var orderedSensors = new List<IntPtr>();
        if (_enumTemps is not null && _tempProps is not null && _tempState is not null)
        {
            var sensors = Enumerate(_enumTemps, handle);
            var gpuSensors = new List<IntPtr>();
            var globalSensors = new List<IntPtr>();
            var otherSensors = new List<IntPtr>();
            foreach (var sensor in sensors)
            {
                var type = ReadTempType(sensor);
                if (type == 1)
                {
                    gpuSensors.Add(sensor);
                }
                else if (type == 0)
                {
                    globalSensors.Add(sensor);
                }
                else if (type is not (2 or 5))
                {
                    otherSensors.Add(sensor);
                }
            }

            orderedSensors.AddRange(gpuSensors);
            orderedSensors.AddRange(globalSensors);
            orderedSensors.AddRange(otherSensors);
        }

        var orderedEngines = new List<IntPtr>();
        if (_enumEngines is not null && _engineProps is not null && _activity is not null)
        {
            var engines = Enumerate(_enumEngines, handle);
            var globalEngines = new List<IntPtr>();
            var renderEngines = new List<IntPtr>();
            foreach (var engine in engines)
            {
                var type = ReadEngineType(engine);
                if (type == 0)
                {
                    globalEngines.Add(engine);
                }
                else if (type == 1)
                {
                    renderEngines.Add(engine);
                }
            }

            orderedEngines.AddRange(globalEngines);
            orderedEngines.AddRange(renderEngines);
        }

        return new IntelDevice(handle, name, pci, luid, isIntegrated, orderedSensors, orderedEngines);
    }

    private IntelDevice? SelectDevice(GpuCard gpu, MonitorLog log)
    {
        if (_devices.Count == 0)
        {
            return null;
        }

        if (_devices.Count == 1)
        {
            log.Clear("intel-multi");
            return _devices[0];
        }

        var wantedPci = HardwareMath.ShortenPci(gpu.Pci);
        if (wantedPci.Length > 0)
        {
            var byPci = _devices.FirstOrDefault(d => d.Pci.Length > 0 && string.Equals(d.Pci, wantedPci, StringComparison.OrdinalIgnoreCase));
            if (byPci is not null)
            {
                log.Clear("intel-multi");
                return byPci;
            }
        }

        var wantedLuid = gpu.Luid.Trim();
        if (wantedLuid.Length > 0)
        {
            var byLuid = _devices.FirstOrDefault(d => d.Luid.Length > 0 && string.Equals(d.Luid, wantedLuid, StringComparison.OrdinalIgnoreCase));
            if (byLuid is not null)
            {
                log.Clear("intel-multi");
                return byLuid;
            }
        }

        var wantedName = gpu.Name.Trim();
        if (wantedName.Length > 0)
        {
            var byName = _devices.FirstOrDefault(d => d.Name.Length > 0 && string.Equals(d.Name, wantedName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                log.Clear("intel-multi");
                return byName;
            }
        }

        var discrete = _devices.FirstOrDefault(d => !d.IsIntegrated);
        if (discrete is not null)
        {
            log.Clear("intel-multi");
            return discrete;
        }

        return _devices[0];
    }

    private (string Name, string Luid, bool IsIntegrated) ReadAdapterIdentity(IntPtr handle)
    {
        if (_deviceProps is null)
        {
            return ("", "", false);
        }

        const int propSize = 192;
        var propBuffer = Marshal.AllocHGlobal(propSize);
        var luidBuffer = Marshal.AllocHGlobal(8);
        try
        {
            for (var i = 0; i < propSize; i++)
            {
                Marshal.WriteByte(propBuffer, i, 0);
            }

            Marshal.WriteInt64(luidBuffer, 0, 0);
            Marshal.WriteInt32(propBuffer, 0, propSize);
            Marshal.WriteByte(propBuffer, 4, 0);
            Marshal.WriteIntPtr(propBuffer, 8, luidBuffer);
            Marshal.WriteInt32(propBuffer, 16, 8);

            if (_deviceProps(handle, propBuffer) != 0)
            {
                return ("", "", false);
            }

            var luidLow = unchecked((uint)Marshal.ReadInt32(luidBuffer, 0));
            var luidHigh = unchecked((uint)Marshal.ReadInt32(luidBuffer, 4));
            var luid = luidLow == 0 && luidHigh == 0
                ? ""
                : $"luid_0x{luidHigh:x8}_0x{luidLow:x8}";

            var name = Marshal.PtrToStringAnsi(IntPtr.Add(propBuffer, 88), 100) ?? "";
            var end = name.IndexOf('\0');
            if (end >= 0)
            {
                name = name[..end];
            }

            var flags = unchecked((uint)Marshal.ReadInt32(propBuffer, 188));
            var isIntegrated = (flags & 1) != 0;
            return (name.Trim(), luid, isIntegrated);
        }
        catch (Exception)
        {
            return ("", "", false);
        }
        finally
        {
            Marshal.FreeHGlobal(luidBuffer);
            Marshal.FreeHGlobal(propBuffer);
        }
    }

    private string ReadAdapterPci(IntPtr handle)
    {
        if (_pciProps is null)
        {
            return "";
        }

        const int size = 64;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, size);
            Marshal.WriteByte(buffer, 4, 0);
            Marshal.WriteInt32(buffer, 8, 24);
            Marshal.WriteByte(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 32, 24);
            Marshal.WriteByte(buffer, 36, 0);

            if (_pciProps(handle, buffer) != 0)
            {
                return "";
            }

            var bus = Marshal.ReadInt32(buffer, 20);
            var device = Marshal.ReadInt32(buffer, 24);
            var function = Marshal.ReadInt32(buffer, 28);
            if (bus is < 0 or > 255 || device is < 0 or > 31 || function is < 0 or > 7)
            {
                return "";
            }

            return $"{bus:x2}:{device:x2}.{function:x}";
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int ReadTempType(IntPtr sensor)
    {
        if (_tempProps is null)
        {
            return -1;
        }

        var props = new CtlTempProperties { Size = 24, Version = 0 };
        return _tempProps(sensor, ref props) == 0 ? props.Type : -1;
    }

    private int ReadEngineType(IntPtr engine)
    {
        if (_engineProps is null)
        {
            return -1;
        }

        var props = new CtlEngineProperties { Size = 12, Version = 0 };
        return _engineProps(engine, ref props) == 0 ? props.Type : -1;
    }

    private static ulong[]? ReadEngine(CtlEngineActivity read, IntPtr engine)
    {
        var stats = new CtlEngineStats { Size = 24, Version = 0 };
        if (read(engine, ref stats) != 0)
        {
            return null;
        }

        return [stats.ActiveTime, stats.Timestamp];
    }

    private static List<IntPtr> Enumerate(CtlEnum callback, IntPtr parent)
    {
        var handles = new List<IntPtr>();
        if (parent == IntPtr.Zero)
        {
            return handles;
        }

        uint count = 0;
        if (callback(parent, ref count, IntPtr.Zero) != 0 || count is 0 or > 64)
        {
            return handles;
        }

        var buffer = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
        try
        {
            if (callback(parent, ref count, buffer) != 0)
            {
                return handles;
            }

            for (var i = 0; i < (int)count; i++)
            {
                handles.Add(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return handles;
    }

    [StructLayout(LayoutKind.Explicit, Size = 36)]
    private struct CtlInitArgs
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public uint AppVersion;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint SupportedVersion;
        [FieldOffset(20)] public uint Data1;
        [FieldOffset(24)] public ushort Data2;
        [FieldOffset(26)] public ushort Data3;
        [FieldOffset(28)] public ulong Data4;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CtlTempProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int Type;
        [FieldOffset(16)] public double MaxTemperature;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct CtlEngineProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public int Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CtlEngineStats
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(8)] public ulong ActiveTime;
        [FieldOffset(16)] public ulong Timestamp;
    }

    private sealed record IntelDevice(
        IntPtr Handle,
        string Name,
        string Pci,
        string Luid,
        bool IsIntegrated,
        IReadOnlyList<IntPtr> Sensors,
        IReadOnlyList<IntPtr> Engines);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlInit(ref CtlInitArgs args, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlClose(IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlDeviceProps(IntPtr device, IntPtr props);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlPciProps(IntPtr device, IntPtr props);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlEnum(IntPtr parent, ref uint count, IntPtr items);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlTempProps(IntPtr sensor, ref CtlTempProperties props);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlTempState(IntPtr sensor, out double celsius);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlEngineProps(IntPtr engine, ref CtlEngineProperties props);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CtlEngineActivity(IntPtr engine, ref CtlEngineStats stats);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32")]
    private static extern bool FreeLibrary(IntPtr module);

    private static T? Bind<T>(IntPtr module, string name) where T : Delegate
    {
        var address = GetProcAddress(module, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}
