using Microsoft.UI.Xaml;

namespace DivoomPcMonitor;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private MainWindow? _window;
    private Thread? _showThread;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            Session.Current.Log.Info("The app hit an error: " + eventArgs.Message);
            eventArgs.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\DivoomPcMonitor", out var created);
        _show = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, @"Local\DivoomPcMonitor.Show");
        if (!created)
        {
            try
            {
                _show.Set();
            }
            catch (Exception)
            {
            }

            _show.Dispose();
            _mutex.Dispose();
            Exit();
            return;
        }

        var background = Environment.GetCommandLineArgs().Any(static arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        var skipAutoStart = string.Equals(Environment.GetEnvironmentVariable("DIVOOM_NO_AUTOSTART"), "1", StringComparison.Ordinal);
        Session.Current.Load();
        _window = new MainWindow();
        var window = _window;
        _showThread = new Thread(() =>
        {
            var gate = _show;
            while (gate is not null && gate.WaitOne())
            {
                if (window.DispatcherQueue == null)
                {
                    continue;
                }

                window.DispatcherQueue.TryEnqueue(window.BringToFront);
            }
        })
        {
            IsBackground = true,
            Name = "DivoomShow",
        };
        _showThread.Start();

        var configReady = Session.Current.HasConfig;
        if (background && configReady)
        {
            window.Activate();
            window.ApplyTitle();
            window.AppWindow.Hide();
        }
        else
        {
            window.Activate();
            window.ApplyTitle();
            if (!configReady)
            {
                window.ShowSetup();
            }
        }

        if (!skipAutoStart && configReady)
        {
            Session.Current.Start();
        }
    }
}
