using DAMeter.Core;
using Xunit;

namespace DAMeter.Core.Tests;

public sealed class MeterBallisticsTests
{
    [Fact]
    public void DecibelsUseAmplitudeAndPreserveNegativeInfinityForSilence()
    {
        Assert.Equal(double.NegativeInfinity, MeterBallistics.ToDb(0));
        Assert.Equal(0, MeterBallistics.ToDb(1));
        Assert.Equal(-6.020599913, MeterBallistics.ToDb(0.5), 8);
        Assert.Equal(-20, MeterBallistics.ToDb(0.1), 10);
    }

    [Fact]
    public void PeakAttackIsImmediateAndDecayDependsOnElapsedTime()
    {
        var meter = new MeterBallistics();
        meter.Update(new AudioLevels(1, 0.5, 0.5, 0.25), 1.0 / 60, 1, 24, -18);
        Assert.Equal(0, meter.LeftPeakDb);
        Assert.Equal(MeterBallistics.ToDb(0.5), meter.RightPeakDb, 8);

        meter.Update(AudioLevels.Silence, 0.25, 1, 24, -18);
        Assert.Equal(-6, meter.LeftPeakDb, 8);
        Assert.Equal(MeterBallistics.ToDb(0.5) - 6, meter.RightPeakDb, 8);
    }

    [Fact]
    public void HoldAndClipRemainAfterPeakFallsAndEventuallyExpire()
    {
        var meter = new MeterBallistics();
        meter.Update(new AudioLevels(1, 0.25, 0.5, 0.125), 0.01, 0.5, 24, -18);
        meter.Update(AudioLevels.Silence, 0.2, 0.5, 24, -18);

        Assert.Equal(0, meter.LeftHoldDb);
        Assert.True(meter.LeftClip);
        Assert.False(meter.RightClip);

        for (int frame = 0; frame < 150; frame++)
        {
            meter.Update(AudioLevels.Silence, 0.02, 0.5, 24, -18);
        }

        Assert.True(meter.LeftHoldDb < 0);
        Assert.False(meter.LeftClip);
    }

    [Fact]
    public void VuResponseIsSmoothAndReferenceControlsItsSteadyReading()
    {
        var meter = new MeterBallistics();
        double rms = Math.Pow(10, -18.0 / 20);
        var input = new AudioLevels(rms, rms, rms, rms);
        meter.Update(input, 0.01, 1, 24, -18);
        double early = meter.LeftVuDb;
        for (int frame = 0; frame < 600; frame++)
        {
            meter.Update(input, 0.01, 1, 24, -18);
        }

        Assert.True(early < -1);
        Assert.InRange(meter.LeftVuDb, -0.01, 0.01);
        meter.Update(input, 0.01, 1, 24, -24);
        Assert.InRange(meter.LeftVuDb, 5.99, 6.01);
    }

    [Fact]
    public void VuAndPeakDecayAreStableAcrossRenderFrameRates()
    {
        var slow = new MeterBallistics();
        var fast = new MeterBallistics();
        var input = new AudioLevels(0.5, 0.25, 0.25, 0.125);
        for (int frame = 0; frame < 30; frame++)
        {
            slow.Update(input, 1.0 / 30, 1, 24, -18);
        }

        for (int frame = 0; frame < 120; frame++)
        {
            fast.Update(input, 1.0 / 120, 1, 24, -18);
        }

        Assert.Equal(slow.LeftVuDb, fast.LeftVuDb, 7);
        Assert.Equal(slow.RightVuDb, fast.RightVuDb, 7);

        for (int frame = 0; frame < 15; frame++)
        {
            slow.Update(AudioLevels.Silence, 1.0 / 30, 1, 24, -18);
        }

        for (int frame = 0; frame < 60; frame++)
        {
            fast.Update(AudioLevels.Silence, 1.0 / 120, 1, 24, -18);
        }

        Assert.Equal(slow.LeftPeakDb, fast.LeftPeakDb, 7);
        Assert.Equal(slow.LeftVuDb, fast.LeftVuDb, 7);
    }
}
