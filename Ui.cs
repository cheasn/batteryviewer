using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace PeripheralBattery;

internal static class Theme
{
    public static bool Light { get; private set; }
    public static bool SystemLight { get; private set; }
    public static void Refresh(bool? previewLight = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        Light = previewLight ?? (int?)k?.GetValue("AppsUseLightTheme") != 0; SystemLight = (int?)k?.GetValue("SystemUsesLightTheme") != 0;
        var r = Application.Current.Resources;
        void Brush(string key, string light, string dark) => r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Light ? light : dark));
        Brush("Surface", "#F5F6F8", "#202226"); Brush("Card", "#FFFFFF", "#2B2D32");
        Brush("Text", "#20242B", "#F3F4F6"); Brush("Secondary", "#616875", "#B2B8C2");
        Brush("Line", "#DFE3E8", "#41454D"); Brush("Track", "#E7EBF0", "#424750");
        Brush("Accent", "#0078C8", "#54B7F0"); Brush("Warning", "#AD5100", "#FFAE62");
    }
    public static void Install()
    {
        Refresh();
        var r = Application.Current.Resources;
        var window = new Style(typeof(Window));
        window.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI")));
        window.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        window.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
        window.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        r[typeof(Window)] = window;
        foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(ComboBox) })
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Card")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Line")));
            if (type == typeof(Button))
            {
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border)); border.Name = "b";
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border;
                style.Setters.Add(new Setter(Control.TemplateProperty, template));
                var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                trigger.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Accent"))); style.Triggers.Add(trigger);
                var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45)); style.Triggers.Add(disabled);
            }
            r[type] = style;
        }
        var item = new Style(typeof(ComboBoxItem));
        item.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
        item.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Card")));
        r[typeof(ComboBoxItem)] = item;
    }
}

internal static class Ui
{
    public static TextBlock Text(string text, double size = 13, string brush = "Text")
    { var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, brush); return t; }
    public static Button Button(string text, Action click)
    { var b = new Button { Content = text, MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) }; b.Click += (_, _) => click(); return b; }
    public static Border Surface(UIElement content, string brush = "Card", double padding = 16)
    { var b = new Border { Child = content, Padding = new(padding), CornerRadius = new(10), BorderThickness = new(1) }; b.SetResourceReference(Border.BackgroundProperty, brush); b.SetResourceReference(Border.BorderBrushProperty, "Line"); return b; }
    public static StackPanel Row(params UIElement[] children) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) p.Children.Add(c); return p; }
    public static TextBox Input(string value = "") => new() { Text = value, Margin = new Thickness(0, 4, 0, 12) };
    public static void Prepare(Window window) => window.Style = (Style)Application.Current.Resources[typeof(Window)];
    public static void Error(Window owner, string text) => MessageBox.Show(owner, text, "无线外设电量", MessageBoxButton.OK, MessageBoxImage.Warning);
    public static System.Windows.Shapes.Path DeviceIcon(DeviceKind kind)
    {
        var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(kind == DeviceKind.Mouse
            ? "M12,1 C7,1 5,4 5,9 L5,17 C5,22 8,25 12,25 C16,25 19,22 19,17 L19,9 C19,4 17,1 12,1 M12,2 L12,10 M5,10 L19,10"
            : "M1,6 L25,6 L25,22 L1,22 Z M5,10 L7,10 M10,10 L12,10 M15,10 L17,10 M20,10 L22,10 M5,14 L7,14 M10,14 L12,14 M15,14 L17,14 M20,14 L22,14 M8,18 L19,18"),
            Width = 24, Height = 25, Stretch = Stretch.Uniform, StrokeThickness = 1.7, Margin = new(0, 0, 10, 0) };
        p.SetResourceReference(Shape.StrokeProperty, "Secondary"); return p;
    }
}

internal sealed class BatteryCard : Border
{
    readonly TextBlock _percent, _source, _time, _status;
    readonly Border _fill;
    readonly Border _track;
    public BatteryCard(DeviceKind kind)
    {
        Padding = new(16); CornerRadius = new(10); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "Card"); SetResourceReference(BorderBrushProperty, "Line");
        var stack = new StackPanel();
        var name = Ui.Text(kind == DeviceKind.Mouse ? "迈从 K7 Ultra" : "ROG 游侠 RX", 14); name.FontWeight = FontWeights.SemiBold; name.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(Ui.Row(Ui.DeviceIcon(kind), name));
        var number = new Grid { Margin = new(0, 9, 0, 10) };
        _percent = Ui.Text("—", 38); _percent.FontWeight = FontWeights.SemiBold;
        _status = Ui.Text("", 12, "Secondary"); _status.HorizontalAlignment = HorizontalAlignment.Right; _status.VerticalAlignment = VerticalAlignment.Bottom; _status.Margin = new(0, 0, 0, 8);
        number.Children.Add(_percent); number.Children.Add(_status); stack.Children.Add(number);
        _track = new Border { Height = 5, CornerRadius = new(2.5), HorizontalAlignment = HorizontalAlignment.Stretch };
        _track.SetResourceReference(BackgroundProperty, "Track");
        _fill = new Border { Height = 5, CornerRadius = new(2.5), HorizontalAlignment = HorizontalAlignment.Left };
        _fill.SetResourceReference(BackgroundProperty, "Accent"); _track.Child = _fill; _track.SizeChanged += (_, _) => ResizeFill(); stack.Children.Add(_track);
        var bottom = new Grid { Margin = new(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new()); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _source = Ui.Text("尚未读取", 11, "Secondary"); _source.TextWrapping = TextWrapping.NoWrap; _source.TextTrimming = TextTrimming.CharacterEllipsis;
        _time = Ui.Text("", 11, "Secondary"); _time.Margin = new(8, 0, 0, 0); Grid.SetColumn(_time, 1);
        bottom.Children.Add(_source); bottom.Children.Add(_time); stack.Children.Add(bottom); Child = stack;
    }
    int _value;
    void ResizeFill() => _fill.Width = Math.Max(0, _track.ActualWidth * _value / 100.0);
    public void Update(DeviceState state)
    {
        _percent.Text = state.PercentText; _value = state.DisplayValue?.Percent ?? 0; ResizeFill();
        _fill.SetResourceReference(BackgroundProperty, state.DisplayValue is { Percent: <= 20 } ? "Warning" : "Accent");
        _source.Text = state.Current != null ? "设备直读 · 2.4G" : state.Status;
        if (state.Current?.Protocol.StartsWith("手动标定") == true) _source.Text = "手动标定 · HID";
        _time.Text = state.DisplayValue != null ? state.Updated?.ToString("MM-dd HH:mm") ?? "" : "";
        _status.Text = state.IsStale ? state.Busy ? "上次读数 · 更新中" : "上次读数" : state.Busy ? "更新中" : state.Current?.Charging == true ? "充电中" : "";
        ToolTip = state.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(this, state.Tooltip);
    }
}

internal sealed class PanelWindow : Window
{
    readonly BatteryCard _mouse = new(DeviceKind.Mouse), _keyboard = new(DeviceKind.Keyboard);
    bool _dragging;
    public bool Exiting;
    public PanelWindow(Action settings, bool testSession)
    {
        Ui.Prepare(this);
        Title = "无线外设电量"; Width = 340; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        var stack = new StackPanel();
        var header = new Grid { Margin = new(0, 0, 0, 14), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll, ToolTip = "拖住标题栏移动面板" };
        var title = Ui.Text(testSession ? "无线外设电量 · 测试" : "无线外设电量", 14); title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        var buttons = Ui.Row(Ui.Button("⚙", settings), Ui.Button("×", Hide)); buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Cursor = System.Windows.Input.Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName((DependencyObject)buttons.Children[0], "设置");
        System.Windows.Automation.AutomationProperties.SetName((DependencyObject)buttons.Children[1], "关闭面板");
        header.Children.Add(title); header.Children.Add(buttons); stack.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            for (DependencyObject? element = e.OriginalSource as DependencyObject; element != null; element = element is Visual ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent)
                if (element is ButtonBase) return;
            e.Handled = true; _dragging = true;
            try { DragMove(); } catch (InvalidOperationException) { /* Mouse may have been released before entering the native move loop. */ }
            finally { _dragging = false; }
        };
        _mouse.Margin = new(0, 0, 0, 12); stack.Children.Add(_mouse); stack.Children.Add(_keyboard);
        Content = Ui.Surface(stack, "Surface", 16);
        Deactivated += (_, _) => { if (!_dragging) Hide(); }; Closing += (_, e) => { if (!Exiting) { e.Cancel = true; Hide(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }
    public void Update(Coordinator c) { _mouse.Update(c.Mouse); _keyboard.Update(c.Keyboard); }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    public void OpenNear(Rect icon)
    {
        var r = new RECT { L = (int)icon.Left, T = (int)icon.Top, R = (int)icon.Right, B = (int)icon.Bottom };
        var monitor = MonitorFromRect(ref r, 2); var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() }; GetMonitorInfoW(monitor, ref mi);
        GetDpiForMonitor(monitor, 0, out uint dpi, out _); if (dpi == 0) dpi = 96;
        Opacity = 0; Show(); UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth * dpi / 96), height = (int)Math.Ceiling(ActualHeight * dpi / 96), gap = (int)(8 * dpi / 96);
        int x = Math.Clamp(r.R - width, mi.Work.L + gap, Math.Max(mi.Work.L + gap, mi.Work.R - width - gap));
        int y = r.T >= mi.Work.B ? mi.Work.B - height - gap : r.B <= mi.Work.T ? mi.Work.T + gap : Math.Clamp(r.B - height, mi.Work.T + gap, Math.Max(mi.Work.T + gap, mi.Work.B - height - gap));
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x0040); Opacity = 1; Activate(); SetForegroundWindow(hwnd);
    }
}

internal sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings settings, bool first, bool debug, Action<Settings> save, Action exit, Action openDebug, string? cacheError = null)
    {
        Ui.Prepare(this);
        Title = first ? "首次设置 · 无线外设电量" : "设置 · 无线外设电量";
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var p = new StackPanel { Margin = new(24) };
        var heading = Ui.Text(first ? "选择电量查询方式" : "刷新设置", 20); heading.Margin = new(0, 0, 0, 18); p.Children.Add(heading);
        var scheduled = new RadioButton { Content = "定时刷新", GroupName = "mode", IsChecked = settings.Mode == RefreshMode.Scheduled, Margin = new(0, 0, 0, 10) };
        var activity = new RadioButton { Content = "鼠标恢复活动／查看时刷新", GroupName = "mode", IsChecked = settings.Mode == RefreshMode.Activity, Margin = new(0, 14, 0, 8) };
        p.Children.Add(scheduled);
        int unitIndex = settings.IntervalMinutes % 1440 == 0 ? 2 : settings.IntervalMinutes % 60 == 0 ? 1 : 0;
        double[] factors = [1, 60, 1440];
        var interval = Ui.Input((settings.IntervalMinutes / factors[unitIndex]).ToString(CultureInfo.InvariantCulture)); interval.Width = 100; interval.Margin = new(0, 0, 8, 0);
        var unit = new ComboBox { ItemsSource = new[] { "分钟", "小时", "天" }, SelectedIndex = unitIndex, Width = 90 };
        var row = Ui.Row(interval, unit); row.Margin = new(20, 0, 0, 0); p.Children.Add(row);
        p.Children.Add(activity);
        p.Children.Add(Ui.Text("鼠标静置5分钟后的首次活动近似视为唤醒，只刷新鼠标。键盘在启动和查看时刷新；不记录鼠标轨迹。", 12, "Secondary"));
        void Toggle() => row.IsEnabled = scheduled.IsChecked == true;
        scheduled.Checked += (_, _) => Toggle(); activity.Checked += (_, _) => Toggle(); Toggle();
        var auto = new CheckBox { Content = "登录 Windows 后自动启动（不展开面板）", IsChecked = settings.AutoStart, Margin = new(0, 22, 0, 12) }; p.Children.Add(auto);
        var hint = Ui.Text("右下角通知区域有鼠标、键盘两个绿色圆环电量图标，若在“∧”内可拖出来常显。左键刷新对应设备，右键打开面板。退出程序请使用下方按钮。", 12, "Secondary");
        p.Children.Add(hint);
        if (ConfigStore.LoadError != null) { var error = Ui.Text(ConfigStore.LoadError, 12, "Warning"); error.Margin = new(0, 10, 0, 0); p.Children.Add(error); }
        if (cacheError != null) { var error = Ui.Text(cacheError, 12, "Warning"); error.Margin = new(0, 10, 0, 0); p.Children.Add(error); }
        var actions = Ui.Row(); actions.Margin = new(0, 24, 0, 0);
        actions.Children.Add(Ui.Button(first ? "保存并开始" : "保存设置", () =>
        {
            try
            {
                double minutes = settings.IntervalMinutes;
                if (scheduled.IsChecked == true)
                {
                    if (!double.TryParse(interval.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)
                        || value * factors[Math.Max(0, unit.SelectedIndex)] is < 1 or > 525600) throw new ArgumentException("刷新间隔需介于1分钟和365天之间");
                    minutes = value * factors[unit.SelectedIndex];
                }
                var next = new Settings { Configured = true, AutoStart = auto.IsChecked == true, Mode = activity.IsChecked == true ? RefreshMode.Activity : RefreshMode.Scheduled,
                    IntervalMinutes = minutes, Calibrations = settings.Calibrations };
                save(next); Close();
            }
            catch (Exception e) { Ui.Error(this, e.Message); }
        }));
        if (debug) actions.Children.Add(Ui.Button("调试", () => { Close(); openDebug(); }));
        actions.Children.Add(Ui.Button("退出程序", exit)); p.Children.Add(actions);
        Content = p;
    }
}
