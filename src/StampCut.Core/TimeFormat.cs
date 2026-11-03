using System.Globalization;

namespace StampCut.Core;

public static class TimeFormat
{
    /// <summary>мм:сс.мс (или ч:мм:сс.мс для длинных).</summary>
    public static string Format(double seconds, bool withMs = true)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        var ms = withMs ? $".{t.Milliseconds:000}" : "";
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{ms}"
            : $"{t.Minutes:00}:{t.Seconds:00}{ms}";
    }

    /// <summary>Разбор "сс", "мм:сс", "ч:мм:сс" с дробной частью через точку или запятую.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} ГБ";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} МБ";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} КБ";
        return $"{bytes} Б";
    }

    /// <summary>Число для командной строки ffmpeg: точка как разделитель, без экспоненты.</summary>
    public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
