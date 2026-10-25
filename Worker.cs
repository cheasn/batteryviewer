using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PeripheralBattery;

internal sealed class WorkerClient : IDisposable
{
    readonly IntPtr _job;
    bool _disposed;
    public int LastProcessId { get; private set; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public WorkerClient()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        int size = IntPtr.Size == 8 ? 144 : 112;
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, ptr, size);
            Marshal.WriteInt32(ptr, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_job, 9, ptr, (uint)size)) { CloseHandle(_job); throw new System.ComponentModel.Win32Exception(); }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public async Task<WorkerReply> Run(WorkerRequest request, CancellationToken token = default, int deadlineMs = 4000)
    {
        if (_disposed) return new(false, "程序正在退出");
        using var p = new Process { StartInfo = new(Environment.ProcessPath!, "--hid-worker")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        try
        {
            p.Start(); LastProcessId = p.Id;
            // The worker cannot touch HID until it receives the request after successful job assignment.
            if (!AssignProcessToJobObject(_job, p.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(deadlineMs);
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            p.StandardInput.Close();
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return JsonSerializer.Deserialize<WorkerReply>(output) ?? new(false, "辅助进程未返回有效响应");
        }
        catch (OperationCanceledException) { return new(false, token.IsCancellationRequested ? "已取消" : "查询超时"); }
        catch (Exception ex) { return new(false, ex.Message); }
        finally { try { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } } catch { } }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; CloseHandle(_job); }
}

internal static class Worker
{
    public static async Task MainAsync()
    {
        WorkerReply result;
        try
        {
            string? line = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (line == null || line.Length > 65536) return;
            var r = JsonSerializer.Deserialize<WorkerRequest>(line) ?? throw new ArgumentException("缺少请求");
            result = r.Operation switch
            {
                "enumerate" => new(true, Devices: Hid.Enumerate()),
                "feature" => new(true, Hex: Convert.ToHexString(Hid.GetFeature(r.Path, r.ReportId, r.Length))),
                "send-feature" => SendFeature(r),
                "send-output" => new(true, Hex: Convert.ToHexString(await Hid.ExchangeOutput(r.Path, Protocols.HexBytes(r.Hex), r.TimeoutMs))),
                "input" => new(true, Hex: Convert.ToHexString(await Hid.ExchangeOutput(r.Path, null, r.TimeoutMs))),
                "rog" => new(true, Battery: Protocols.ParseRog(await Hid.QueryRog(r.Path), r.Path)),
                "mchose" => new(true, Battery: Protocols.ParseMchose(Hid.QueryMchose(r.Path), r.Path)),
                "mchose-inspect" => Inspect(r),
                "calibration" => await Calibrated(r),
                "test-hang" => Hang(),
                _ => new(false, "未知操作")
            };
        }
        catch (OperationCanceledException) { result = new(false, "设备查询超时"); }
        catch (Exception ex) { result = new(false, ex.Message); }
        Console.Write(JsonSerializer.Serialize(result));
    }
    static WorkerReply SendFeature(WorkerRequest r)
    {
        Hid.SetFeature(r.Path, Protocols.HexBytes(r.Hex)); Thread.Sleep(25);
        return new(true, Hex: Convert.ToHexString(Hid.GetFeature(r.Path, r.ReportId, r.Length)));
    }
    static WorkerReply Inspect(WorkerRequest r)
    {
        var bytes = Hid.QueryMchose(r.Path);
        try { return new(true, Battery: Protocols.ParseMchose(bytes, r.Path, false), Hex: Convert.ToHexString(bytes)); }
        catch (Exception e) { return new(false, e.Message, Hex: Convert.ToHexString(bytes)); }
    }
    static async Task<WorkerReply> Calibrated(WorkerRequest r)
    {
        var c = JsonSerializer.Deserialize<Calibration>(r.Hex, Json.Options) ?? throw new ArgumentException("缺少标定");
        c.Validate();
        var d = Hid.Enumerate().SingleOrDefault(c.Matches) ?? throw new IOException("标定设备不唯一或已断开");
        byte[] response;
        if (c.Transport == "feature") { Hid.SetFeature(d.Path, Protocols.HexBytes(c.QueryHex)); Thread.Sleep(25); response = Hid.GetFeature(d.Path, c.ReportId); }
        else response = await Hid.ExchangeOutput(d.Path, Protocols.HexBytes(c.QueryHex), 1000);
        return new(true, Battery: Protocols.ParseCalibration(response, c, d.Path), Hex: Convert.ToHexString(response));
    }
    static WorkerReply Hang() { Thread.Sleep(Timeout.Infinite); return new(false); }
}
