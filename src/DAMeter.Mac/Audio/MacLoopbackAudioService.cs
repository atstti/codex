using System.Diagnostics;
using System.Text;
using DAMeter.Core;

namespace DAMeter.Mac.Audio;

/// <summary>
/// Captures macOS system audio through the bundled ScreenCaptureKit helper.
/// PCM travels through an anonymous process pipe only, is analyzed in memory,
/// then discarded. Microphone input, audio files, and network access are unused.
/// </summary>
public sealed class MacLoopbackAudioService : IDisposable
{
    private readonly object lifecycleGate = new();
    private readonly object samplesGate = new();
    private readonly object statusGate = new();
    private readonly SemaphoreSlim retrySignal = new(0, 1);
    private readonly Action<Exception>? log;
    private readonly string helperPath;
    private readonly bool explicitHelperPath;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private volatile bool disposed;
    private string? lastStatus;
    private double leftPeak;
    private double rightPeak;
    private WeightedRmsAccumulator leftRms;
    private WeightedRmsAccumulator rightRms;
    private long frameCount;
    private AudioLevels lastLevels;
    private long lastBufferTimestamp;

    public MacLoopbackAudioService(Action<Exception>? log = null, string? helperPath = null)
    {
        this.log = log;
        explicitHelperPath = helperPath is not null;
        this.helperPath = helperPath is null ? ResolveBundledHelper() : Path.GetFullPath(helperPath);
    }

    /// <summary>Raised on a background thread; subscribers must dispatch to UI asynchronously.</summary>
    public event Action<string>? StatusChanged;

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { IsCompleted: false }) return;
            workerCancellation?.Dispose();
            workerCancellation = new CancellationTokenSource();
            var token = workerCancellation.Token;
            worker = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>Explicitly retries permission or installation failures, or reconnects active capture.</summary>
    public void Retry()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (retrySignal.CurrentCount == 0) retrySignal.Release();
        }
    }

    /// <summary>Permission-free packaged-agent identity check used by Mac CI.</summary>
    public async Task VerifyAgentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = CreateHelperProcess("--agent-check");
        if (!process.Start()) throw new IOException("The capture-agent identity check did not start.");
        var diagnostics = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await diagnostics.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !text.Contains("bundle=org.atstti.DAMeter.AudioCapture", StringComparison.Ordinal) ||
                !text.Contains("activationPolicy=accessory", StringComparison.Ordinal) ||
                (await output.WaitAsync(timeout.Token).ConfigureAwait(false)).Length != 0)
                throw new InvalidOperationException($"The packaged capture agent has an invalid identity: {text.Trim()}");
        }
        finally
        {
            timeout.Cancel();
            await StopHelperAsync(process).ConfigureAwait(false);
            await ObserveTaskAsync(Task.WhenAll(diagnostics, output)).ConfigureAwait(false);
        }
    }

    /// <summary>Aggregates callback frames and retains measurements for at most 150 ms.</summary>
    public AudioLevels DrainLevels()
    {
        lock (samplesGate)
        {
            if (frameCount > 0)
                lastLevels = new AudioLevels(leftPeak, rightPeak,
                    leftRms.GetRms(frameCount), rightRms.GetRms(frameCount));
            ClearPendingSamples();
            return lastBufferTimestamp != 0 && Stopwatch.GetElapsedTime(lastBufferTimestamp).TotalMilliseconds <= 150
                ? lastLevels : AudioLevels.Silence;
        }
    }

    public void Stop()
    {
        Task? currentWorker;
        CancellationTokenSource? currentCancellation;
        lock (lifecycleGate)
        {
            currentWorker = worker;
            currentCancellation = workerCancellation;
            currentCancellation?.Cancel();
        }

        // The worker never synchronously dispatches to the UI and owns all child
        // cleanup. Its child shutdown has a bounded grace period before a kill.
        try { currentWorker?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            lock (lifecycleGate)
            {
                if (ReferenceEquals(worker, currentWorker))
                {
                    worker = null;
                    workerCancellation = null;
                    currentCancellation?.Dispose();
                }
            }
            ResetMeasurements();
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!OperatingSystem.IsMacOS() && !explicitHelperPath)
                {
                    PublishStatus("Mac版のシステム音声取得には macOS 15 以降が必要です。");
                    await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                if (!File.Exists(helperPath))
                {
                    PublishStatus("音声取得ヘルパーが見つかりません。Mac 配布版を再展開して再試行してください。");
                    await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var outcome = await RunCaptureSessionAsync(stop).ConfigureAwait(false);
                    if (outcome.RestartRequested) continue;
                    if (outcome.ExitCode == 77)
                    {
                        PublishStatus("画面・システム音声の収録権限が必要です。システム設定で許可し、「音声を再接続」を押してください。");
                        await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                        continue;
                    }
                    if (outcome.ExitCode == 78)
                    {
                        PublishStatus("音声ヘルパーのアプリ構成が不正です。最新版を再インストールし、再接続してください。");
                        await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                        continue;
                    }
                    PublishStatus("音声取得が停止しました。自動的に再接続します。");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (UnauthorizedAccessException exception)
                {
                    SafeLog(exception);
                    PublishStatus("音声取得ヘルパーを起動できません。Mac 配布版の配置と実行権限を確認し、再試行してください。");
                    await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    SafeLog(exception);
                    PublishStatus("音声取得ヘルパーを起動できません。Mac 配布版を再展開し、再試行してください。");
                    await retrySignal.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                catch (Exception exception)
                {
                    SafeLog(exception);
                    PublishStatus("音声取得に失敗しました。自動的に再接続します。");
                }

                await WaitForRetryOrDelayAsync(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { ResetMeasurements(); }
    }

    private async Task<(int ExitCode, bool RestartRequested)> RunCaptureSessionAsync(CancellationToken stop)
    {
        using var process = CreateHelperProcess();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (!process.Start()) throw new IOException("The macOS capture helper did not start.");
        process.StandardInput.AutoFlush = true;
        ResetMeasurements();
        PublishStatus("システム音声を準備しています。初回は macOS の収録権限を許可してください。");
        var frames = ReadAudioAsync(process.StandardOutput.BaseStream, sessionCancellation.Token);
        var errors = ReadStatusAsync(process.StandardError, sessionCancellation.Token);
        var exited = process.WaitForExitAsync(sessionCancellation.Token);
        var retry = retrySignal.WaitAsync(sessionCancellation.Token);
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellation.Token);
        var restartRequested = false;
        try
        {
            var completed = await Task.WhenAny(frames, exited, retry, stopped).ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            restartRequested = completed == retry;
            if (completed == frames) await frames.ConfigureAwait(false);
        }
        finally
        {
            // Cancelling pending pipe reads and the retry waiter prevents a past
            // session from consuming the next user's explicit retry request.
            sessionCancellation.Cancel();
            await StopHelperAsync(process).ConfigureAwait(false);
            await ObserveTaskAsync(Task.WhenAll(frames, errors, exited, retry, stopped)).ConfigureAwait(false);
            ResetMeasurements();
        }
        restartRequested |= retry.IsCompletedSuccessfully;
        return (process.HasExited ? process.ExitCode : 1, restartRequested);
    }

    private Process CreateHelperProcess(params string[] arguments)
    {
        var info = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        // Finder's parent-app identity must not override the agent's own bundle.
        // Authentication, proxy and other inherited settings remain untouched.
        info.Environment.Remove("__CFBundleIdentifier");
        info.Environment.Remove("__CFBundleIdentifierOverride");
        info.Environment.Remove("CFProcessPath");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return new Process { StartInfo = info };
    }

    private async Task ReadAudioAsync(Stream stream, CancellationToken cancellationToken)
    {
        var receivedAudio = false;
        while (await PcmFrameProtocol.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is { } payload)
        {
            var levels = AudioSampleAnalyzer.Analyze(payload, 32, 2, SampleEncoding.IeeeFloat);
            var frames = payload.Length / (2 * sizeof(float));
            lock (samplesGate)
            {
                leftPeak = Math.Max(leftPeak, levels.LeftPeak);
                rightPeak = Math.Max(rightPeak, levels.RightPeak);
                leftRms.Add(levels.LeftRms, frames);
                rightRms.Add(levels.RightRms, frames);
                frameCount += frames;
                lastBufferTimestamp = Stopwatch.GetTimestamp();
            }
            if (!receivedAudio)
            {
                receivedAudio = true;
                PublishStatus("システム音声を計測中（ScreenCaptureKit・ステレオ）");
            }
        }
    }

    private async Task ReadStatusAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var text = line.Length > 1000 ? line[..1000] : line;
            PublishStatus(text.StartsWith("STATUS:", StringComparison.Ordinal) ? text[7..].Trim() : text);
            if (text.StartsWith("ERROR:", StringComparison.Ordinal)) SafeLog(new IOException(text));
        }
    }

    private static async Task StopHelperAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            // A helper can be waiting in a macOS permission dialog. Neither a
            // pipe write nor its eventual process exit may block shutdown forever.
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await process.StandardInput.WriteLineAsync("stop".AsMemory(), writeTimeout.Token)
                    .WaitAsync(writeTimeout.Token).ConfigureAwait(false);
                process.StandardInput.BaseStream.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) { }
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        finally
        {
            // Another process may inherit a pipe handle. Explicitly close the
            // parent ends even if the helper exited before cleanup began.
            ClosePipe(process.StandardInput.BaseStream);
            ClosePipe(process.StandardOutput.BaseStream);
            ClosePipe(process.StandardError.BaseStream);
        }
    }

    private static void ClosePipe(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
    }

    private async Task WaitForRetryOrDelayAsync(CancellationToken stop)
    {
        await retrySignal.WaitAsync(TimeSpan.FromSeconds(2), stop).ConfigureAwait(false);
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
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

    private void PublishStatus(string status)
    {
        lock (statusGate)
        {
            if (disposed || lastStatus == status) return;
            lastStatus = status;
        }
        try { StatusChanged?.Invoke(status); }
        catch (Exception exception) { SafeLog(exception); }
    }

    private void SafeLog(Exception exception)
    {
        try { log?.Invoke(exception); }
        catch { /* Diagnostics must not prevent capture shutdown or recovery. */ }
    }

    private static string ResolveBundledHelper()
    {
        var appDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        if (appDirectory.Name == "MacOS" && appDirectory.Parent?.Name == "Contents")
        {
            // A bare executable here would inherit the main app's identity in
            // LaunchServices/TCC. Packaged apps must use the dedicated agent.
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers",
                "DAMeter.AudioCapture.app", "Contents", "MacOS", "DAMeter.AudioCapture"));
        }
        var besideApp = Path.Combine(AppContext.BaseDirectory, "DAMeter.AudioCapture");
        if (File.Exists(besideApp)) return besideApp;
        var helpersDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "DAMeter.AudioCapture"));
        return File.Exists(helpersDirectory) ? helpersDirectory : besideApp;
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop();
        retrySignal.Dispose();
        GC.SuppressFinalize(this);
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
