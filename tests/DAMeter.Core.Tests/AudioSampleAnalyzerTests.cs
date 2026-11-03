using System.Buffers.Binary;
using DAMeter.Core;
using Xunit;

namespace DAMeter.Core.Tests;

public sealed class AudioSampleAnalyzerTests
{
    [Fact]
    public void StereoPcm16CalculatesIndependentPeakAndRms()
    {
        // Two frames: left +/- 0.5, right 0.25 followed by silence.
        byte[] samples = Pcm16(16384, 8192, -16384, 0);
        AudioLevels result = AudioSampleAnalyzer.Analyze(samples, 16, 2, SampleEncoding.Pcm);

        Assert.Equal(0.5, result.LeftPeak, 10);
        Assert.Equal(0.25, result.RightPeak, 10);
        Assert.Equal(0.5, result.LeftRms, 10);
        Assert.Equal(Math.Sqrt(0.25 * 0.25 / 2), result.RightRms, 10);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void SignedAndUnsignedPcmFormatsDecodeHalfScaleAndDuplicateMono(int bits)
    {
        byte[] data = bits switch
        {
            8 => [192, 64],
            16 => Pcm16(16384, -16384),
            24 => [0x00, 0x00, 0x40, 0x00, 0x00, 0xc0],
            32 => [0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0xc0],
            _ => throw new InvalidOperationException()
        };
        AudioLevels result = AudioSampleAnalyzer.Analyze(data, bits, 1, SampleEncoding.Pcm);

        Assert.Equal(0.5, result.LeftPeak, 10);
        Assert.Equal(0.5, result.LeftRms, 10);
        Assert.Equal(result.LeftPeak, result.RightPeak);
        Assert.Equal(result.LeftRms, result.RightRms);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void FloatFormatsHandleFullScaleAndNonFiniteSamples(int bits)
    {
        double[] samples = [1.25, -0.5, double.NaN, double.PositiveInfinity];
        byte[] data = Floats(samples, bits);
        AudioLevels result = AudioSampleAnalyzer.Analyze(data, bits, 2, SampleEncoding.IeeeFloat);

        Assert.Equal(1.25, result.LeftPeak, 10);
        Assert.Equal(0.5, result.RightPeak, 10);
        Assert.Equal(1.25 / Math.Sqrt(2), result.LeftRms, 10);
        Assert.Equal(0.5 / Math.Sqrt(2), result.RightRms, 10);
    }

    [Fact]
    public void MultichannelUsesFrontStereoWithoutMistakingOtherChannelsForNewFrames()
    {
        byte[] samples = Pcm16(8192, 16384, 32767, 32767, -8192, -16384, 32767, 32767);
        AudioLevels result = AudioSampleAnalyzer.Analyze(samples, 16, 4, SampleEncoding.Pcm);

        Assert.Equal(0.25, result.LeftPeak, 10);
        Assert.Equal(0.5, result.RightPeak, 10);
        Assert.Equal(0.25, result.LeftRms, 10);
        Assert.Equal(0.5, result.RightRms, 10);
    }

    [Fact]
    public void IncompleteTrailingFrameDoesNotAffectMeasurement()
    {
        byte[] complete = Pcm16(8192, -16384);
        byte[] incomplete = [.. complete, 0xff, 0x7f, 0xff];
        Assert.Equal(
            AudioSampleAnalyzer.Analyze(complete, 16, 2, SampleEncoding.Pcm),
            AudioSampleAnalyzer.Analyze(incomplete, 16, 2, SampleEncoding.Pcm));
    }

    [Fact]
    public void EmptyInputAndDigitalSilenceProduceSilence()
    {
        Assert.Equal(AudioLevels.Silence, AudioSampleAnalyzer.Analyze([], 16, 2, SampleEncoding.Pcm));
        Assert.Equal(AudioLevels.Silence, AudioSampleAnalyzer.Analyze(new byte[80], 16, 2, SampleEncoding.Pcm));
        Assert.Equal(AudioLevels.Silence, AudioSampleAnalyzer.Analyze([128, 128], 8, 1, SampleEncoding.Pcm));
    }

    [Fact]
    public void Pcm24MinimumSampleRetainsCorrectSignAndMagnitude()
    {
        AudioLevels result = AudioSampleAnalyzer.Analyze([0, 0, 0x80], 24, 1, SampleEncoding.Pcm);
        Assert.Equal(1, result.LeftPeak);
        Assert.Equal(1, result.LeftRms);
    }

    [Theory]
    [InlineData(12, SampleEncoding.Pcm)]
    [InlineData(24, SampleEncoding.IeeeFloat)]
    [InlineData(32, (SampleEncoding)99)]
    public void UnsupportedFormatsFailExplicitlyRatherThanMisreadingAudio(int bits, SampleEncoding encoding)
    {
        Assert.Throws<NotSupportedException>(() => AudioSampleAnalyzer.Analyze(new byte[16], bits, 2, encoding));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidChannelCountsAreRejected(int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSampleAnalyzer.Analyze([], 32, channels, SampleEncoding.Pcm));
    }

    private static byte[] Pcm16(params short[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
        }

        return bytes;
    }

    private static byte[] Floats(double[] values, int bits)
    {
        byte[] bytes = new byte[values.Length * bits / 8];
        for (int index = 0; index < values.Length; index++)
        {
            if (bits == 32)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), (float)values[index]);
            }
            else
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(index * 8), values[index]);
            }
        }

        return bytes;
    }
}
