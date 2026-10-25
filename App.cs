using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PeripheralBattery;

internal sealed class BatteryApp(string[] args, SingleInstance instance) : Application
{
    readonly CancellationTokenSource _lifetime = new();
    WorkerClient? _worker;
    Coordinator? _coordinator;
    Scheduler? _scheduler;
    TrayHost? _tray;
    PanelWindow? _panel;
    SettingsWindow? _settingsWindow;
    DebugWindow? _debugWindow;
    Settings _settings = new();
    BatteryCache? _cache;
    bool _stopping, _debug;
    readonly bool _test = args.Contains("--test-session");
    readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, error) => { DebugConsole.Write(error.Exception.ToString()); error.Handled = true; Stop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _worker?.Dispose();
        Theme.Install(); SystemEvents.UserPreferenceChanged += PreferenceChanged;
        if (args.Contains("--debug")) { _debug = true; DebugConsole.Enable(); }
        Console.CancelKeyPress += ConsoleCancel;
        _settings = _test ? new Settings { Configured = true, AutoStart = false } : ConfigStore.Load();
        try { _worker = new(); }
        catch (Exception ex) { MessageBox.Show("无法建立安全的 HID 辅助进程：" + ex.Message); Shutdown(1); return; }
        _coordinator = new(_worker, _settings, _lifetime.Token);
        if (!_test)
        {
            _cache = new(_settings); _cache.Restore(_coordinator.Mouse); _cache.Restore(_coordinator.Keyboard);
            if (_cache.LastError != null) DebugConsole.Write(_cache.LastError);
            _coordinator.ReadSucceeded += state => { if (!_cache.SaveSuccessful(state) && _cache.LastError != null) DebugConsole.Write(_cache.LastError); };
        }
        _coordinator.Diagnostic += text => { if (_debugWindow == null) DebugConsole.Write(text); };
        _panel = new(OpenSettings, _test); MainWindow = _panel;
        _tray = new(_coordinator, (_, rect) => ShowPanel(rect), () => _ = _coordinator.Refresh(DeviceKind.Mouse), DevicesChanged, resumed =>
        { if (resumed) { DevicesChanged(); _scheduler?.Resume(); } else _scheduler?.Suspend(); },
        status => Dispatcher.BeginInvoke(() =>
        {
            if (_stopping) return;
            ShowPanel(_tray!.IconRect(DeviceKind.Mouse), false);
            MessageBox.Show("无法创建两个托盘电量图标。请保持面板可用，稍后重新启动程序。\n" + status, "托盘图标注册失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }));
        _scheduler = new(_settings, () => _coordinator.RefreshAll());
        _coordinator.Changed += () => { _panel.Update(_coordinator); _tray?.Update(); };
        _deviceDebounce.Tick += async (_, _) => { _deviceDebounce.Stop(); try { await _coordinator.Enumerate(); } catch (OperationCanceledException) { } };
        _ = instance.Listen(command => Dispatcher.BeginInvoke(() =>
        {
            switch (command) { case "debug": OpenDebug(); break; case "settings": OpenSettings(); break; case "exit": Stop(); break; default: ShowPanel(_tray.IconRect(DeviceKind.Mouse)); break; }
        }));
        if (!_settings.Configured) OpenSettings();
        else Dispatcher.BeginInvoke(async () => await SafeStart());
        if (_debug) OpenDebug(); else if (args.Contains("--settings")) OpenSettings();
    }
    void ConsoleCancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; Dispatcher.BeginInvoke(Stop); }
    async Task SafeStart()
    {
        try { await StartMonitoring(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DebugConsole.Write(ex.ToString()); if (!_stopping) MessageBox.Show("启动查询失败：" + ex.Message); }
    }
    async Task StartMonitoring()
    {
        Configure(); await _coordinator!.Enumerate(); await _coordinator.RefreshAll();
        if (!args.Contains("--startup") && _settingsWindow == null && _debugWindow == null && !_stopping)
            ShowPanel(_tray!.IconRect(DeviceKind.Mouse), false);
    }
    void Configure() { _scheduler!.Configure(); _tray!.ConfigureActivity(_settings.Mode == RefreshMode.Activity); }
    void SaveSettings(Settings next)
    {
        bool first = !_settings.Configured;
        if (!_test) { ConfigStore.AutoStart(next.AutoStart); ConfigStore.Save(next); }
        _settings.Configured = true; _settings.AutoStart = next.AutoStart; _settings.Mode = next.Mode; _settings.IntervalMinutes = next.IntervalMinutes;
        Configure(); if (first) Dispatcher.BeginInvoke(async () => await SafeStart());
    }
    void OpenSettings()
    {
        _panel?.Hide();
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new(_settings, !_settings.Configured, _debug, SaveSettings, Stop, OpenDebug, _cache?.LastError);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; if (!_settings.Configured) Stop(); };
        _settingsWindow.Show();
    }
    void OpenDebug()
    {
        _debug = true; DebugConsole.Enable();
        _panel?.Hide();
        if (_debugWindow != null) { _debugWindow.Activate(); return; }
        _debugWindow = new(_coordinator!, _worker!, _settings, () => { if (!_test) ConfigStore.Save(_settings); }, _lifetime.Token);
        _debugWindow.Closed += (_, _) => _debugWindow = null; _debugWindow.Show();
    }
    void ShowPanel(Rect rect, bool refresh = true)
    {
        if (_stopping) return;
        if (!_settings.Configured) { OpenSettings(); return; }
        try { _panel!.Update(_coordinator!); _panel.OpenNear(rect); if (refresh) _ = _coordinator!.RefreshAll(); }
        catch (Exception ex)
        {
            DebugConsole.Write(ex.ToString());
            MessageBox.Show("无法打开电量面板，程序仍在托盘运行。请重试或重新启动程序。\n" + ex.Message,
                "面板打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    void DevicesChanged() { if (_stopping) return; _deviceDebounce.Stop(); _deviceDebounce.Start(); }
    void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { Theme.Refresh(); _tray?.Update(); });
    public void Stop()
    {
        if (_stopping) return; _stopping = true;
        _scheduler?.Stop(); _deviceDebounce.Stop(); _lifetime.Cancel();
        _tray?.Dispose(); _tray = null; _worker?.Dispose();
        if (_panel != null) _panel.Exiting = true;
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _stopping = true; _lifetime.Cancel(); _scheduler?.Stop(); _deviceDebounce.Stop();
        SystemEvents.UserPreferenceChanged -= PreferenceChanged; _tray?.Dispose(); _worker?.Dispose();
        Console.CancelKeyPress -= ConsoleCancel;
        base.OnExit(e);
    }
}
