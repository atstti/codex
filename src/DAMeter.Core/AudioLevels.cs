namespace DAMeter.Core;

/// <summary>Linear sample peaks and root-mean-square amplitudes for a single audio block.</summary>
public readonly record struct AudioLevels(
    double LeftPeak,
    double RightPeak,
    double LeftRms,
    double RightRms)
{
    public static AudioLevels Silence => default;
}

public enum SampleEncoding
{
    Pcm,
    IeeeFloat
}
