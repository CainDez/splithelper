using System.ServiceProcess;

namespace SplitHelper.Services;

public sealed class ClientEnvironment
{
    public string? ClashExe { get; }
    public string? MullvadCli { get; }
    public string? WindscribeExe { get; }
    public string? WindscribeCli { get; }
    public string ClashDirectory { get; }
    public string ClashHome { get; }
    public string? ClashVersion => ClashExe is null ? null : FileVersionInfo.GetVersionInfo(ClashExe).ProductVersion;
    public bool SupportsImport => Version.TryParse(ClashVersion?.Split('+')[0].Split('-')[0], out var version) && version.Major == 2 && version.Minor == 4 && version.Build == 7;
    public ClientEnvironment(string? clashHome = null, string? clashExe = null)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ClashExe = clashExe ?? Existing(Path.Combine(programs, "Clash Verge", "clash-verge.exe"), Path.Combine(local, "Programs", "Clash Verge", "clash-verge.exe"));
        if (ClashExe is null)
        {
            foreach (var process in Process.GetProcessesByName("clash-verge"))
            {
                using (process) { try { if (process.MainModule?.FileName is { } path) { ClashExe = path; break; } } catch (System.ComponentModel.Win32Exception) { } }
            }
        }
        ClashDirectory = Path.GetDirectoryName(ClashExe) ?? "";
        var portable = Path.Combine(ClashDirectory, ".config");
        ClashHome = clashHome ?? (ClashDirectory.Length > 0 && File.Exists(Path.Combine(portable, "PORTABLE")) ? Path.Combine(portable, "io.github.clash-verge-rev.clash-verge-rev") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.clash-verge-rev.clash-verge-rev"));
        MullvadCli = Existing(Path.Combine(programs, "Mullvad VPN", "resources", "mullvad.exe"));
        WindscribeExe = Existing(Path.Combine(programs, "Windscribe", "Windscribe.exe"));
        WindscribeCli = Existing(Path.Combine(programs, "Windscribe", "windscribe-cli.exe"));
    }
    private static string? Existing(params string[] paths) => paths.FirstOrDefault(File.Exists);
    public static bool ClashIsRunning
    {
        get { var processes = Process.GetProcessesByName("clash-verge"); var running = processes.Length > 0; foreach (var process in processes) process.Dispose(); return running; }
    }
    public static bool ServiceRunning
    {
        get { try { using var service = new ServiceController("clash_verge_service"); return service.Status == ServiceControllerStatus.Running; } catch (InvalidOperationException) { return false; } }
    }
    public void OpenClash() => Launch(ClashExe ?? throw new UserError("未找到 Clash Verge Rev，请先安装官方客户端。"));
    public void OpenProvider(string provider) => Launch(provider == "Mullvad" ? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mullvad VPN", "Mullvad VPN.exe")) ?? throw new UserError("未找到 Mullvad 官方客户端。") : WindscribeExe ?? throw new UserError("未找到 Windscribe 官方客户端。"));
    public void InstallClashService()
    {
        var installer = Path.Combine(ClashDirectory, "resources", "clash-verge-service-install.exe");
        if (!File.Exists(installer)) throw new UserError("未找到官方 Clash 服务安装器，请在 Clash 设置页安装服务模式。");
        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" });
    }
    public static void Launch(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    public static async Task<(int ExitCode, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, int timeoutSeconds = 12)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new UserError("无法启动官方客户端命令。");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); return (process.ExitCode, (await output) + "\n" + (await error)); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } cancellationToken.ThrowIfCancellationRequested(); throw new UserError("官方客户端查询超时，请确认客户端已打开。"); }
    }
}
