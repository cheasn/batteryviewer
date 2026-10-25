using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery;

internal static class Hid
{
    const uint Share = 3, OpenExisting = 3, Overlapped = 0x40000000;
    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, int index, ref InterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr device);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vid, Pid, Version; }
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] data, int length);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, IntPtr caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] data, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputLength, IntPtr output, int outputLength, out int returned, ref OVERLAPPED overlap);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle handle, ref OVERLAPPED overlap, out int transferred, bool wait);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initial, string? name);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct OVERLAPPED { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }

    static SafeFileHandle Open(string path, bool write)
    {
        var h = CreateFileW(path, write ? 0xc0000000u : 0, Share, IntPtr.Zero, OpenExisting, write ? Overlapped : 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return h;
    }
    static HidDevice Describe(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int vid = match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : 0;
        int pid = match.Success ? Convert.ToInt32(match.Groups[2].Value, 16) : 0;
        try
        {
            using var handle = Open(path, false);
            var attr = new Attributes { Size = Marshal.SizeOf<Attributes>() };
            if (HidD_GetAttributes(handle, ref attr)) { vid = attr.Vid; pid = attr.Pid; }
            var nameBytes = new byte[254];
            string name = HidD_GetProductString(handle, nameBytes, nameBytes.Length) ? Encoding.Unicode.GetString(nameBytes).Split('\0')[0] : "HID 设备";
            if (!HidD_GetPreparsedData(handle, out var pp)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var caps = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(pp, caps) < 0) throw new IOException("无法读取 HID capabilities");
                return new(path, vid, pid, name, (ushort)Marshal.ReadInt16(caps, 2), (ushort)Marshal.ReadInt16(caps),
                    (ushort)Marshal.ReadInt16(caps, 4), (ushort)Marshal.ReadInt16(caps, 6), (ushort)Marshal.ReadInt16(caps, 8));
            }
            finally { Marshal.FreeHGlobal(caps); HidD_FreePreparsedData(pp); }
        }
        catch (Exception ex) { return new(path, vid, pid, "无法访问的 HID 接口", 0, 0, 0, 0, 0, ex.Message); }
    }
    public static HidDevice[] Enumerate()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<HidDevice>();
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                { if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error()); break; }
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required < 8 || required > 65536) continue;
                var ptr = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(ptr, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, ptr, required, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(ptr + 4);
                    if (path != null) list.Add(Describe(path));
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list.ToArray();
    }
    public static byte[] GetFeature(string path, int id, int length = 0)
    {
        using var h = Open(path, true);
        if (length == 0) length = Describe(path).FeatureLength;
        return GetFeature(h, id, length);
    }
    static byte[] GetFeature(SafeFileHandle h, int id, int length)
    {
        if (length is < 1 or > 4096) throw new IOException("此接口无 Feature Report 或长度不合法");
        var b = new byte[length]; b[0] = checked((byte)id);
        var pinned = GCHandle.Alloc(b, GCHandleType.Pinned); var overlap = new OVERLAPPED { Event = CreateEventW(IntPtr.Zero, false, false, null) };
        try
        {
            bool ok = DeviceIoControl(h, 0xB0192, pinned.AddrOfPinnedObject(), b.Length, pinned.AddrOfPinnedObject(), b.Length, out int n, ref overlap);
            if (!ok && Marshal.GetLastWin32Error() != 997) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetOverlappedResult(h, ref overlap, out n, true)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return b[..Math.Min(b.Length, n + (b[0] == 0 ? 1 : 0))];
        }
        finally { CloseHandle(overlap.Event); pinned.Free(); }
    }
    public static void SetFeature(string path, byte[] bytes)
    {
        CommandSafety.Validate(bytes, "feature");
        int length = Describe(path).FeatureLength;
        if (length < bytes.Length || length > 4096) throw new IOException("Feature Report 超出接口长度");
        var padded = new byte[length]; bytes.CopyTo(padded, 0);
        using var h = Open(path, true);
        if (!HidD_SetFeature(h, padded, padded.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static async Task<byte[]> ExchangeOutput(string path, byte[]? bytes, int timeout)
    {
        var device = Describe(path);
        if (device.InputLength is < 1 or > 4096) throw new IOException("接口无 Input Report");
        using var h = Open(path, true);
        using var stream = new FileStream(h, FileAccess.ReadWrite, 1, true);
        using var cts = new CancellationTokenSource(Math.Clamp(timeout, 50, 10000));
        if (bytes != null)
        {
            CommandSafety.Validate(bytes, "output");
            if (device.OutputLength < bytes.Length) throw new IOException("Output Report 超出接口长度");
            var padded = new byte[device.OutputLength]; bytes.CopyTo(padded, 0);
            await stream.WriteAsync(padded, cts.Token);
        }
        var input = new byte[device.InputLength];
        int n = await stream.ReadAsync(input, cts.Token);
        return input[..n];
    }
    public static async Task<byte[]> QueryRog(string path)
    {
        var device = Describe(path);
        using var h = Open(path, true);
        using var stream = new FileStream(h, FileAccess.ReadWrite, 1, true);
        using var cts = new CancellationTokenSource(1200);
        if (device.OutputLength < 3 || device.InputLength < 11) throw new IOException("ROG 接口 Report 长度不匹配");
        var query = new byte[device.OutputLength]; query[1] = 0x12; query[2] = 1;
        await stream.WriteAsync(query, cts.Token);
        while (true)
        {
            var response = new byte[device.InputLength];
            int n = await stream.ReadAsync(response, cts.Token);
            var raw = response[..n];
            if (n >= 3 && (raw[0] == 0x12 && raw[1] == 1 || raw[0] == 0 && raw[1] == 0x12 && raw[2] == 1)) return raw;
        }
    }
    public static byte[] QueryMchose(string path)
    {
        var query = Enumerable.Repeat((byte)255, 21).ToArray(); query[0] = 0x11; query[1] = 0xf9;
        int length = Describe(path).FeatureLength;
        if (length < query.Length) throw new IOException("接口不支持迈从状态查询");
        using var h = Open(path, true);
        var padded = new byte[length]; query.CopyTo(padded, 0);
        if (!HidD_SetFeature(h, padded, padded.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] response = [];
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(100);
            response = GetFeature(h, 0x11, length);
            if (response.Length >= 13 && response[0] == 0x11 && response[1] == 0xf9) break;
        }
        return response;
    }
    public static bool IsRog(HidDevice d) => d.Vid == 0x0b05 && d.Pid == 0x1a07 && d.UsagePage == 0xff00 && d.Usage == 1 && d.Path.Contains("mi_01", StringComparison.OrdinalIgnoreCase);
    public static bool IsMchose(HidDevice d) => d.Vid is 0x3837 or 0x5253 && d.UsagePage == 0xff01 && d.Usage == 1
        && d.Name.Contains("K7 Ultra", StringComparison.OrdinalIgnoreCase) && d.Path.Contains("mi_02", StringComparison.OrdinalIgnoreCase);
}
