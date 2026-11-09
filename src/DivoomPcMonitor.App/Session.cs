using Microsoft.UI.Dispatching;

namespace DivoomPcMonitor;

public sealed class Session
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];
    private MonitorEngine? _engine;
    private CancellationTokenSource? _cancel;
    private DivoomDeviceClient? _device;
    private Task? _run;
    private bool _restartAfterStop;
    private int _generation;
    private int _publishPending;
    private string? _cycleError;

    public static Session Current { get; } = new();

    public MonitorLog Log { get; } = new();

    public AppSettings Settings { get; private set; } = new();

    public string? ConfigPath { get; set; }

    public DisplayReading Reading { get; private set; } = new();

    public string Status { get; private set; } = "Not monitoring.";

    public bool HasError { get; private set; }

    public bool Monitoring { get; private set; }

    public bool HasConfig => ConfigPath is not null && File.Exists(ConfigPath);

    public DispatcherQueue? Ui { get; set; }

    public event Action? Changed;

    public event Action<string>? NavigateRequested;

    public IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Load()
    {
        Settings = AppSettings.Load();
        Log.WriteToFile = Settings.DebugLog;
        Log.Line += Remember;
        ConfigPath = ConfigLocator.Find(executableDirectory: AppContext.BaseDirectory);
        if (!HasConfig)
        {
            Status = "Open Setup to choose the Divoom device.";
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false })
            {
                if (!Monitoring)
                {
                    _restartAfterStop = true;
                }

                return;
            }

            BeginStart();
        }

        Publish();
    }

    public void Stop()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            _restartAfterStop = false;
            Interlocked.Increment(ref _generation);
            Monitoring = false;
            _cycleError = null;
            HasError = false;
            Status = "Monitor stopped.";
            cancel = _cancel;
        }

        Cancel(cancel);
        Publish();
    }

    public void Restart()
    {
        CancellationTokenSource? cancel;
        var startNow = false;
        lock (_gate)
        {
            _restartAfterStop = true;
            Interlocked.Increment(ref _generation);
            Monitoring = false;
            _cycleError = null;
            HasError = false;
            cancel = _cancel;
            startNow = _run is null or { IsCompleted: true };
            if (startNow)
            {
                _restartAfterStop = false;
                if (!BeginStart())
                {
                    _restartAfterStop = false;
                }
            }
        }

        Cancel(cancel);
        Publish();
    }

    private bool BeginStart()
    {
        if (!HasConfig || ConfigPath is null)
        {
            Status = "Open Setup to choose the Divoom device.";
            NavigateRequested?.Invoke("setup");
            return false;
        }

        var generation = Interlocked.Increment(ref _generation);
        var log = Log;
        var sensors = new WindowsSensorSource(log);
        var device = new DivoomDeviceClient(log.Info);
        var engine = new MonitorEngine(sensors, device, log)
        {
            IntervalSeconds = Math.Max(1, Settings.IntervalSeconds),
            Verbose = Settings.DebugLog,
        };
        engine.Updated += reading =>
        {
            Reading = reading;
            Publish();
        };

        var cancel = new CancellationTokenSource();
        _engine = engine;
        _device = device;
        _cancel = cancel;
        _cycleError = null;
        HasError = false;
        Monitoring = true;
        Status = "Collecting the first sample.";

        var path = ConfigPath;
        var token = cancel.Token;
        Task run = null!;
        run = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(() => path, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Info("Monitor stopped: " + ex.Message);
            }
            finally
            {
                device.Dispose();
                cancel.Dispose();
                var restart = false;
                var publishStop = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_run, run))
                    {
                        _run = null;
                    }

                    if (generation == Volatile.Read(ref _generation))
                    {
                        Monitoring = false;
                        HasError = false;
                        Status = "Monitor stopped.";
                        publishStop = true;
                    }

                    restart = _restartAfterStop;
                    _restartAfterStop = false;
                }

                if (restart)
                {
                    Start();
                }
                else if (publishStop)
                {
                    Publish();
                }
            }
        });
        _run = run;
        return true;
    }

    private static void Cancel(CancellationTokenSource? cancel)
    {
        try
        {
            cancel?.Cancel();
        }
        catch (Exception)
        {
        }
    }

    public void SetInterval(int seconds)
    {
        if (seconds < 1)
        {
            seconds = 1;
        }

        Settings.IntervalSeconds = seconds;
        Settings.Save();
        if (_engine is not null)
        {
            _engine.IntervalSeconds = seconds;
        }
    }

    public void SetDebugLog(bool enabled)
    {
        Settings.DebugLog = enabled;
        Settings.Save();
        Log.WriteToFile = enabled;
        if (_engine is not null)
        {
            _engine.Verbose = enabled;
        }
    }

    public void RequestNavigate(string page)
    {
        NavigateRequested?.Invoke(page);
    }

    private void Remember(string message)
    {
        lock (_gate)
        {
            _lines.Add(message);
            if (_lines.Count > 200)
            {
                _lines.RemoveAt(0);
            }
        }

        if (message.StartsWith("Data collection started at ", StringComparison.Ordinal))
        {
            _cycleError = null;
        }
        else if (IsErrorLine(message))
        {
            _cycleError = message;
            HasError = true;
            Status = message;
        }
        else if (message.StartsWith("HTTP Post - OKAY!", StringComparison.Ordinal))
        {
            _cycleError = null;
            HasError = false;
            Status = message;
        }
        else if (message.StartsWith("Waiting ", StringComparison.Ordinal))
        {
            if (_cycleError is not null)
            {
                HasError = true;
                Status = $"{_cycleError} ({message})";
            }
            else
            {
                HasError = false;
                Status = message;
            }
        }

        Publish();
    }

    private static bool IsErrorLine(string message)
    {
        return message.Contains("ERROR!", StringComparison.Ordinal)
            || message.StartsWith("config.env", StringComparison.Ordinal)
            || message.StartsWith("Configuration file not found:", StringComparison.Ordinal)
            || message.StartsWith("Data collection failed:", StringComparison.Ordinal);
    }

    private void Publish()
    {
        var queue = Ui;
        if (queue is null)
        {
            Changed?.Invoke();
            return;
        }

        if (Interlocked.CompareExchange(ref _publishPending, 1, 0) != 0)
        {
            return;
        }

        if (!queue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _publishPending, 0);
            Changed?.Invoke();
        }))
        {
            Interlocked.Exchange(ref _publishPending, 0);
        }
    }
}
