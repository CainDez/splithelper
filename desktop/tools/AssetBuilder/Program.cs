using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MahApps.Metro.IconPacks;
using SplitHelper;
using SplitHelper.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "ui-capture") return CaptureUi(args[1]);
        if (args.Length == 2 && args[0] == "profile-fixtures")
        {
            Directory.CreateDirectory(args[1]);
            foreach (var provider in new[] { "Mullvad", "Windscribe" })
                File.WriteAllText(Path.Combine(args[1], provider + ".local.yaml"), ProfileService.Generate(new AppSettings { Provider = provider, GatewayHost = "10.23.45.1", GatewayUsername = "fixture: user # only", GatewayPassword = "fixture: 'quoted' # only" }));
            return 0;
        }
        if (args.Length > 0 && args[0] == "proxy-read")
        {
            var snapshot = WindowsProxy.Read();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { flags = snapshot.Flags, serverLength = snapshot.Server.Length, bypassLength = snapshot.Bypass.Length, autoConfigLength = snapshot.AutoConfigUrl.Length }));
            return 0;
        }
        if (args.Length != 1) throw new ArgumentException("Provide the output PNG path.");
        var app = new System.Windows.Application();
        var icon = new PackIconPhosphorIcons { Kind = PackIconPhosphorIconsKind.ShareNetwork, Width = 208, Height = 208, Foreground = new SolidColorBrush(Color.FromRgb(0x69, 0xd7, 0xc7)), Margin = new Thickness(24) };
        var host = new System.Windows.Controls.Grid { Width = 256, Height = 256 }; host.Children.Add(icon);
        host.Measure(new Size(256, 256)); host.Arrange(new Rect(0, 0, 256, 256)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        var pixels = new byte[256 * 256 * 4]; bitmap.CopyPixels(pixels, 256 * 4, 0);
        if (!Enumerable.Range(0, 256 * 256).Any(index => pixels[index * 4 + 3] != 0)) throw new InvalidOperationException("Icon render is empty.");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var output = File.Create(args[0]); encoder.Save(output);
        Console.WriteLine("Rendered licensed Phosphor ShareNetwork icon.");
        return 0;
    }

    private static int CaptureUi(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SplitHelper;component/Styles.xaml", UriKind.Relative) });
        using var model = new DesktopViewModel(new AppPaths(dataDirectory: Path.Combine(directory, "isolated-data")), true);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0 };
        var result = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await model.InitializeAsync();
                foreach (var scene in new[] { ("overview", "overview", 0, "Windscribe", 0, 1440d, 960d), ("windscribe", "settings", 1, "Windscribe", 0, 1440d, 960d), ("mullvad", "settings", 1, "Mullvad", 0, 1440d, 960d), ("windscribe-narrow", "settings", 1, "Windscribe", 0, 960d, 720d), ("startup-narrow", "settings", 1, "Windscribe", 1, 960d, 720d), ("custom-rules", "settings", 0, "Windscribe", 2, 1440d, 960d), ("custom-rules-narrow", "settings", 0, "Windscribe", 2, 960d, 720d), ("diagnostics", "diagnostics", 0, "Windscribe", 0, 1440d, 960d) })
                {
                    model.Page = scene.Item2; model.WizardStep = scene.Item3; model.Provider = scene.Item4; model.SettingsTab = scene.Item5;
                    if (scene.Item5 == 2 && model.CustomRules.Count == 0)
                    {
                        model.CustomRules.Add(new CustomRule("example.com", true, "ChinaDirect"));
                        model.CustomRules.Add(new CustomRule("video.example.org", false, "VPN"));
                        model.CustomRules.Add(new CustomRule("ads.example.org", true, "REJECT", false));
                        model.SelectedCustomRule = model.CustomRules[0];
                    }
                    window.Width = scene.Item6; window.Height = scene.Item7;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    bitmap.Render(background); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, scene.Item1 + ".png")); encoder.Save(output);
                }
                Console.WriteLine("Rendered eight native WPF scenes with isolated, read-only data.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window); return result;
    }
}
