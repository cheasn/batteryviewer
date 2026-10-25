using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PeripheralBattery;

internal sealed class TrayHost : IDisposable
{
    const int Callback = 0x8051;
    readonly HwndSource _source;
    readonly Coordinator _coordinator;
    readonly Action<DeviceKind, Rect> _show;
    readonly Action _activity;
    readonly Action _devicesChanged;
    readonly Action<bool> _power;
    readonly Dictionary<DeviceKind, IntPtr> _icons = [];
    readonly Dictionary<IntPtr, bool> _rawDevices = [];
    readonly ActivityGate _gate = new();
    readonly uint _taskbarCreated;
    readonly Dictionary<DeviceKind, Guid> _identities = new()
    {
        [DeviceKind.Mouse] = TrayIdentity.ForExecutable(Environment.ProcessPath!, DeviceKind.Mouse),
        [DeviceKind.Keyboard] = TrayIdentity.ForExecutable(Environment.ProcessPath!, DeviceKind.Keyboard)
    };
    readonly HashSet<DeviceKind> _registered = [];
    readonly Dictionary<DeviceKind, bool> _version4 = [];
    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Action<string>? _registrationFailed;
    int _retryCount;
    bool _disposed;
    bool _activityEnabled;
    public bool AllRegistered => _registered.Count == 2;
    public string RegistrationStatus => string.Join("；", new[] { DeviceKind.Mouse, DeviceKind.Keyboard }.Select(k => $"{_coordinator.State(k).Name}：{(_registered.Contains(k) ? "图标已注册" : "图标注册失败")}"));
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct NotifyData
    {
        public int Size; public IntPtr Window; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NotifyData data);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint number, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, System.Text.StringBuilder data, ref uint size);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }

    public TrayHost(Coordinator coordinator, Action<DeviceKind, Rect> show, Action activity, Action devicesChanged, Action<bool> power, Action<string>? registrationFailed = null)
    {
        _coordinator = coordinator; _show = show; _activity = activity; _devicesChanged = devicesChanged; _power = power;
        _registrationFailed = registrationFailed;
        _source = new(new HwndSourceParameters("PeripheralBattery.Events") { Width = 0, Height = 0, WindowStyle = 0x80000 });
        _source.AddHook(Hook);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _retry.Tick += (_, _) =>
        {
            foreach (var kind in new[] { DeviceKind.Mouse, DeviceKind.Keyboard }) if (!_registered.Contains(kind)) Register(kind);
            if (AllRegistered) _retry.Stop();
            else if (++_retryCount >= 5) { _retry.Stop(); _registrationFailed?.Invoke(RegistrationStatus); }
        };
        Add(DeviceKind.Mouse); Add(DeviceKind.Keyboard);
        RetryMissing();
    }
    NotifyData Data(DeviceKind kind) => new()
    {
        Size = Marshal.SizeOf<NotifyData>(), Window = _source.Handle, Id = (uint)kind + 1, Flags = 1 | 2 | 4 | 0x20 | 0x80,
        Message = (uint)(Callback + (int)kind), Icon = _icons.GetValueOrDefault(kind), Tip = _coordinator.State(kind).Tooltip[..Math.Min(127, _coordinator.State(kind).Tooltip.Length)],
        Info = "", InfoTitle = "", Guid = _identities[kind]
    };
    void Add(DeviceKind kind)
    {
        if (_icons.Remove(kind, out var old)) DestroyIcon(old);
        _icons[kind] = Draw(kind, _coordinator.State(kind).DisplayValue);
        Register(kind);
    }
    void Register(DeviceKind kind)
    {
        var d = Data(kind);
        if (d.Icon == IntPtr.Zero || (!Shell_NotifyIconW(0, ref d) && !Shell_NotifyIconW(1, ref d)))
        {
            _registered.Remove(kind);
            DebugConsole.Write($"通知区域图标注册失败 {kind}，GUID={d.Guid}，EXE={Environment.ProcessPath}");
            return;
        }
        _registered.Add(kind);
        d.Version = 4; _version4[kind] = Shell_NotifyIconW(4, ref d);
        DebugConsole.Write($"通知区域图标已注册 {kind}，GUID={d.Guid}");
    }
    void RetryMissing()
    {
        if (!_disposed && !AllRegistered && !_retry.IsEnabled) { _retryCount = 0; _retry.Start(); }
    }
    public void Update()
    {
        foreach (var kind in new[] { DeviceKind.Mouse, DeviceKind.Keyboard })
        {
            var old = _icons.GetValueOrDefault(kind);
            _icons[kind] = Draw(kind, _coordinator.State(kind).DisplayValue);
            var d = Data(kind);
            if (!Shell_NotifyIconW(1, ref d)) { _registered.Remove(kind); Register(kind); }
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        RetryMissing();
        _rawDevices.Clear();
    }
    public void ConfigureActivity(bool enabled)
    {
        _activityEnabled = enabled; _gate.Reset(Environment.TickCount64); _rawDevices.Clear();
        var devices = new[] { new RawDevice { Page = 1, Usage = 2, Flags = enabled ? 0x2100u : 1u, Target = enabled ? _source.Handle : IntPtr.Zero } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>()) && enabled)
            DebugConsole.Write("Raw Input 注册失败：" + Marshal.GetLastWin32Error());
    }
    public Rect IconRect(DeviceKind kind)
    {
        if (TryIconRect(kind, out var rect)) return rect;
        GetCursorPos(out var p); return new(p.X, p.Y, 1, 1);
    }
    public bool TryIconRect(DeviceKind kind, out Rect rect)
    {
        var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = _source.Handle, Id = (uint)kind + 1, Guid = _identities[kind] };
        int result = Shell_NotifyIconGetRect(ref id, out var r);
        // S_FALSE (1) is also a success HRESULT: Explorer can return the overflow anchor.
        DebugConsole.Write($"通知区域位置 {kind}：HRESULT=0x{result:X8}");
        if (result >= 0 && r.Right > r.Left && r.Bottom > r.Top) { rect = new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
        rect = Rect.Empty; return false;
    }
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if ((uint)msg == _taskbarCreated) { _retry.Stop(); _registered.Clear(); Add(DeviceKind.Mouse); Add(DeviceKind.Keyboard); RetryMissing(); }
        if (msg == Callback || msg == Callback + 1)
        {
            int evt = (int)((long)lp & 0xffff);
            // Separate callback messages identify GUID icons without relying on version-specific uID packing.
            var kind = (DeviceKind)(msg - Callback);
            if (evt is 0x400 or 0x401 || evt == 0x202 && !_version4.GetValueOrDefault(kind)) _ = _coordinator.Refresh(kind);
            if (evt is 0x7b or 0x205) _show(kind, IconRect(kind));
            handled = true;
        }
        if (msg == 0x00ff && _activityEnabled) ObserveInput(lp);
        if (msg == 0xfe || msg == 0x219) { _rawDevices.Clear(); _devicesChanged(); }
        if (msg == 0x218) { if ((int)wp == 4) _power(false); if ((int)wp is 18 or 7) _power(true); }
        if (msg is 0x1a or 0x31e) { Theme.Refresh(); Update(); }
        return IntPtr.Zero;
    }
    unsafe void ObserveInput(IntPtr raw)
    {
        uint size = (uint)(IntPtr.Size == 8 ? 24 : 16);
        byte* header = stackalloc byte[(int)size];
        if (GetRawInputData(raw, 0x10000005, (IntPtr)header, ref size, size) == uint.MaxValue || *(uint*)header != 0) return;
        var device = *(IntPtr*)(header + 8);
        if (!_rawDevices.TryGetValue(device, out bool target))
        {
            uint count = 1024; var sb = new System.Text.StringBuilder((int)count);
            GetRawInputDeviceInfoW(device, 0x20000007, sb, ref count);
            var current = _coordinator.Mouse.LastGood;
            var id = current == null ? "" : System.Text.RegularExpressions.Regex.Match(current.Path, @"vid_[0-9a-f]{4}&pid_[0-9a-f]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
            target = id.Length > 0 && sb.ToString().Contains(id, StringComparison.OrdinalIgnoreCase);
            _rawDevices[device] = target;
        }
        if (target && _gate.Observe(Environment.TickCount64)) _activity();
    }
    static IntPtr Draw(DeviceKind kind, BatteryValue? value) =>
        DeviceRingIcons.Native(DeviceRingIcons.Render(kind, value?.Percent, Theme.SystemLight, 32));
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _retry.Stop();
        ConfigureActivity(false);
        foreach (var kind in new[] { DeviceKind.Mouse, DeviceKind.Keyboard })
        { var d = Data(kind); Shell_NotifyIconW(2, ref d); if (_icons.TryGetValue(kind, out var icon)) DestroyIcon(icon); }
        _icons.Clear(); _source.Dispose();
    }
}
