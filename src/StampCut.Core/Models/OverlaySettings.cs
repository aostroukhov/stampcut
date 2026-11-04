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
/// <summary>Оформление текстовой плашки. Размер шрифта — в процентах высоты выходного кадра, чтобы не зависеть от разрешения.</summary>
public sealed class TextStyle
{
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    /// <summary>Высота шрифта, % высоты кадра (1…30).</summary>
    public double SizePercent { get; set; } = 5;
    public bool Bold { get; set; } = true;
    /// <summary>Цвет текста, #RRGGBB или #AARRGGBB.</summary>
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>Цвет подложки (#AARRGGBB); пусто — без подложки.</summary>
    public string Background { get; set; } = "#99000000";
    /// <summary>Обводка текста (тёмная), для текста без подложки.</summary>
    public bool Outline { get; set; }
    /// <summary>Выравнивание строк: left / center / right.</summary>
    public string Align { get; set; } = "center";
    /// <summary>Максимальная ширина плашки, % ширины кадра (переносы строк по словам).</summary>
    public double MaxWidthPercent { get; set; } = 90;

    public TextStyle Clone() => new() { Text = Text, FontFamily = FontFamily, SizePercent = SizePercent, Bold = Bold, Color = Color, Background = Background, Outline = Outline, Align = Align, MaxWidthPercent = MaxWidthPercent };

    public bool SameAs(TextStyle o) => Text == o.Text && FontFamily == o.FontFamily && Math.Abs(SizePercent - o.SizePercent) < 0.001 && Bold == o.Bold
        && Color == o.Color && Background == o.Background && Outline == o.Outline && Align == o.Align && Math.Abs(MaxWidthPercent - o.MaxWidthPercent) < 0.001;
}

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
    /// <summary>Движение: ключевые точки (время выходной ленты → левый верхний угол в долях выходного кадра). Пусто — положение по якорю.</summary>
    public List<MotionKey> Motion { get; } = new();
    /// <summary>Текстовая плашка: вместо файла PNG приложение само рисует текст. null — обычный оверлей из файла.</summary>
    public TextStyle? Text { get; set; }
    public bool IsText => Text is not null;

    public OverlaySettings Clone()
    {
        var c = new OverlaySettings
        {
            Id = Id, Name = Name, File = File, Anchor = Anchor, OffsetX = OffsetX, OffsetY = OffsetY, OffsetUnit = OffsetUnit,
            Width = Width, WidthUnit = WidthUnit, Opacity = Opacity, FromSec = FromSec, ToSec = ToSec,
            FadeInSec = FadeInSec, FadeOutSec = FadeOutSec, Text = Text?.Clone()
        };
        c.Motion.AddRange(Motion.Select(k => k.Clone()));
        return c;
    }
}
