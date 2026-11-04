namespace StampCut.Core.Models;

/// <summary>Фрагмент ролика: вход и выход в секундах исходника.</summary>
public sealed class Segment
{
    public double InSec { get; set; }
    public double OutSec { get; set; }
    public double DurationSec => Math.Max(0, OutSec - InSec);
    public bool Contains(double t) => t >= InSec - 1e-6 && t <= OutSec + 1e-6;
    public Segment Clone() => new() { InSec = InSec, OutSec = OutSec };
}

/// <summary>Один исходный ролик в проекте: файл, фрагменты (вход/выход), кроп, области ретуши, запикивание.</summary>
public sealed class Clip
{
    public required MediaInfo Media { get; init; }
    /// <summary>Фрагменты по порядку воспроизведения; из одного ролика можно взять несколько кусков.</summary>
    public List<Segment> Segments { get; } = new();
    public CropRect? Crop { get; set; }
    public List<RedactRegion> Redactions { get; } = new();
    public List<AudioCensor> Censors { get; } = new();
    /// <summary>Поправка уровня звука ролика, дБ (−20…+20): она же громкость просмотра, она же усиление при экспорте.</summary>
    public double AudioGainDb { get; set; }
    public VideoFilters Filters { get; set; } = new();

    public static Clip FromMedia(MediaInfo media)
    {
        var c = new Clip { Media = media };
        c.Segments.Add(new Segment { InSec = 0, OutSec = media.DurationSec });
        return c;
    }

    /// <summary>Вход первого фрагмента (для простых случаев и совместимости).</summary>
    public double InSec
    {
        get => Segments.Count > 0 ? Segments[0].InSec : 0;
        set { EnsureSegment(); Segments[0].InSec = value; }
    }

    /// <summary>Выход первого фрагмента.</summary>
    public double OutSec
    {
        get => Segments.Count > 0 ? Segments[0].OutSec : Media.DurationSec;
        set { EnsureSegment(); Segments[0].OutSec = value; }
    }

    private void EnsureSegment() { if (Segments.Count == 0) Segments.Add(new Segment { InSec = 0, OutSec = Media.DurationSec }); }

    public double DurationSec => Segments.Sum(s => s.DurationSec);
    public string FileName => Path.GetFileName(Media.Path);
    public bool IsTrimmed => Segments.Count != 1 || Segments[0].InSec > 0.01 || Segments[0].OutSec < Media.DurationSec - 0.01;

    /// <summary>Фрагменты по порядку времени, без пересечений (для экспорта и воспроизведения по склейке).</summary>
    public List<Segment> OrderedSegments() => Segments.Where(s => s.DurationSec >= 0.05).OrderBy(s => s.InSec).ToList();

    /// <summary>Размер кадра после кропа (в пикселях исходника с учётом поворота), чётный.</summary>
    public (int Width, int Height) CroppedSize()
    {
        if (Crop is null) return (Even(Media.Width), Even(Media.Height));
        var r = Crop.ToPixels(Media.Width, Media.Height);
        return (Even(r.W), Even(r.H));
    }

    internal static int Even(int v) => Math.Max(2, v - v % 2);
}

/// <summary>Прямоугольник в долях (0..1) кадра исходника. Позволяет копировать кроп между роликами разного разрешения.</summary>
public sealed class CropRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;

    public bool IsFull => X <= 0 && Y <= 0 && W >= 1 && H >= 1;

    public (int X, int Y, int W, int H) ToPixels(int frameW, int frameH)
    {
        var x = (int)Math.Round(Math.Clamp(X, 0, 1) * frameW);
        var y = (int)Math.Round(Math.Clamp(Y, 0, 1) * frameH);
        var w = (int)Math.Round(Math.Clamp(W, 0, 1) * frameW);
        var h = (int)Math.Round(Math.Clamp(H, 0, 1) * frameH);
        w = Math.Clamp(w, 2, frameW - x);
        h = Math.Clamp(h, 2, frameH - y);
        return (x, y, w, h);
    }

    public CropRect Clone() => new() { X = X, Y = Y, W = W, H = H };

    /// <summary>Центрированный кроп под заданное соотношение сторон.</summary>
    public static CropRect Centered(int frameW, int frameH, double aspect)
    {
        double frameAspect = (double)frameW / frameH;
        if (aspect >= frameAspect)
        {
            double h = frameAspect / aspect;
            return new CropRect { X = 0, Y = (1 - h) / 2, W = 1, H = h };
        }
        else
        {
            double w = aspect / frameAspect;
            return new CropRect { X = (1 - w) / 2, Y = 0, W = w, H = 1 };
        }
    }
}

public enum RedactMode { Blur, Pixelate }

/// <summary>Область ретуши (лицо, номер, недопустимый контент): прямоугольник в долях кадра исходника, режим, параметры, интервал в секундах исходника.</summary>
public sealed class RedactRegion
{
    public double X { get; set; } = 0.4;
    public double Y { get; set; } = 0.4;
    public double W { get; set; } = 0.2;
    public double H { get; set; } = 0.2;
    public RedactMode Mode { get; set; } = RedactMode.Blur;
    /// <summary>Размер квадратика мозаики в пикселях исходника; 0 — авто (высота кадра / 45).</summary>
    public int BlockSize { get; set; }
    /// <summary>Радиус размытия в пикселях исходника; 0 — авто (четверть меньшей стороны области).</summary>
    public int BlurRadius { get; set; }
    /// <summary>Интервал показа в секундах исходника; null — весь ролик.</summary>
    public double? FromSec { get; set; }
    public double? ToSec { get; set; }

    public int EffectiveBlockSize(int frameH) => BlockSize > 0 ? BlockSize : Math.Max(4, frameH / 45);
    public int EffectiveBlurRadius(int regionW, int regionH) => BlurRadius > 0 ? BlurRadius : Math.Max(2, Math.Min(regionW, regionH) / 4);

    public RedactRegion Clone() => new()
    {
        X = X, Y = Y, W = W, H = H, Mode = Mode, BlockSize = BlockSize, BlurRadius = BlurRadius, FromSec = FromSec, ToSec = ToSec
    };
}

/// <summary>Базовая коррекция картинки ролика. Все значения — проценты от −100 до 100, 0 — без изменений (насыщенность: 100 — без изменений).</summary>
public sealed class VideoFilters
{
    /// <summary>Тени: больше нуля — высветлить тёмные участки.</summary>
    public int Shadows { get; set; }
    /// <summary>Светлые участки: больше нуля — приглушить яркие места.</summary>
    public int Highlights { get; set; }
    /// <summary>Насыщенность: 100 — исходная, 0 — чёрно-белое, 200 — вдвое насыщеннее.</summary>
    public int Saturation { get; set; } = 100;
    public int Brightness { get; set; }
    public int Contrast { get; set; }

    public bool IsDefault => Shadows == 0 && Highlights == 0 && Saturation == 100 && Brightness == 0 && Contrast == 0;
    public VideoFilters Clone() => new() { Shadows = Shadows, Highlights = Highlights, Saturation = Saturation, Brightness = Brightness, Contrast = Contrast };

    /// <summary>Фильтры ffmpeg для этой коррекции (пусто — ничего не нужно). Работает на yuv420p.</summary>
    public List<string> ToFfmpeg()
    {
        var list = new List<string>();
        if (Shadows != 0 || Highlights != 0)
        {
            // кривая по четырём точкам: тени двигают точку 0.25, светлые участки — 0.75; ±0.15 — заметно, но без постеризации
            double y1 = 0.25 + Math.Clamp(Shadows, -100, 100) / 100.0 * 0.15;
            double y2 = 0.75 - Math.Clamp(Highlights, -100, 100) / 100.0 * 0.15;
            list.Add($"curves=all='0/0 0.25/{TimeFormat.Num(y1)} 0.5/0.5 0.75/{TimeFormat.Num(y2)} 1/1'");
        }
        if (Saturation != 100 || Brightness != 0 || Contrast != 0)
        {
            var eq = new List<string>();
            if (Saturation != 100) eq.Add("saturation=" + TimeFormat.Num(Math.Clamp(Saturation, 0, 300) / 100.0));
            if (Brightness != 0) eq.Add("brightness=" + TimeFormat.Num(Math.Clamp(Brightness, -100, 100) / 100.0 * 0.3));
            if (Contrast != 0) eq.Add("contrast=" + TimeFormat.Num(1 + Math.Clamp(Contrast, -100, 100) / 100.0 * 0.5));
            list.Add("eq=" + string.Join(":", eq));
        }
        return list;
    }
}

public enum CensorMode { Beep, Mute }

/// <summary>«Запикать» фрагмент звука: интервал в секундах исходника, тон 1 кГц или тишина.</summary>
public sealed class AudioCensor
{
    public double FromSec { get; set; }
    public double ToSec { get; set; }
    public CensorMode Mode { get; set; } = CensorMode.Beep;

    public AudioCensor Clone() => new() { FromSec = FromSec, ToSec = ToSec, Mode = Mode };
}
