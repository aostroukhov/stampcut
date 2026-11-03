namespace StampCut.Core.Models;

/// <summary>Один исходный ролик в проекте: файл, точки входа/выхода, кроп, области ретуши.</summary>
public sealed class Clip
{
    public required MediaInfo Media { get; init; }
    public double InSec { get; set; }
    public double OutSec { get; set; }
    public CropRect? Crop { get; set; }
    public List<RedactRegion> Redactions { get; } = new();
    public List<AudioCensor> Censors { get; } = new();

    public static Clip FromMedia(MediaInfo media) => new() { Media = media, InSec = 0, OutSec = media.DurationSec };

    public double DurationSec => Math.Max(0, OutSec - InSec);
    public string FileName => Path.GetFileName(Media.Path);

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

/// <summary>Область ретуши (лицо, номер, недопустимый контент): прямоугольник в долях кадра исходника, режим, сила, интервал в секундах исходника.</summary>
public sealed class RedactRegion
{
    public double X { get; set; } = 0.4;
    public double Y { get; set; } = 0.4;
    public double W { get; set; } = 0.2;
    public double H { get; set; } = 0.2;
    public RedactMode Mode { get; set; } = RedactMode.Blur;
    /// <summary>Сила эффекта 1..10.</summary>
    public int Strength { get; set; } = 6;
    /// <summary>Интервал показа в секундах исходника; null — весь ролик.</summary>
    public double? FromSec { get; set; }
    public double? ToSec { get; set; }

    public RedactRegion Clone() => new()
    {
        X = X, Y = Y, W = W, H = H, Mode = Mode, Strength = Strength, FromSec = FromSec, ToSec = ToSec
    };
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
