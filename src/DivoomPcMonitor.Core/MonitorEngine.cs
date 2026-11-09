namespace DivoomPcMonitor;

public interface ISensorSource : IDisposable
{
    Task<RawSample> MeasureAsync(GpuCard gpu, IReadOnlyList<string> drives, CancellationToken cancellationToken);
}

public sealed class MonitorEngine : IAsyncDisposable
{
    private readonly ISensorSource _sensors;
    private readonly IDevicePoster _device;
    private readonly MonitorLog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ReadingHold _hold = new();
    private readonly HardwareCache _hardware = new();
    private readonly object _waitGate = new();
    private CancellationTokenSource? _waitCancel;
    private int _intervalSeconds = 30;

    public MonitorEngine(
        ISensorSource sensors,
        IDevicePoster device,
        MonitorLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sensors = sensors;
        _device = device;
        _log = log ?? new MonitorLog();
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    public MonitorLog Log => _log;

    public bool Testing { get; set; }

    public bool Verbose { get; set; }

    public int IntervalSeconds
    {
        get => Volatile.Read(ref _intervalSeconds);
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Interval must be at least 1 second.");
            }

            var previous = Interlocked.Exchange(ref _intervalSeconds, value);
            if (previous != value)
            {
                lock (_waitGate)
                {
                    try
                    {
                        _waitCancel?.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }

    public event Action<DisplayReading>? Updated;

    public async Task RunAsync(Func<string> configPath, CancellationToken cancellationToken)
    {
        if (IntervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IntervalSeconds), "Interval must be at least 1 second.");
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var config = ConfigStore.Read(configPath());
                    _log.Info("Data collection started at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    var selection = _hardware.Resolve(config, _log);
                    var raw = await _sensors.MeasureAsync(selection.Gpu, config.HardDrive, cancellationToken).ConfigureAwait(false);
                    if (raw.GpuUtil is null)
                    {
                        _hardware.Invalidate();
                    }
                    if (raw.CpuTemp is null)
                    {
                        _log.WriteOnce("cpu-temp", "CPU temperature unavailable.");
                        _log.Clear("cpu-temp-source");
                    }
                    else if (!string.IsNullOrEmpty(raw.CpuTempSource))
                    {
                        _log.Clear("cpu-temp");
                        _log.WriteOnce("cpu-temp-source", "CPU temperature from " + raw.CpuTempSource + ".");
                    }

                    var shown = _hold.Apply(raw);

                    if (Verbose)
                    {
                        _log.Info("CPU vendor: " + selection.CpuVendor);
                        _log.Info("GPU: " + selection.Gpu.Label);
                        _log.Info($"CPU Usage: {shown.CpuUtilText}, CPU Temperature: {shown.CpuTempText}");
                        _log.Info($"GPU Usage: {shown.GpuUtilText}, GPU Temperature: {shown.GpuTempText}");
                        _log.Info("Memory Usage: " + shown.MemoryText);
                        _log.Info($"Disk with highest utilization: {shown.DiskName}, Utilization: {shown.DiskText}");
                    }

                    _log.Info($"CPU {shown.CpuUtilText} {shown.CpuTempText}; GPU {shown.GpuUtilText} {shown.GpuTempText}; RAM {shown.MemoryText}; Disk {shown.DiskName} {shown.DiskText}");
                    Updated?.Invoke(shown);
                    if (!Testing)
                    {
                        await _device.SendSystemAsync(config, shown, Verbose, cancellationToken).ConfigureAwait(false);
                    }

                    _log.Info("Data collection completed at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ConfigException ex)
                {
                    _log.Info(ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Info("Data collection failed: " + ex.Message);
                }

                var interval = IntervalSeconds;
                _log.Info($"Waiting {interval} seconds before the next update.");
                using var waitCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_waitGate)
                {
                    _waitCancel = waitCancel;
                }

                try
                {
                    await _delay(TimeSpan.FromSeconds(interval), waitCancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // IntervalSeconds changed while waiting; start the next update with the new interval.
                }
                finally
                {
                    lock (_waitGate)
                    {
                        if (ReferenceEquals(_waitCancel, waitCancel))
                        {
                            _waitCancel = null;
                        }
                    }
                }
            }
        }
        finally
        {
            _sensors.Dispose();
            _log.Info("Monitor stopped.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _sensors.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class HardwareCache
    {
        private string? _key;
        private HardwareSelection? _value;

        public void Invalidate()
        {
            _key = null;
            _value = null;
        }

        public HardwareSelection Resolve(MonitorConfig config, MonitorLog log)
        {
            if (_value is not null && _key == config.HardwareKey)
            {
                return _value;
            }

            var gpus = WindowsHardware.ListGpus();
            _key = config.HardwareKey;
            _value = HardwareMath.Resolve(
                config,
                gpus,
                WindowsHardware.ReadCpuVendor,
                message => log.WriteOnce("hardware", message));
            return _value;
        }
    }
}
