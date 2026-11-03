using StampCut.Core.Models;

namespace StampCut.Core.Geometry;

public readonly record struct PixelRect(int X, int Y, int W, int H);

/// <summary>Единый расчёт положения оверлея для превью и для ffmpeg: один код — одинаковый результат.</summary>
public static class OverlayGeometry
{
    public static PixelRect Compute(int frameW, int frameH, OverlaySettings o, int imageW, int imageH)
    {
        double w = o.WidthUnit == SizeUnit.PercentOfVideoWidth ? frameW * o.Width / 100.0 : o.Width;
        w = Math.Max(2, w);
        double h = imageW > 0 ? w * imageH / imageW : w;
        double ox = o.OffsetUnit == OffsetUnit.Percent ? frameW * o.OffsetX / 100.0 : o.OffsetX;
        double oy = o.OffsetUnit == OffsetUnit.Percent ? frameH * o.OffsetY / 100.0 : o.OffsetY;

        // центр по оси означает «ровно по центру»: отступ по этой оси не учитывается
        double x = Column(o.Anchor) switch
        {
            0 => ox,
            1 => (frameW - w) / 2,
            _ => frameW - w - ox,
        };
        double y = Row(o.Anchor) switch
        {
            0 => oy,
            1 => (frameH - h) / 2,
            _ => frameH - h - oy,
        };
        return new PixelRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
    }

    /// <summary>Обратная задача: по положению левого верхнего угла (px) и ширине вернуть отступы для текущего якоря.</summary>
    public static void ApplyPosition(int frameW, int frameH, OverlaySettings o, int imageW, int imageH, double x, double y, double w)
    {
        if (o.WidthUnit == SizeUnit.PercentOfVideoWidth) o.Width = Math.Clamp(w / frameW * 100.0, 1, 100);
        else o.Width = Math.Max(2, w);
        double h = imageW > 0 ? w * imageH / imageW : w;
        int col = Column(o.Anchor), row = Row(o.Anchor);
        double ox = col switch { 0 => x, 1 => 0, _ => frameW - w - x };
        double oy = row switch { 0 => y, 1 => 0, _ => frameH - h - y };
        if (o.OffsetUnit == OffsetUnit.Percent)
        {
            if (col != 1) o.OffsetX = Math.Round(ox / frameW * 100.0, 2);
            if (row != 1) o.OffsetY = Math.Round(oy / frameH * 100.0, 2);
        }
        else
        {
            if (col != 1) o.OffsetX = Math.Round(ox);
            if (row != 1) o.OffsetY = Math.Round(oy);
        }
    }

    /// <summary>Ближайший якорь к точке центра оверлея (для «магнита» при перетаскивании).</summary>
    public static Anchor NearestAnchor(int frameW, int frameH, double centerX, double centerY)
    {
        int col = centerX < frameW / 3.0 ? 0 : centerX < frameW * 2 / 3.0 ? 1 : 2;
        int row = centerY < frameH / 3.0 ? 0 : centerY < frameH * 2 / 3.0 ? 1 : 2;
        return (Anchor)(row * 3 + col);
    }

    public static int Column(Anchor a) => (int)a % 3;
    public static int Row(Anchor a) => (int)a / 3;
}
