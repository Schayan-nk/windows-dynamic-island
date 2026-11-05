using System;
using System.Windows;
using System.Windows.Media;

namespace Island;

internal static class Paint
{
    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => Brush(Color.FromArgb(a, r, g, b));
}

/// <summary>Circular progress ring.</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Ring), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Ring), new FrameworkPropertyMetadata(Paint.Brush(0x3A, 255, 255, 255), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double t = Thickness, w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2 - t / 2;
        if (r <= 0) return;
        var c = new Point(w / 2, h / 2);
        dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
        double p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.001) return;
        var pen = new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a0 = -Math.PI / 2, a1 = a0 + p * 2 * Math.PI;
        var start = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var end = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

public enum EyesMode { Happy, Dots, Hearts, Closed }

/// <summary>The two little eyes of the island. They blink, glow and change shape with the state.</summary>
public sealed class EyesView : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(EyesMode), typeof(EyesView), new FrameworkPropertyMetadata(EyesMode.Happy, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(EyesView), new FrameworkPropertyMetadata(Color.FromRgb(0xDC, 0xF2, 0xE2), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(EyesView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OpenProperty = DependencyProperty.Register(
        nameof(Open), typeof(double), typeof(EyesView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(double), typeof(EyesView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public EyesMode Mode { get => (EyesMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    /// <summary>0..1, drives the breathing of dots and hearts.</summary>
    public double Pulse { get => (double)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    /// <summary>0 = closed (blink), 1 = open.</summary>
    public double Open { get => (double)GetValue(OpenProperty); set => SetValue(OpenProperty, value); }
    /// <summary>-1..1, eyes glance left or right.</summary>
    public double Look { get => (double)GetValue(LookProperty); set => SetValue(LookProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var brush = Paint.Brush(Color);
        double s = Math.Min(h, w * 0.36);
        double cy = h / 2;
        double shift = Look * w * 0.06;
        double open = Math.Clamp(Open, 0.08, 1);
        foreach (var cx0 in new[] { w * 0.25, w * 0.75 })
        {
            double cx = cx0 + shift;
            switch (Mode)
            {
                case EyesMode.Happy:
                {
                    var pen = new Pen(brush, Math.Max(1.7, s * 0.2)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    pen.Freeze();
                    double lift = s * 0.5 * open;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(cx - s * 0.42, cy + s * 0.2), false, false);
                        c.QuadraticBezierTo(new Point(cx, cy + s * 0.2 - lift * 1.5), new Point(cx + s * 0.42, cy + s * 0.2), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, pen, g);
                    break;
                }
                case EyesMode.Dots:
                {
                    double r = s * 0.24 * (0.86 + 0.14 * Pulse);
                    dc.PushOpacity(0.6 + 0.4 * Pulse);
                    dc.DrawEllipse(brush, null, new Point(cx, cy), r * 0.9, r * 1.25 * open);
                    dc.Pop();
                    break;
                }
                case EyesMode.Hearts:
                {
                    double hs = s * (0.62 + 0.12 * Pulse);
                    dc.PushOpacity(0.78 + 0.22 * Pulse);
                    dc.DrawGeometry(brush, null, Heart(cx - hs / 2, cy - hs / 2, hs));
                    dc.Pop();
                    break;
                }
                case EyesMode.Closed:
                {
                    var pen = new Pen(brush, Math.Max(1.6, s * 0.18)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    pen.Freeze();
                    dc.DrawLine(pen, new Point(cx - s * 0.36, cy + s * 0.05), new Point(cx + s * 0.36, cy + s * 0.05));
                    break;
                }
            }
        }
    }

    static Geometry Heart(double x, double y, double s)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            Point P(double u, double v) => new(x + u * s, y + v * s);
            c.BeginFigure(P(0.5, 0.92), true, true);
            c.BezierTo(P(0.12, 0.66), P(-0.04, 0.36), P(0.2, 0.16), true, true);
            c.BezierTo(P(0.34, 0.05), P(0.5, 0.13), P(0.5, 0.28), true, true);
            c.BezierTo(P(0.5, 0.13), P(0.66, 0.05), P(0.8, 0.16), true, true);
            c.BezierTo(P(1.04, 0.36), P(0.88, 0.66), P(0.5, 0.92), true, true);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Four little audio level bars.</summary>
public sealed class LevelBars : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelBars), new FrameworkPropertyMetadata(Paint.Brush(255, 0xFF, 0x4D, 0x5E), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    readonly double[] _levels = { 0.3, 0.55, 0.4, 0.6 };

    public void SetLevels(double a, double b, double c, double d)
    {
        _levels[0] = a; _levels[1] = b; _levels[2] = c; _levels[3] = d;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        int n = _levels.Length;
        double bw = Math.Max(2, w / (n * 1.75));
        double gap = (w - n * bw) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double bh = Math.Max(bw, h * Math.Clamp(_levels[i], 0, 1));
            var rect = new Rect(i * (bw + gap), (h - bh) / 2, bw, bh);
            dc.DrawRoundedRectangle(Fill, null, rect, bw / 2, bw / 2);
        }
    }
}

/// <summary>A starburst in the spirit of the Claude mark.</summary>
public sealed class ClaudeMark : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ClaudeMark), new FrameworkPropertyMetadata(Paint.Brush(255, 0xE2, 0x88, 0x5F), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(ClaudeMark), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    static readonly double[] Lengths = { 1.0, 0.8, 0.92, 0.84, 1.0, 0.78, 0.95, 0.82, 1.0, 0.8, 0.9, 0.86 };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2;
        if (r <= 0) return;
        var c = new Point(w / 2, h / 2);
        var pen = new Pen(Fill, Math.Max(1.2, r * 0.22)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        double baseA = Angle * Math.PI / 180;
        for (int i = 0; i < Lengths.Length; i++)
        {
            double a = baseA + i * 2 * Math.PI / Lengths.Length;
            double len = (r - pen.Thickness / 2) * Lengths[i];
            var dir = new Vector(Math.Cos(a), Math.Sin(a));
            dc.DrawLine(pen, c + dir * (r * 0.1), c + dir * len);
        }
    }
}

/// <summary>Inbox tray icon (Segoe Fluent has no good one).</summary>
public sealed class TrayGlyph : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TrayGlyph), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double k = Math.Min(w, h) / 18.0;
        if (k <= 0) return;
        double ox = (w - 18 * k) / 2, oy = (h - 18 * k) / 2;
        Point P(double x, double y) => new(ox + x * k, oy + y * k);
        var pen = new Pen(Stroke, 1.45 * Math.Max(1, k * 0.95)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(1.8, 9.5), false, true);
            c.LineTo(P(4.4, 3.4), true, true);
            c.LineTo(P(13.6, 3.4), true, true);
            c.LineTo(P(16.2, 9.5), true, true);
            c.LineTo(P(16.2, 13.6), true, true);
            c.QuadraticBezierTo(P(16.2, 15.2), P(14.6, 15.2), true, true);
            c.LineTo(P(3.4, 15.2), true, true);
            c.QuadraticBezierTo(P(1.8, 15.2), P(1.8, 13.6), true, true);
            c.BeginFigure(P(1.8, 9.5), false, false);
            c.LineTo(P(6.1, 9.5), true, true);
            c.LineTo(P(7.3, 11.5), true, true);
            c.LineTo(P(10.7, 11.5), true, true);
            c.LineTo(P(11.9, 9.5), true, true);
            c.LineTo(P(16.2, 9.5), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
