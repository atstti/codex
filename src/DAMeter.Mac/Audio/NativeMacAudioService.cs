using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DAMeter.Core;

namespace DAMeter.Mac.Audio;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void NativePcmCallback(nint context, nint samples, int byteCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void NativeStatusCallback(nint context, int code, nint message);

/// <summary>The in-process C ABI, injectable without loading macOS frameworks.</summary>
public interface INativeAudioBridge
{
    nint Start(nint context, NativePcmCallback pcmCallback, NativeStatusCallback statusCallback);
    void Stop(nint handle);
    void Release(nint handle);
    string GetIdentity();
    int SelfTest();
}

/// <summary>
/// Loads the bundled library by its full path. Libraries remain loaded for the
/// process lifetime, including while macOS permission dialogs delay shutdown.
/// </summary>
public sealed class NativeMacAudioBridge : INativeAudioBridge
{
    private static readonly ConcurrentDictionary<string, Lazy<Exports>> Libraries = new(StringComparer.Ordinal);
    private readonly string libraryPath;

    public NativeMacAudioBridge(string? libraryPath = null)
    {
        this.libraryPath = Path.GetFullPath(libraryPath ?? ResolveBundledLibrary());
    }

    public nint Start(nint context, NativePcmCallback pcmCallback, NativeStatusCallback statusCallback) =>
        Library.Start(context, pcmCallback, statusCallback);

    public void Stop(nint handle) => Library.Stop(handle);
    public void Release(nint handle) => Library.Release(handle);
    public int SelfTest() => Library.SelfTest();

    public string GetIdentity()
    {
        var required = Library.Identity(0, 0);
        if (required is < 2 or > 4096) throw new InvalidDataException("Invalid native audio identity length.");
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (Library.Identity(buffer, required) != required)
                throw new InvalidDataException("The native audio identity changed during verification.");
            var bytes = new byte[required];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            if (bytes[^1] != 0 || bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0))
                throw new InvalidDataException("Invalid native audio identity terminator.");
            return new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 1);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private Exports Library
    {
        get
        {
            var cached = Libraries.GetOrAdd(libraryPath,
                path => new Lazy<Exports>(() => new Exports(path), LazyThreadSafetyMode.ExecutionAndPublication));
            try { return cached.Value; }
            catch
            {
                // A missing or incomplete installation can be repaired before
                // Retry. Successfully loaded libraries are never removed.
                ((ICollection<KeyValuePair<string, Lazy<Exports>>>)Libraries).Remove(new(libraryPath, cached));
                throw;
            }
        }
    }

    private static string ResolveBundledLibrary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        return directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            ? Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libDAMeterAudio.dylib")
            : Path.Combine(AppContext.BaseDirectory, "libDAMeterAudio.dylib");
    }

    private sealed class Exports
    {
        // The library handle and every export stay strongly rooted in Libraries.
        private readonly nint library;
        public readonly StartFunction Start;
        public readonly HandleFunction Stop;
        public readonly HandleFunction Release;
        public readonly IdentityFunction Identity;
        public readonly SelfTestFunction SelfTest;

        public Exports(string path)
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("System audio capture requires macOS 15 or later.");
            library = NativeLibrary.Load(path);
            Start = Load<StartFunction>("dameter_audio_start");
            Stop = Load<HandleFunction>("dameter_audio_stop");
            Release = Load<HandleFunction>("dameter_audio_release");
            Identity = Load<IdentityFunction>("dameter_audio_identity");
            SelfTest = Load<SelfTestFunction>("dameter_audio_self_test");
        }

        private T Load<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint StartFunction(nint context, NativePcmCallback pcmCallback, NativeStatusCallback statusCallback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HandleFunction(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IdentityFunction(nint buffer, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SelfTestFunction();
}

/// <summary>
/// Meters ScreenCaptureKit PCM inside the main app, whose identity owns macOS
/// permission. Audio is analyzed in memory and discarded after each callback.
/// </summary>
public sealed class NativeMacAudioService : IDisposable, IAsyncDisposable
{
    private const string MainBundleIdentifier = "org.atstti.DAMeter";
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private readonly object lifecycleGate = new();
    private readonly object samplesGate = new();
    private readonly object statusGate = new();
    private readonly SemaphoreSlim retrySignal = new(0, 1);
    private readonly INativeAudioBridge bridge;
    private readonly Action<Exception>? log;
    private readonly TimeSpan shutdownTimeout;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private NativeSession? activeSession;
    private volatile bool disposed;
    private string? status;
    private double leftPeak;
    private double rightPeak;
    private WeightedRmsAccumulator leftRms;
    private WeightedRmsAccumulator rightRms;
    private long frameCount;
    private long lastBufferTimestamp;
    private AudioLevels lastLevels;

    public NativeMacAudioService(Action<Exception>? log = null, INativeAudioBridge? bridge = null,
        TimeSpan? shutdownTimeout = null)
    {
        this.log = log;
        this.bridge = bridge ?? new NativeMacAudioBridge();
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (this.shutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
    }

    /// <summary>Subscribers must dispatch to the UI asynchronously.</summary>
    public event Action<string>? StatusChanged;
    public string? Status { get { lock (statusGate) return status; } }

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { IsCompleted: false }) return;
            workerCancellation?.Dispose();
            workerCancellation = new CancellationTokenSource();
            var cancellation = workerCancellation.Token;
            worker = Task.Run(() => RunAsync(cancellation));
        }
    }

    /// <summary>Explicitly retries permission, installation, or capture failures.</summary>
    public void Retry()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (retrySignal.CurrentCount == 0) retrySignal.Release();
        }
    }

    /// <summary>Checks the main-app identity and PCM conversion without asking for permission.</summary>
    public Task VerifyNativeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyNative();
        }, cancellationToken);
    }

    /// <summary>Aggregates frame-weighted RMS and expires measurements after 150 ms.</summary>
    public AudioLevels DrainLevels()
    {
        lock (samplesGate)
        {
            if (frameCount > 0)
                lastLevels = new AudioLevels(leftPeak, rightPeak, leftRms.GetRms(frameCount), rightRms.GetRms(frameCount));
            ClearPendingSamples();
            return lastBufferTimestamp != 0 && Stopwatch.GetElapsedTime(lastBufferTimestamp).TotalMilliseconds <= 150
                ? lastLevels : AudioLevels.Silence;
        }
    }

    /// <summary>Requests shutdown without blocking the UI or waiting for a macOS dialog.</summary>
    public void Stop()
    {
        lock (lifecycleGate)
        {
            workerCancellation?.Cancel();
            activeSession = null;
        }
        ResetMeasurements();
    }

    /// <summary>
    /// Waits a bounded time. A delayed native session remains rooted until its
    /// final stopped callback, at which point its native retain is released.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? current;
        lock (lifecycleGate)
        {
            workerCancellation?.Cancel();
            activeSession = null;
            current = worker;
        }
        ResetMeasurements();
        if (current is null) return;
        try { await current.WaitAsync(shutdownTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { /* Native callbacks still own the pending session. */ }
    }

    private void VerifyNative()
    {
        var identity = bridge.GetIdentity();
        if (!string.Equals(identity, MainBundleIdentifier, StringComparison.Ordinal))
            throw new InvalidOperationException($"The native audio library must run in {MainBundleIdentifier}; actual identity: {identity}.");
        if (bridge.SelfTest() != 0) throw new InvalidDataException("The native audio PCM conversion self-test failed.");
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { VerifyNative(); }
                catch (Exception exception)
                {
                    SafeLog(exception);
                    PublishStatus("音声取得ライブラリまたはアプリ構成を確認し、Mac 配布版を再展開して再試行してください。");
                    await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }

                ResetMeasurements();
                PublishStatus("システム音声を準備しています。初回は macOS の収録権限を許可してください。");
                NativeSession session;
                lock (lifecycleGate)
                {
                    stop.ThrowIfCancellationRequested();
                    session = new NativeSession(bridge, ReceivePcm, ReceiveStatus, SafeLog);
                    activeSession = session;
                }
                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var retry = retrySignal.WaitAsync(sessionCancellation.Token);
                var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellation.Token);
                var restartRequested = false;
                try
                {
                    session.Start();
                    var completed = await Task.WhenAny(session.Completion, retry, cancelled).ConfigureAwait(false);
                    restartRequested = completed == retry;
                    if (completed != session.Completion) session.RequestStop();
                    // Cancellation cannot revoke native callback ownership. An OS
                    // permission dialog may finish long after StopAsync times out.
                    await session.Completion.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SafeLog(exception);
                    session.RequestStop();
                    PublishStatus("システム音声の取得に失敗しました。再接続してください。");
                }
                finally
                {
                    sessionCancellation.Cancel();
                    // SemaphoreSlim can complete its cancellation-aware waiter
                    // asynchronously after Release. Observe the settled result
                    // so Retry racing with final0 cannot be consumed and lost.
                    try
                    {
                        await retry.ConfigureAwait(false);
                        restartRequested = true;
                    }
                    catch (OperationCanceledException) { }
                    lock (lifecycleGate)
                    {
                        if (ReferenceEquals(activeSession, session)) activeSession = null;
                    }
                    ResetMeasurements();
                }

                if (stop.IsCancellationRequested) break;
                if (restartRequested) continue;
                // Failures wait for an explicit retry, preventing repeated OS
                // permission requests after refusal or an invalid installation.
                await retrySignal.WaitAsync(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { ResetMeasurements(); }
    }

    private void ReceivePcm(NativeSession session, nint pointer, int byteCount)
    {
        if (disposed || session.StopRequested || !ReferenceEquals(Volatile.Read(ref activeSession), session)) return;
        if (pointer == 0 || byteCount <= 0 || byteCount > MaximumPayloadBytes || byteCount % 8 != 0)
        {
            var exception = new InvalidDataException("Native audio must be stereo Float32LE, at most 4 MiB, in complete eight-byte frames.");
            SafeLog(exception);
            PublishStatus("音声データの形式が不正です。再接続してください。");
            session.RequestStop();
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        AudioLevels levels;
        try
        {
            Marshal.Copy(pointer, buffer, 0, byteCount);
            levels = AudioSampleAnalyzer.Analyze(buffer.AsSpan(0, byteCount), 32, 2, SampleEncoding.IeeeFloat);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

        bool firstBuffer;
        lock (samplesGate)
        {
            if (disposed || !ReferenceEquals(Volatile.Read(ref activeSession), session)) return;
            firstBuffer = lastBufferTimestamp == 0;
            var frames = byteCount / 8;
            leftPeak = Math.Max(leftPeak, levels.LeftPeak);
            rightPeak = Math.Max(rightPeak, levels.RightPeak);
            leftRms.Add(levels.LeftRms, frames);
            rightRms.Add(levels.RightRms, frames);
            frameCount += frames;
            lastBufferTimestamp = Stopwatch.GetTimestamp();
        }
        session.PublishSignal(levels.LeftPeak > 0 || levels.RightPeak > 0, firstBuffer,
            hasSignal => PublishStatus(hasSignal ? "システム音声を計測中（ScreenCaptureKit・ステレオ）" : "音声データを受信しています（現在は無音です）"));
    }

    private void ReceiveStatus(NativeSession session, int code, string message)
    {
        if (disposed || session.StopRequested || !ReferenceEquals(Volatile.Read(ref activeSession), session)) return;
        if (code == 77)
        {
            ResetMeasurements();
            if (message.Length != 0) SafeLog(new UnauthorizedAccessException(message));
            PublishStatus("macOSの画面・システム音声の収録で DAMeter の許可を確認してください。許可済みの場合も⌘Qで終了して起動し直してください。『権限設定』から確認できます。");
        }
        else if (code == 70)
        {
            ResetMeasurements();
            if (message.Length != 0) SafeLog(new IOException(message));
            PublishStatus(message.Length == 0 ? "システム音声の取得に失敗しました。再接続してください。" : message);
        }
        else if (code is 1 or 2)
        {
            lock (samplesGate) { if (lastBufferTimestamp != 0) return; }
            PublishStatus(message.Length == 0 ? "最初のシステム音声データを待っています。" : message);
        }
    }

    private void ClearPendingSamples()
    {
        leftPeak = rightPeak = 0;
        leftRms = rightRms = default;
        frameCount = 0;
    }

    private void ResetMeasurements()
    {
        lock (samplesGate)
        {
            ClearPendingSamples();
            lastLevels = AudioLevels.Silence;
            lastBufferTimestamp = 0;
        }
    }

    private void PublishStatus(string value)
    {
        lock (statusGate)
        {
            if (disposed || status == value) return;
            status = value;
        }
        try { StatusChanged?.Invoke(value); }
        catch (Exception exception) { SafeLog(exception); }
    }

    private void SafeLog(Exception exception)
    {
        try { log?.Invoke(exception); }
        catch { /* Diagnostics cannot escape native callbacks. */ }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            workerCancellation?.Cancel();
            activeSession = null;
        }
        ResetMeasurements();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class NativeSession
    {
        // A GCHandle cannot be freed at a timeout: native may still call back.
        // Opaque IDs and this registry retain delegates, context and bridge until0.
        private static readonly ConcurrentDictionary<nint, NativeSession> Pending = new();
        private static readonly NativePcmCallback PcmCallback = DispatchPcm;
        private static readonly NativeStatusCallback StatusCallback = DispatchStatus;
        private static long nextContext;
        private readonly object gate = new();
        private readonly INativeAudioBridge bridge;
        private readonly Action<NativeSession, nint, int> pcm;
        private readonly Action<NativeSession, int, string> status;
        private readonly Action<Exception> log;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly nint context;
        private nint handle;
        private bool handlePublished;
        private bool stopped;
        private bool stopRequested;
        private bool? lastSignal;
        private long lastSignalTimestamp;

        public NativeSession(INativeAudioBridge bridge, Action<NativeSession, nint, int> pcm,
            Action<NativeSession, int, string> status, Action<Exception> log)
        {
            this.bridge = bridge;
            this.pcm = pcm;
            this.status = status;
            this.log = log;
            context = checked((nint)Interlocked.Increment(ref nextContext));
            if (!Pending.TryAdd(context, this)) throw new InvalidOperationException("Duplicate native callback context.");
        }

        public Task Completion => completion.Task;
        public bool StopRequested { get { lock (gate) return stopRequested || stopped; } }

        public void Start()
        {
            lock (gate)
            {
                try
                {
                    handle = bridge.Start(context, PcmCallback, StatusCallback);
                    handlePublished = true;
                    if (handle == 0) throw new IOException("The native audio library returned an empty capture handle.");
                    if (stopped) ReleaseStopped();
                    else if (stopRequested) bridge.Stop(handle);
                }
                catch
                {
                    if (handle == 0)
                    {
                        stopped = true;
                        Pending.TryRemove(context, out _);
                        completion.TrySetResult();
                    }
                    throw;
                }
            }
        }

        public void RequestStop()
        {
            lock (gate)
            {
                if (stopRequested || stopped) return;
                stopRequested = true;
                // Serialize Stop with Release so a late request cannot use a
                // handle released by a concurrent final callback.
                if (handle != 0)
                {
                    try { bridge.Stop(handle); }
                    catch (Exception exception) { log(exception); }
                }
            }
        }

        public void PublishSignal(bool hasSignal, bool firstBuffer, Action<bool> publish)
        {
            bool changed;
            lock (gate)
            {
                changed = firstBuffer || lastSignal is null ||
                    (lastSignal != hasSignal && Stopwatch.GetElapsedTime(lastSignalTimestamp).TotalSeconds >= 1);
                if (changed)
                {
                    lastSignal = hasSignal;
                    lastSignalTimestamp = Stopwatch.GetTimestamp();
                }
            }
            if (changed) publish(hasSignal);
        }

        private void ReceiveStatus(int code, nint message)
        {
            if (code == 0)
            {
                lock (gate)
                {
                    if (stopped) return;
                    stopped = true;
                    if (handlePublished) ReleaseStopped();
                }
                return;
            }
            lock (gate) { if (stopped) return; }
            var value = message == 0 ? string.Empty : Marshal.PtrToStringUTF8(message) ?? string.Empty;
            status(this, code, value.Length > 1000 ? value[..1000] : value);
        }

        private void ReleaseStopped()
        {
            if (handle == 0) return;
            try
            {
                bridge.Release(handle);
                handle = 0;
                Pending.TryRemove(context, out _);
            }
            catch (Exception exception) { log(exception); }
            finally { completion.TrySetResult(); }
        }

        private static void DispatchPcm(nint context, nint samples, int byteCount)
        {
            if (!Pending.TryGetValue(context, out var session)) return;
            try { session.pcm(session, samples, byteCount); }
            catch (Exception exception)
            {
                session.log(exception);
                session.RequestStop();
            }
        }

        private static void DispatchStatus(nint context, int code, nint message)
        {
            if (!Pending.TryGetValue(context, out var session)) return;
            try { session.ReceiveStatus(code, message); }
            catch (Exception exception)
            {
                session.log(exception);
                session.RequestStop();
            }
        }
    }

    private struct WeightedRmsAccumulator
    {
        private double scale;
        private double scaledSquares;
        public void Add(double rms, int frames)
        {
            if (rms <= 0 || !double.IsFinite(rms) || frames <= 0) return;
            if (rms > scale)
            {
                var ratio = scale / rms;
                scaledSquares = scaledSquares * ratio * ratio + frames;
                scale = rms;
            }
            else
            {
                var ratio = rms / scale;
                scaledSquares += frames * ratio * ratio;
            }
        }
        public readonly double GetRms(long frames) => frames <= 0 ? 0 : scale * Math.Sqrt(Math.Clamp(scaledSquares / frames, 0, 1));
    }
}
