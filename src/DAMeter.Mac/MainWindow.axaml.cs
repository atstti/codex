using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac.Platform;
using DAMeter.Mac.ViewModels;

namespace DAMeter.Mac;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly bool smokeTest;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly MacTrayService? tray;
    private SettingsWindow? settingsWindow;
    private bool appliedFloating;
    private bool applyingBounds;
    private bool opened;
    private bool exiting;
    public MainViewModel ViewModel { get; }
    protected virtual bool HasRecoveryIcon => tray?.IsAvailable == true;

    public MainWindow() : this(new SettingsStore(), new AppSettings(), Design.IsDesignMode) { }

    public MainWindow(SettingsStore store, AppSettings settings, bool smokeTest = false)
    {
        InitializeComponent();
        this.store = store;
        this.smokeTest = smokeTest;
        ViewModel = new MainViewModel(store, settings);
        DataContext = ViewModel;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DAMeter.Mac/Assets/dameter.png")));
        ViewModel.FrameReady += (state, mode) => { if (IsVisible) Meter.Update(state, mode); };
        ViewModel.OpenSettingsRequested += OpenSettings;
        ViewModel.PropertyChanged += SettingsChanged;
        saveTimer.Tick += (_, _) => Persist();
        if (!smokeTest || (Program.SmokeTest && OperatingSystem.IsMacOS()))
        {
            tray = new MacTrayService(Application.Current!, ShowAccessible, HideSafely, ReturnToNormal,
                () => { ShowAccessible(); ViewModel.Floating = true; }, ToggleClickThrough,
                () => ViewModel.Topmost = !ViewModel.Topmost, OpenSettings, Exit);
            if (Program.SmokeTest && !tray.IsAvailable)
                throw new InvalidOperationException("The native menu-bar recovery icon is unavailable during the quit smoke test.");
        }
        appliedFloating = settings.Floating;
        ApplyBounds(appliedFloating ? settings.FloatingBounds : settings.NormalBounds);
        Topmost = settings.Topmost;
        Opened += (_, _) =>
        {
            opened = true;
            MacWindowInterop.EnsureVisible(this);
            if (smokeTest)
            {
                ViewModel.Status = "UI smoke test: no audio capture or permission request";
                Meter.Update(new MeterBallistics(), ViewModel.Meter);
                DispatcherTimer.RunOnce(() => Meter.Update(new MeterBallistics(), MeterMode.AnalogVu), TimeSpan.FromMilliseconds(200));
                DispatcherTimer.RunOnce(() => Meter.Update(new MeterBallistics(), MeterMode.VerticalPeak), TimeSpan.FromMilliseconds(400));
                if (Program.SmokeTest && OperatingSystem.IsMacOS()) _ = VerifyAgentAndExitAsync();
                else DispatcherTimer.RunOnce(Exit, TimeSpan.FromSeconds(1));
            }
            else
            {
                ApplyClickThrough();
                ViewModel.Start();
            }
        };
        Closing += (_, e) =>
        {
            if (exiting) return;
            if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            {
                PrepareForShutdown();
                return;
            }
            if (HasRecoveryIcon) { e.Cancel = true; HideSafely(); }
            else
            {
                // Let this close finish instead of recursively calling Close
                // from inside Closing when no recovery icon is available.
                PrepareForShutdown();
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
                });
            }
        };
        Closed += (_, _) =>
        {
            saveTimer.Stop();
            Screens.Changed -= ScreensChanged;
            tray?.Dispose();
            ViewModel.Dispose();
        };
        PositionChanged += (_, _) => GeometryChanged();
        SizeChanged += (_, _) => GeometryChanged();
        Screens.Changed += ScreensChanged;
    }

    private void GeometryChanged()
    {
        if (!opened || applyingBounds || WindowState != WindowState.Normal) return;
        CaptureBounds();
        ScheduleSave();
    }
    private async Task VerifyAgentAndExitAsync()
    {
        var exitCode = 0;
        try
        {
            await ViewModel.VerifyAgentAsync().ConfigureAwait(false);
            await Task.Delay(1000).ConfigureAwait(false);
        }
        catch (Exception ex) { store.Log(ex); Console.Error.WriteLine(ex); exitCode = 1; }
        Dispatcher.UIThread.Post(() =>
        {
            if (exitCode == 0 && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Exercise the same cancelable shutdown path as Dock Quit. In
                // particular, the recovery icon must not turn Quit into Hide.
                if (!desktop.TryShutdown())
                {
                    Console.Error.WriteLine("Native application shutdown was unexpectedly canceled.");
                    PrepareForShutdown();
                    desktop.Shutdown(1);
                }
            }
            else ExitWithCode(exitCode);
        });
    }
    private void ScreensChanged(object? sender, EventArgs e)
    {
        if (exiting) return;
        MacWindowInterop.EnsureVisible(this);
        Dispatcher.UIThread.Post(() => { CaptureBounds(); ScheduleSave(); }, DispatcherPriority.Loaded);
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Floating))
        {
            CaptureBounds();
            applyingBounds = true;
            try
            {
                appliedFloating = ViewModel.Floating;
                if (!appliedFloating) ViewModel.ClickThrough = false;
                WindowState = WindowState.Normal;
                ApplyBounds(appliedFloating ? ViewModel.Settings.FloatingBounds : ViewModel.Settings.NormalBounds);
                ApplyClickThrough();
                MacWindowInterop.EnsureVisible(this);
            }
            finally { applyingBounds = false; }
            Dispatcher.UIThread.Post(() => { CaptureBounds(); ScheduleSave(); }, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainViewModel.Topmost)) Topmost = ViewModel.Topmost;
        else if (e.PropertyName == nameof(MainViewModel.ClickThrough)) ApplyClickThrough();
        if (e.PropertyName != nameof(MainViewModel.Status) && !applyingBounds) ScheduleSave();
    }
    private void ApplyClickThrough()
    {
        if (!opened || smokeTest) return;
        var enabled = ViewModel.Floating && ViewModel.ClickThrough;
        if (enabled && !HasRecoveryIcon)
        {
            ViewModel.ClickThrough = false;
            ViewModel.Status = "復帰用のメニューバーアイコンが利用できないため、クリック透過は有効にできません。";
            return;
        }
        if (!OperatingSystem.IsMacOS()) { if (enabled) ViewModel.ClickThrough = false; return; }
        try { MacWindowInterop.SetClickThrough(this, enabled); }
        catch (Exception ex)
        {
            store.Log(ex);
            if (enabled) ViewModel.ClickThrough = false;
            ViewModel.Status = "クリック透過を変更できませんでした。例外ログを確認してください。";
        }
    }
    private void ApplyBounds(WindowBounds source)
    {
        var bounds = source with { };
        bool previous = applyingBounds; applyingBounds = true;
        try
        {
            MinWidth = appliedFloating ? 120 : 420;
            MinHeight = appliedFloating ? 120 : 300;
            Position = new PixelPoint((int)bounds.Left, (int)bounds.Top);
            Width = Math.Max(MinWidth, bounds.Width); Height = Math.Max(MinHeight, bounds.Height);
        }
        finally { applyingBounds = previous; }
    }
    private void CaptureBounds()
    {
        if (applyingBounds || !opened || WindowState != WindowState.Normal) return;
        var bounds = new WindowBounds { Left = Position.X, Top = Position.Y, Width = Bounds.Width, Height = Bounds.Height };
        if (appliedFloating) ViewModel.Settings.FloatingBounds = bounds;
        else ViewModel.Settings.NormalBounds = bounds;
    }
    private void ScheduleSave() { if (smokeTest || exiting) return; saveTimer.Stop(); saveTimer.Start(); }
    private void Persist()
    {
        saveTimer.Stop();
        if (smokeTest) return;
        try { ViewModel.Save(); } catch (Exception ex) { store.Log(ex); }
    }
    public void ShowAccessible()
    {
        ViewModel.ClickThrough = false;
        Show(); WindowState = WindowState.Normal;
        MacWindowInterop.EnsureVisible(this); Activate();
    }
    private void HideSafely()
    {
        if (!HasRecoveryIcon) { Exit(); return; }
        CaptureBounds(); Persist(); Hide();
    }
    public void ReturnToNormal() { ShowAccessible(); ViewModel.Floating = false; }
    private void ReturnToNormal(object? sender, RoutedEventArgs e) => ReturnToNormal();
    public void OpenSettings()
    {
        ShowAccessible();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow { DataContext = ViewModel };
        settingsWindow.Closed += (_, _) => { settingsWindow = null; Persist(); };
        settingsWindow.Show(this);
    }
    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void ResizeWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.SouthEast, e);
    }
    private void ToggleFloating(object? sender, RoutedEventArgs e) => ViewModel.Floating = !ViewModel.Floating;
    private void ToggleTopmost(object? sender, RoutedEventArgs e) => ViewModel.Topmost = !ViewModel.Topmost;
    private void ToggleClickThrough() { if (ViewModel.Floating) ViewModel.ClickThrough = !ViewModel.ClickThrough; }
    private void ToggleClickThrough(object? sender, RoutedEventArgs e) => ToggleClickThrough();
    private void HideWindow(object? sender, RoutedEventArgs e) => HideSafely();
    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ExitApplication(object? sender, RoutedEventArgs e) => Exit();
    public void Exit() => ExitWithCode(0);
    public void PrepareForShutdown()
    {
        if (exiting) return;
        exiting = true;
        CaptureBounds(); Persist(); settingsWindow?.Close();
    }
    public void ExitWithCode(int exitCode)
    {
        if (exiting) return;
        PrepareForShutdown(); Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exitCode);
    }
}
