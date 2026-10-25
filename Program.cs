using System.Runtime.InteropServices;
using System.Text.Json;

namespace PeripheralBattery;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--hid-worker")) { Worker.MainAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--probe")) return Probe(args.Contains("--summary")).GetAwaiter().GetResult();
        if (args.Contains("--self-test")) return SelfTests.Run().GetAwaiter().GetResult();
        if (args.Contains("--tray-self-test")) return TraySelfTest.Run();
        if (args.Contains("--cache-inspect"))
        {
            DebugConsole.Enable(); var cache = new BatteryCache(ConfigStore.Load());
            var states = new[] { new DeviceState(DeviceKind.Mouse), new DeviceState(DeviceKind.Keyboard) };
            foreach (var state in states) cache.Restore(state);
            Console.WriteLine(JsonSerializer.Serialize(new { Error = cache.LastError, Devices = states.Select(s => new { s.Name, Percent = s.DisplayValue?.Percent, s.Updated, s.IsStale, s.Status }) }, Json.Options));
            return cache.LastError == null ? 0 : 1;
        }
        if (args.Contains("--test-job-parent"))
        {
            var client = new WorkerClient(); _ = client.Run(new("test-hang"), deadlineMs: 60000);
            Thread.Sleep(200); Console.WriteLine(client.LastProcessId); Console.Out.Flush();
            Environment.Exit(23); return 23;
        }
        if (args.Length == 2 && args[0] == "--render-preview") return Preview.Render(args[1]);
        if (args.Length == 2 && args[0] == "--render-notification-icons") return Preview.NotificationSamples(args[1]);
        using var instance = new SingleInstance();
        if (args.Contains("--exit")) { if (!instance.IsFirst) instance.Notify("exit").GetAwaiter().GetResult(); return 0; }
        if (!instance.IsFirst) { instance.Notify(args.Contains("--debug") ? "debug" : args.Contains("--settings") ? "settings" : "show").GetAwaiter().GetResult(); return 0; }
        var app = new BatteryApp(args, instance);
        return app.Run();
    }
    static async Task<int> Probe(bool summary)
    {
        DebugConsole.Enable();
        using var worker = new WorkerClient();
        var r = await worker.Run(new("enumerate"), deadlineMs: 12000);
        Console.WriteLine(summary ? $"HID interfaces: {r.Devices?.Length}; enum ok: {r.Ok}; error: {r.Error}" : JsonSerializer.Serialize(r, Json.Options));
        foreach (var d in r.Devices ?? [])
        {
            if (!Hid.IsRog(d) && !Hid.IsMchose(d)) continue;
            var response = await worker.Run(new(Hid.IsRog(d) ? "rog" : "mchose-inspect", d.Path));
            Console.WriteLine(JsonSerializer.Serialize(new { d.Id, d.UsagePage, d.Usage, d.Name, Response = response }, Json.Options));
        }
        return r.Ok ? 0 : 1;
    }
}
