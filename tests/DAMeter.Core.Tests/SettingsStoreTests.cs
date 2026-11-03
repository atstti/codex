using DAMeter.Core;
using Xunit;

namespace DAMeter.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DAMeter-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndReloadPreservesAllSettingsIncludingNegativeMonitorCoordinates()
    {
        var store = new SettingsStore(directory);
        var expected = new AppSettings
        {
            Meter = MeterMode.VerticalPeak,
            DeviceId = "デバイス-1",
            NormalBounds = new WindowBounds { Left = -1400, Top = -50, Width = 1000, Height = 700 },
            FloatingBounds = new WindowBounds { Left = 220, Top = 80, Width = 170, Height = 480 },
            Floating = true,
            BackgroundOpacity = 0.35,
            Topmost = true,
            ClickThrough = true,
            PeakHoldSeconds = 2.5,
            DecayDbPerSecond = 32,
            VuReferenceDb = -20
        };
        store.Save(expected);
        AppSettings actual = store.Load();

        Assert.Equal(expected.Meter, actual.Meter);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.NormalBounds, actual.NormalBounds);
        Assert.Equal(expected.FloatingBounds, actual.FloatingBounds);
        Assert.Equal(expected.Floating, actual.Floating);
        Assert.Equal(expected.BackgroundOpacity, actual.BackgroundOpacity);
        Assert.Equal(expected.Topmost, actual.Topmost);
        Assert.Equal(expected.ClickThrough, actual.ClickThrough);
        Assert.Equal(expected.PeakHoldSeconds, actual.PeakHoldSeconds);
        Assert.Equal(expected.DecayDbPerSecond, actual.DecayDbPerSecond);
        Assert.Equal(expected.VuReferenceDb, actual.VuReferenceDb);
    }

    [Fact]
    public void RepeatedSaveReplacesPreviousJsonAndLeavesNoTemporaryFiles()
    {
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { Meter = MeterMode.AnalogVu });
        store.Save(new AppSettings { Meter = MeterMode.VerticalPeak });

        Assert.Equal(MeterMode.VerticalPeak, store.Load().Meter);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("{ incomplete json")]
    [InlineData("null")]
    [InlineData("{\"Meter\":\"Nonexistent\"}")]
    public void CorruptSettingsLoadDefaultsAndWriteExceptionLog(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), json);
        AppSettings settings = new SettingsStore(directory).Load();

        Assert.Equal(MeterMode.HorizontalPeak, settings.Meter);
        Assert.Equal(1.5, settings.PeakHoldSeconds);
        Assert.True(File.Exists(Path.Combine(directory, "exceptions.log")));
    }

    [Fact]
    public void MissingSettingsLoadDefaultsWithoutWritingAnyFiles()
    {
        AppSettings settings = new SettingsStore(directory).Load();
        Assert.Equal(MeterMode.HorizontalPeak, settings.Meter);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void ValidateRejectsNonFiniteValuesAndClampsUnsafeRanges()
    {
        var settings = new AppSettings
        {
            Meter = (MeterMode)999,
            DeviceId = "  ",
            BackgroundOpacity = double.NaN,
            PeakHoldSeconds = -10,
            DecayDbPerSecond = double.PositiveInfinity,
            VuReferenceDb = 20,
            NormalBounds = new WindowBounds { Left = double.NaN, Top = -1200, Width = -1, Height = double.NaN }
        };
        settings.Validate();

        Assert.Equal(MeterMode.HorizontalPeak, settings.Meter);
        Assert.Null(settings.DeviceId);
        Assert.Equal(0.8, settings.BackgroundOpacity);
        Assert.Equal(0, settings.PeakHoldSeconds);
        Assert.Equal(24, settings.DecayDbPerSecond);
        Assert.Equal(0, settings.VuReferenceDb);
        Assert.True(double.IsFinite(settings.NormalBounds.Left));
        Assert.Equal(-1200, settings.NormalBounds.Top);
        Assert.True(settings.NormalBounds.Width >= 320);
        Assert.True(settings.NormalBounds.Height >= 240);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
