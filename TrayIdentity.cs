using System.Security.Cryptography;
using System.Text;

namespace PeripheralBattery;

internal static class TrayIdentity
{
    // Explorer binds a GUID to the full EXE path. Keep it stable for upgrades in
    // the same directory, but do not reuse it after moving an unsigned portable EXE.
    public static Guid ForExecutable(string executablePath, DeviceKind kind)
    {
        string path = Path.GetFullPath(executablePath).Replace('/', '\\').ToUpperInvariant();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"PeripheralBattery.Tray.v2|{path}|{kind}"))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
