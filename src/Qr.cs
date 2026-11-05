using System.Windows;
using System.Windows.Media;
using QRCoder;

namespace Island;

internal static class Qr
{
    /// <summary>Crisp vector QR code (render with RenderOptions.EdgeMode = Aliased).</summary>
    public static DrawingImage Render(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        int n = m.Count;
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            for (int y = 0; y < n; y++)
            {
                int x = 0;
                while (x < n)
                {
                    if (!m[y][x]) { x++; continue; }
                    int x0 = x;
                    while (x < n && m[y][x]) x++;
                    ctx.BeginFigure(new Point(x0, y), true, true);
                    ctx.LineTo(new Point(x, y), false, false);
                    ctx.LineTo(new Point(x, y + 1), false, false);
                    ctx.LineTo(new Point(x0, y + 1), false, false);
                }
            }
        }
        geo.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, n, n))));
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, geo));
        group.Freeze();
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }
}
