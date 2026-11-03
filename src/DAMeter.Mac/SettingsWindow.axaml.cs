using Avalonia.Controls;
using Avalonia.Interactivity;
namespace DAMeter.Mac;
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();
    private void CloseSettings(object? sender, RoutedEventArgs e) => Close();
}
