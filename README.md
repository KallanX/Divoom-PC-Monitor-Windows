# Divoom PC Monitor

Windows app for the Divoom PC Monitor clock on a Times Gate or Pixoo64.

Open `DivoomPcMonitor.exe`. The window sets up the device and shows the readings. Closing the window leaves the monitor running in the tray. Quit on the tray icon stops it. Start at sign-in launches it in the tray. A second launch brings the existing window forward.

Administrator rights are not required.

## Readings

| Display field | Windows source |
| --- | --- |
| CPU usage | `GetSystemTimes`, measured for one second. Intel, AMD, and Snapdragon. |
| CPU temperature | AMD ADL2 CPU sensor (`atiadlxx.dll`) when available, or the warmest CPU ACPI thermal zone on Intel, AMD, and Snapdragon (including Qualcomm Oryon cluster TSENS zones `QCOM0C58` / `QCOM0C59` / `QCOM0D01`). |
| GPU usage | NVIDIA: `nvidia-smi`. AMD: AMD Display Library (`atiadlxx.dll`). Intel, including Arc: Intel Graphics Control Library (`ControlLib.dll`). Every vendor, including Qualcomm Adreno, also falls back to the Windows GPU Engine counters for the adapter's current DXGI LUID. |
| GPU temperature | NVIDIA: `nvidia-smi`. AMD: AMD Display Library. Intel, including Arc: Intel Graphics Control Library. Qualcomm Adreno: ACPI GPU TSENS thermal zone (`QCOM0C91`). Also falls back to WDDM `D3DKMTQueryAdapterInfo` (`KMTQAITYPE_ADAPTERPERFDATA`) and named GPU ACPI thermal zones. |
| Memory usage | `GlobalMemoryStatusEx` |
| Disk usage | Logical disk `% Idle Time` over that same one-second window. The busiest selected drive is shown. |

A failed sensor is sent as `--`. A real zero is sent as `0%` or `0°C`. The previous good reading is kept when a later sample fails. After each update the app waits the number of seconds you set. Each update spends about one second measuring CPU, GPU, and disk, and that wait starts after the update finishes.

The device protocol has six text fields and no drive-name field. The drive letter is shown in the window and the debug log.

## Requirements

- Windows 10 or later
- A Divoom Times Gate or Pixoo64 on the same LAN
- Internet access while Setup asks the Divoom cloud for devices and LCD faces

The published folder contains the .NET runtime and the Windows App SDK. PowerShell is not required. The app does not ship a sensor library.

### What to install

Install the normal graphics driver for the card. That driver already contains the library this app calls. There is no separate NVIDIA, AMD, Intel, or Qualcomm sensor package to download.

| Hardware | Usage and temperature | Install |
| --- | --- | --- |
| NVIDIA GPU | `nvidia-smi.exe` from the driver. Usage also falls back to the Windows GPU Engine counters, and temperature falls back to WDDM adapter performance data. | The NVIDIA graphics driver. It includes `nvidia-smi.exe`. The CUDA toolkit is not required. |
| AMD GPU | `atiadlxx.dll`, the AMD Display Library, from the driver. Usage also falls back to the Windows GPU Engine counters, and temperature falls back to WDDM adapter performance data. | AMD Software: Adrenalin Edition, or the AMD driver offered by Windows Update. Both include `atiadlxx.dll`. The ADL SDK is not required. |
| Intel GPU, including Arc B580 | `ControlLib.dll`, the Intel Graphics Control Library, from the driver. Usage also falls back to the Windows GPU Engine counters, and temperature falls back to WDDM adapter performance data. | The Intel graphics driver for Arc and Iris Xe graphics. It includes `ControlLib.dll`. The Intel Graphics Control Library SDK and oneAPI are not required. |
| Qualcomm Adreno | Usage from the Windows GPU Engine counters published by the Adreno driver. Temperature from the Qualcomm PEP GPU TSENS ACPI thermal zone (`ACPI\QCOM0C91`, `\_SB.TZ6`). | The Adreno and Qualcomm PEP drivers that come with the Snapdragon PC. Nothing else. |
| Intel, AMD, and Snapdragon CPUs | Usage from Windows (`GetSystemTimes`). Temperature from AMD ADL2 (`atiadlxx.dll`) or the warmest CPU ACPI thermal zone the firmware publishes. | Nothing beyond Windows. |

Pin the card in Setup when the PC has more than one GPU. Usage and temperature follow the pinned card, and ephemeral DXGI adapter LUIDs are automatically refreshed across reboots.

An Intel Arc card such as the B580 gets its temperature and usage from `ControlLib.dll` even when an Intel integrated GPU is also active. The app matches the configured Intel adapter by PCI bus address, DXGI LUID, or adapter name (and prefers discrete Arc over integrated graphics by default).

CPU temperature needs no extra install. On Snapdragon PCs, the app maps Qualcomm's Oryon CPU cluster thermal zones (`ACPI\QCOM0C58`, `QCOM0C59`, `QCOM0D01`) and Adreno GPU thermal zone (`ACPI\QCOM0C91`) via PnP ACPI hardware IDs so CPU and GPU temperatures are tracked independently. Intel and AMD machines use the Windows ACPI thermal zones (or AMD ADL2 CPU temperature when available). A zone at 0 or near -40°C is ignored. If the firmware publishes no usable zone, temperature stays `--`.

If `nvidia-smi.exe`, `atiadlxx.dll`, or `ControlLib.dll` is missing, GPU usage still comes from the Windows GPU Engine counters and GPU temperature falls back to WDDM `D3DKMTQueryAdapterInfo` or ACPI thermal zones. The other readings continue.

## Setup

Open the app and choose Setup. It lists the Divoom devices, the LCD face, the fixed drives, and the GPU, writes `config.env`, and selects the PC Monitor app (clock 625) on the device.

If the PC Monitor app is already on a face, that face is labeled in the list. The app can still be assigned to any face. A device with no LCD groups, such as a Pixoo64, uses independence 0, index 0, and clock 625.

`config.env` uses the same keys as the Linux monitor, plus `gpu_luid` so a Windows adapter can be pinned when it has no PCI address. An older file that uses `DeviceIP`, `LcdIndex`, and `SelectedDriveLetters` still loads.

The app looks for `config.env` in this order: the current directory, the folder that contains the exe, parent folders of those two, then `%LOCALAPPDATA%\DivoomPCMonitor\config.env`. Setup writes back to the file it loaded. A new file is saved beside the exe when that folder is writable, and otherwise under `%LOCALAPPDATA%\DivoomPCMonitor`.

The wait between updates is stored in `%LOCALAPPDATA%\DivoomPCMonitor\settings.json`. Changing it does not rewrite `config.env`. The default is 30 seconds and the minimum is 1.

## Run

Double-click `DivoomPcMonitor.exe` in the published folder. Keep `DivoomPcMonitor.exe` and the `runtime` folder together. When Setup creates `config.env`, it saves it right beside `DivoomPcMonitor.exe`.

- Start / Pause turns the monitor on and off.
- Start at sign-in writes a per-user startup entry. It does not need administrator and does not create a scheduled task.
- Write a debug log appends to `%LOCALAPPDATA%\DivoomPCMonitor\LogFile.log`.
- Closing the window hides it. The tray icon remains. Open shows the window again. Quit stops the monitor.

Sign-in starts the app with `--background`. A valid configuration starts monitoring in the tray. An unfinished configuration opens Setup.

`dotnet test` checks formatting, config, disk, GPU, and one live sample that does not send anything to the device.

If an older logon task named `DivoomPCMonitor` is still registered, remove it. This app does not use that task.

```powershell
Unregister-ScheduledTask -TaskName DivoomPCMonitor -Confirm:$false
```

## Version

The version in the window and in the exe file properties comes from the build. [Directory.Build.props](Directory.Build.props) sets the local default, currently `0.1.0`.

GitHub builds only when you push a version tag. A push to `main` does not build. Pushing tag `v0.1.0` builds that version for `win-x64` and `win-arm64` and attaches these files to the GitHub release for that tag:

- `DivoomPcMonitor-0.1.0-win-x64.zip`
- `DivoomPcMonitor-0.1.0-win-arm64.zip`

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The tag needs a `v` and three numbers, such as `v0.1.0`, and it has to point at a commit that contains this workflow. Push `main` first, then push the tag.

## Build

`publish.ps1` builds on this PC only. It does not publish a GitHub release. It runs the tests, then writes a self-contained folder for a 64-bit Intel/AMD PC and for a Windows ARM PC. Each folder contains `DivoomPcMonitor.exe` at the root and a `runtime` subfolder containing the .NET and Windows App SDK runtime files. The script also writes a zip of that folder.

```powershell
./publish.ps1
./publish.ps1 -Version 0.1.0
```

To build one kind of PC:

```powershell
./publish.ps1 -Runtime win-x64 -Version 0.1.0
./publish.ps1 -Runtime win-arm64 -Version 0.1.0
```

For version `0.1.0` the exes are `dist/DivoomPcMonitor-0.1.0-win-x64/DivoomPcMonitor.exe` and `dist/DivoomPcMonitor-0.1.0-win-arm64/DivoomPcMonitor.exe`. The .NET 8 SDK is required to build. The published app does not need it. When `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` exists, the script uses that SDK.

## Author

Keith Carichner Jr.

## License

MIT. See [LICENSE](LICENSE).
