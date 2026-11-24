using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;

namespace SplitHelper;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopViewModel vm && sender is Button { Tag: string page }) vm.Page = page;
    }
    private void SelectStep(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopViewModel vm && sender is Button { Tag: string step } && int.TryParse(step, out int value)) vm.WizardStep = value;
    }
    private void SelectSettingsTab(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopViewModel vm && sender is Button { Tag: string tab } && int.TryParse(tab, out int value)) vm.SettingsTab = value;
    }
    private void SelectProvider(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopViewModel vm && sender is Button { Tag: string provider }) vm.Provider = provider;
    }
    private void SelectRuleTarget(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopViewModel vm && sender is Button { Tag: string target }) vm.RuleTarget = target;
    }
    private void FocusRuleInput(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { RuleDomainInput.Focus(); RuleDomainInput.SelectAll(); }));
    }
}

public sealed class EqualVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class ToneBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(123, 220, 197));
    private static readonly Brush Attention = new SolidColorBrush(Color.FromRgb(255, 194, 72));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(161, 186, 197));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch { "good" => Good, "attention" => Attention, _ => Muted };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
