using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DivoomPcMonitor;

public sealed partial class SetupPage : Page
{
    private readonly DivoomDeviceClient _cloud = new();
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
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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

    private void BeginWizard()
    {
        _wizard = true;
        _stepIndex = 0;
        SummaryView.Visibility = Visibility.Collapsed;
        WizardView.Visibility = Visibility.Visible;
        RecreateButton.Visibility = Visibility.Collapsed;
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
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
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

    private static void SetValue(TextBlock block, string value)
    {
        block.Text = string.IsNullOrWhiteSpace(value) ? "Not set" : value;
    }

    private async Task LoadDevicesAsync()
    {
        SetBusy(true, "Looking for Divoom devices on this network…");
        try
        {
            _devices = await _cloud.ListDevicesAsync(CancellationToken.None);
            _drives = WindowsHardware.ListFixedDrives();
            _gpus = WindowsHardware.ListGpus();
            if (_devices.Count == 1)
            {
                _device = _devices[0];
            }

            if (_drives.Count == 1)
            {
                _driveIndex = 0;
            }

            if (_gpus.Count == 1)
            {
                _gpuIndex = 0;
            }

            Notice.IsOpen = false;
            RebuildSteps();
            ShowStep();
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
        StepTitle.Text = $"Step {_stepIndex + 1} of {_steps.Count}: {TitleFor(step)}";
        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = step == SetupStep.Review ? "Apply" : "Next";
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
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxWidth = 720 };
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
                    _groups = await _cloud.ListLcdGroupsAsync(_device.Id, CancellationToken.None);
                    Notice.IsOpen = false;
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
                    _group = _groups.Count == 1 ? _groups[0] : _group is not null && _groups.Contains(_group) ? _group : null;
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
        var cpu = WindowsHardware.ReadCpuVendor();
        if (cpu.Length == 0)
        {
            cpu = "Unknown";
        }

        var path = Session.Current.ConfigPath ?? ConfigLocator.DefaultSavePath(AppContext.BaseDirectory);
        SetBusy(true, "Saving the configuration and selecting the PC Monitor app…");
        try
        {
            ConfigStore.Write(path, _device.Id, _device.Ip, _lcdIndependence, _lcdIndex.ToString(), _lcdClockId, drives, cpu, gpu);
            Session.Current.ConfigPath = path;
            var selected = await _cloud.SelectClockAsync(_device.Id, _device.Ip, _lcdIndependence, _lcdIndex.ToString(), _lcdClockId, verbose: true, CancellationToken.None);
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
            if (!Session.Current.Monitoring)
            {
                Session.Current.Start();
            }

            Session.Current.RequestNavigate("monitor");
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
        if (!string.IsNullOrEmpty(title))
        {
            StepTitle.Text = title;
        }
    }
}
