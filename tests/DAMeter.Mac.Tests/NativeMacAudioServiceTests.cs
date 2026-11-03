using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DAMeter.Core;
using DAMeter.Mac.Audio;
using Xunit;

namespace DAMeter.Mac.Tests;

/// <summary>Exercises the actual managed callback boundary without macOS permission or audio devices.</summary>
public sealed class NativeMacAudioServiceTests
{
    [Fact]
    public async Task StereoCallbacksAggregatePeaksAndFrameWeightedRmsThenExpire()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitSamples(0.5f, -0.25f);
        bridge.Latest.EmitSamples(-1, 0.75f, -1, 0.75f, -1, 0.75f);

        var levels = service.DrainLevels();
        Assert.Equal(1, levels.LeftPeak);
        Assert.Equal(0.75, levels.RightPeak);
        Assert.Equal(Math.Sqrt(3.25 / 4), levels.LeftRms, 10);
        Assert.Equal(Math.Sqrt(1.75 / 4), levels.RightRms, 10);
        Assert.Equal(levels, service.DrainLevels());
        await Task.Delay(200);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
    }

    [Fact]
    public async Task SilentAndNonfiniteSamplesRemainFiniteAndProveDataWasReceived()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitSamples(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0);

        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        Assert.Contains("無音", service.Status);
        Assert.DoesNotContain("許可を確認", service.Status);
    }

    [Fact]
    public async Task LateReadyStatusDoesNotHideMeasuredAudio()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitSamples(0.5f, 0.25f);
        bridge.Latest.EmitStatus(2, "Waiting for first buffer");
        Assert.Contains("計測中", service.Status);
    }

    [Fact]
    public async Task StartIsIdempotentAndRetryReleasesTheOldSessionBeforeRestarting()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        service.Start();
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        service.Retry();
        await WaitUntilAsync(() => bridge.StartCount == 2);
        Assert.Equal(1, bridge.StopCount);
        Assert.Equal(1, bridge.ReleaseCount);

        await service.StopAsync();
        Assert.Equal(2, bridge.ReleaseCount);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 3);
    }

    [Fact]
    public async Task PermissionRefusalWaitsForExplicitRetry()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitStatus(77, "ScreenCaptureKit permission denied");
        bridge.Latest.EmitStatus(0);
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        Assert.Contains("DAMeter の許可", service.Status);
        await Task.Delay(100);
        Assert.Equal(1, bridge.StartCount);

        service.Retry();
        await WaitUntilAsync(() => bridge.StartCount == 2);
        bridge.Latest.EmitSamples(1, 0.5f);
        Assert.Equal(1, service.DrainLevels().LeftPeak);
    }

    [Fact]
    public async Task RetryDuringSynchronousPermissionFailureIsNotLost()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var bridge = new FakeBridge();
            await using var service = new NativeMacAudioService(bridge: bridge);
            bridge.OnStart = session =>
            {
                if (session.Handle != 1) return;
                session.EmitStatus(77, "Permission denied");
                service.Retry();
                session.EmitStatus(0);
            };
            service.Start();
            await WaitUntilAsync(() => bridge.StartCount == 2);
            Assert.Equal(1, bridge.ReleaseCount);
        }
    }

    [Fact]
    public async Task StopBeforeFirstBufferReleasesOnceAndCanRestart()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        await service.StopAsync();
        Assert.Equal(1, bridge.StopCount);
        Assert.Equal(1, bridge.ReleaseCount);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 2);
    }

    [Fact]
    public async Task StopWhileStartHasNotReturnedIsAppliedAfterHandlePublication()
    {
        var bridge = new FakeBridge();
        using var started = new ManualResetEventSlim();
        using var returnHandle = new ManualResetEventSlim();
        bridge.OnStart = _ =>
        {
            started.Set();
            Assert.True(returnHandle.Wait(TimeSpan.FromSeconds(5)));
        };
        await using var service = new NativeMacAudioService(bridge: bridge, shutdownTimeout: TimeSpan.FromMilliseconds(20));
        service.Start();
        Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))));
        try
        {
            await service.StopAsync();
            Assert.Equal(0, bridge.StopCount);
            Assert.Equal(0, bridge.ReleaseCount);
        }
        finally { returnHandle.Set(); }
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        Assert.Equal(1, bridge.StopCount);
    }

    [Fact]
    public async Task SynchronousFinalStatusWaitsForHandleThenReleasesExactlyOnce()
    {
        var bridge = new FakeBridge { OnStart = session => session.EmitStatus(0) };
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        await service.StopAsync();
        Assert.Equal(0, bridge.StopCount);
        Assert.Equal(1, bridge.ReleaseCount);
        Assert.Equal(0, bridge.StopAfterReleaseCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(4 * 1024 * 1024 + 8)]
    public async Task InvalidPayloadIsRejectedBeforeReadingItsPointer(int length)
    {
        var bridge = new FakeBridge();
        var errors = new ConcurrentQueue<Exception>();
        await using var service = new NativeMacAudioService(errors.Enqueue, bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitRaw(1, length);
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        Assert.Contains(errors, exception => exception is InvalidDataException);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        Assert.Equal(1, bridge.StopCount);
    }

    [Fact]
    public async Task NullPointerIsRejectedEvenForAnOtherwiseValidPayload()
    {
        var bridge = new FakeBridge();
        var errors = new ConcurrentQueue<Exception>();
        await using var service = new NativeMacAudioService(errors.Enqueue, bridge);
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitRaw(0, 8);
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        Assert.Contains(errors, exception => exception is InvalidDataException);
    }

    [Fact]
    public async Task DisposeReturnsPromptlyAndRootsLateCallbacksUntilFinalStatus()
    {
        var bridge = new FakeBridge { CompleteOnStop = false };
        var statuses = new ConcurrentQueue<string>();
        var service = new NativeMacAudioService(bridge: bridge, shutdownTimeout: TimeSpan.FromMilliseconds(20));
        service.StatusChanged += statuses.Enqueue;
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitSamples(1, 0.5f);
        var timer = Stopwatch.StartNew();
        service.Dispose();
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500), "Dispose blocked the thread needed by native shutdown.");
        await service.DisposeAsync();
        await WaitUntilAsync(() => bridge.StopCount == 1);
        Assert.Equal(0, bridge.ReleaseCount);
        var statusCount = statuses.Count;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        bridge.Latest.EmitSamples(1, 1);
        bridge.Latest.EmitStatus(77, "Late permission refusal");
        Assert.Equal(statusCount, statuses.Count);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        bridge.Latest.EmitStatus(0);
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
        // A stale context is ignored even if an erroneous test bridge reuses it.
        bridge.Latest.EmitStatus(0);
        Assert.Equal(1, bridge.ReleaseCount);
        Assert.Throws<ObjectDisposedException>(service.Start);
        Assert.Throws<ObjectDisposedException>(service.Retry);
    }

    [Fact]
    public async Task StopAndFinalStatusCannotUseAReleasedHandle()
    {
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var bridge = new FakeBridge { CompleteOnStop = false };
            await using var service = new NativeMacAudioService(bridge: bridge);
            service.Start();
            await WaitUntilAsync(() => bridge.StartCount == 1);
            await Task.WhenAll(service.StopAsync(), Task.Run(() => bridge.Latest.EmitStatus(0)));
            Assert.Equal(1, bridge.ReleaseCount);
            Assert.Equal(0, bridge.StopAfterReleaseCount);
        }
    }

    [Fact]
    public async Task SubscriberAndLoggerExceptionsCannotEscapeCallbacks()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(_ => throw new IOException("Logger failed"), bridge);
        service.StatusChanged += _ => throw new InvalidOperationException("Subscriber failed");
        service.Start();
        await WaitUntilAsync(() => bridge.StartCount == 1);
        bridge.Latest.EmitSamples(1, 0.5f);
        bridge.Latest.EmitStatus(70, "Capture error");
        bridge.Latest.EmitStatus(0);
        await WaitUntilAsync(() => bridge.ReleaseCount == 1);
    }

    [Fact]
    public async Task PermissionFreeVerificationRequiresMainAppIdentityAndWorkingConversion()
    {
        var bridge = new FakeBridge();
        await using var service = new NativeMacAudioService(bridge: bridge);
        await service.VerifyNativeAsync();
        Assert.Equal(0, bridge.StartCount);
        Assert.Equal(1, bridge.SelfTestCount);
        Assert.Equal(AudioLevels.Silence, service.DrainLevels());
        bridge.Identity = "org.atstti.DAMeter.AudioCapture";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyNativeAsync());
        bridge.Identity = "org.atstti.DAMeter";
        bridge.SelfTestResult = 70;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyNativeAsync());
    }

    [Fact]
    public async Task WrongMainIdentityPreventsCaptureUntilFixedAndRetried()
    {
        var bridge = new FakeBridge { Identity = "org.atstti.DAMeter.AudioCapture" };
        await using var service = new NativeMacAudioService(bridge: bridge);
        service.Start();
        await WaitUntilAsync(() => service.Status?.Contains("アプリ構成", StringComparison.Ordinal) == true);
        Assert.Equal(0, bridge.StartCount);
        bridge.Identity = "org.atstti.DAMeter";
        service.Retry();
        await WaitUntilAsync(() => bridge.StartCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for native callback state.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeBridge : INativeAudioBridge
    {
        private readonly ConcurrentDictionary<nint, Session> sessions = new();
        private readonly ConcurrentQueue<Session> starts = new();
        private long nextHandle;
        private int stopCount;
        private int releaseCount;
        private int stopAfterReleaseCount;
        private int selfTestCount;
        public string Identity { get; set; } = "org.atstti.DAMeter";
        public int SelfTestResult { get; set; }
        public bool CompleteOnStop { get; set; } = true;
        public Action<Session>? OnStart { get; set; }
        public int StartCount => starts.Count;
        public int StopCount => Volatile.Read(ref stopCount);
        public int ReleaseCount => Volatile.Read(ref releaseCount);
        public int StopAfterReleaseCount => Volatile.Read(ref stopAfterReleaseCount);
        public int SelfTestCount => Volatile.Read(ref selfTestCount);
        public Session Latest => starts.Last();

        public nint Start(nint context, NativePcmCallback pcmCallback, NativeStatusCallback statusCallback)
        {
            var handle = checked((nint)Interlocked.Increment(ref nextHandle));
            var session = new Session(handle, context, pcmCallback, statusCallback);
            Assert.True(sessions.TryAdd(handle, session));
            starts.Enqueue(session);
            OnStart?.Invoke(session);
            return handle;
        }

        public void Stop(nint handle)
        {
            var session = sessions[handle];
            if (session.Released) Interlocked.Increment(ref stopAfterReleaseCount);
            Interlocked.Increment(ref stopCount);
            if (CompleteOnStop) session.EmitStatus(0);
        }

        public void Release(nint handle)
        {
            var session = sessions[handle];
            Assert.True(session.FinalStatusSent, "Release preceded native final status0.");
            Assert.False(session.Released, "Native handle released twice.");
            session.Released = true;
            Interlocked.Increment(ref releaseCount);
        }

        public string GetIdentity() => Identity;
        public int SelfTest()
        {
            Interlocked.Increment(ref selfTestCount);
            return SelfTestResult;
        }

        public sealed class Session(nint handle, nint context, NativePcmCallback pcmCallback, NativeStatusCallback statusCallback)
        {
            public nint Handle { get; } = handle;
            public bool FinalStatusSent { get; private set; }
            public bool Released { get; set; }

            public void EmitSamples(params float[] samples)
            {
                var bytes = new byte[checked(samples.Length * sizeof(float))];
                for (var index = 0; index < samples.Length; index++)
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), samples[index]);
                var pointer = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    EmitRaw(pointer, bytes.Length);
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }

            public void EmitRaw(nint pointer, int byteCount) => pcmCallback(context, pointer, byteCount);

            public void EmitStatus(int code, string message = "")
            {
                if (code == 0) FinalStatusSent = true;
                var pointer = Marshal.StringToCoTaskMemUTF8(message);
                try { statusCallback(context, code, pointer); }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
    }
}
