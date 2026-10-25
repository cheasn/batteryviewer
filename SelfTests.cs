using System.Diagnostics;

namespace PeripheralBattery;

internal static class SelfTests
{
    public static async Task<int> Run()
    {
        DebugConsole.Enable(); int count = 0;
        var testedPids = new List<int>();
        bool Exited(int pid) { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } }
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); count++; }
        void Reject(Action action, string label) { try { action(); } catch { Check(true, label); return; } throw new Exception(label); }
        try
        {
            var mouseIcon = TrayIdentity.ForExecutable(@"C:\Tools\Battery\PeripheralBattery.exe", DeviceKind.Mouse);
            Check(mouseIcon == TrayIdentity.ForExecutable(@"c:/tools/battery/PeripheralBattery.exe", DeviceKind.Mouse), "tray identity is stable across path case and separators");
            Check(mouseIcon != TrayIdentity.ForExecutable(@"C:\Tools\Battery\PeripheralBattery.exe", DeviceKind.Keyboard), "mouse and keyboard have different tray identities");
            Check(mouseIcon != TrayIdentity.ForExecutable(@"D:\Portable\Battery\PeripheralBattery.exe", DeviceKind.Mouse), "moving portable EXE changes path-bound tray identity");
            Check(mouseIcon != new Guid("9fa1798d-826c-4c3d-a967-6bd826e0689a"), "do not reuse the legacy globally bound tray GUID");
            int GreenPixels(int? percent)
            {
                var icon = DeviceRingIcons.Render(DeviceKind.Mouse, percent, false, 32); var pixels = new byte[32 * 32 * 4]; icon.CopyPixels(pixels, 128, 0);
                int green = 0;
                for (int i = 0; i < pixels.Length; i += 4) if (pixels[i + 1] > 140 && pixels[i + 1] > pixels[i + 2] * 2 && pixels[i + 1] > pixels[i] * 1.5) green++;
                return green;
            }
            Check(GreenPixels(100) > GreenPixels(75) && GreenPixels(75) > GreenPixels(20), "green ring length tracks the actual percentage");
            Check(GreenPixels(20) > 0 && GreenPixels(0) == 0 && GreenPixels(null) == 0, "low battery stays green while zero and unknown have no green progress");
            Check(DeviceRingIcons.Render(DeviceKind.Keyboard, 50, true, 24).PixelWidth == 24 && DeviceRingIcons.Render(DeviceKind.Keyboard, 50, true, 16).PixelWidth == 16, "keyboard ring renders at native notification icon sizes");
            byte[] rog = new byte[65]; rog[1] = 0x12; rog[2] = 1; rog[6] = 73; rog[9] = 1; rog[11] = 73;
            Check(Protocols.ParseRog(rog, "test").Percent == 73 && Protocols.ParseRog(rog, "test").Charging, "ROG Report-ID normalization and fields");
            Check(Protocols.ParseRog(rog[1..], "test").Percent == 73, "ROG payload-only response");
            rog[6] = 101; Reject(() => Protocols.ParseRog(rog, ""), "reject out-of-range battery");
            Reject(() => Protocols.ParseRog([0, 0x13, 1], ""), "reject unrelated/short response");
            byte[] decoded = [6, 0x37, 0x38, 0x19, 0x40, 0, 0, 0, 1, 0, 88, 0];
            byte[] mouse = [0x11, .. decoded.Select(x => (byte)(x ^ 255))];
            Check(Protocols.ParseMchose(mouse, "", false).Percent == 88, "MCHOSE inversion and logical identity");
            Reject(() => Protocols.ParseMchose(mouse, ""), "do not mislabel A7 as K7");
            var capturedK7 = Convert.FromHexString("11F9C8C7AFBEE3FAFDFAF6A2FFA2FFFFFFFFFFFFFF");
            Check(Protocols.ParseMchose(capturedK7, "").Percent == 93 && Protocols.ParseMchose(capturedK7, "").LogicalPid == 0x4150, "actual K7 Ultra 93% response and model");
            Reject(() => Protocols.ParseMchose(Convert.FromHexString("11F9FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), ""), "reject detached dock or all-FF response");
            var c = new Calibration { Vid = 1, Pid = 2, MatchHex = "11 06", ResponseXor = 255, BatteryOffset = 11, ChargingOffset = 12 };
            Check(Protocols.ParseCalibration(mouse, c, "").Percent == 88, "declarative calibrated response");
            c.MatchHex = "11 07"; Reject(() => Protocols.ParseCalibration(mouse, c, ""), "reject mismatched calibrated response");
            var scaled = new Calibration { Vid = 1, Pid = 2, MatchHex = "11 F9", BatteryOffset = 2, BatteryBytes = 2, Scale = 0.25, ChargingOffset = 4, ChargingMask = 128, ChargingValue = 128 };
            var scaledValue = Protocols.ParseCalibration([0x11, 0xf9, 0x2c, 1, 128], scaled, "");
            Check(scaledValue.Percent == 75 && scaledValue.Charging, "calibration little-endian scaling and charging mask");
            scaled.BigEndian = true;
            Check(Protocols.ParseCalibration([0x11, 0xf9, 1, 0x2c, 0], scaled, "").Percent == 75, "calibration big-endian scaling");
            Reject(() => Protocols.ParseCalibration([0x11, 0xf9], scaled, ""), "reject calibration outside response bounds");
            Reject(() => CommandSafety.Validate([0x11, 0xa8], "feature"), "block known persistent config writes");
            var now = DateTimeOffset.UtcNow; var clock = new RefreshClock(); clock.Configure(now, 1440);
            Check(!clock.TakeDue(now.AddHours(23), 1440), "daily default not prematurely due");
            Check(clock.TakeDue(now.AddDays(4), 1440) && !clock.TakeDue(now.AddDays(4), 1440), "resume coalesces missed deadlines");
            var gate = new ActivityGate(); gate.Reset(0);
            Check(!gate.Observe(299999) && !gate.Observe(300000) && gate.Observe(600000), "activity reset and five-minute threshold");
            var state = new DeviceState(DeviceKind.Mouse); state.Apply(new(true, Battery: new(50, false, "test", "")), now); state.Apply(new(false, "timeout"), now);
            Check(state.Current == null && state.DisplayValue?.Percent == 50 && state.Updated == now && state.IsStale && state.PercentText == "50%", "timeout keeps the last percentage and original successful timestamp");
            Check(state.Tooltip.Contains("上次读数") && state.Tooltip.Contains("timeout"), "cached tooltip explains the failed refresh");
            state.Disconnected();
            Check(state.DisplayValue?.Percent == 50 && state.PercentText == "50%" && state.Updated == now && state.Status == "接收器已断开", "disconnect keeps the displayed battery level");
            state.Apply(new(false, "查询失败"), now.AddMinutes(1));
            Check(state.DisplayValue?.Percent == 50 && state.Updated == now, "repeated failures never overwrite cached battery or time");
            state.Apply(new(true, Battery: new(45, true, "test", "")), now.AddMinutes(2));
            Check(!state.IsStale && state.DisplayValue?.Percent == 45 && state.Updated == now.AddMinutes(2) && !state.Tooltip.Contains("上次读数") && state.Tooltip.Contains("充电中"), "successful recovery replaces cached value and clears stale status");
            state.Apply(new(true, Battery: new(101, false, "bad", "")), now.AddMinutes(3));
            Check(state.IsStale && state.DisplayValue?.Percent == 45 && !state.Tooltip.Contains("充电中"), "invalid response preserves last value without claiming current charging state");
            var emptyState = new DeviceState(DeviceKind.Keyboard); emptyState.Apply(new(false, "查询超时"), now);
            Check(emptyState.DisplayValue == null && emptyState.PercentText == "—" && !emptyState.IsStale, "first failure without a prior successful reading stays unknown");
            emptyState.Disconnected();
            Check(emptyState.PercentText == "—" && emptyState.Updated == null, "disconnect without any successful reading never invents battery data");
            var zeroState = new DeviceState(DeviceKind.Mouse); zeroState.Apply(new(true, Battery: new(0, false, "test", "")), now); zeroState.Disconnected();
            Check(zeroState.DisplayValue?.Percent == 0 && zeroState.PercentText == "0%", "zero percent is a valid cached reading after disconnect");
            string configPath = Path.Combine(Path.GetTempPath(), "PeripheralBattery-test-" + Guid.NewGuid().ToString("N"), "settings.json");
            try
            {
                ConfigStore.Save(new Settings { Configured = true, Mode = RefreshMode.Activity, AutoStart = false, IntervalMinutes = 1440 }, configPath);
                var loaded = ConfigStore.Load(configPath);
                Check(loaded.Configured && loaded.Mode == RefreshMode.Activity && !loaded.AutoStart && loaded.IntervalMinutes == 1440, "settings atomic save/load and mode persistence");
                Check(!File.Exists(configPath + ".tmp"), "settings commit leaves no temporary file");
                File.WriteAllText(configPath, "{bad json");
                Check(!ConfigStore.Load(configPath).Configured && ConfigStore.LoadError != null, "corrupt settings return first-run defaults with explanation");
            }
            finally { if (File.Exists(configPath)) File.Delete(configPath); Directory.Delete(Path.GetDirectoryName(configPath)!); }
            string cachePath = Path.Combine(Path.GetTempPath(), "PeripheralBattery-cache-test-" + Guid.NewGuid().ToString("N"), "battery-cache.json");
            try
            {
                var settings = new Settings(); var cache = new BatteryCache(settings, cachePath);
                var savedMouse = new DeviceState(DeviceKind.Mouse); var savedKeyboard = new DeviceState(DeviceKind.Keyboard);
                var oldTime = now.AddDays(-60);
                savedMouse.Apply(new(true, Battery: new(93, true, "HID", "mouse-path", 0x3837, 0x4150)), oldTime);
                savedKeyboard.Apply(new(true, Battery: new(96, false, "HID", "keyboard-path")), now.AddDays(-1));
                Check(cache.SaveSuccessful(savedMouse) && cache.SaveSuccessful(savedKeyboard), "successful device readings are persisted immediately");
                var fresh = new BatteryCache(settings, cachePath); var restoredMouse = new DeviceState(DeviceKind.Mouse); var restoredKeyboard = new DeviceState(DeviceKind.Keyboard);
                Check(fresh.Restore(restoredMouse) && fresh.Restore(restoredKeyboard) && restoredMouse.PercentText == "93%" && restoredKeyboard.PercentText == "96%", "new application state restores both device percentages from disk");
                Check(restoredMouse.Updated == oldTime && restoredMouse.IsStale && restoredMouse.Current == null && !restoredMouse.Tooltip.Contains("充电中"), "old cache never expires or masquerades as a live charging reading");
                string before = File.ReadAllText(cachePath); restoredMouse.Apply(new(false, "查询超时"), now);
                Check(!fresh.SaveSuccessful(restoredMouse) && File.ReadAllText(cachePath) == before && restoredMouse.PercentText == "93%", "startup query failure cannot overwrite persisted battery");
                restoredMouse.Apply(new(true, Battery: new(0, false, "HID", "mouse-path", 0x3837, 0x4150)), now); fresh.SaveSuccessful(restoredMouse);
                var afterRecovery = new BatteryCache(settings, cachePath); var zero = new DeviceState(DeviceKind.Mouse); afterRecovery.Restore(zero);
                Check(zero.PercentText == "0%" && zero.Updated == now, "new successful zero-percent reading replaces persistent cache");
                var document = System.Text.Json.JsonSerializer.Deserialize<BatteryCache.CacheDocument>(File.ReadAllText(cachePath), Json.Options)!;
                Check(document.Readings.Count == 2 && !File.Exists(cachePath + ".tmp"), "cache holds only the latest two readings with no leftover temp file");
                settings.Calibrations.Add(new Calibration { Target = DeviceKind.Mouse, Confirmed = true, Vid = 1, Pid = 2, MatchHex = "11 06" });
                var changed = new BatteryCache(settings, cachePath);
                Check(!changed.Restore(new DeviceState(DeviceKind.Mouse)) && changed.Restore(new DeviceState(DeviceKind.Keyboard)), "changed device calibration cannot restore the other binding's cache");
                File.WriteAllText(cachePath, "{bad cache"); var corrupted = new BatteryCache(settings, cachePath);
                Check(corrupted.LastError != null && !corrupted.Restore(new DeviceState(DeviceKind.Mouse)), "corrupt battery cache is ignored without resetting user settings");
            }
            finally { if (File.Exists(cachePath)) File.Delete(cachePath); Directory.Delete(Path.GetDirectoryName(cachePath)!); }
            using (var worker = new WorkerClient())
            {
                var timer = Stopwatch.StartNew(); var r = await worker.Run(new("test-hang"), deadlineMs: 400);
                testedPids.Add(worker.LastProcessId);
                Check(!r.Ok && r.Error == "查询超时" && timer.Elapsed < TimeSpan.FromSeconds(4), "terminate blocked HID worker on deadline");
                using var cancelled = new CancellationTokenSource(150);
                r = await worker.Run(new("test-hang"), cancelled.Token);
                testedPids.Add(worker.LastProcessId);
                Check(!r.Ok && r.Error == "已取消", "terminate worker on cancellation");
            }
            var owned = new WorkerClient();
            var blocked = owned.Run(new("test-hang"), deadlineMs: 30000); await Task.Delay(200);
            testedPids.Add(owned.LastProcessId); owned.Dispose();
            Check(!(await blocked.WaitAsync(TimeSpan.FromSeconds(3))).Ok, "closing job terminates an in-flight worker");
            using (var parent = new Process { StartInfo = new(Environment.ProcessPath!, "--test-job-parent") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } })
            {
                parent.Start(); string? line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(parent.ExitCode == 23 && int.TryParse(line, out _), "crash-simulation parent exited without managed cleanup");
                testedPids.Add(int.Parse(line!)); await Task.Delay(300);
                Check(Exited(int.Parse(line!)), "OS job cleanup kills worker after unexpected parent exit");
            }
            await Task.Delay(300);
            Check(testedPids.All(Exited), "all tested worker processes actually exited");
            Console.WriteLine($"{count} tests passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
