using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeripheralBattery;

internal sealed class BatteryCache
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBattery", "battery-cache.json");
    readonly string _path;
    readonly Settings _settings;
    readonly Dictionary<DeviceKind, CachedReading> _readings = [];
    public string? LastError { get; private set; }
    public sealed record CachedReading(DeviceKind Kind, string Binding, BatteryValue Value, DateTimeOffset Updated);
    public sealed record CacheDocument(int Version, List<CachedReading> Readings);

    public BatteryCache(Settings settings, string? path = null)
    {
        _settings = settings; _path = path ?? DefaultPath;
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 65536) throw new InvalidDataException("电量缓存文件过大");
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(_path), Json.Options);
            if (document?.Version != 1 || document.Readings == null) throw new InvalidDataException("电量缓存格式不支持");
            foreach (var reading in document.Readings)
                if (reading != null && Enum.IsDefined(reading.Kind) && reading.Value is { Percent: >= 0 and <= 100 }
                    && reading.Updated != default && reading.Binding == Binding(reading.Kind))
                    _readings[reading.Kind] = reading;
        }
        catch (Exception ex) { LastError = "无法读取上次电量：" + ex.Message; }
    }
    string Binding(DeviceKind kind)
    {
        var calibration = _settings.Calibrations.FirstOrDefault(c => c.Confirmed && c.Target == kind);
        return calibration == null ? $"builtin:v1:{kind}" : "calibrated:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(calibration))));
    }
    public bool Restore(DeviceState state)
    {
        if (!_readings.TryGetValue(state.Kind, out var reading) || reading.Binding != Binding(state.Kind)) return false;
        state.RestoreCachedReading(reading.Value, reading.Updated); return true;
    }
    public bool SaveSuccessful(DeviceState state)
    {
        // Failures and disconnects must not overwrite a successful reading or its timestamp.
        if (state.Current is not { Percent: >= 0 and <= 100 } value || state.Updated is not { } updated) return false;
        string temporary = _path + ".tmp";
        try
        {
            _readings[state.Kind] = new(state.Kind, Binding(state.Kind), value, updated);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var current = _readings.Values.Where(r => r.Binding == Binding(r.Kind)).OrderBy(r => r.Kind).ToList();
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new CacheDocument(1, current), Json.Options);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(data); file.Flush(true); }
            File.Move(temporary, _path, true); LastError = null; return true;
        }
        catch (Exception ex) { LastError = "无法保存上次电量：" + ex.Message; return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
