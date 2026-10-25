using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeripheralBattery;

internal static class DeviceRingIcons
{
    public static readonly Color Green = Color.FromRgb(30, 200, 90);
    public static BitmapSource Render(DeviceKind kind, int? percent, bool light, int size = 64)
    {
        if (percent is < 0 or > 100 || size is < 16 or > 256) throw new ArgumentException("图标参数无效");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
            var track = new Pen(new SolidColorBrush(light ? Color.FromRgb(183, 189, 197) : Color.FromRgb(99, 109, 121)), 7);
            var green = new Pen(new SolidColorBrush(Green), 7) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            dc.DrawEllipse(null, track, new Point(32, 32), 27, 27);
            if (percent == 100) dc.DrawEllipse(null, green, new Point(32, 32), 27, 27);
            else if (percent is > 0)
            {
                double angle = percent.Value * Math.PI * 2 / 100.0;
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(new Point(32, 5), false, false);
                    g.ArcTo(new Point(32 + 27 * Math.Sin(angle), 32 - 27 * Math.Cos(angle)), new Size(27, 27), 0,
                        percent > 50, SweepDirection.Clockwise, true, false);
                }
                arc.Freeze(); dc.DrawGeometry(null, green, arc);
            }
            var glyph = new SolidColorBrush(percent == null ? Color.FromRgb(139, 147, 157) : light ? Color.FromRgb(30, 37, 45) : Color.FromRgb(244, 247, 250));
            var pen = new Pen(glyph, 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (kind == DeviceKind.Mouse)
            {
                dc.DrawRoundedRectangle(null, pen, new Rect(24, 16, 16, 32), 8, 8);
                dc.DrawLine(pen, new Point(32, 18), new Point(32, 26));
                dc.DrawLine(pen, new Point(25, 28), new Point(39, 28));
            }
            else
            {
                dc.DrawRoundedRectangle(null, pen, new Rect(16, 22, 32, 20), 2, 2);
                for (int row = 0; row < 2; row++) for (int col = 0; col < 4; col++)
                    dc.DrawRectangle(glyph, null, new Rect(20 + col * 6.4, 26 + row * 5, 3.2, 2.4));
                dc.DrawRoundedRectangle(glyph, null, new Rect(25, 37, 14, 2), 1, 1);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    public static IntPtr Native(BitmapSource bitmap)
    {
        int size = bitmap.PixelWidth; var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0);
        var info = new BitmapInfo { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        IntPtr mask = IntPtr.Zero;
        try
        {
            if (color == IntPtr.Zero || bits == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            // Initialize the AND mask explicitly; a null CreateBitmap buffer is unspecified.
            var maskBits = new byte[((size + 15) / 16 * 2) * size];
            mask = CreateBitmap(size, size, 1, 1, maskBits);
            if (mask == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var icon = new IconInfo { Icon = true, Mask = mask, Color = color };
            IntPtr handle = CreateIconIndirect(ref icon);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            return handle;
        }
        finally { if (color != IntPtr.Zero) DeleteObject(color); if (mask != IntPtr.Zero) DeleteObject(mask); }
    }
    public static void Destroy(IntPtr icon) { if (icon != IntPtr.Zero) DestroyIcon(icon); }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint X, Y; public IntPtr Mask, Color; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int width, int height, uint planes, uint count, byte[] bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref IconInfo icon);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
