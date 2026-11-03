namespace DAMeter.Core;

/// <summary>
/// Time-based display envelopes, independent of the audio callback and UI rate.
/// The VU-style response uses a 300 ms exponential integration constant; it is
/// intentionally not a standards-compliant VU meter.
/// </summary>
public sealed class MeterBallistics
{
    private const double VuTimeConstantSeconds = 0.300;
    private const double ClipHoldSeconds = 1;
    private readonly Channel _left = new();
    private readonly Channel _right = new();

    public double LeftPeakDb => _left.PeakDb;
    public double RightPeakDb => _right.PeakDb;
    public double LeftHoldDb => _left.HoldDb;
    public double RightHoldDb => _right.HoldDb;
    public double LeftVuDb { get; private set; } = double.NegativeInfinity;
    public double RightVuDb { get; private set; } = double.NegativeInfinity;
    public bool LeftClip => _left.ClipRemaining > 0;
    public bool RightClip => _right.ClipRemaining > 0;

    public void Update(
        AudioLevels levels,
        double elapsedSeconds,
        double holdSeconds,
        double decayDbPerSecond,
        double vuReferenceDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(holdSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(decayDbPerSecond);
        if (!double.IsFinite(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!double.IsFinite(holdSeconds))
            throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        if (!double.IsFinite(decayDbPerSecond))
            throw new ArgumentOutOfRangeException(nameof(decayDbPerSecond));
        if (!double.IsFinite(vuReferenceDb))
            throw new ArgumentOutOfRangeException(nameof(vuReferenceDb));

        UpdateChannel(_left, levels.LeftPeak, levels.LeftRms, elapsedSeconds, holdSeconds, decayDbPerSecond);
        UpdateChannel(_right, levels.RightPeak, levels.RightRms, elapsedSeconds, holdSeconds, decayDbPerSecond);
        LeftVuDb = ToDb(_left.VuAmplitude) - vuReferenceDb;
        RightVuDb = ToDb(_right.VuAmplitude) - vuReferenceDb;
    }

    public static double ToDb(double amplitude)
    {
        return double.IsFinite(amplitude) && amplitude > 0
            ? 20 * Math.Log10(amplitude)
            : double.NegativeInfinity;
    }

    private static void UpdateChannel(
        Channel state,
        double samplePeak,
        double rms,
        double elapsed,
        double hold,
        double decay)
    {
        samplePeak = Sanitize(samplePeak);
        rms = Sanitize(rms);
        var inputDb = ToDb(samplePeak);
        state.PeakDb = Math.Max(inputDb, state.PeakDb - decay * elapsed);

        if (inputDb >= state.HoldDb)
        {
            state.HoldDb = inputDb;
            state.HoldRemaining = hold;
        }
        else
        {
            var decaySeconds = Math.Max(0, elapsed - state.HoldRemaining);
            state.HoldRemaining = Math.Max(0, state.HoldRemaining - elapsed);
            state.HoldDb = Math.Max(state.PeakDb, state.HoldDb - decay * decaySeconds);
        }

        state.ClipRemaining = samplePeak >= 1
            ? ClipHoldSeconds
            : Math.Max(0, state.ClipRemaining - elapsed);

        // Integrate RMS energy, using scaled amplitudes to avoid overflow for
        // malformed finite input while preserving legitimate over-range audio.
        var weight = 1 - Math.Exp(-elapsed / VuTimeConstantSeconds);
        var scale = Math.Max(state.VuAmplitude, rms);
        if (scale == 0)
        {
            state.VuAmplitude = 0;
        }
        else
        {
            var previous = state.VuAmplitude / scale;
            var current = rms / scale;
            state.VuAmplitude = scale * Math.Sqrt(previous * previous * (1 - weight) + current * current * weight);
            if (state.VuAmplitude < 1e-12)
                state.VuAmplitude = 0;
        }
    }

    private static double Sanitize(double amplitude) => double.IsFinite(amplitude) && amplitude > 0 ? amplitude : 0;

    private sealed class Channel
    {
        public double PeakDb = double.NegativeInfinity;
        public double HoldDb = double.NegativeInfinity;
        public double HoldRemaining;
        public double ClipRemaining;
        public double VuAmplitude;
    }
}
