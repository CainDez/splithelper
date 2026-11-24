using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SplitHelper.Services;
using Forms = System.Windows.Forms;

namespace SplitHelper;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex, _quitting, _exitInProgress;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private DesktopViewModel? _model;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private DispatcherTimer? _timer;
    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        var safeMode = args.Args.Contains("--safe-mode");
        var dataIndex = Array.IndexOf(args.Args, "--data-dir");
        var data = dataIndex >= 0 && dataIndex + 1 < args.Args.Length ? args.Args[dataIndex + 1] : null;
        var paths = new AppPaths(dataDirectory: data);
        var maintenance = args.Args.Contains("--prepare-update") || args.Args.Contains("--prepare-uninstall");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = safeMode ? "safe-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectory))).Substring(0, 12) : "main";
        var identity = "Local\\SplitHelper.Desktop." + sid + "." + suffix;
        _mutex = new Mutex(false, identity);
        try { _ownsMutex = _mutex.WaitOne(0); } catch (AbandonedMutexException) { _ownsMutex = true; }
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, identity + ".activate");
        if (!_ownsMutex)
        {
            if (!maintenance && !args.Args.Contains("--background")) _activation.Set();
            Shutdown(maintenance ? 2 : 0); return;
        }
        if (maintenance)
        {
            if (safeMode) { Shutdown(3); return; }
            try
            {
                var environment = new ClientEnvironment();
                var store = new SettingsStore(paths); var profiles = new ProfileService(paths, environment); var helper = new HelperManager(paths); var proxy = new WindowsProxy(paths); var startup = new StartupService(paths);
                using var connection = new ConnectionService(paths, environment, store, profiles, helper, proxy, startup, new MihomoClient(paths.MainPipeName));
                await connection.StopAsync(requireComponentsStopped: true);
                if (args.Args.Contains("--prepare-uninstall")) { profiles.RemoveManagedSetup(); startup.SetEnabled(false); }
                Shutdown(0);
            }
            catch (Exception error) { MessageBox.Show(error is UserError ? error.Message : "未能完成代理还原，安装/卸载已停止。请先打开分流助手处理诊断。", "分流助手", MessageBoxButton.OK, MessageBoxImage.Warning); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            MessageBox.Show("应用遇到意外错误，请先通过托盘停止并还原代理，然后重新打开。", "分流助手", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        _model = new DesktopViewModel(paths, safeMode);
        var window = new MainWindow { DataContext = _model };
        window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width * .94);
        window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height * .94);
        window.MaxHeight = SystemParameters.WorkArea.Height;
        window.SourceInitialized += (_, _) => { var enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int)); };
        window.Closing += (_, eventArgs) =>
        {
            if (_quitting) return;
            eventArgs.Cancel = true;
            if (!safeMode && _model.CloseToTray) { window.Hide(); return; }
            Dispatcher.BeginInvoke(new Action(async () => await QuitAsync()));
        };
        MainWindow = window;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, false);
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Text = "分流助手 " + _model.AppVersion, Icon = _trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开分流助手", null, (_, _) => ShowWindow());
        menu.Items.Add("检测访问链路", null, (_, _) => { if (_model.RefreshCommand.CanExecute(null)) _model.RefreshCommand.Execute(null); });
        menu.Items.Add("停止并还原代理", null, (_, _) => { if (_model.StopCommand.CanExecute(null)) _model.StopCommand.Execute(null); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出并还原代理", null, async (_, _) => await QuitAsync());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ShowWindow();
        if (!args.Args.Contains("--background") || safeMode) window.Show();
        await _model.InitializeAsync();
        if (_quitting || _exitInProgress || Dispatcher.HasShutdownStarted) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => { if (!_quitting && !_exitInProgress && _model is not null) await _model.RefreshBackgroundAsync(); };
        _timer.Start();
        if (_model.AutoEnable && !safeMode) _ = _model.RunAutoConnectAsync();
    }
    private void ShowWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show(); if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }
    private async Task QuitAsync()
    {
        if (_quitting || _exitInProgress || _model is null) return;
        _exitInProgress = true;
        try
        {
            if (!await _model.PrepareExitAsync()) { ShowWindow(); return; }
            _quitting = true; _timer?.Stop();
            if (_tray is not null) _tray.Visible = false;
            MainWindow?.Close(); Shutdown();
        }
        finally { _exitInProgress = false; }
    }
    protected override void OnExit(ExitEventArgs args)
    {
        _timer?.Stop(); _activationWait?.Unregister(null); _tray?.Dispose(); _trayIcon?.Dispose(); _model?.Dispose(); _activation?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex(); _mutex?.Dispose();
        base.OnExit(args);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
