using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DAMeter.Core;

namespace DAMeter.Controls;

/// <summary>Draws the latest meter state on the UI thread; never handles audio buffers.</summary>
public sealed class MeterDisplay : FrameworkElement
{
    private static readonly Brush Panel = Brush(25, 29, 35);
    private static readonly Brush Frame = Brush(47, 55, 65);
    private static readonly Brush Text = Brush(226, 232, 240);
    private static readonly Brush Muted = Brush(148, 163, 184);
    private static readonly Brush Green = Brush(59, 218, 139);
    private static readonly Brush Yellow = Brush(249, 203, 73);
    private static readonly Brush Red = Brush(255, 81, 95);
    private static readonly Brush OffGreen = Brush(24, 57, 43);
    private static readonly Brush OffYellow = Brush(62, 55, 29);
    private static readonly Brush OffRed = Brush(62, 30, 35);
    private static readonly Brush Ivory = Brush(244, 233, 202);
    private static readonly Brush Ink = Brush(40, 36, 29);
    private static readonly Brush Needle = Brush(197, 34, 39);
    private static readonly Pen FramePen = Pen(Frame, 1.5);
    private static readonly Pen HoldPen = Pen(Text, 2);
    private static readonly Pen NeedlePen = Pen(Needle, 3.2);
    private static readonly Pen BlackTick = Pen(Ink, 1.5);
    private static readonly Pen RedTick = Pen(Needle, 1.5);
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly int[] VuTicks = [-20, -10, -7, -5, -3, -2, -1, 0, 1, 2, 3];
    private static readonly StreamGeometry VuArc = CreateVuArc();

    private readonly Dictionary<string, FormattedText> _labels = [];
    private double _pixelsPerDip = 1;
    private MeterMode _mode;
    private double _leftPeak = double.NegativeInfinity;
    private double _rightPeak = double.NegativeInfinity;
    private double _leftHold = double.NegativeInfinity;
    private double _rightHold = double.NegativeInfinity;
    private double _leftVu = double.NegativeInfinity;
    private double _rightVu = double.NegativeInfinity;
    private bool _leftClip;
    private bool _rightClip;

    public MeterDisplay()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Focusable = false;
    }

    public void Update(MeterBallistics state, MeterMode mode)
    {
        _mode = mode;
        _leftPeak = state.LeftPeakDb;
        _rightPeak = state.RightPeakDb;
        _leftHold = state.LeftHoldDb;
        _rightHold = state.RightHoldDb;
        _leftVu = state.LeftVuDb;
        _rightVu = state.RightVuDb;
        _leftClip = state.LeftClip;
        _rightClip = state.RightClip;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _pixelsPerDip)
        {
            _pixelsPerDip = dpi;
            _labels.Clear();
        }

        Size canvas = _mode switch
        {
            MeterMode.AnalogVu => new Size(1000, 440),
            MeterMode.VerticalPeak => new Size(320, 540),
            _ => new Size(1000, 280)
        };
        double scale = Math.Min(ActualWidth / canvas.Width, ActualHeight / canvas.Height);
        dc.PushTransform(new TranslateTransform(
            (ActualWidth - canvas.Width * scale) / 2,
            (ActualHeight - canvas.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        switch (_mode)
        {
            case MeterMode.AnalogVu:
                DrawAnalog(dc, new Rect(12, 12, 476, 382), "L", _leftVu);
                DrawAnalog(dc, new Rect(512, 12, 476, 382), "R", _rightVu);
                Label(dc, "RMS ベースの VU 風表示 · 約 300 ms 応答 · 厳密な規格準拠ではありません", 500, 410, 17, Muted);
                break;
            case MeterMode.VerticalPeak:
                DrawVertical(dc);
                break;
            default:
                DrawHorizontal(dc);
                break;
        }
        dc.Pop();
        dc.Pop();
    }

    private void DrawHorizontal(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Panel, FramePen, new Rect(12, 12, 976, 256), 18, 18);
        Label(dc, "S T E R E O   P E A K", 40, 28, 17, Muted, false);
        Label(dc, "dBFS", 942, 28, 17, Muted);
        for (int db = -60; db <= 0; db += 6)
        {
            double x = 100 + (db + 60) / 60.0 * 738;
            Label(dc, db.ToString(CultureInfo.InvariantCulture), x, 62, 16, Muted);
        }
        HorizontalChannel(dc, "L", 96, _leftPeak, _leftHold, _leftClip);
        HorizontalChannel(dc, "R", 171, _rightPeak, _rightHold, _rightClip);
    }

    private void HorizontalChannel(DrawingContext dc, string name, double y, double level, double hold, bool clip)
    {
        Label(dc, name, 55, y + 12, 24, Text);
        const double x = 100;
        const double width = 738;
        for (int i = 0; i < 60; i++)
        {
            double db = i - 59;
            bool lit = level > -60 && level >= db;
            dc.DrawRoundedRectangle(SegmentBrush(db, lit), null,
                new Rect(x + i * width / 60, y, width / 60 - 3, 48), 2, 2);
        }
        if (hold >= -60)
        {
            double holdX = x + Fraction(hold) * width;
            dc.DrawLine(HoldPen, new Point(holdX, y - 4), new Point(holdX, y + 52));
        }
        Numeric(dc, DbText(level), 902, y + 4, 25, Text);
        Label(dc, "CLIP", 902, y + 36, 13, clip ? Red : Muted);
        dc.DrawEllipse(clip ? Red : OffRed, null, new Point(953, y + 44), 6, 6);
    }

    private void DrawVertical(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Panel, FramePen, new Rect(12, 12, 296, 516), 18, 18);
        Label(dc, "P E A K   /   dBFS", 160, 29, 16, Muted);
        for (int db = -60; db <= 0; db += 6)
        {
            double y = 434 - (db + 60) / 60.0 * 342;
            Label(dc, db.ToString(CultureInfo.InvariantCulture), 160, y - 10, 14, Muted);
        }
        VerticalChannel(dc, "L", 53, _leftPeak, _leftHold, _leftClip);
        VerticalChannel(dc, "R", 213, _rightPeak, _rightHold, _rightClip);
    }

    private void VerticalChannel(DrawingContext dc, string name, double x, double level, double hold, bool clip)
    {
        Label(dc, name, x + 27, 58, 22, Text);
        const double top = 92;
        const double height = 342;
        for (int i = 0; i < 60; i++)
        {
            double db = i - 59;
            bool lit = level > -60 && level >= db;
            dc.DrawRoundedRectangle(SegmentBrush(db, lit), null,
                new Rect(x, top + height - (i + 1) * height / 60, 54, height / 60 - 1.8), 1, 1);
        }
        if (hold >= -60)
        {
            double y = top + height - Fraction(hold) * height;
            dc.DrawLine(HoldPen, new Point(x - 4, y), new Point(x + 58, y));
        }
        Numeric(dc, DbText(level), x + 27, 447, 20, Text);
        Label(dc, "CLIP", x + 27, 486, 14, clip ? Red : Muted);
        dc.DrawEllipse(clip ? Red : OffRed, null, new Point(x + 64, 495), 5, 5);
    }

    private void DrawAnalog(DrawingContext dc, Rect rect, string channel, double vu)
    {
        dc.DrawRoundedRectangle(Ivory, FramePen, rect, 18, 18);
        dc.PushClip(new RectangleGeometry(rect));
        var pivot = new Point(rect.X + rect.Width / 2, rect.Y + 355);
        const double radius = 258;
        Label(dc, $"CHANNEL {channel}", pivot.X, rect.Y + 20, 16, Ink);
        foreach (int tick in VuTicks)
        {
            double angle = VuAngle(tick);
            Point outer = Radial(pivot, radius, angle);
            Point inner = Radial(pivot, radius - 16, angle);
            dc.DrawLine(tick > 0 ? RedTick : BlackTick, inner, outer);
            Point number = Radial(pivot, radius + 20, angle);
            Label(dc, tick > 0 ? $"+{tick}" : tick.ToString(CultureInfo.InvariantCulture), number.X, number.Y - 10, 16,
                tick > 0 ? Needle : Ink);
        }
        dc.PushTransform(new TranslateTransform(pivot.X, pivot.Y));
        dc.DrawGeometry(null, BlackTick, VuArc);
        dc.Pop();
        Label(dc, "VU", pivot.X, rect.Y + 195, 38, Ink);
        Label(dc, channel, pivot.X, rect.Y + 244, 18, Ink);
        dc.DrawLine(NeedlePen, pivot, Radial(pivot, radius - 5, VuAngle(vu)));
        dc.DrawEllipse(Ink, null, pivot, 15, 15);
        dc.DrawEllipse(Needle, null, pivot, 6, 6);
        dc.Pop();
    }

    private void Label(DrawingContext dc, string value, double x, double y, double size, Brush color, bool centered = true)
    {
        string key = $"{value}|{size}|{color}";
        if (!_labels.TryGetValue(key, out FormattedText? text))
        {
            text = MakeText(value, size, color);
            _labels.Add(key, text);
        }
        dc.DrawText(text, new Point(centered ? x - text.Width / 2 : x, y));
    }

    private void Numeric(DrawingContext dc, string value, double x, double y, double size, Brush color)
    {
        FormattedText text = MakeText(value, size, color);
        dc.DrawText(text, new Point(x - text.Width / 2, y));
    }

    private FormattedText MakeText(string value, double size, Brush color) => new(
        value, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, Face, size, color, _pixelsPerDip);

    private static double Fraction(double db) => double.IsFinite(db) ? Math.Clamp((db + 60) / 60, 0, 1) : 0;
    private static double VuAngle(double db) => (-55 + Math.Clamp((double.IsFinite(db) ? db : -20) + 20, 0, 23) / 23 * 110) * Math.PI / 180;
    private static Point Radial(Point origin, double radius, double angle) => new(origin.X + radius * Math.Sin(angle), origin.Y - radius * Math.Cos(angle));
    private static string DbText(double db) => double.IsNegativeInfinity(db) ? "−∞" : Math.Max(-60, db).ToString("0.0", CultureInfo.InvariantCulture);
    private static Brush SegmentBrush(double db, bool lit) => db >= -3 ? (lit ? Red : OffRed) : db >= -12 ? (lit ? Yellow : OffYellow) : lit ? Green : OffGreen;

    private static StreamGeometry CreateVuArc()
    {
        const double radius = 236;
        var arc = new StreamGeometry();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(Radial(default, radius, VuAngle(-20)), false, false);
            context.ArcTo(Radial(default, radius, VuAngle(3)), new Size(radius, radius), 0,
                false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        return arc;
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
