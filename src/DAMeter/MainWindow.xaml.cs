using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DAMeter.Core;
using DAMeter.Platform;
using DAMeter.ViewModels;

namespace DAMeter;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly SettingsStore store;
    private readonly TrayService tray;
    private readonly DisplayMonitorWatcher displayWatcher;
    private SettingsWindow? settingsWindow;
    private bool appliedFloating;
    private bool applyingBounds;
    private bool exiting;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public MainWindow(SettingsStore store, AppSettings settings)
    {
        InitializeComponent();
        this.store = store;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Persist(); };
        viewModel = new MainViewModel(store, settings, Dispatcher);
        DataContext = viewModel;
        viewModel.FrameReady += Meter.Update;
        viewModel.OpenSettingsRequested += OpenSettings;
        viewModel.PropertyChanged += SettingsChanged;
        // Recovery UI must exist before restoring a saved click-through setting.
        tray = new TrayService(ShowAccessible, HideSafely, ReturnToNormal,
            () => { ShowAccessible(); viewModel.Floating = true; },
            () => { if (viewModel.Floating) viewModel.ClickThrough = !viewModel.ClickThrough; },
            () => viewModel.Topmost = !viewModel.Topmost, OpenSettings, Exit);
        displayWatcher = new DisplayMonitorWatcher(Dispatcher, () =>
        {
            WindowInterop.EnsureVisible(this);
            Dispatcher.InvokeAsync(() => { CaptureBounds(); Persist(); }, DispatcherPriority.Loaded);
        });
        appliedFloating = settings.Floating;
        ApplyBounds(settings.Floating ? settings.FloatingBounds : settings.NormalBounds);
        Topmost = settings.Topmost;
        SourceInitialized += (_, _) => WindowInterop.SetClickThrough(this, settings.Floating && settings.ClickThrough);
        Loaded += (_, _) => { WindowInterop.EnsureVisible(this); viewModel.Start(); };
        Closing += OnClosing;
        Closed += (_, _) => { saveTimer.Stop(); displayWatcher.Dispose(); tray.Dispose(); viewModel.Dispose(); };
        LocationChanged += (_, _) => { if (IsLoaded && WindowState == WindowState.Normal) CaptureBounds(); };
        SizeChanged += (_, _) => { if (IsLoaded && WindowState == WindowState.Normal) CaptureBounds(); };
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Floating))
        {
            CaptureBounds();
            applyingBounds = true;
            try
            {
                appliedFloating = viewModel.Floating;
                if (!appliedFloating) viewModel.ClickThrough = false;
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                ApplyBounds(appliedFloating ? viewModel.Settings.FloatingBounds : viewModel.Settings.NormalBounds);
                WindowInterop.SetClickThrough(this, appliedFloating && viewModel.ClickThrough);
                WindowInterop.EnsureVisible(this);
            }
            finally { applyingBounds = false; }
            // Native sizing and WPF layout can finish after Width/Height setters.
            // Capture the complete result once Render has processed that layout.
            Dispatcher.InvokeAsync(() =>
            {
                if (exiting) return;
                CaptureBounds();
                Persist();
            }, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainViewModel.Topmost)) Topmost = viewModel.Topmost;
        else if (e.PropertyName == nameof(MainViewModel.ClickThrough))
            WindowInterop.SetClickThrough(this, viewModel.Floating && viewModel.ClickThrough);
        if (!applyingBounds && e.PropertyName != nameof(MainViewModel.Status)) { saveTimer.Stop(); saveTimer.Start(); }
    }

    private void ApplyBounds(WindowBounds bounds)
    {
        // A copy alone does not suppress LocationChanged / SizeChanged: they can
        // otherwise replace the destination bounds with a partially applied size.
        var snapshot = bounds with { };
        bool previouslyApplying = applyingBounds;
        applyingBounds = true;
        try
        {
            MinWidth = appliedFloating ? 120 : 420;
            MinHeight = appliedFloating ? 120 : 300;
            Left = snapshot.Left;
            Top = snapshot.Top;
            Width = Math.Max(MinWidth, snapshot.Width);
            Height = Math.Max(MinHeight, snapshot.Height);
        }
        finally { applyingBounds = previouslyApplying; }
    }

    private void CaptureBounds()
    {
        if (applyingBounds) return;
        var restored = RestoreBounds;
        bool useRestored = WindowState != WindowState.Normal && !restored.IsEmpty;
        var bounds = new WindowBounds
        {
            Left = useRestored ? restored.Left : Left,
            Top = useRestored ? restored.Top : Top,
            Width = useRestored ? restored.Width : ActualWidth > 0 ? ActualWidth : Width,
            Height = useRestored ? restored.Height : ActualHeight > 0 ? ActualHeight : Height
        };
        if (appliedFloating) viewModel.Settings.FloatingBounds = bounds;
        else viewModel.Settings.NormalBounds = bounds;
    }

    private void Persist()
    {
        saveTimer.Stop();
        try { viewModel.Save(); }
        catch (Exception ex) { store.Log(ex); }
    }

    public void ShowAccessible()
    {
        viewModel.ClickThrough = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowInterop.EnsureVisible(this);
        Activate();
    }

    private void HideSafely() { CaptureBounds(); Persist(); Hide(); }
    private void ReturnToNormal() { ShowAccessible(); viewModel.Floating = false; }
    private void ReturnToNormal(object sender, RoutedEventArgs e) => ReturnToNormal();
    private void HideWindow(object sender, RoutedEventArgs e) => HideSafely();
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ExitApplication(object sender, RoutedEventArgs e) => Exit();

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); CaptureBounds(); Persist(); }
        catch (InvalidOperationException ex) { store.Log(ex); }
    }

    private void ResizeWindow(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
        CaptureBounds();
    }

    private void ResizeCompleted(object sender, DragCompletedEventArgs e) { CaptureBounds(); Persist(); }

    private void OpenSettings()
    {
        ShowAccessible();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow { Owner = this, DataContext = viewModel };
        settingsWindow.Closed += (_, _) => { settingsWindow = null; Persist(); };
        settingsWindow.Show();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        HideSafely();
    }

    public void Exit()
    {
        if (exiting) return;
        exiting = true;
        CaptureBounds();
        Persist();
        settingsWindow?.Close();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
