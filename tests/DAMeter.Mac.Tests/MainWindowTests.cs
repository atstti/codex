using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DAMeter.Core;
using DAMeter.Mac.Controls;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void PermissionButtonUsesTheViewModelActionWithoutOpeningSettingsDuringTests()
    {
        using var fixture = new WindowFixture();
        int requested = 0;
        fixture.Window.ViewModel.OpenPermissionsRequested += () => requested++;
        Button button = FindButton(fixture.Window, "権限設定");
        Assert.Same(fixture.Window.ViewModel.OpenPermissionsCommand, button.Command);
        Click(fixture.Window, button);
        Assert.Equal(1, requested);
    }

    [AvaloniaFact]
    public void ExitButtonClosesTheApplicationWindow()
    {
        using var fixture = new WindowFixture();
        bool closed = false;
        fixture.Window.Closed += (_, _) => closed = true;
        Click(fixture.Window, FindButton(fixture.Window, "終了"));
        Assert.True(closed);
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void MeterButtonsAreBoundAndSwitchTheSharedSettingThroughPointerInput()
    {
        using var fixture = new WindowFixture();
        MainWindow window = fixture.Window;
        Assert.Equal(MeterMode.HorizontalPeak, window.ViewModel.Meter);

        foreach (var choice in new[]
        {
            (Label: "アナログ VU", Mode: MeterMode.AnalogVu),
            (Label: "縦型ピーク", Mode: MeterMode.VerticalPeak),
            (Label: "横型ピーク", Mode: MeterMode.HorizontalPeak)
        })
        {
            Button button = FindButton(window, choice.Label);
            Assert.Same(window.ViewModel.SelectMeterCommand, button.Command);
            Click(window, button);
            Assert.Equal(choice.Mode, window.ViewModel.Meter);
            Assert.Equal(choice.Mode, window.ViewModel.Settings.Meter);
        }
    }

    [AvaloniaFact]
    public void FloatingButtonHidesChromeAndReturningToNormalRestoresIt()
    {
        using var fixture = new WindowFixture();
        MainWindow window = fixture.Window;
        Button modeButton = FindButton(window, "横型ピーク");
        WrapPanel toolbar = modeButton.GetVisualAncestors().OfType<WrapPanel>().Single();
        Assert.True(toolbar.IsVisible);

        Click(window, FindButton(window, "浮動表示"));

        Assert.True(window.ViewModel.Floating);
        Assert.False(toolbar.IsVisible);
        Assert.True(window.FindControl<MeterDisplay>("Meter")!.IsVisible);

        window.ReturnToNormal();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.ViewModel.Floating);
        Assert.True(toolbar.IsVisible);
    }

    [AvaloniaFact]
    public void TransparentFloatingBackdropDoesNotChangeMeterControlOpacity()
    {
        using var fixture = new WindowFixture(new AppSettings { BackgroundOpacity = 0.35 });
        MainWindow window = fixture.Window;
        Border backdrop = window.FindControl<Border>("Backdrop")!;
        MeterDisplay meter = window.FindControl<MeterDisplay>("Meter")!;
        Assert.Equal(1, backdrop.Opacity);

        window.ViewModel.Floating = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.35, backdrop.Opacity, 10);

        window.ViewModel.FloatingBackgroundOpacity = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, backdrop.Opacity);
        Assert.Equal(1, meter.Opacity);
        Assert.True(meter.IsVisible);
        Assert.True(meter.Bounds.Width > 0 && meter.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void SettingsSlidersUpdateTheViewModelAndPersistTheirValues()
    {
        using var fixture = new WindowFixture(new AppSettings { BackgroundOpacity = 0.4, PeakHoldSeconds = 2.5 });
        var settingsWindow = new SettingsWindow { DataContext = fixture.Window.ViewModel };
        try
        {
            settingsWindow.Show(fixture.Window);
            Dispatcher.UIThread.RunJobs();
            Slider[] sliders = settingsWindow.GetVisualDescendants().OfType<Slider>().ToArray();
            Slider opacity = sliders.Single(s => s.Minimum == 0 && s.Maximum == 1);
            Slider hold = sliders.Single(s => s.Minimum == 0 && s.Maximum == 10);
            Slider decay = sliders.Single(s => s.Minimum == 1 && s.Maximum == 120);
            Slider reference = sliders.Single(s => s.Minimum == -60 && s.Maximum == 0);
            Assert.Equal(0.4, opacity.Value, 10);
            Assert.Equal(2.5, hold.Value, 10);

            opacity.Value = 0.2;
            hold.Value = 3.5;
            decay.Value = 36;
            reference.Value = -24;
            Dispatcher.UIThread.RunJobs();
            fixture.Window.ViewModel.Save();

            AppSettings saved = fixture.Store.Load();
            Assert.Equal(0.2, saved.BackgroundOpacity, 10);
            Assert.Equal(3.5, saved.PeakHoldSeconds, 10);
            Assert.Equal(36, saved.DecayDbPerSecond, 10);
            Assert.Equal(-24, saved.VuReferenceDb, 10);

            fixture.Window.ViewModel.PeakHoldSeconds = 1.25;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.25, hold.Value, 10);
        }
        finally { settingsWindow.Close(); }
    }

    [AvaloniaFact]
    public void FloatingAndNormalModesKeepIndependentPositionAndSize()
    {
        var settings = new AppSettings
        {
            NormalBounds = new WindowBounds { Left = 25, Top = 35, Width = 700, Height = 450 },
            FloatingBounds = new WindowBounds { Left = 150, Top = 160, Width = 360, Height = 200 }
        };
        using var fixture = new WindowFixture(settings);
        MainWindow window = fixture.Window;
        Assert.Equal(700, window.Width);
        Assert.Equal(450, window.Height);

        window.ViewModel.Floating = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(360, window.Width);
        Assert.Equal(200, window.Height);
        window.Position = new PixelPoint(180, 210);
        window.Width = 410;
        window.Height = 250;
        Dispatcher.UIThread.RunJobs();

        window.ViewModel.Floating = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(700, window.Width);
        Assert.Equal(450, window.Height);
        Assert.Equal(new PixelPoint(25, 35), window.Position);

        window.ViewModel.Floating = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(410, window.Width);
        Assert.Equal(250, window.Height);
        Assert.Equal(new PixelPoint(180, 210), window.Position);
        Assert.Equal(700, settings.NormalBounds.Width);
        Assert.Equal(450, settings.NormalBounds.Height);
    }

    private static Button FindButton(MainWindow window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == content);

    private static void Click(MainWindow window, Button button)
    {
        Point location = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The visible button must belong to the test window's visual tree.");
        window.MouseDown(location, MouseButton.Left);
        window.MouseUp(location, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"dameter-ui-tests-{Guid.NewGuid():N}");
        public SettingsStore Store { get; }
        public MainWindow Window { get; }

        public WindowFixture(AppSettings? settings = null)
        {
            Store = new SettingsStore(directory);
            Window = new MainWindow(Store, settings ?? new AppSettings(), smokeTest: true);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Exit();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
