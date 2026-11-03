using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DAMeter.Core;
using System.Reflection;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class ShutdownTests
{
    [AvaloniaFact]
    public void ClosingMeterWindowHidesItWhenRecoveryMenuIsAvailable()
    {
        using var fixture = new WindowFixture();
        bool closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        bool canceled = RequestNativeClose(fixture.Window, WindowCloseReason.WindowClosing);

        Assert.True(canceled);
        Assert.False(fixture.Window.IsVisible);
        Assert.False(closed);
        fixture.Window.ShowAccessible();
        Assert.True(fixture.Window.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public void NativeApplicationAndSystemQuitAreNotCanceled(WindowCloseReason reason)
    {
        using var fixture = new WindowFixture();
        bool closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        bool canceled = RequestNativeClose(fixture.Window, reason);

        Assert.False(canceled);
        Assert.True(fixture.Window.IsShutdownRequested);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void PreparedNativeQuitCannotBeCanceledByAWindowCloseCallback()
    {
        using var fixture = new WindowFixture();
        bool closed = false;
        fixture.Window.Closed += (_, _) => closed = true;
        fixture.Window.PrepareForShutdown();

        bool canceled = RequestNativeClose(fixture.Window, WindowCloseReason.Undefined);

        Assert.False(canceled);
        Assert.True(closed);
        Assert.False(fixture.Window.IsVisible);
    }

    private static bool RequestNativeClose(MainWindow window, WindowCloseReason reason)
    {
        // Avalonia pins this native-platform callback to an internal interface.
        // Exercise the real callback rather than duplicating the app's closing
        // predicate or substituting a test-only WindowClosingEventArgs value.
        var callback = typeof(Window).GetMethod("HandleClosing", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(callback);
        return Assert.IsType<bool>(callback.Invoke(window, [reason]));
    }

    private sealed class RecoverableMainWindow(SettingsStore store) : MainWindow(store, new AppSettings(), smokeTest: true)
    {
        protected override bool HasRecoveryIcon => true;
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"dameter-shutdown-tests-{Guid.NewGuid():N}");
        public MainWindow Window { get; }

        public WindowFixture()
        {
            Window = new RecoverableMainWindow(new SettingsStore(directory));
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            // The tests exercise the native pre-close callback, so complete the
            // platform destruction without asking to quit a second time.
            Window.PrepareForShutdown();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
