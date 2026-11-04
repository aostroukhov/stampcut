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
    /// <summary>Выровнять громкость результата (EBU R128, −16 LUFS).</summary>
    public bool NormalizeLoudness { get; init; }
    /// <summary>Переход между соседними фрагментами: длительность в секундах (0 — встык) и тип xfade.</summary>
    public double TransitionSec { get; init; }
    public string Transition { get; init; } = "fade";
    /// <summary>Папка для временных файлов (анализ стабилизации, PNG плашек).</summary>
    public string? TempDir { get; init; }

    /// <summary>Длительность результата с учётом переходов (каждый съедает TransitionSec).</summary>
    public double TotalDurationSec
    {
        get
        {
            var parts = Clips.Sum(c => c.OrderedSegments().Count);
            return Math.Max(0, Clips.Sum(c => c.DurationSec) - EffectiveTransitionSec * Math.Max(0, parts - 1));
        }
    }

    /// <summary>Переход не длиннее половины самого короткого фрагмента (иначе xfade не сходится). 0 — переходов нет.</summary>
    public double EffectiveTransitionSec
    {
        get
        {
            if (TransitionSec <= 0.01) return 0;
            var durations = Clips.SelectMany(c => c.OrderedSegments()).Select(s => s.DurationSec).ToList();
            if (durations.Count < 2) return 0;
            return Math.Max(0, Math.Min(TransitionSec, durations.Min() / 2));
        }
    }

    public static readonly string[] Transitions = { "fade", "dissolve", "fadeblack", "fadewhite", "wipeleft", "wiperight", "slideleft", "slideright", "smoothleft", "circleopen", "pixelize" };

}
