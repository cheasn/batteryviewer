using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PeripheralBattery;

internal sealed class DebugWindow : Window
{
    readonly Coordinator _coordinator;
    readonly WorkerClient _worker;
    readonly Settings _settings;
    readonly Action _save;
    readonly CancellationToken _lifetime;
    readonly ComboBox _devices = new() { MinWidth = 180, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _report = Ui.Input("11"), _end = Ui.Input("20");
    readonly TextBox _query = Ui.Input("11 F9 FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF");
    readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), FontSize = 12 };
    readonly TextBox _calibration = new() { AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), FontSize = 12 };
    readonly CheckBox _unlock = new() { Content = "本次会话解锁试发（仅状态查询）" };
    readonly CheckBox _confirm = new() { Content = "我已与官方软件核对，本次标定为真实电量" };
    CancellationTokenSource? _operation;
    string? _testedCalibration;
    HidDevice Selected => _devices.SelectedItem as HidDevice ?? throw new ArgumentException("请先选择 HID 接口");
    public DebugWindow(Coordinator coordinator, WorkerClient worker, Settings settings, Action save, CancellationToken lifetime)
    {
        Ui.Prepare(this);
        _coordinator = coordinator; _worker = worker; _settings = settings; _save = save; _lifetime = lifetime;
        Title = "HID 调试 · 无线外设电量"; Width = 980; Height = 760; MinWidth = 720; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var header = new StackPanel();
        header.Children.Add(Ui.Text("HID 设备与协议调试", 20));
        var hint = Ui.Text("仅输出到本窗口和 --debug 控制台，不写日志。操作期间暂停日常查询；仅使用厂商接口试发状态查询，禁止固件、配对与配置写入。", 12, "Secondary"); hint.Margin = new(0, 8, 0, 12); header.Children.Add(hint);
        var selector = new Grid(); selector.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); selector.ColumnDefinitions.Add(new()); selector.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        selector.Children.Add(Ui.Button("重新枚举", () => _ = Execute(Enumerate)));
        Grid.SetColumn(_devices, 1); selector.Children.Add(_devices);
        var stop = Ui.Button("停止操作", () => _operation?.Cancel()); stop.Margin = new(8, 0, 0, 0); Grid.SetColumn(stop, 2); selector.Children.Add(stop); header.Children.Add(selector);
        root.Children.Add(header);
        var details = Ui.Text("", 11, "Secondary"); details.Margin = new(0, 10, 0, 10); Grid.SetRow(details, 1); root.Children.Add(details);
        _devices.SelectionChanged += (_, _) =>
        {
            if (_devices.SelectedItem is not HidDevice d) return;
            details.Text = $"Input {d.InputLength} / Output {d.OutputLength} / Feature {d.FeatureLength}\n{d.Path}" + (d.Error == null ? "" : "\n" + d.Error);
        };
        var tabs = new TabControl(); tabs.SetResourceReference(Control.BackgroundProperty, "Surface"); tabs.SetResourceReference(Control.ForegroundProperty, "Text");
        tabs.SetResourceReference(Control.BorderBrushProperty, "Line"); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        var operations = new Grid { Margin = new(12) }; operations.RowDefinitions.Add(new() { Height = GridLength.Auto }); operations.RowDefinitions.Add(new());
        var controls = new StackPanel();
        _report.Width = 70; _end.Width = 70; _report.Margin = _end.Margin = new(8, 0, 8, 0);
        controls.Children.Add(Ui.Row(Ui.Text("Report ID（十六进制）"), _report, Ui.Text("探测至"), _end));
        var readButtons = Ui.Row(Ui.Button("读取 Feature", () => _ = Execute(async () => LogReply(await Request(new("feature", Selected.Path, ReportId()))))),
            Ui.Button("探测 ID 范围", () => _ = Execute(Scan)), Ui.Button("被动监听 Input", () => _ = Execute(ListenInput)),
            Ui.Button("查看迈从状态", () => _ = Execute(async () => { RequireVendor(); LogReply(await Request(new("mchose-inspect", Selected.Path))); })));
        readButtons.Margin = new(0, 10, 0, 12); controls.Children.Add(readButtons);
        controls.Children.Add(Ui.Text("试发报文（包含 Report ID）", 12)); controls.Children.Add(_query); controls.Children.Add(_unlock);
        var sends = Ui.Row(Ui.Button("发送 Feature 并读取", () => _ = Execute(async () => { RequireUnlocked(); LogReply(await Request(new("send-feature", Selected.Path, ReportId(), _query.Text))); })),
            Ui.Button("发送 Output 并读取", () => _ = Execute(async () => { RequireUnlocked(); LogReply(await Request(new("send-output", Selected.Path, ReportId(), _query.Text))); })),
            Ui.Button("清空显示", () => _output.Clear())); sends.Margin = new(0, 10, 0, 12); controls.Children.Add(sends);
        operations.Children.Add(controls); Grid.SetRow(_output, 1); operations.Children.Add(_output);
        var reportTab = new TabItem { Header = "报文与探测", Content = operations }; reportTab.SetResourceReference(Control.BackgroundProperty, "Card"); reportTab.SetResourceReference(Control.ForegroundProperty, "Text"); tabs.Items.Add(reportTab);
        var calibrationPanel = new Grid { Margin = new(12) }; calibrationPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); calibrationPanel.RowDefinitions.Add(new()); calibrationPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var calibrationTop = new StackPanel();
        calibrationTop.Children.Add(Ui.Text("声明式标定：偏移从原始响应第0字节开始；XOR 从 XorStart 开始；Target：0=鼠标，1=键盘。先测试，再对照确认，最后保存。", 12, "Secondary"));
        var templateActions = Ui.Row(Ui.Button("所选接口生成模板", MakeTemplate), Ui.Button("载入已保存标定", () =>
        { try { var c = settings.Calibrations.FirstOrDefault(x => x.Matches(Selected)); if (c == null) throw new ArgumentException("此接口没有标定"); _calibration.Text = JsonSerializer.Serialize(c, Json.Options); } catch (Exception e) { Ui.Error(this, e.Message); } }));
        templateActions.Margin = new(0, 10, 0, 10); calibrationTop.Children.Add(templateActions); calibrationPanel.Children.Add(calibrationTop);
        Grid.SetRow(_calibration, 1); calibrationPanel.Children.Add(_calibration);
        var calibrationBottom = new StackPanel { Margin = new(0, 12, 0, 0) }; calibrationBottom.Children.Add(_confirm);
        var calibrationActions = Ui.Row(Ui.Button("测试标定", () => _ = Execute(TestCalibration)), Ui.Button("保存已验证标定", SaveCalibration),
            Ui.Button("移除此设备标定", () =>
            { try { var d = Selected; var old = settings.Calibrations.ToList(); settings.Calibrations.RemoveAll(x => x.Matches(d)); try { _save(); } catch { settings.Calibrations = old; throw; } Log("已移除所选接口标定"); } catch (Exception e) { Ui.Error(this, e.Message); } }));
        calibrationActions.Margin = new(0, 10, 0, 0); calibrationBottom.Children.Add(calibrationActions); Grid.SetRow(calibrationBottom, 2); calibrationPanel.Children.Add(calibrationBottom);
        var calibrationTab = new TabItem { Header = "手动协议标定", Content = calibrationPanel }; calibrationTab.SetResourceReference(Control.BackgroundProperty, "Card"); calibrationTab.SetResourceReference(Control.ForegroundProperty, "Text"); tabs.Items.Add(calibrationTab);
        Content = root;
        Closed += (_, _) => { _operation?.Cancel(); _coordinator.Diagnostic -= Log; };
        Loaded += (_, _) => _ = Execute(Enumerate); _coordinator.Diagnostic += Log;
    }
    async Task Execute(Func<Task> action)
    {
        if (_operation != null) { Log("已有操作运行，请先停止"); return; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); _operation = cts;
        _devices.IsEnabled = false;
        try { await _coordinator.DebugOperation(action, cts.Token); }
        catch (OperationCanceledException) { Log("操作已停止"); }
        catch (Exception e) { Log("错误：" + e.Message); }
        finally { _operation = null; _devices.IsEnabled = true; }
    }
    int ReportId() => Convert.ToInt32(_report.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16) is var id && id is >= 0 and <= 255 ? id : throw new ArgumentException("Report ID 必须为00–FF");
    Task<WorkerReply> Request(WorkerRequest r) => _worker.Run(r, _operation?.Token ?? _lifetime);
    async Task Enumerate()
    {
        var r = await _worker.Run(new("enumerate"), _operation!.Token, 12000);
        if (!r.Ok) { LogReply(r); return; }
        _devices.ItemsSource = r.Devices; _devices.SelectedIndex = 0;
        foreach (var d in r.Devices ?? []) Log($"{d.Display}  In={d.InputLength} Out={d.OutputLength} Feature={d.FeatureLength}{(d.Error != null ? "  " + d.Error : "")}\n{d.Path}");
    }
    async Task Scan()
    {
        int start = ReportId(), end = Convert.ToInt32(_end.Text, 16); var path = Selected.Path;
        if (end < start || end > 255) throw new ArgumentException("探测范围必须在00–FF内");
        for (int id = start; id <= end; id++)
        {
            _operation!.Token.ThrowIfCancellationRequested();
            var r = await Request(new("feature", path, id)); Log($"ID {id:X2}: {(r.Ok ? r.Hex : r.Error)}");
            await Task.Delay(100, _operation.Token);
        }
    }
    async Task ListenInput()
    {
        RequireVendor(); string path = Selected.Path; Log("开始被动监听（不会发送查询）；点击停止结束");
        while (true)
        {
            _operation!.Token.ThrowIfCancellationRequested(); var r = await Request(new("input", path));
            if (r.Ok) LogReply(r);
            else if (r.Error != "设备查询超时") { LogReply(r); if (r.Error != "查询超时") break; }
            await Task.Delay(100, _operation.Token);
        }
    }
    void RequireVendor() { if (Selected.UsagePage < 0xff00) throw new ArgumentException("试发/监听仅支持厂商接口，禁止对系统键鼠输入集合操作"); }
    void RequireUnlocked() { RequireVendor(); if (_unlock.IsChecked != true) throw new ArgumentException("请先勾选本次会话解锁"); }
    void MakeTemplate()
    {
        try
        {
            var d = Selected;
            var c = new Calibration { Vid = d.Vid, Pid = d.Pid, UsagePage = d.UsagePage, Usage = d.Usage, PathContains = d.Path,
                ResponseXor = 255, XorStart = 1, MatchHex = "11 06", MatchOffset = 0, BatteryOffset = 11, ChargingOffset = 12 };
            if (Hid.IsRog(d)) { c.Target = DeviceKind.Keyboard; c.Transport = "output"; c.ReportId = 0; c.QueryHex = "00 12 01"; c.ResponseXor = 0; c.MatchHex = "00 12 01"; c.BatteryOffset = 6; c.ChargingOffset = 9; }
            _calibration.Text = JsonSerializer.Serialize(c, Json.Options); _confirm.IsChecked = false; _testedCalibration = null;
        }
        catch (Exception e) { Ui.Error(this, e.Message); }
    }
    async Task TestCalibration()
    {
        RequireUnlocked();
        var c = JsonSerializer.Deserialize<Calibration>(_calibration.Text, Json.Options) ?? throw new ArgumentException("标定为空"); c.Validate();
        if (!c.Matches(Selected)) throw new ArgumentException("标定必须匹配所选接口");
        var r = await Request(new("calibration", Hex: JsonSerializer.Serialize(c)));
        LogReply(r); _testedCalibration = r.Ok ? _calibration.Text : null;
    }
    void SaveCalibration()
    {
        try
        {
            if (_testedCalibration != _calibration.Text || _confirm.IsChecked != true) throw new ArgumentException("先成功测试当前标定，并勾选已与官方软件核对");
            var c = JsonSerializer.Deserialize<Calibration>(_calibration.Text, Json.Options)!; c.Validate(); c.Confirmed = true;
            var old = _settings.Calibrations.ToList();
            _settings.Calibrations.RemoveAll(x => x.Target == c.Target); _settings.Calibrations.Add(c);
            try { _save(); } catch { _settings.Calibrations = old; throw; }
            Log("已保存已验证标定；关闭调试操作后可手动刷新");
        }
        catch (Exception e) { Ui.Error(this, e.Message); }
    }
    void LogReply(WorkerReply r)
    {
        Log(r.Ok ? "成功" + (r.Battery is { } b ? $" {b.Percent}% 充电={b.Charging} {b.Protocol}" : "") : "失败：" + r.Error);
        if (r.Hex != null) Log(string.Join(" ", Protocols.HexBytes(r.Hex).Select(b => b.ToString("X2"))));
    }
    public void Log(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(text)); return; }
        DebugConsole.Write(text); _output.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        if (_output.Text.Length > 100000) _output.Text = _output.Text[^80000..]; _output.ScrollToEnd();
    }
}
