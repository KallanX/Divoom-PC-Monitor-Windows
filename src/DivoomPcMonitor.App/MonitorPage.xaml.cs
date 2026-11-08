using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DivoomPcMonitor;

public sealed partial class MonitorPage : Page
{
    private bool _ready;

    public MonitorPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = false;
        IntervalBox.Value = Session.Current.Settings.IntervalSeconds;
        LoginSwitch.IsOn = LoginStartup.IsEnabled();
        DebugSwitch.IsOn = Session.Current.Settings.DebugLog;
        _ready = true;
        Session.Current.Changed += OnChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Session.Current.Changed -= OnChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnChanged()
    {
        Refresh();
    }

    private void Refresh()
    {
        var session = Session.Current;
        var reading = session.Reading;
        CpuValue.Text = reading.CpuUtilText;
        GpuValue.Text = reading.GpuUtilText;
        CpuTempValue.Text = reading.CpuTempText;
        GpuTempValue.Text = reading.GpuTempText;
        MemoryValue.Text = reading.MemoryText;
        DiskValue.Text = reading.DiskText;
        DiskName.Text = string.IsNullOrEmpty(reading.DiskName) ? "" : reading.DiskName;
        StatusBar.Message = session.Status;
        StatusBar.Severity = session.Status.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            ? InfoBarSeverity.Error
            : session.Monitoring ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        RunIcon.Symbol = session.Monitoring ? Symbol.Pause : Symbol.Play;
        RunLabel.Text = session.Monitoring ? "Pause" : "Start";

        if (session.HasConfig && session.ConfigPath is not null)
        {
            try
            {
                var config = ConfigStore.Read(session.ConfigPath);
                Subtitle.Text = "Sending to " + config.DeviceIp + " after each sample. The device shows CPU, GPU, CPU temperature, GPU temperature, memory, and disk.";
            }
            catch (ConfigException ex)
            {
                Subtitle.Text = ex.Message;
            }
        }
    }

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || double.IsNaN(args.NewValue))
        {
            return;
        }

        var seconds = (int)Math.Round(args.NewValue);
        if (seconds < 1)
        {
            seconds = 1;
        }

        Session.Current.SetInterval(seconds);
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Current.Monitoring)
        {
            Session.Current.Stop();
        }
        else
        {
            Session.Current.Start();
        }

        Refresh();
    }

    private void LoginSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (LoginSwitch.IsOn)
        {
            LoginStartup.Enable(path);
        }
        else
        {
            LoginStartup.Disable();
        }
    }

    private void DebugSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Session.Current.SetDebugLog(DebugSwitch.IsOn);
    }
}
