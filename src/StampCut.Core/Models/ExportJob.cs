namespace StampCut.Core.Models;

/// <summary>Оверлей с уже найденным файлом и его размером в пикселях.</summary>
public sealed class ResolvedOverlay
{
    public required OverlaySettings Settings { get; init; }
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>Всё, что нужно для одного экспорта: ролики по порядку, оверлеи, пресет, выходной файл и решения по железу.</summary>
public sealed class ExportJob
{
    public List<Clip> Clips { get; init; } = new();
    public List<ResolvedOverlay> Overlays { get; init; } = new();
    public required ExportPreset Preset { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Выбранный кодер (после автоопределения). None = libx264. Auto здесь недопустим.</summary>
    public HardwareEncoder Encoder { get; init; } = HardwareEncoder.None;
    /// <summary>Аппаратное декодирование (d3d11va / cuda / vaapi). Пустая строка — программное.</summary>
    public string HwAccel { get; init; } = "";

    public double TotalDurationSec => Clips.Sum(c => c.DurationSec);
}
