namespace DAMeter.Core;

public enum MeterMode
{
    AnalogVu,
    HorizontalPeak,
    VerticalPeak
}

public sealed record WindowBounds
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 640;
}

public sealed class AppSettings
{
    public MeterMode Meter { get; set; } = MeterMode.HorizontalPeak;
    public string? DeviceId { get; set; }
    public WindowBounds NormalBounds { get; set; } = new();
    public WindowBounds FloatingBounds { get; set; } = new() { Width = 500, Height = 220 };
    public bool Floating { get; set; }
    public double BackgroundOpacity { get; set; } = 0.8;
    public bool Topmost { get; set; }
    public bool ClickThrough { get; set; }
    public double PeakHoldSeconds { get; set; } = 1.5;
    public double DecayDbPerSecond { get; set; } = 24;
    public double VuReferenceDb { get; set; } = -18;

    public void Validate()
    {
        if (!Enum.IsDefined(Meter))
        {
            Meter = MeterMode.HorizontalPeak;
        }

        DeviceId = string.IsNullOrWhiteSpace(DeviceId) ? null : DeviceId;
        NormalBounds = ValidateBounds(NormalBounds, new WindowBounds(), 320, 240);
        FloatingBounds = ValidateBounds(FloatingBounds, new WindowBounds { Width = 500, Height = 220 }, 120, 120);
        BackgroundOpacity = ClampFinite(BackgroundOpacity, 0, 1, 0.8);
        PeakHoldSeconds = ClampFinite(PeakHoldSeconds, 0, 30, 1.5);
        DecayDbPerSecond = ClampFinite(DecayDbPerSecond, 1, 120, 24);
        VuReferenceDb = ClampFinite(VuReferenceDb, -60, 0, -18);
    }

    private static WindowBounds ValidateBounds(WindowBounds? bounds, WindowBounds defaults, double minWidth, double minHeight)
    {
        bounds ??= defaults;
        // Negative desktop coordinates are valid on monitors left/above the primary monitor.
        bounds.Left = ClampFinite(bounds.Left, -100_000, 100_000, defaults.Left);
        bounds.Top = ClampFinite(bounds.Top, -100_000, 100_000, defaults.Top);
        bounds.Width = ClampFinite(bounds.Width, minWidth, 16_384, defaults.Width);
        bounds.Height = ClampFinite(bounds.Height, minHeight, 16_384, defaults.Height);
        return bounds;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
