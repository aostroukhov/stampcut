namespace StampCut.Core.Models;

public enum Anchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight
}

public enum SizeUnit { PercentOfVideoWidth, Pixels }
public enum OffsetUnit { Percent, Pixels }

/// <summary>Оверлей: PNG с альфой, положение, размер, прозрачность, интервал показа (в секундах выходного ролика).</summary>
public sealed class OverlaySettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Имя файла относительно библиотеки оверлеев или абсолютный путь.</summary>
    public string File { get; set; } = "";
    public Anchor Anchor { get; set; } = Anchor.BottomRight;
    public double OffsetX { get; set; } = 3;
    public double OffsetY { get; set; } = 3;
    public OffsetUnit OffsetUnit { get; set; } = OffsetUnit.Percent;
    public double Width { get; set; } = 25;
    public SizeUnit WidthUnit { get; set; } = SizeUnit.PercentOfVideoWidth;
    public double Opacity { get; set; } = 0.8;
    public double? FromSec { get; set; }
    public double? ToSec { get; set; }
    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }

    public OverlaySettings Clone() => new()
    {
        Id = Id, Name = Name, File = File, Anchor = Anchor, OffsetX = OffsetX, OffsetY = OffsetY, OffsetUnit = OffsetUnit,
        Width = Width, WidthUnit = WidthUnit, Opacity = Opacity, FromSec = FromSec, ToSec = ToSec,
        FadeInSec = FadeInSec, FadeOutSec = FadeOutSec
    };
}
