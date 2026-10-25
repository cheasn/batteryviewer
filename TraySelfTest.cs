using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PeripheralBattery;

internal static class TraySelfTest
{
    public static int Run()
    {
        DebugConsole.Enable();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Install();
        using var worker = new WorkerClient();
        var coordinator = new Coordinator(worker, new Settings(), CancellationToken.None);
        using var tray = new TrayHost(coordinator, (_, _) => { }, () => { }, () => { }, _ => { });
        int code = 1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
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
            code = tray.AllRegistered && mouse && keyboard ? 0 : 1;
            tray.Dispose(); app.Shutdown();
        };
        timer.Start(); app.Run(); return code;
    }
}
