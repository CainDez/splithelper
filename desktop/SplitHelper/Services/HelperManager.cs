using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SplitHelper.Services;

public sealed record HelperInstance(int Pid, string Path, DateTime StartTime);
public sealed record HelperManifest(string Version, string Sha256, string ConfigSha256 = "");
public sealed class HelperManager(AppPaths paths) : IDisposable
{
    private Process? _launchedProcess;
    public HelperManifest? Manifest => File.Exists(paths.HelperManifest) ? JsonSerializer.Deserialize<HelperManifest>(File.ReadAllText(paths.HelperManifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var manifest = Manifest;
        if (manifest is null || !File.Exists(paths.HelperExe) || !File.Exists(paths.HelperConfig)) throw new UserError("内置直连组件不完整，请使用正式安装包修复安装，无需单独下载 helper。");
        using var stream = File.OpenRead(paths.HelperExe);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UserError("内置直连组件校验失败，请修复安装。");
        using var configStream = File.OpenRead(paths.HelperConfig);
        var configHash = Convert.ToHexString(await SHA256.HashDataAsync(configStream, cancellationToken));
        if (!string.Equals(configHash, manifest.ConfigSha256, StringComparison.OrdinalIgnoreCase)) throw new UserError("内置组件配置校验失败，请修复安装。");
    }
    public IReadOnlyList<HelperInstance> Instances()
    {
        var result = new List<HelperInstance>();
        foreach (var process in Process.GetProcessesByName("mullvad-direct-helper"))
        {
            using (process)
            {
                try { result.Add(new HelperInstance(process.Id, process.MainModule?.FileName ?? "", process.StartTime.ToUniversalTime())); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { result.Add(new HelperInstance(process.Id, "", DateTime.MinValue)); }
            }
        }
        return result;
    }
    public bool OwnListenerReady => Instances().Any(instance => IsOwn(instance) && TcpOwners(18997).Contains(instance.Pid));
    public bool IsOwn(HelperInstance instance) => string.Equals(instance.Path, paths.HelperExe, StringComparison.OrdinalIgnoreCase);
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        await VerifyAsync(cancellationToken);
        var instances = Instances();
        if (instances.Any(instance => !IsOwn(instance))) throw new UserError("检测到其他目录的直连组件；请先在诊断页“迁移旧版组件”，本次未启动第二个组件。");
        if (instances.Count > 0)
        {
            if (instances.Count != 1 || !OwnListenerReady) throw new UserError("内置组件运行状态不一致，请先停止本助手组件再重试。");
            return false;
        }
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 18997)) throw new UserError("端口 18997 被其他程序占用；本助手没有结束该进程，请查看诊断。");
        Directory.CreateDirectory(paths.RuntimeDirectory);
        var start = new ProcessStartInfo(paths.HelperExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.HelperDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(paths.RuntimeDirectory); start.ArgumentList.Add("-f"); start.ArgumentList.Add(paths.HelperConfig);
        var process = Process.Start(start) ?? throw new UserError("无法启动内置直连组件。");
        var identity = new HelperInstance(process.Id, paths.HelperExe, process.StartTime.ToUniversalTime());
        // Drain output without retaining potentially sensitive destination logs.
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new UserError("内置直连组件启动失败；请检查端口占用或修复安装。");
                if (TcpOwners(18997).Contains(process.Id)) { await SocksGreetingAsync(18997, cancellationToken); _launchedProcess?.Dispose(); _launchedProcess = process; return true; }
                await Task.Delay(100, cancellationToken);
            }
            throw new UserError("内置直连组件未就绪，已停止本次启动的进程。");
        }
        catch { StopIdentity(identity); process.Dispose(); throw; }
    }
    public void StopOwn()
    {
        foreach (var instance in Instances().Where(IsOwn)) StopIdentity(instance);
        _launchedProcess?.Dispose(); _launchedProcess = null;
    }
    public static void StopIdentity(HelperInstance instance)
    {
        try
        {
            using var process = Process.GetProcessById(instance.Pid);
            if (process.StartTime.ToUniversalTime() != instance.StartTime || !string.Equals(process.MainModule?.FileName, instance.Path, StringComparison.OrdinalIgnoreCase)) throw new UserError("组件进程身份已改变，本次没有停止它。");
            process.Kill();
            if (!process.WaitForExit(5000)) throw new UserError("组件进程未能停止。");
        }
        catch (ArgumentException) { }
    }
    public static async Task SocksGreetingAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(2500);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply, timeout.Token);
        if (reply[0] != 5 || reply[1] != 0) throw new UserError("内置组件的 SOCKS 接口未正确就绪。");
    }
    public static IReadOnlyList<int> TcpOwners(int port)
    {
        uint size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 3, 0);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var error = GetExtendedTcpTable(buffer, ref size, true, 2, 3, 0);
            if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
            var count = Marshal.ReadInt32(buffer); var result = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var raw = (uint)Marshal.ReadInt32(row, 8); var number = ((raw & 255) << 8) | ((raw >> 8) & 255);
                if (number == port) result.Add(Marshal.ReadInt32(row, 20));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    public void Dispose() => _launchedProcess?.Dispose();
}
