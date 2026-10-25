# PeripheralBattery

[简体中文](README.md) | **English**

**v1.0.0 · Initial public release**

A Windows utility for monitoring the battery levels of selected 2.4 GHz wireless peripherals. It currently supports the **MCHOSE K7 Ultra mouse** and the **ROG Strix Scope RX TKL Wireless Deluxe keyboard**.

Each device has its own green battery ring in the Windows notification area. A compact panel shows exact percentages, reading times, and connection status. HID debugging tools are available for inspecting supported interfaces and validating manual protocol calibrations.

The application interface is currently in Chinese. This README provides usage instructions in English.

## Supported devices and platforms

| Device or platform | Validation status |
| --- | --- |
| MCHOSE K7 Ultra | 2.4 GHz receiver `3837:1001`, logical device ID `3837:4150`; battery reading checked against the official software |
| ROG Strix Scope RX TKL Wireless Deluxe | 2.4 GHz receiver `0B05:1A07`; HID battery reading verified, comparison with the official software pending |
| Windows 10 22H2 x64 | Tested on a physical machine |
| Windows 11 x64 | Compatibility target; physical-machine testing pending |

The initial release targets these two devices and their verified interfaces. Other models, receiver IDs, connection modes, and firmware versions are not guaranteed to work.

## Features

- **Two battery ring icons:** one for the mouse and one for the keyboard. The green arc represents the remaining charge, without numbers inside the ring or continuous animation. Icons support light and dark notification-area backgrounds.
- **Compact battery panel:** two cards display percentages, progress bars, data sources, and the time of each successful reading. Drag the header to move the panel.
- **Readings preserved across restarts:** the latest successful reading remains visible after a disconnect, timeout, or restart and is marked as a previous reading (`上次读数`). A new successful reading replaces it. Devices with no valid reading show `—`.
- **Configurable refresh behavior:** refresh once per day by default, use a custom interval, or refresh when mouse activity resumes and when the panel is opened.
- **Single instance and optional autostart:** run at Windows sign-in for the current user. Hiding the panel keeps the application in the notification area; use Exit application (`退出程序`) in Settings to stop it completely.
- **HID debugging tools:** enumerate interfaces, inspect Feature Reports, probe Report IDs, passively listen for Input Reports, send controlled test queries, and validate manual protocol calibrations.
- **No runtime log files:** normal operation stores settings and the latest two battery snapshots. Debug output stays in memory and the console.

### Icon preview

These are **16 px and 24 px design samples**. The percentages are example labels, not live device readings. The rings do not contain numbers.

![Sample green battery rings in the Windows notification area](docs/images/notification-ring-preview.png)

## Download and use

1. Open [Releases](https://github.com/cheasn/batteryviewer/releases) and download `PeripheralBattery-v1.0.0-win-x64.zip`.
2. Extract the entire folder to a permanent location and run `PeripheralBattery.exe`. The package includes the .NET runtime, so no separate runtime installation is needed. Keep the executable together with the other extracted files.
3. On first launch, choose a refresh mode and whether to start at Windows sign-in, then save. Autostart runs in the notification area; a normal manual launch opens the panel.
4. Find the mouse and keyboard icons beside the Windows clock. If they are in the `∧` overflow area, drag them into the visible notification area. Windows manages whether these icons are hidden.

GitHub's automatically generated **Source code** archives contain the source, not a ready-to-run application package.

| Action | Result |
| --- | --- |
| Left-click the mouse or keyboard tray icon | Refresh that device |
| Right-click either tray icon | Open or bring forward the single battery panel and refresh both devices |
| Hover over a tray icon | Show the exact percentage, original reading time, and previous-reading status without querying the device |
| Drag the panel header or its empty area | Move the panel |
| Click outside the panel or click its × button | Hide the panel and keep the application running |
| Open Settings (`设置`) and select Exit application (`退出程序`) | Remove both icons, stop queries, and end the application and its worker processes |

The tray rings stay green, with gray representing the remaining part of the circle. The panel's progress bars use a warning color for low battery. After a failed refresh, a previously recorded charging flag is not presented as a current charging state.

## Refresh modes and cached readings

| Mode | Behavior |
| --- | --- |
| Scheduled refresh | Once per day by default; configurable from 1 minute to 365 days. Does not wake the computer. After resume, missed refreshes are combined into at most one catch-up refresh. |
| Activity / panel refresh | No periodic queries. The first mouse activity after 5 minutes of inactivity refreshes the mouse. The keyboard is refreshed at startup and when the panel is opened. |

Both modes attempt to read both devices at startup. Saved refresh settings take effect immediately.

Resumed mouse activity is an approximation of device wake-up. Detection uses the input source and elapsed time; it does not record pointer movement or listen for keyboard keystrokes.

Local data is stored in `%LOCALAPPDATA%\PeripheralBattery\`:

- `settings.json`: refresh settings, autostart preference, device bindings, and confirmed calibrations.
- `battery-cache.json`: the latest successful battery reading, source, binding, and original timestamp for each device. Successful readings overwrite the corresponding snapshot. Failures do not overwrite it, readings do not automatically expire, and no history is accumulated.

Autostart uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PeripheralBattery` registry entry. If you move the application folder, save the autostart setting again to update its path.

## Debugging and protocol calibration

```powershell
.\PeripheralBattery.exe --debug
```

If the application is already running, this opens the debug window in the existing instance without creating another pair of icons.

The debug tools can:

- Enumerate connected HID interfaces and their access errors, read Feature Reports, and probe a selected range of Report IDs.
- Passively listen for Input Reports on vendor interfaces without sending query commands.
- Send manual test queries after unlocking the current debug session. These tools are intended for battery and status queries. Known MCHOSE configuration writes are blocked; firmware updates, pairing, and device resets are not provided.
- Define query templates, response matching, XOR decoding, field offsets and encodings, scaling, and charging flags. Offsets start at byte `0` of the raw response. `Target` is `0` for the mouse or `1` for the keyboard.

A calibration must match exactly one interface, pass its test, and be manually checked against the official software before it can be saved. Changing a binding prevents the application from reusing another binding's cached reading.

Routine queries pause during debug operations to prevent report interference. Raw reports are not automatically saved to disk.

Other application commands:

```powershell
.\PeripheralBattery.exe --settings  # Open Settings
.\PeripheralBattery.exe --exit      # Ask the running instance to exit normally
```

## Build from source

Requires **Windows** and the **.NET 10 SDK**.

Run these commands from the repository root:

```powershell
dotnet build -c Release
& .\bin\Release\net10.0-windows\PeripheralBattery.exe --self-test | Out-String
.\publish.ps1
```

The publishing script reads the version from `PeripheralBattery.csproj`, creates a self-contained Windows x64 package, and runs the automated regressions and Windows tray checks. For version `1.0.0`, it produces:

```text
dist/PeripheralBattery-v1.0.0-win-x64/
dist/PeripheralBattery-v1.0.0-win-x64.zip
```

Exit any application instance running from the destination folder before publishing again.

The application uses Windows APIs and WPF without third-party HID or UI NuGet packages. Published packages include the .NET runtime and its license files.

### Additional verification commands

```powershell
$exe = '.\bin\Release\net10.0-windows\PeripheralBattery.exe'
& $exe --tray-self-test | Out-String
& $exe --probe --summary | Out-String
& $exe --cache-inspect | Out-String
```

- `--tray-self-test`: temporarily registers two real tray icons and exercises panel opening, callback coalescing, and cleanup through native messages sent to the test process. Briefly shows a test panel without querying HID devices or changing settings or cached readings. Run without another instance from the same directory.
- `--probe --summary`: enumerates HID interfaces and queries supported devices for diagnostic output.
- `--cache-inspect`: reads existing cached battery values, timestamps, and previous-reading status without querying HID devices.
- `--test-session`: runs a test session without saving user settings or cached readings or registering autostart.

See [Validation and retest instructions](docs/VALIDATION.md) for detailed results and other developer commands. This supporting document is in Chinese.

## Known limitations

- Windows 11, other firmware versions, charging-state transitions, sustained 8K mouse input, high DPI, and multiple monitors still need further physical-machine testing.
- ROG battery readings have been retrieved through HID, but comparison with the official software and charging-state validation are still pending.
- Sleep/resume, receiver hot-plugging, Explorer restart recovery, and autostart after a fresh sign-in require further end-to-end checks.
- If another application holds the required interface, a device is asleep, or no valid status response is available, the application keeps the last successful reading and shows the failure reason. If there has never been a valid reading, it shows `—`.

## Reporting a bug

Open an [issue](https://github.com/cheasn/batteryviewer/issues) and include the application version, Windows version, device model and connection mode, steps to reproduce, and expected and actual behavior. Add any visible error message or relevant debug output. Normal operation does not create a log file to attach.

## Project documentation

The following documents are currently in Chinese:

- [Initial release notes](docs/RELEASE-v1.0.0.md)
- [Changelog, including unreleased changes](CHANGELOG.md)
- [Validation results and retest instructions](docs/VALIDATION.md)
- [HID protocol details](docs/PROTOCOL.md)
- [References and third-party licenses](docs/THIRD-PARTY.md)
