using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StampCut.Core.Models;

namespace StampCut.App.Services;

/// <summary>
/// Рисует текстовую плашку в картинку с прозрачностью под конкретный размер выходного кадра: размер шрифта — доля высоты кадра,
/// перенос строк по словам в пределах заданной доли ширины. Одна и та же функция для превью и для экспорта, поэтому они совпадают.
/// </summary>
public static class TextRenderer
{
    public static BitmapSource? Render(TextStyle t, int outW, int outH)
    {
        if (outW <= 0 || outH <= 0) return null;
        string text = (t.Text ?? "").Trim();
        if (text.Length == 0) text = " ";
        double fontPx = Math.Max(6, outH * Math.Clamp(t.SizePercent, 1, 30) / 100.0);
        bool hasBg = TryColor(t.Background, out var bgColor) && bgColor.A > 0;
        double pad = hasBg ? fontPx * 0.45 : fontPx * 0.15;
        double maxTextW = Math.Max(fontPx, outW * Math.Clamp(t.MaxWidthPercent, 10, 100) / 100.0 - 2 * pad);
        var typeface = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily), FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var align = t.Align switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, _ => TextAlignment.Center };
        var fg = TryColor(t.Color, out var fgColor) ? fgColor : Colors.White;

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontPx, new SolidColorBrush(fg), 1.0)
        { TextAlignment = align, Trimming = TextTrimming.None };
        if (ft.Width > maxTextW) ft.MaxTextWidth = maxTextW; // перенос по словам
        // ширина абзаца всегда равна самой длинной строке: без неё WPF выравнивает «по центру»/«справа» относительно нуля и текст уходит за край
        ft.MaxTextWidth = Math.Min(maxTextW, ft.Width + 1);
        double textW = Math.Ceiling(Math.Max(1, ft.Width)), textH = Math.Ceiling(Math.Max(1, ft.Height));
        int w = (int)Math.Ceiling(textW + 2 * pad), h = (int)Math.Ceiling(textH + 2 * pad);
        w = Math.Clamp(w, 2, Math.Max(2, outW * 2)); h = Math.Clamp(h, 2, Math.Max(2, outH * 2));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (hasBg) dc.DrawRoundedRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, w, h), fontPx * 0.25, fontPx * 0.25);
            var origin = new Point(pad, pad);
            if (t.Outline)
            {
                var outline = Luminance(fg) > 0.5 ? Color.FromArgb(0xE0, 0, 0, 0) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF);
                var pen = new Pen(new SolidColorBrush(outline), Math.Max(1, fontPx * 0.09)) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, ft.BuildGeometry(origin));
            }
            else if (!hasBg)
            {
                // без подложки и обводки — мягкая тень, чтобы текст читался на любом фоне
                var shadow = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontPx, new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 1.0)
                { TextAlignment = align, Trimming = TextTrimming.None, MaxTextWidth = ft.MaxTextWidth };
                double d = Math.Max(1, fontPx * 0.04);
                dc.DrawText(shadow, new Point(origin.X + d, origin.Y + d));
            }
            dc.DrawText(ft, origin);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static bool TryColor(string? s, out Color c)
    {
        c = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { c = (Color)ColorConverter.ConvertFromString(s.Trim()); return true; }
        catch { return false; }
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Популярные семейства — первыми, остальные системные — по алфавиту.</summary>
    public static List<string> FontFamilies()
    {
        var preferred = new[] { "Segoe UI", "Segoe UI Variable Display", "Arial", "Arial Black", "Impact", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Calibri", "Bahnschrift", "Consolas" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var f in Fonts.SystemFontFamilies) all.Add(f.Source); } catch { }
        var list = preferred.Where(all.Contains).ToList();
        list.AddRange(all.Where(f => !preferred.Contains(f, StringComparer.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
        if (list.Count == 0) list.Add("Segoe UI");
        return list;
    }
}
