using DAMeter.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace DAMeter.Audio;

/// <summary>
/// Captures the render endpoint, never the microphone. Buffers are analyzed in memory
/// and immediately discarded; no audio is recorded, persisted, or transmitted.
/// </summary>
public sealed class LoopbackAudioService : IDisposable
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private readonly object _lifecycleGate = new();
    private readonly object _selectionGate = new();
    private readonly object _samplesGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<Exception>? _log;
    private Task? _worker;
    private string? _selectedDeviceId;
    private Session? _acceptedSession;
    private Session? _current;
    private double _leftPeak;
    private double _rightPeak;
    private WeightedRmsAccumulator _leftRms;
    private WeightedRmsAccumulator _rightRms;
    private long _frameCount;
    private AudioLevels _lastLevels = AudioLevels.Silence;
    private long _lastBufferTimestamp;
    private string? _lastStatus;
    private string? _lastError;
    private volatile bool _disposed;

    public LoopbackAudioService(Action<Exception>? log = null) => _log = log;

    /// <summary>Raised on the capture worker; UI subscribers must dispatch asynchronously.</summary>
    public event Action<string>? StatusChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }

        return result;
    }

    /// <summary>
    /// Null/empty follows the default multimedia render endpoint. A missing manually
    /// selected endpoint temporarily falls back to the default and is retried later.
    /// </summary>
    public void SelectDevice(string? id)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_selectionGate)
            {
                _selectedDeviceId = string.IsNullOrWhiteSpace(id) ? null : id;
            }

            _wake.Set();
        }
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(Run);
        }
    }

    /// <summary>
    /// Retrieves all complete frames received since the previous call. Peak is the
    /// maximum sample magnitude; RMS is weighted by frame count, not callback count.
    /// A measurement remains fresh for at most 150 ms to bridge WASAPI buffer
    /// batching between render frames. After that, missing buffers yield silence.
    /// </summary>
    public AudioLevels DrainLevels()
    {
        lock (_samplesGate)
        {
            if (_frameCount > 0)
            {
                _lastLevels = new AudioLevels(_leftPeak, _rightPeak,
                    _leftRms.GetRms(_frameCount),
                    _rightRms.GetRms(_frameCount));
            }

            ClearSamples();
            return _lastBufferTimestamp != 0 &&
                   Stopwatch.GetElapsedTime(_lastBufferTimestamp).TotalMilliseconds <= 150
                ? _lastLevels
                : AudioLevels.Silence;
        }
    }

    private void Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    ReconcileDevice();
                }
                catch (Exception exception)
                {
                    StopCurrent();
                    LogOnce(exception);
                    PublishStatus("音声出力を待っています。自動的に再接続します。");
                }

                if (!_stop.IsCancellationRequested)
                {
                    _wake.WaitOne(TimeSpan.FromSeconds(2));
                }
            }
        }
        finally
        {
            StopCurrent();
        }
    }

    private void ReconcileDevice()
    {
        string? selected;
        lock (_selectionGate)
        {
            selected = _selectedDeviceId;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? target = null;
        var fallback = false;
        try
        {
            if (selected is not null)
            {
                try
                {
                    target = enumerator.GetDevice(selected);
                    if (target.State != DeviceState.Active)
                    {
                        target.Dispose();
                        target = null;
                    }
                }
                catch (Exception exception)
                {
                    // A removed endpoint can disappear from the enumerator altogether.
                    LogOnce(exception);
                    target?.Dispose();
                    target = null;
                }

                fallback = target is null;
            }

            target ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_current is { Failed: false } current && current.DeviceId == target.ID)
            {
                PublishStatus(StatusText(target.FriendlyName, fallback));
                return;
            }

            StopCurrent();
            var capture = new WasapiLoopbackCapture(target);
            var format = capture.WaveFormat;
            SampleEncoding encoding;
            try
            {
                encoding = GetEncoding(format);
                if (format.Channels < 1 || format.BlockAlign < 1 ||
                    format.BlockAlign != format.Channels * (format.BitsPerSample / 8) ||
                    (encoding == SampleEncoding.Pcm && format.BitsPerSample is not (8 or 16 or 24 or 32)) ||
                    (encoding == SampleEncoding.IeeeFloat && format.BitsPerSample is not (32 or 64)))
                {
                    throw new NotSupportedException($"Unsupported loopback format: {format}");
                }
            }
            catch
            {
                capture.Dispose();
                throw;
            }

            var session = new Session(target, capture, encoding);
            target = null; // Session now owns the endpoint.
            _current = session;
            capture.DataAvailable += (_, args) => OnDataAvailable(session, args);
            capture.RecordingStopped += (_, args) =>
            {
                if (!session.Active) return;
                session.Failed = true;
                if (args.Exception is not null) SafeLog(args.Exception);
                SignalWorker();
            };
            lock (_samplesGate)
            {
                ClearSamples();
                ResetFreshness();
                _acceptedSession = session;
            }

            capture.StartRecording();
            PublishStatus(StatusText(session.Device.FriendlyName, fallback));
        }
        finally
        {
            target?.Dispose();
        }
    }

    private static string StatusText(string name, bool fallback) => fallback
        ? $"選択した出力は未接続です。既定の出力を計測中: {name}"
        : $"システム音声を計測中: {name}";

    private static SampleEncoding GetEncoding(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Pcm) return SampleEncoding.Pcm;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return SampleEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == PcmSubFormat) return SampleEncoding.Pcm;
            if (extensible.SubFormat == FloatSubFormat) return SampleEncoding.IeeeFloat;
        }

        throw new NotSupportedException($"Unsupported loopback encoding: {format.Encoding}");
    }

    private void OnDataAvailable(Session session, WaveInEventArgs args)
    {
        if (!session.Active || session.Failed || args.BytesRecorded <= 0) return;
        try
        {
            var format = session.Capture.WaveFormat;
            var frames = args.BytesRecorded / format.BlockAlign;
            if (frames == 0) return;
            var levels = AudioSampleAnalyzer.Analyze(
                args.Buffer.AsSpan(0, frames * format.BlockAlign),
                format.BitsPerSample, format.Channels, session.Encoding);
            lock (_samplesGate)
            {
                if (_acceptedSession != session || !session.Active) return;
                _leftPeak = Math.Max(_leftPeak, levels.LeftPeak);
                _rightPeak = Math.Max(_rightPeak, levels.RightPeak);
                _leftRms.Add(levels.LeftRms, frames);
                _rightRms.Add(levels.RightRms, frames);
                _frameCount += frames;
                _lastBufferTimestamp = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception exception)
        {
            session.Failed = true;
            SafeLog(exception);
            SignalWorker();
        }
    }

    private void StopCurrent()
    {
        var session = _current;
        _current = null;
        lock (_samplesGate)
        {
            _acceptedSession = null;
            ClearSamples();
            ResetFreshness();
        }

        if (session is null) return;
        session.Active = false;
        // Do not hold the sample lock here: StopRecording may wait for a callback.
        try { session.Capture.StopRecording(); }
        catch (Exception exception) { SafeLog(exception); }
        try { session.Capture.Dispose(); }
        catch (Exception exception) { SafeLog(exception); }
        try { session.Device.Dispose(); }
        catch (Exception exception) { SafeLog(exception); }
    }

    private void ClearSamples()
    {
        _leftPeak = _rightPeak = 0;
        _leftRms = default;
        _rightRms = default;
        _frameCount = 0;
    }

    private void ResetFreshness()
    {
        _lastLevels = AudioLevels.Silence;
        _lastBufferTimestamp = 0;
    }

    private void PublishStatus(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception exception) { SafeLog(exception); }
    }

    private void LogOnce(Exception exception)
    {
        var key = exception.GetType().FullName + ":" + exception.Message;
        if (key == _lastError) return;
        _lastError = key;
        SafeLog(exception);
    }

    private void SafeLog(Exception exception)
    {
        try { _log?.Invoke(exception); }
        catch { /* Diagnostic failures must not interrupt capture recovery. */ }
    }

    private void SignalWorker()
    {
        lock (_lifecycleGate)
        {
            if (!_disposed) _wake.Set();
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _wake.Set();
            worker = _worker;
        }

        // All capture shutdown happens on its worker, outside callback locks.
        worker?.GetAwaiter().GetResult();
        _wake.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Session(MMDevice device, WasapiLoopbackCapture capture, SampleEncoding encoding)
    {
        public MMDevice Device { get; } = device;
        public string DeviceId { get; } = device.ID;
        public WasapiLoopbackCapture Capture { get; } = capture;
        public SampleEncoding Encoding { get; } = encoding;
        public volatile bool Active = true;
        public volatile bool Failed;
    }

    /// <summary>
    /// Scaled sum of squares keeps even very large finite float samples finite.
    /// The denominator includes silent frames, supplied by the shared frame count.
    /// </summary>
    private struct WeightedRmsAccumulator
    {
        private double _scale;
        private double _scaledSquares;

        public void Add(double rms, int frames)
        {
            if (rms <= 0 || !double.IsFinite(rms) || frames <= 0) return;
            if (rms > _scale)
            {
                var ratio = _scale / rms;
                _scaledSquares = _scaledSquares * ratio * ratio + frames;
                _scale = rms;
            }
            else
            {
                var ratio = rms / _scale;
                _scaledSquares += frames * ratio * ratio;
            }
        }

        public readonly double GetRms(long totalFrames) => totalFrames <= 0
            ? 0
            : _scale * Math.Sqrt(Math.Clamp(_scaledSquares / totalFrames, 0, 1));
    }
}
