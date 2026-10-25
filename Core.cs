using System.Text.Json;
using System.Text.RegularExpressions;

namespace PeripheralBattery;

public enum DeviceKind { Mouse, Keyboard }
public enum RefreshMode { Scheduled, Activity }
public record HidDevice(string Path, int Vid, int Pid, string Name, int UsagePage, int Usage,
    int InputLength, int OutputLength, int FeatureLength, string? Error = null)
{
    public string Id => $"{Vid:X4}:{Pid:X4}";
    public string Display => $"{Id}  {Name}  Usage {UsagePage:X4}:{Usage:X4}";
    public override string ToString() => Display;
}
public record BatteryValue(int Percent, bool Charging, string Protocol, string Path, int LogicalVid = 0, int LogicalPid = 0);
public record WorkerRequest(string Operation, string Path = "", int ReportId = 0, string Hex = "", int Length = 0, int TimeoutMs = 1000);
public record WorkerReply(bool Ok, string? Error = null, HidDevice[]? Devices = null, BatteryValue? Battery = null, string? Hex = null);

public sealed class DeviceState(DeviceKind kind)
{
    public DeviceKind Kind { get; } = kind;
    public string Name => Kind == DeviceKind.Mouse ? "迈从 K7 Ultra" : "ROG 游侠 RX";
    public BatteryValue? Current { get; private set; }
    public BatteryValue? LastGood { get; private set; }
    public BatteryValue? DisplayValue => Current ?? LastGood;
    public bool IsStale => Current == null && LastGood != null;
    public DateTimeOffset? Updated { get; private set; }
    public string Status { get; private set; } = "尚未读取";
    public bool Busy { get; set; }
    public string PercentText => DisplayValue is { } b ? $"{b.Percent}%" : "—";
    public string Tooltip => DisplayValue is { } b
        ? $"{Name}：{b.Percent}%（{Updated:MM-dd HH:mm:ss}）" + (IsStale ? $" · 上次读数；{Status}" : b.Charging ? " · 充电中" : "")
        : $"{Name}：{Status}";
    public void Apply(WorkerReply result, DateTimeOffset now)
    {
        Busy = false;
        if (result.Ok && result.Battery is { Percent: >= 0 and <= 100 } value)
        { Current = LastGood = value; Updated = now; Status = value.Charging ? "充电中" : "设备直读 · 2.4G"; }
        else { Current = null; Status = result.Error ?? "响应无法验证"; }
    }
    public void Disconnected() { Current = null; Status = "接收器已断开"; }
    public void RestoreCachedReading(BatteryValue value, DateTimeOffset updated)
    {
        if (value.Percent is < 0 or > 100 || updated == default) throw new ArgumentException("上次电量缓存无效");
        Current = null; LastGood = value; Updated = updated; Status = "等待刷新";
    }
}

public sealed class Calibration
{
    public DeviceKind Target { get; set; }
    public int Vid { get; set; }
    public int Pid { get; set; }
    public int UsagePage { get; set; }
    public int Usage { get; set; }
    public string PathContains { get; set; } = "";
    public string Transport { get; set; } = "feature";
    public int ReportId { get; set; } = 0x11;
    public string QueryHex { get; set; } = "11 F9 FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF";
    public int ResponseXor { get; set; }
    public int XorStart { get; set; } = 1;
    public string MatchHex { get; set; } = "";
    public int MatchOffset { get; set; }
    public int BatteryOffset { get; set; }
    public int BatteryBytes { get; set; } = 1;
    public bool BigEndian { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public int ChargingOffset { get; set; } = -1;
    public int ChargingMask { get; set; } = 1;
    public int ChargingValue { get; set; } = 1;
    public bool Confirmed { get; set; }
    public bool Matches(HidDevice d) => d.Vid == Vid && d.Pid == Pid && d.UsagePage == UsagePage && d.Usage == Usage
        && (PathContains.Length == 0 || d.Path.Contains(PathContains, StringComparison.OrdinalIgnoreCase));
    public void Validate()
    {
        if (!Enum.IsDefined(Target)) throw new ArgumentException("Target 必须为鼠标0或键盘1");
        if (Vid is <= 0 or > 65535 || Pid is <= 0 or > 65535 || UsagePage is < 0 or > 65535 || Usage is < 0 or > 65535)
            throw new ArgumentException("VID/PID/Usage 超出范围");
        if (Transport is not ("feature" or "output")) throw new ArgumentException("Transport 只允许 feature / output");
        if (ReportId is < 0 or > 255 || ResponseXor is < 0 or > 255 || XorStart is < 0 or > 4096 || MatchOffset is < 0 or > 4096
            || BatteryOffset is < 0 or > 4096 || BatteryBytes is < 1 or > 2 || ChargingOffset is < -1 or > 4096
            || ChargingMask is < 0 or > 255 || ChargingValue is < 0 or > 255
            || !double.IsFinite(Scale) || !double.IsFinite(Offset)) throw new ArgumentException("标定字段超出范围");
        var query = Protocols.HexBytes(QueryHex);
        if (query.Length == 0 || query[0] != ReportId) throw new ArgumentException("查询必须包含与 ReportId 一致的首字节");
        _ = Protocols.HexBytes(MatchHex);
        if (MatchHex.Trim().Length == 0) throw new ArgumentException("必须提供响应匹配，禁止仅凭任意字节显示电量");
        CommandSafety.Validate(query, Transport);
    }
}
public sealed class Settings
{
    public bool Configured { get; set; }
    public RefreshMode Mode { get; set; }
    public double IntervalMinutes { get; set; } = 1440;
    public bool AutoStart { get; set; } = true;
    public List<Calibration> Calibrations { get; set; } = [];
}

public static class Protocols
{
    public static byte[] HexBytes(string hex)
    {
        var cleaned = Regex.Replace(hex, @"[\s,:-]", "");
        if (cleaned.Length > 8192 || cleaned.Length % 2 != 0) throw new ArgumentException("十六进制长度必须为偶数，最多4096字节");
        return Convert.FromHexString(cleaned);
    }
    public static BatteryValue ParseRog(byte[] raw, string path)
    {
        var p = raw.AsSpan();
        if (p.Length > 2 && p[0] == 0 && p[1] == 0x12) p = p[1..];
        if (p.Length < 11 || p[0] != 0x12 || p[1] != 1 || p[5] > 100 || p[8] > 1)
            throw new InvalidDataException("ROG 电量响应无效");
        return new(p[5], p[8] == 1, "ROG HID 12/01", path);
    }
    public static BatteryValue ParseMchose(byte[] raw, string path, bool requireK7 = true)
    {
        if (raw.Length < 13 || raw[0] != 0x11) throw new InvalidDataException("迈从响应长度或 Report ID 错误");
        var p = raw.Skip(1).Select(b => (byte)(b ^ 0xff)).ToArray();
        int vid = p[1] | p[2] << 8, pid = p[3] | p[4] << 8;
        if (p[0] != 6 || vid == 0 || pid == 0 || p[10] > 100 || p[11] > 1)
            throw new InvalidDataException("迈从设备状态响应无效或鼠标未连接");
        // The K7 logical model must be established on the actual receiver; never accept A7/L7 as K7.
        if (requireK7 && (vid != 0x3837 || !K7LogicalPids.Contains(pid))) throw new InvalidDataException($"待确认迈从逻辑型号 {vid:X4}:{pid:X4}，请使用 --debug 标定");
        return new(p[10], p[11] == 1, $"MCHOSE HID 06 ({vid:X4}:{pid:X4})", path, vid, pid);
    }
    public static readonly HashSet<int> K7LogicalPids = [0x4150]; // Official M HUB model table 16720; confirmed on user's 3837:1001 receiver.
    public static BatteryValue ParseCalibration(byte[] raw, Calibration c, string path)
    {
        c.Validate();
        var data = raw.ToArray();
        for (int i = c.XorStart; i < data.Length; i++) data[i] ^= (byte)c.ResponseXor;
        var match = HexBytes(c.MatchHex);
        if (c.MatchOffset + match.Length > data.Length || !data.AsSpan(c.MatchOffset, match.Length).SequenceEqual(match))
            throw new InvalidDataException("标定响应不匹配");
        if (c.BatteryOffset + c.BatteryBytes > data.Length || c.ChargingOffset >= data.Length)
            throw new InvalidDataException("标定字段超出响应长度");
        int value = data[c.BatteryOffset];
        if (c.BatteryBytes == 2) value = c.BigEndian ? value << 8 | data[c.BatteryOffset + 1] : value | data[c.BatteryOffset + 1] << 8;
        double percent = value * c.Scale + c.Offset;
        if (!double.IsFinite(percent) || percent < 0 || percent > 100) throw new InvalidDataException("标定电量超出0–100%");
        bool charge = c.ChargingOffset >= 0 && (data[c.ChargingOffset] & c.ChargingMask) == c.ChargingValue;
        return new((int)Math.Round(percent), charge, "手动标定 · HID", path);
    }
}

public static class CommandSafety
{
    public static void Validate(byte[] bytes, string transport)
    {
        if (bytes.Length == 0) throw new ArgumentException("报文为空");
        // Known MCHOSE configuration writes are outside this battery tool's scope.
        if (transport == "feature" && bytes[0] is 0x11 or 0x12 && bytes.Length > 1)
        {
            int cmd = bytes[1] ^ 255;
            if (cmd is 0x02 or 0x0A or 0x0B or 0x2B or 0x40 or 0x41 or 0x42 or 0x43 or 0x58 or 0x52 or 0x55 or 0x57 or 0x2D)
                throw new ArgumentException("此命令涉及设备配置写入，电量工具禁止发送");
        }
    }
}

public sealed class RefreshClock
{
    public DateTimeOffset Next { get; private set; }
    public void Configure(DateTimeOffset now, double minutes) => Next = now.AddMinutes(Math.Clamp(minutes, 1, 525600));
    public bool TakeDue(DateTimeOffset now, double minutes)
    { if (now < Next) return false; Configure(now, minutes); return true; }
}
public sealed class ActivityGate
{
    private long _last = Environment.TickCount64;
    public bool Observe(long now) { long gap = now - _last; _last = now; return gap >= 300000; }
    public void Reset(long now) => _last = now;
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
