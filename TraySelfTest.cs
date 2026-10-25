using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PeripheralBattery;

internal static class TraySelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? className, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static int Run()
    {
        DebugConsole.Enable();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Install();
        using var worker = new WorkerClient();
        var coordinator = new Coordinator(worker, new Settings(), CancellationToken.None);
        var panel = new PanelWindow(() => { }, true);
        int showCount = 0;
        bool showing = false;
        using var tray = new TrayHost(coordinator, (kind, rect) =>
        {
            if (showing) throw new InvalidOperationException("Panel opening reentered the tray callback");
            showing = true;
            try { showCount++; panel.Update(coordinator); panel.OpenNear(rect); _ = coordinator.RefreshAll(); }
            finally { showing = false; }
        }, () => { }, () => { }, _ => { });
        var refreshed = new List<DeviceKind>();
        coordinator.Changed += () =>
        {
            if (coordinator.Mouse.Busy) refreshed.Add(DeviceKind.Mouse);
            if (coordinator.Keyboard.Busy) refreshed.Add(DeviceKind.Keyboard);
            panel.Update(coordinator); tray.Update();
        };
        Exception? callbackError = null;
        app.DispatcherUnhandledException += (_, error) => { callbackError = error.Exception; error.Handled = true; };
        int code = 1;
        void Check(bool condition, string label)
        {
            if (callbackError != null) throw new InvalidOperationException("Tray callback failed", callbackError);
            if (!condition) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }
        Task Drain() => app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                bool mouse = tray.TryIconRect(DeviceKind.Mouse, out var mouseRect);
                bool keyboard = tray.TryIconRect(DeviceKind.Keyboard, out var keyboardRect);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Executable = Environment.ProcessPath,
                    Registered = tray.AllRegistered,
                    Status = tray.RegistrationStatus,
                    MouseHasShellBounds = mouse,
                    MouseBounds = mouse ? mouseRect.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    KeyboardHasShellBounds = keyboard,
                    KeyboardBounds = keyboard ? keyboardRect.ToString(System.Globalization.CultureInfo.InvariantCulture) : null
                }, Json.Options));
                Check(tray.AllRegistered && mouse && keyboard, "both icons have real Windows Shell bounds");
                var window = FindWindowW(null, "PeripheralBattery.Events");
                GetWindowThreadProcessId(window, out uint processId);
                Check(window != IntPtr.Zero && processId == Environment.ProcessId, "native callbacks target this test process");
                void Send(int icon, int notification) => SendMessageW(window, 0x8051 + icon, IntPtr.Zero, new IntPtr(((icon + 1) << 16) | notification));

                panel.SourceInitialized += (_, _) => Send(0, 0x7b);
                Send(0, 0x7b);
                Check(showCount == 0, "right-click returns before opening a WPF window");
                await Drain();
                Check(showCount == 1 && panel.IsVisible, "callback during window creation is coalesced without reentry");
                for (int i = 0; i < 10; i++)
                {
                    int before = showCount;
                    await Task.Run(() => { Send(i % 2, 0x205); Send(i % 2, 0x7b); });
                    await Drain();
                    Check(panel.IsVisible && showCount == before + 1, $"right-click {i + 1} opens or activates the panel exactly once");
                    if (i % 3 == 0) panel.Close(); // Closing is canceled and hides the reusable panel.
                    else if (i % 3 == 1) panel.Hide();
                }
                int beforeBurst = showCount;
                for (int i = 0; i < 20; i++) Send(i % 2, 0x7b);
                await Drain();
                Check(showCount == beforeBurst + 1, "rapid callbacks share one pending panel open");

                refreshed.Clear();
                int beforeLeftClick = showCount;
                Send(0, 0x202); Send(0, 0x400);
                Check(refreshed.Count == 0, "left-click also defers refresh until the native callback returns");
                await Drain();
                Check(refreshed.SequenceEqual(new[] { DeviceKind.Mouse }) && showCount == beforeLeftClick, "left-click refreshes only the mouse without opening the panel");
                refreshed.Clear(); Send(1, 0x401);
                await Drain();
                Check(refreshed.SequenceEqual(new[] { DeviceKind.Keyboard }), "keyboard activation refreshes only its device");

                int beforeDispose = showCount;
                Send(0, 0x7b); tray.Dispose();
                await Drain();
                Check(showCount == beforeDispose, "disposing the tray cancels pending panel opens");
                code = 0;
            }
            catch (Exception ex) { code = 1; Console.WriteLine("FAIL " + ex); }
            finally { panel.Exiting = true; panel.Close(); tray.Dispose(); app.Shutdown(); }
        };
        timer.Start(); app.Run(); return code;
    }
}
