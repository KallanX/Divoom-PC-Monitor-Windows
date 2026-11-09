using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DivoomPcMonitor;

public sealed partial class SetupPage : Page
{
    private DivoomDeviceClient _cloud;
    private CancellationTokenSource _alive;
    private IReadOnlyList<LanDevice> _devices = [];
    private IReadOnlyList<LcdGroupInfo> _groups = [];
    private IReadOnlyList<FixedDrive> _drives = [];
    private IReadOnlyList<GpuCard> _gpus = [];
    private IReadOnlyList<SetupStep> _steps = [SetupStep.Device];
    private LanDevice? _device;
    private LcdGroupInfo? _group;
    private int _lcdIndex;
    private string _lcdClockId = "625";
    private string _lcdIndependence = "0";
    private bool _pixoo;
    private bool _allDrives;
    private int _driveIndex;
    private int _gpuIndex;
    private int _stepIndex;
    private bool _wizard;

    public SetupPage()
    {
        InitializeComponent();
        _cloud = new DivoomDeviceClient(Session.Current.Log.Info);
        _alive = new CancellationTokenSource();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _alive.Cancel();
        _cloud.Dispose();
        base.OnNavigatedFrom(e);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_alive.IsCancellationRequested)
        {
            _alive.Dispose();
            _alive = new CancellationTokenSource();
            _cloud = new DivoomDeviceClient(Session.Current.Log.Info);
        }

        if (_wizard)
        {
            ShowStep();
            return;
        }

        if (!ShowSummary())
        {
            BeginWizard();
        }
    }

    private void Recreate_Click(object sender, RoutedEventArgs e)
    {
        BeginWizard();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _wizard = false;
        Notice.IsOpen = false;
        ShowSummary();
    }

    private void BeginWizard()
    {
        _wizard = true;
        _stepIndex = 0;
        _device = null;
        _group = null;
        _lcdIndex = 0;
        _lcdClockId = "625";
        _lcdIndependence = "0";
        _pixoo = false;
        _allDrives = false;
        _driveIndex = 0;
        _gpuIndex = 0;
        SummaryView.Visibility = Visibility.Collapsed;
        WizardView.Visibility = Visibility.Visible;
        RecreateButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Session.Current.HasConfig ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        _ = LoadDevicesAsync();
    }

    private bool ShowSummary()
    {
        var path = Session.Current.ConfigPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        SummaryView.Visibility = Visibility.Visible;
        WizardView.Visibility = Visibility.Collapsed;
        RecreateButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        StepIcon.Glyph = "\uE713";
        StepTitle.Text = "Current configuration";
        FileText.Text = path;
        try
        {
            var config = ConfigStore.Read(path);
            SetValue(DeviceIdText, config.DeviceId);
            SetValue(DeviceIpText, config.DeviceIp);
            SetValue(LcdIndependenceText, config.LcdIndependence);
            SetValue(LcdIndexText, config.LcdIndex);
            SetValue(LcdClockText, config.LcdClockId);
            SetValue(DrivesText, config.HardDrive.Count == 0 ? "" : string.Join(", ", config.HardDrive));
            SetValue(CpuText, config.CpuVendor);
            SetValue(GpuVendorText, config.GpuVendor);
            SetValue(GpuNameText, config.GpuName);
            SetValue(GpuPciText, config.GpuPci);
            SetValue(GpuLuidText, config.GpuLuid);
            Notice.IsOpen = false;
        }
        catch (Exception ex)
        {
            SetValue(DeviceIdText, "");
            SetValue(DeviceIpText, "");
            SetValue(LcdIndependenceText, "");
            SetValue(LcdIndexText, "");
            SetValue(LcdClockText, "");
            SetValue(DrivesText, "");
            SetValue(CpuText, "");
            SetValue(GpuVendorText, "");
            SetValue(GpuNameText, "");
            SetValue(GpuPciText, "");
            SetValue(GpuLuidText, "");
            ShowError(ex.Message);
        }

        return true;
    }

    private static bool IsPageGone(Exception ex)
    {
        return ex is OperationCanceledException or ObjectDisposedException;
    }

    private static void SetValue(TextBlock block, string value)
    {
        block.Text = string.IsNullOrWhiteSpace(value) ? "Not set" : value;
    }

    private async Task LoadDevicesAsync()
    {
        SetBusy(true, "Looking for Divoom devices on this network…");
        var cloud = _cloud;
        var token = _alive.Token;
        try
        {
            var devicesTask = cloud.ListDevicesAsync(token);
            var hardwareTask = Task.Run(static () => (Drives: WindowsHardware.ListFixedDrives(), Gpus: WindowsHardware.ListGpus()));
            _devices = await devicesTask;
            var hardware = await hardwareTask;
            _drives = hardware.Drives;
            _gpus = hardware.Gpus;
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (_devices.Count > 0)
            {
                _device = _devices[0];
            }

            if (_drives.Count > 0)
            {
                _driveIndex = 0;
            }

            if (_gpus.Count > 0)
            {
                var preferred = HardwareMath.SelectDefault(_gpus);
                var preferredIdx = preferred is null ? -1 : _gpus.ToList().IndexOf(preferred);
                _gpuIndex = preferredIdx >= 0 ? preferredIdx : 0;
            }

            Notice.IsOpen = false;
            RebuildSteps();
            ShowStep();
        }
        catch (Exception ex) when (IsPageGone(ex))
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            StepTitle.Text = "Devices";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void RebuildSteps()
    {
        var groupCount = _pixoo ? 0 : _groups.Count;
        _steps = HardwareMath.PlanSteps(groupCount, _pixoo, _gpus.Count);
        if (_stepIndex >= _steps.Count)
        {
            _stepIndex = _steps.Count - 1;
        }
    }

    private void ShowStep()
    {
        Body.Children.Clear();
        if (_steps.Count == 0)
        {
            return;
        }

        var step = _steps[_stepIndex];
        StepIcon.Glyph = step switch
        {
            SetupStep.Device => "\uE7F4",
            SetupStep.LcdGroup => "\uE7F8",
            SetupStep.Lcd => "\uE823",
            SetupStep.Drive => "\uEDA2",
            SetupStep.Gpu => "\uE950",
            _ => "\uE73E",
        };
        StepTitle.Text = $"Step {_stepIndex + 1} of {_steps.Count}: {TitleFor(step)}";
        BackButton.IsEnabled = _stepIndex > 0;
        NextButtonText.Text = step == SetupStep.Review ? "Apply" : "Next";
        NextButtonIcon.Glyph = step == SetupStep.Review ? "\uE73E" : "\uE72A";
        switch (step)
        {
            case SetupStep.Device:
                AddChoices(_devices.Select(device => $"{device.Name}\n{device.Ip}    {device.Id}").ToArray(), _devices.ToList().IndexOf(_device!), index =>
                {
                    _device = _devices[index];
                });
                if (_devices.Count == 0)
                {
                    Body.Children.Add(new TextBlock { Text = "No devices found on the network.", TextWrapping = TextWrapping.Wrap });
                }

                break;
            case SetupStep.LcdGroup:
                AddChoices(_groups.Select(group => "Independence " + group.Independence).ToArray(), _groups.ToList().FindIndex(group => ReferenceEquals(group, _group)), index => _group = _groups[index]);
                break;
            case SetupStep.Lcd:
                var faces = _group?.Faces ?? [];
                AddChoices(faces.Select(face => face.IsPcMonitor ? $"Clock {face.ClockId}    Divoom PC Monitor" : $"Clock {face.ClockId}").ToArray(), _lcdIndex, index => _lcdIndex = index);
                break;
            case SetupStep.Drive:
                var labels = new List<string> { "All fixed drives" };
                labels.AddRange(_drives.Select(drive => string.Join("    ", new[] { drive.DeviceId, drive.VolumeName, drive.SizeLabel }.Where(static part => part.Length > 0))));
                var selected = _allDrives ? 0 : _driveIndex + 1;
                AddChoices(labels.ToArray(), selected, index =>
                {
                    _allDrives = index == 0;
                    if (!_allDrives)
                    {
                        _driveIndex = index - 1;
                    }
                });
                break;
            case SetupStep.Gpu:
                AddChoices(_gpus.Select(gpu => $"{gpu.Vendor}    {gpu.Name}\n{(gpu.Pci.Length > 0 ? gpu.Pci : gpu.Luid)}").ToArray(), _gpuIndex, index => _gpuIndex = index);
                break;
            case SetupStep.Review:
                Body.Children.Add(new TextBlock { Text = ReviewText(), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] });
                break;
        }
    }

    private void AddChoices(string[] labels, int selected, Action<int> choose)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var label in labels)
        {
            list.Items.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        }

        if (selected >= 0 && selected < list.Items.Count)
        {
            list.SelectedIndex = selected;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                choose(list.SelectedIndex);
            }
        };
        Body.Children.Add(list);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        var step = _steps[_stepIndex];
        if (step == SetupStep.Review)
        {
            await ApplyAsync();
            return;
        }

        if (!await ValidateAsync(step))
        {
            return;
        }

        _stepIndex++;
        ShowStep();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 0)
        {
            return;
        }

        _stepIndex--;
        ShowStep();
    }

    private async Task<bool> ValidateAsync(SetupStep step)
    {
        var cloud = _cloud;
        var token = _alive.Token;
        switch (step)
        {
            case SetupStep.Device:
                if (_device is null)
                {
                    ShowError("Select a device.");
                    return false;
                }

                SetBusy(true, "Reading the LCD layout…");
                try
                {
                    _groups = await cloud.ListLcdGroupsAsync(_device.Id, token);
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    Notice.IsOpen = false;
                }
                catch (Exception ex) when (IsPageGone(ex))
                {
                    return false;
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                    return false;
                }
                finally
                {
                    SetBusy(false, null);
                }

                if (_groups.Count == 0)
                {
                    _pixoo = true;
                    _lcdIndependence = "0";
                    _lcdIndex = 0;
                    _lcdClockId = "625";
                    Notice.Message = "This device has no LCD groups. It will be set up as a Pixoo: independence 0, index 0, clock 625.";
                    Notice.Severity = InfoBarSeverity.Informational;
                    Notice.IsOpen = true;
                }
                else
                {
                    _pixoo = false;
                    if (_groups.Count == 1)
                    {
                        if (_groups[0].Faces.Count == 0)
                        {
                            ShowError("LcdList is empty.");
                            return false;
                        }

                        _group = _groups[0];
                    }
                    else
                    {
                        _group = _group is not null
                            ? _groups.FirstOrDefault(g => g.Independence == _group.Independence) ?? _groups[0]
                            : _groups[0];
                    }
                }

                RebuildSteps();
                return true;
            case SetupStep.LcdGroup:
                if (_group is null)
                {
                    ShowError("Select an LCD group.");
                    return false;
                }

                if (_group.Faces.Count == 0)
                {
                    ShowError("LcdList is empty.");
                    return false;
                }

                return true;
            case SetupStep.Lcd:
                var faces = _group?.Faces ?? [];
                if (_lcdIndex < 0 || _lcdIndex >= faces.Count)
                {
                    ShowError("Select an LCD.");
                    return false;
                }

                _lcdIndependence = _group?.Independence ?? "0";
                _lcdClockId = faces[_lcdIndex].ClockId;
                return true;
            case SetupStep.Drive:
                if (_drives.Count == 0)
                {
                    ShowError("No fixed drives were found.");
                    return false;
                }

                if (!_allDrives && (_driveIndex < 0 || _driveIndex >= _drives.Count))
                {
                    ShowError("Select a drive, or choose all fixed drives.");
                    return false;
                }

                return true;
            case SetupStep.Gpu:
                if (_gpus.Count > 1 && (_gpuIndex < 0 || _gpuIndex >= _gpus.Count))
                {
                    ShowError("Select a GPU.");
                    return false;
                }

                return true;
            default:
                return true;
        }
    }

    private async Task ApplyAsync()
    {
        if (_device is null)
        {
            ShowError("Select a device.");
            return;
        }

        var drives = _allDrives ? _drives.Select(drive => drive.DeviceId).ToArray() : [_drives[_driveIndex].DeviceId];
        var gpu = _gpus.Count == 0 ? new GpuCard() : _gpus[Math.Clamp(_gpuIndex, 0, _gpus.Count - 1)];
        var path = Session.Current.ConfigPath ?? ConfigLocator.DefaultSavePath(AppContext.BaseDirectory);
        var cloud = _cloud;
        var token = _alive.Token;
        SetBusy(true, "Saving the configuration and selecting the PC Monitor app…");
        try
        {
            var cpu = await Task.Run(WindowsHardware.ReadCpuVendor);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (cpu.Length == 0)
            {
                cpu = "Unknown";
            }

            ConfigStore.Write(path, _device.Id, _device.Ip, _lcdIndependence, _lcdIndex.ToString(), "625", drives, cpu, gpu);
            Session.Current.ConfigPath = path;
            var selected = await cloud.SelectClockAsync(_device.Id, _device.Ip, _lcdIndependence, _lcdIndex.ToString(), _lcdClockId, verbose: true, token);
            if (!selected)
            {
                Notice.Severity = InfoBarSeverity.Warning;
                Notice.Message = "Configuration was saved, but the device did not switch to the PC Monitor app.";
                Notice.IsOpen = true;
                return;
            }

            Notice.Severity = InfoBarSeverity.Success;
            Notice.Message = "Configuration saved. The device is showing the PC Monitor app.";
            Notice.IsOpen = true;
            _wizard = false;
            Session.Current.Restart();
            Session.Current.RequestNavigate("monitor");
        }
        catch (Exception ex) when (IsPageGone(ex))
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private string ReviewText()
    {
        var drives = _allDrives
            ? string.Join(", ", _drives.Select(drive => drive.DeviceId))
            : _drives.Count == 0 ? "none" : _drives[Math.Clamp(_driveIndex, 0, _drives.Count - 1)].DeviceId;
        var gpu = _gpus.Count == 0 ? new GpuCard() : _gpus[Math.Clamp(_gpuIndex, 0, _gpus.Count - 1)];
        return string.Join(Environment.NewLine,
        [
            "Device: " + (_device?.Name ?? "none") + " (" + (_device?.Ip ?? "none") + ")",
            "LCD independence: " + _lcdIndependence,
            "LCD index: " + _lcdIndex,
            "Previous clock id: " + _lcdClockId + " (the device will be set to clock 625)",
            "Drives: " + drives,
            "GPU: " + (gpu.Name.Length > 0 ? gpu.Label : "none"),
            "",
            "Apply writes config.env and selects the Divoom PC Monitor app.",
        ]);
    }

    private static string TitleFor(SetupStep step)
    {
        return step switch
        {
            SetupStep.Device => "Device",
            SetupStep.LcdGroup => "LCD group",
            SetupStep.Lcd => "LCD",
            SetupStep.Drive => "Drive",
            SetupStep.Gpu => "GPU",
            _ => "Review",
        };
    }

    private void ShowError(string message)
    {
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    private void SetBusy(bool busy, string? title)
    {
        Busy.IsActive = busy;
        NextButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy && _stepIndex > 0;
        CancelButton.IsEnabled = !busy;
        if (!string.IsNullOrEmpty(title))
        {
            StepTitle.Text = title;
        }
    }
}
