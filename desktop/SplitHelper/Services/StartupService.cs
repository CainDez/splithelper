using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace SplitHelper.Services;

public sealed class StartupService(AppPaths paths)
{
    private const string FileName = "SplitHelper.lnk";
    private const string Description = "SplitHelper Desktop current-user login";
    private string Entry => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), FileName);
    public bool WindowsDisabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
            return key?.GetValue(FileName) is byte[] { Length: > 0 } bytes && bytes[0] == 3;
        }
    }
    public bool IsEnabled => File.Exists(Entry) && !WindowsDisabled && IsOwned(Entry);
    public string Status => WindowsDisabled ? "Windows 已禁用此启动项，请在“启动应用”中启用" : IsEnabled ? "已注册当前用户登录启动" : "未启用登录启动";
    public void SetEnabled(bool enabled)
    {
        if (File.Exists(Entry) && !IsOwned(Entry)) throw new UserError("同名启动项不属于本助手，已保留，请在 Windows 启动应用中检查。");
        if (!enabled) { if (File.Exists(Entry)) File.Delete(Entry); return; }
        if (WindowsDisabled) throw new UserError("Windows 已禁用分流助手启动项，请在“启动应用”里明确启用，助手不会覆盖禁用记录。");
        var executable = Path.Combine(paths.BaseDirectory, "SplitHelper.exe");
        if (!File.Exists(executable)) throw new UserError("应用程序位置不完整，无法注册启动项。");
        object? shell = null; object? link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            link = ((dynamic)shell!).CreateShortcut(Entry);
            dynamic shortcut = link;
            shortcut.TargetPath = executable; shortcut.Arguments = "--background"; shortcut.WorkingDirectory = paths.BaseDirectory; shortcut.Description = Description; shortcut.IconLocation = executable + ",0"; shortcut.Save();
        }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        if (!IsOwned(Entry)) throw new UserError("启动项写入后的校验未通过。");
    }
    private bool IsOwned(string entry)
    {
        object? shell = null; object? link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(entry);
            dynamic shortcut = link;
            return string.Equals((string)shortcut.Description, Description, StringComparison.Ordinal) && string.Equals((string)shortcut.TargetPath, Path.Combine(paths.BaseDirectory, "SplitHelper.exe"), StringComparison.OrdinalIgnoreCase) && (string)shortcut.Arguments == "--background";
        }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }
    public void RemoveLegacyEntry(string legacyDirectory)
    {
        var entry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Mullvad Domestic Direct Helper.lnk");
        if (!File.Exists(entry)) return;
        object? shell = null; object? link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(entry);
            dynamic shortcut = link;
            var description = (string)shortcut.Description;
            var arguments = (string)shortcut.Arguments;
            if (!description.StartsWith("Start the domestic direct helper after current-user logon;", StringComparison.Ordinal) || !arguments.Contains('"' + Path.Combine(legacyDirectory, "Start-Helper.ps1") + '"', StringComparison.OrdinalIgnoreCase)) throw new UserError("旧版启动项无法确认归属，迁移已取消；请先在 Windows 启动应用中处理该项。");
            var backup = Path.Combine(paths.DataDirectory, "migration-backup"); Directory.CreateDirectory(backup);
            File.Copy(entry, Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".lnk")); File.Delete(entry);
        }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }
}
