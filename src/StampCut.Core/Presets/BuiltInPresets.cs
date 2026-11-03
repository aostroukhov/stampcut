using StampCut.Core.Models;

namespace StampCut.Core.Presets;

public static class BuiltInPresets
{
    public static List<ExportPreset> Export() => new()
    {
        new ExportPreset
        {
            Id = "telegram-1080p", Name = "Telegram 1080p (универсальный)", BuiltIn = true,
            Description = "H.264 + AAC, длинная сторона не больше 1920, частота кадров исходника, SDR. Качество подбирается по исходнику.",
            MaxLongSide = 1920, Fit = FitMode.Keep, MaxFps = 60, Crf = 20, OutputSuffix = "_tg",
        },
        new ExportPreset
        {
            Id = "vertical-9x16", Name = "Вертикаль 9:16 (1080×1920)", BuiltIn = true,
            Description = "Для вертикальных постов, Reels и Shorts. Горизонтальный исходник вписывается с размытым фоном.",
            Width = 1080, Height = 1920, Fit = FitMode.PadBlur, MaxFps = 60, Crf = 20, OutputSuffix = "_9x16",
        },
        new ExportPreset
        {
            Id = "square-1x1", Name = "Квадрат 1:1 (1080×1080)", BuiltIn = true,
            Description = "Квадрат для ленты: кадр заполняется с обрезкой по центру.",
            Width = 1080, Height = 1080, Fit = FitMode.Crop, MaxFps = 60, Crf = 20, OutputSuffix = "_1x1",
        },
        new ExportPreset
        {
            Id = "hq-1080p", Name = "Высокое качество 1080p", BuiltIn = true,
            Description = "Размер кадра и частота исходника сохраняются полностью (в т.ч. 4K), сжатие минимальное: для отправки «как файл» или дальнейшего монтажа.",
            MaxLongSide = null, Fit = FitMode.Keep, MaxFps = 120, AutoQuality = false, Crf = 16, X264Preset = "medium", OutputSuffix = "_hq",
        },
        new ExportPreset
        {
            Id = "lossless-cut", Name = "Только обрезка, без перекодирования", BuiltIn = true,
            Description = "Режим Machete: потоки копируются как есть, занимает секунды. Оверлей, кроп и ретушь недоступны; разрез по ключевым кадрам.",
            StreamCopy = true, OutputSuffix = "_cut",
        },
    };

    public static List<OverlaySettings> Overlay() => new()
    {
        new OverlaySettings
        {
            Id = "logo-bottom-right", Name = "Логотип справа внизу", File = "",
            Anchor = Anchor.BottomRight, OffsetX = 3, OffsetY = 3, Width = 25, Opacity = 0.8,
        },
        new OverlaySettings
        {
            Id = "logo-top-left", Name = "Логотип слева вверху", File = "",
            Anchor = Anchor.TopLeft, OffsetX = 3, OffsetY = 3, Width = 20, Opacity = 0.9,
        },
    };
}
