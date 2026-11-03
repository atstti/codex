using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac.Audio;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class InProcessShutdownTests
{
    [AvaloniaFact]
    public async Task NativeTerminationDoesNotWaitForDelayedAudioStop()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dameter-native-termination-{Guid.NewGuid():N}");
        var bridge = new WaitingBridge();
        using var audio = new NativeMacAudioService(bridge: bridge, shutdownTimeout: TimeSpan.FromSeconds(5));
        var window = new MainWindow(new SettingsStore(directory), new AppSettings(), smokeTest: true, audio: audio);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.ViewModel.Start();
            await bridge.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            window.PrepareForNativeShutdown();
            window.Close();

            Assert.True(closed);
            Assert.True(window.IsShutdownPrepared);
            Assert.Equal(0, bridge.Releases);
            await bridge.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bridge.CompleteStop();
            Assert.Equal(1, bridge.Releases);
        }
        finally
        {
            bridge.CompleteStop();
            window.PrepareForShutdown();
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task QuitKeepsTheUiAliveUntilNativeCaptureHasStopped()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dameter-native-quit-{Guid.NewGuid():N}");
        var bridge = new WaitingBridge();
        using var audio = new NativeMacAudioService(bridge: bridge, shutdownTimeout: TimeSpan.FromSeconds(5));
        var window = new MainWindow(new SettingsStore(directory), new AppSettings(), smokeTest: true, audio: audio);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.ViewModel.Start();
            await bridge.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            window.Exit();
            Dispatcher.UIThread.RunJobs();
            await bridge.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsShutdownRequested);
            Assert.False(closed);
            bool uiResponded = false;
            Dispatcher.UIThread.Post(() => uiResponded = true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(uiResponded);

            bridge.CompleteStop();
            await window.ShutdownCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(closed);
            Assert.False(window.IsVisible);
            Assert.Equal(1, bridge.Releases);
        }
        finally
        {
            bridge.CompleteStop();
            window.Exit();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class WaitingBridge : INativeAudioBridge
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NativeStatusCallback? status;
        private nint context;
        private bool completed;
        public int Releases { get; private set; }
        public string GetIdentity() => "org.atstti.DAMeter";
        public int SelfTest() => 0;
        public nint Start(nint context, NativePcmCallback pcm, NativeStatusCallback status)
        {
            this.context = context;
            this.status = status;
            Started.TrySetResult();
            return 1;
        }
        public void Stop(nint handle) => StopRequested.TrySetResult();
        public void Release(nint handle) => Releases++;
        public void CompleteStop()
        {
            if (completed || status is null) return;
            completed = true;
            status(context, 0, 0);
        }
    }
}
