using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DAMeter.Core;

namespace DAMeter.Mac;

public partial class App : Application
{
    private readonly SettingsStore store = new();
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(store, Program.SmokeTest ? new AppSettings() : store.Load(), Program.SmokeTest);
            desktop.MainWindow = window;
            // macOS Command+Q and Dock Quit first request application shutdown,
            // then close its windows. A tray-style window must not cancel this
            // request as though the user had only closed the meter window.
            desktop.ShutdownRequested += (_, _) => window.PrepareForShutdown();
            if (!Program.SmokeTest || OperatingSystem.IsMacOS())
            {
                var menu = new NativeMenu();
                var restore = new NativeMenuItem("DAMeter を表示（クリック透過を解除）");
                restore.Click += (_, _) => window.ShowAccessible();
                var settings = new NativeMenuItem("設定…"); settings.Click += (_, _) => window.OpenSettings();
                var quit = new NativeMenuItem("DAMeter を終了") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
                quit.Click += (_, _) => window.Exit();
                menu.Add(restore); menu.Add(settings); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);
                NativeMenu.SetMenu(this, menu);
            }
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                store.Log(args.Exception); args.Handled = true; window.ExitWithCode(1);
            };
            desktop.Exit += (_, _) => window.ViewModel.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
