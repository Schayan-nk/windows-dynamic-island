using System;
using System.Windows;
using System.Windows.Media;

namespace Island;

/// <summary>
/// The island's little mascot: a soft white block with two eyes and a mouth on top of its head.
/// It looks around, opens its mouth to catch files, chews and smiles.
/// </summary>
public sealed class Mascot : FrameworkElement
{
    public static readonly DependencyProperty LookProperty = Reg(nameof(Look), 0.0);
    public static readonly DependencyProperty MouthProperty = Reg(nameof(Mouth), 0.0);
    public static readonly DependencyProperty HappyProperty = Reg(nameof(Happy), 0.0);
    public static readonly DependencyProperty OpenProperty = Reg(nameof(Open), 1.0);

    static DependencyProperty Reg(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(Mascot), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>-1..1: where the eyes look.</summary>
    public double Look { get => (double)GetValue(LookProperty); set => SetValue(LookProperty, value); }
    /// <summary>0..1: the mouth on top of the head.</summary>
    public double Mouth { get => (double)GetValue(MouthProperty); set => SetValue(MouthProperty, value); }
    /// <summary>0..1: content, closed smiling eyes and pink cheeks.</summary>
    public double Happy { get => (double)GetValue(HappyProperty); set => SetValue(HappyProperty, value); }
    /// <summary>0..1: eyelids, for blinking.</summary>
    public double Open { get => (double)GetValue(OpenProperty); set => SetValue(OpenProperty, value); }

    static readonly Brush Body = MakeBody();
    static readonly Brush Ink = Paint.Brush(255, 0x1C, 0x1C, 0x22);
    static readonly Brush Cheek = Paint.Brush(0x70, 0xFF, 0x7A, 0x9A);

    static Brush MakeBody()
    {
        var b = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE4, 0xE4, 0xEA), 90);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(w, h) * 0.3;
        dc.DrawRoundedRectangle(Body, null, new Rect(0, 0, w, h), r, r);

        double mouth = Math.Clamp(Mouth, 0, 1);
        if (mouth > 0.02)
        {
            double mw = w * (0.5 + 0.12 * mouth), mh = h * (0.05 + 0.2 * mouth);
            var m = new Rect((w - mw) / 2, h * 0.14, mw, mh);
            dc.DrawRoundedRectangle(Ink, null, m, mh / 2, mh / 2);
        }

        double happy = Math.Clamp(Happy, 0, 1);
        double eyeY = h * (0.6 - 0.04 * mouth);
        double shift = Math.Clamp(Look, -1, 1) * w * 0.08;
        double ew = w * 0.085, eh = h * 0.17 * Math.Clamp(Open, 0.08, 1);
        foreach (var cx0 in new[] { w * 0.37, w * 0.63 })
        {
            double cx = cx0 + shift;
            if (happy < 0.5)
            {
                dc.DrawRoundedRectangle(Ink, null, new Rect(cx - ew / 2, eyeY - eh / 2, ew, eh), ew / 2, ew / 2);
            }
            else
            {
                var pen = new Pen(Ink, w * 0.055) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(cx - w * 0.07, eyeY - h * 0.02), false, false);
                    c.QuadraticBezierTo(new Point(cx, eyeY + h * 0.07), new Point(cx + w * 0.07, eyeY - h * 0.02), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
        if (happy > 0.05)
        {
            dc.PushOpacity(happy);
            dc.DrawEllipse(Cheek, null, new Point(w * 0.24 + shift, h * 0.74), w * 0.07, h * 0.045);
            dc.DrawEllipse(Cheek, null, new Point(w * 0.76 + shift, h * 0.74), w * 0.07, h * 0.045);
            dc.Pop();
        }
    }
}
