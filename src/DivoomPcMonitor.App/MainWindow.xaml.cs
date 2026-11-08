using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DivoomPcMonitor;

public sealed partial class MainWindow : Window
{
    private bool _quit;
    private bool _navigating;

    public ICommand OpenCommand { get; }

    public MainWindow()
    {
        OpenCommand = new RelayCommand(BringToFront);
        InitializeComponent();
        Session.Current.Ui = DispatcherQueue;
        Session.Current.NavigateRequested += ShowPage;
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1080, 760));
        Activated += (_, _) => ApplyTitle();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyTitle);
        ApplyTitle();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
            Tray.Icon = new Icon(iconPath);
        }
        else if (!string.IsNullOrEmpty(Environment.ProcessPath))
        {
            Tray.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }

        AppWindow.Closing += (_, eventArgs) =>
        {
            if (_quit)
            {
                return;
            }

            eventArgs.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => Tray.Dispose();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void BringToFront()
    {
        AppWindow.Show();
        Activate();
        ApplyTitle();
    }

    public void ApplyTitle()
    {
        Title = Product.Name;
        AppWindow.Title = Product.Name;
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
        {
            SetWindowText(hwnd, Product.Name);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    public void ShowSetup()
    {
        ShowPage("setup");
    }

    public void Quit()
    {
        _quit = true;
        Session.Current.Stop();
        Tray.Dispose();
        Close();
    }

    private void Open_Click(object sender, RoutedEventArgs args)
    {
        BringToFront();
    }

    private void Quit_Click(object sender, RoutedEventArgs args)
    {
        Quit();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        if (_navigating)
        {
            return;
        }

        _navigating = true;
        try
        {
            foreach (var entry in Nav.MenuItems)
            {
                if (entry is NavigationViewItem item && item.Tag as string == tag)
                {
                    Nav.SelectedItem = item;
                }
            }

            var page = tag == "setup" ? typeof(SetupPage) : typeof(MonitorPage);
            if (RootFrame.CurrentSourcePageType != page)
            {
                RootFrame.Navigate(page);
            }
        }
        finally
        {
            _navigating = false;
        }
    }
}
