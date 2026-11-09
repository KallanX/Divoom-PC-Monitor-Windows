using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace DivoomPcMonitor;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private MainWindow? _window;
    private Thread? _showThread;

    [ModuleInitializer]
    internal static void InitializeRuntimeDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return;
        }

        var processDir = Path.GetDirectoryName(processPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(processDir) || string.Equals(baseDir, processDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
        SetDllDirectoryW(baseDir);
        var manifestPath = Path.Combine(baseDir, "app.manifest");
        if (!File.Exists(manifestPath))
        {
            TryExtractManifest(manifestPath);
        }

        if (File.Exists(manifestPath))
        {
            var actCtx = new ActCtxW
            {
                Size = Marshal.SizeOf<ActCtxW>(),
                Flags = 0,
                Source = manifestPath,
            };
            var handle = CreateActCtxW(ref actCtx);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                ActivateActCtx(handle, out _);
            }
        }
    }

    private static void TryExtractManifest(string destinationPath)
    {
        try
        {
            var module = GetModuleHandleW(null);
            if (module == IntPtr.Zero)
            {
                return;
            }

            var resInfo = FindResourceW(module, (IntPtr)1, (IntPtr)24);
            if (resInfo == IntPtr.Zero)
            {
                return;
            }

            var size = SizeofResource(module, resInfo);
            var resData = LoadResource(module, resInfo);
            var ptr = LockResource(resData);
            if (size == 0 || ptr == IntPtr.Zero)
            {
                return;
            }

            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, (int)size);
            File.WriteAllBytes(destinationPath, bytes);
        }
        catch (Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActCtxW
    {
        public int Size;
        public uint Flags;
        public string Source;
        public ushort ProcessorArchitecture;
        public ushort LangId;
        public string? AssemblyDirectory;
        public IntPtr ResourceName;
        public string? ApplicationName;
        public IntPtr Module;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string pathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateActCtxW(ref ActCtxW actCtx);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ActivateActCtx(IntPtr actCtx, out IntPtr cookie);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            var detail = eventArgs.Exception is null ? eventArgs.Message : eventArgs.Exception.ToString();
            Session.Current.Log.Info("The app hit an error: " + detail);
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
