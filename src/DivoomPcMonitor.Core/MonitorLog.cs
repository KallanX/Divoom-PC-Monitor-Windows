namespace DivoomPcMonitor;

public sealed class MonitorLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private string? _file;

    public event Action<string>? Line;

    public bool WriteToFile
    {
        get => _file is not null;
        set
        {
            if (!value)
            {
                _file = null;
                return;
            }

            var directory = ConfigLocator.LocalFolder();
            Directory.CreateDirectory(directory);
            _file = Path.Combine(directory, "LogFile.log");
        }
    }

    public void Info(string message)
    {
        Line?.Invoke(message);
        Append(message);
    }

    public void WriteOnce(string key, string message)
    {
        lock (_gate)
        {
            if (_seen.TryGetValue(key, out var previous) && previous == message)
            {
                return;
            }

            _seen[key] = message;
        }

        Info(message);
    }

    public void Clear(string key)
    {
        lock (_gate)
        {
            _seen.Remove(key);
        }
    }

    public void OnceToken(string token)
    {
        var split = token.IndexOf('|');
        if (split < 1)
        {
            Info(token);
            return;
        }

        WriteOnce(token[..split], token[(split + 1)..]);
    }

    private void Append(string message)
    {
        var path = _file;
        if (path is null)
        {
            return;
        }

        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message;
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // A log file the user cannot write must not stop the monitor.
        }
    }
}
