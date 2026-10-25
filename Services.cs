using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PeripheralBattery;

internal static class ConfigStore
{
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBattery", "settings.json");
    public static string? LoadError { get; private set; }
    public static Settings Load(string? pathOverride = null)
    {
        LoadError = null;
        try
        {
            string path = pathOverride ?? PathName;
            if (!File.Exists(path)) return new();
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json.Options) ?? throw new IOException("配置为空");
            if (!Enum.IsDefined(s.Mode) || !double.IsFinite(s.IntervalMinutes) || s.IntervalMinutes < 1 || s.IntervalMinutes > 525600)
                throw new IOException("刷新间隔不合法");
            s.Calibrations ??= [];
            foreach (var c in s.Calibrations) c.Validate();
            return s;
        }
        catch (Exception e) { LoadError = "无法加载配置，将重新设置：" + e.Message; return new(); }
    }
    public static void Save(Settings s, string? pathOverride = null)
    {
        string path = pathOverride ?? PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options)); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static void AutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled) key.SetValue("PeripheralBattery", $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue("PeripheralBattery", false);
    }
}

internal sealed class Coordinator(WorkerClient worker, Settings settings, CancellationToken lifetime)
{
    readonly SemaphoreSlim _io = new(1, 1);
    readonly HashSet<DeviceKind> _refreshing = [];
    int _debugPause;
    public HidDevice[] Devices { get; private set; } = [];
    public DeviceState Mouse { get; } = new(DeviceKind.Mouse);
    public DeviceState Keyboard { get; } = new(DeviceKind.Keyboard);
    public event Action? Changed;
    public event Action<string>? Diagnostic;
    public event Action<DeviceState>? ReadSucceeded;
    public DeviceState State(DeviceKind kind) => kind == DeviceKind.Mouse ? Mouse : Keyboard;
    public async Task Enumerate()
    {
        await _io.WaitAsync(lifetime);
        try
        {
            var r = await worker.Run(new("enumerate"), lifetime, 12000);
            if (!r.Ok) { Diagnostic?.Invoke("枚举失败：" + r.Error); return; }
            Devices = r.Devices ?? [];
            foreach (var state in new[] { Mouse, Keyboard })
                if (state.DisplayValue is { } b && !Devices.Any(d => d.Path == b.Path)) state.Disconnected();
            Changed?.Invoke();
        }
        finally { _io.Release(); }
    }
    public Task RefreshAll() => RefreshPair();
    async Task RefreshPair() { await Refresh(DeviceKind.Mouse); await Refresh(DeviceKind.Keyboard); }
    public async Task Refresh(DeviceKind kind)
    {
        if (lifetime.IsCancellationRequested || _debugPause > 0 || !_refreshing.Add(kind)) return;
        var state = State(kind); state.Busy = true; Changed?.Invoke();
        bool entered = false;
        try
        {
            await _io.WaitAsync(lifetime); entered = true;
            if (_debugPause > 0) return;
            WorkerReply result;
            var calibration = settings.Calibrations.SingleOrDefault(c => c.Confirmed && c.Target == kind);
            if (calibration != null)
                result = await worker.Run(new("calibration", Hex: JsonSerializer.Serialize(calibration)), lifetime);
            else
            {
                var candidates = Devices.Where(kind == DeviceKind.Keyboard ? Hid.IsRog : Hid.IsMchose)
                    .OrderByDescending(d => d.UsagePage == 0xff01).ToArray();
                if (candidates.Length == 0) result = new(false, "接收器未连接或接口不可访问");
                else
                {
                    result = new(false, "尚未验证此设备协议");
                    var successes = new List<WorkerReply>();
                    foreach (var d in candidates)
                    {
                        var r = await worker.Run(new(kind == DeviceKind.Keyboard ? "rog" : "mchose", d.Path), lifetime);
                        Diagnostic?.Invoke($"{state.Name} {d.Id} {d.UsagePage:X4}:{d.Usage:X4} → {(r.Ok ? r.Battery?.Percent + "% " + r.Battery?.Protocol : r.Error)}");
                        if (r.Ok) successes.Add(r); else result = r;
                    }
                    var unique = successes.GroupBy(r => kind == DeviceKind.Mouse ? $"{r.Battery!.LogicalVid}:{r.Battery.LogicalPid}" : r.Battery!.Path).ToArray();
                    if (unique.Length == 1) result = unique[0].First();
                    else if (unique.Length > 1) result = new(false, "多个目标设备，请在调试中绑定唯一接口");
                }
            }
            state.Apply(result, DateTimeOffset.Now);
            if (state.Current != null) ReadSucceeded?.Invoke(state);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { if (entered) _io.Release(); _refreshing.Remove(kind); state.Busy = false; Changed?.Invoke(); }
    }
    public async Task DebugOperation(Func<Task> operation, CancellationToken token)
    {
        Interlocked.Increment(ref _debugPause);
        bool entered = false;
        try { await _io.WaitAsync(token); entered = true; await operation(); }
        finally { if (entered) _io.Release(); Interlocked.Decrement(ref _debugPause); }
    }
}

internal sealed class Scheduler
{
    readonly DispatcherTimer _timer = new();
    readonly RefreshClock _clock = new();
    readonly Settings _settings;
    readonly Func<Task> _refresh;
    public Scheduler(Settings settings, Func<Task> refresh)
    {
        _settings = settings; _refresh = refresh;
        _timer.Tick += async (_, _) => { _timer.Stop(); if (_clock.TakeDue(DateTimeOffset.UtcNow, _settings.IntervalMinutes)) await _refresh(); Arm(); };
    }
    public void Configure() { _timer.Stop(); _clock.Configure(DateTimeOffset.UtcNow, _settings.IntervalMinutes); Arm(); }
    void Arm()
    {
        if (_settings.Mode != RefreshMode.Scheduled) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, (_clock.Next - DateTimeOffset.UtcNow).TotalMilliseconds)); _timer.Start();
    }
    public void Suspend() => _timer.Stop();
    public async void Resume() { if (_settings.Mode == RefreshMode.Scheduled && _clock.TakeDue(DateTimeOffset.UtcNow, _settings.IntervalMinutes)) await _refresh(); Arm(); }
    public void Stop() => _timer.Stop();
}

internal sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly CancellationTokenSource _stop = new();
    readonly string _name = "PeripheralBattery-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    public bool IsFirst { get; }
    public SingleInstance() { _mutex = new(true, "Local\\" + _name, out bool first); IsFirst = first; }
    public async Task Notify(string command)
    {
        using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.Asynchronous);
        try { await client.ConnectAsync(2000); using var writer = new StreamWriter(client); await writer.WriteLineAsync(command); }
        catch { }
    }
    public async Task Listen(Action<string> show)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); deadline.CancelAfter(2000);
                    using var reader = new StreamReader(server);
                    var text = new System.Text.StringBuilder(); var buffer = new char[1];
                    while (text.Length < 16 && await reader.ReadAsync(buffer.AsMemory(), deadline.Token) > 0)
                    { if (buffer[0] == '\n') break; if (buffer[0] != '\r') text.Append(buffer[0]); }
                    string line = text.ToString();
                    if (line is "debug" or "show" or "exit" or "settings") show(line);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { DebugConsole.Write(e.Message); }
    }
    public void Dispose() { _stop.Cancel(); if (IsFirst) _mutex.ReleaseMutex(); _mutex.Dispose(); _stop.Dispose(); }
}

internal static class DebugConsole
{
    public static bool Enabled { get; private set; }
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint process);
    public static void Enable()
    {
        if (Enabled) return;
        if (GetConsoleWindow() == IntPtr.Zero && GetFileType(GetStdHandle(-11)) == 0 && !AttachConsole(uint.MaxValue)) AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Enabled = true;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
    public static void Write(string message) { if (Enabled) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"); }
}
