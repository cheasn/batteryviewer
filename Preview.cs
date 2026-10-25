using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeripheralBattery;

internal static class Preview
{
    public static int NotificationSamples(string directory)
    {
        DebugConsole.Enable(); Directory.CreateDirectory(directory);
        var app = new Application(); Theme.Install(); var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 620, 326));
            void Text(string value, double x, double y, Brush brush, double size = 12) => dc.DrawText(new FormattedText(value,
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), size, brush, 1), new Point(x, y));
            Text("右下角通知区圆环图标 · 示例值（每格16px和24px）", 16, 12, Brushes.Black, 15);
            int?[] percentages = [100, 75, 20, 0, null];
            for (int i = 0; i < percentages.Length; i++) Text(percentages[i]?.ToString() + (percentages[i] == null ? "未知" : "%"), 153 + i * 90, 42, Brushes.Black);
            for (int row = 0; row < 4; row++)
            {
                bool light = row < 2; var kind = row % 2 == 0 ? DeviceKind.Mouse : DeviceKind.Keyboard; double top = 66 + row * 64;
                dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(239, 242, 245) : Color.FromRgb(34, 39, 46)), null, new Rect(0, top, 620, 64));
                Text((light ? "浅色 · " : "深色 · ") + (kind == DeviceKind.Mouse ? "鼠标" : "键盘"), 16, top + 23, light ? Brushes.Black : Brushes.White);
                for (int i = 0; i < percentages.Length; i++)
                {
                    dc.DrawImage(DeviceRingIcons.Render(kind, percentages[i], light, 16), new Rect(148 + i * 90, top + 24, 16, 16));
                    dc.DrawImage(DeviceRingIcons.Render(kind, percentages[i], light, 24), new Rect(180 + i * 90, top + 20, 24, 24));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(620, 326, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, "notification-ring-preview.png"); using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine(path); app.Shutdown(); return 0;
    }
    // Developer-only layout verification. Values come from actual HID queries, never mock application readings.
    public static int Render(string directory)
    {
        DebugConsole.Enable();
        var app = new Application(); Theme.Install();
        using var worker = new WorkerClient();
        var coordinator = new Coordinator(worker, new Settings(), CancellationToken.None);
        coordinator.Enumerate().GetAwaiter().GetResult(); coordinator.RefreshAll().GetAwaiter().GetResult();
        Directory.CreateDirectory(directory);
        foreach (bool light in new[] { true, false })
        {
            Theme.Refresh(light);
            var panel = new PanelWindow(() => { }, false); panel.Update(coordinator);
            var root = (FrameworkElement)panel.Content;
            root.Measure(new Size(340, double.PositiveInfinity)); root.Arrange(new Rect(0, 0, 340, root.DesiredSize.Height)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(340, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string name = Path.Combine(directory, light ? "panel-light.png" : "panel-dark.png");
            using (var file = File.Create(name)) encoder.Save(file);
            Console.WriteLine($"{name} {bitmap.PixelWidth}x{bitmap.PixelHeight}; mouse={coordinator.Mouse.PercentText}; keyboard={coordinator.Keyboard.PercentText}");
        }
        app.Shutdown(); return 0;
    }
}
