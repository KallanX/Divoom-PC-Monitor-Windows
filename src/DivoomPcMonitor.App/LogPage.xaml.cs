using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace DivoomPcMonitor;

public sealed partial class LogPage : Page
{
    private DispatcherQueueTimer? _copiedReset;

    public LogPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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
        var lines = Session.Current.RecentLines;
        LineCountText.Text = lines.Count == 1
            ? "1 line"
            : lines.Count.ToString(CultureInfo.InvariantCulture) + " lines";
        CopyButton.IsEnabled = lines.Count > 0;
        var follow = LogScroller.ScrollableHeight <= 1
            || LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 48;
        LogText.Text = lines.Count == 0 ? "No log lines yet." : string.Join(Environment.NewLine, lines);
        if (follow)
        {
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var lines = Session.Current.RecentLines;
        if (lines.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        Clipboard.SetContent(package);
        CopyIcon.Glyph = "\uE73E";
        CopyLabel.Text = "Copied";
        _copiedReset ??= CreateCopyReset();
        _copiedReset.Start();
    }

    private DispatcherQueueTimer CreateCopyReset()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            CopyIcon.Glyph = "\uE8C8";
            CopyLabel.Text = "Copy";
        };
        return timer;
    }
}
