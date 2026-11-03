using System.Windows;
using DAMeter.Core;

namespace DAMeter;

public partial class App : System.Windows.Application
{
    private readonly SettingsStore store = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            store.Log(args.Exception);
            args.Handled = true;
            System.Windows.MessageBox.Show("エラーが発生しました。DAMeter を終了します。詳細は例外ログを確認してください。", "DAMeter", MessageBoxButton.OK, MessageBoxImage.Error);
            if (MainWindow is DAMeter.MainWindow window) window.Exit();
            else Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) store.Log(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { store.Log(args.Exception); args.SetObserved(); };
        try
        {
            MainWindow = new MainWindow(store, store.Load());
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            store.Log(ex);
            System.Windows.MessageBox.Show("起動できませんでした。%LOCALAPPDATA%\\DAMeter の例外ログを確認してください。", "DAMeter", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is DAMeter.MainWindow window) window.Exit();
        base.OnSessionEnding(e);
    }
}
