using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DivoomPcMonitor;

public sealed partial class LogPage : Page
{
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
        LogText.Text = lines.Count == 0 ? "No log lines yet." : string.Join(Environment.NewLine, lines);
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }
}
