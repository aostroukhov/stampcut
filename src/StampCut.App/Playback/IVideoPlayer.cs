using System.Windows;
using StampCut.Core.Models;

namespace StampCut.App.Playback;

/// <summary>
/// Движок просмотра видео. Превью работает с ним через этот интерфейс, поэтому движок можно менять:
/// MediaElement (Windows Media Foundation) или встроенный плеер на библиотеках FFmpeg.
/// Все вызовы — из потока интерфейса; события приходят в поток интерфейса.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    /// <summary>Элемент, который рисует кадр (кладётся внутрь превью, остаётся в дереве WPF).</summary>
    UIElement View { get; }
    string Name { get; }
    bool IsOpen { get; }
    /// <summary>Позиция в секундах исходника; запись — перемотка (точная, если движок умеет).</summary>
    TimeSpan Position { get; set; }
    /// <summary>Громкость 0…1 (линейная).</summary>
    double Volume { get; set; }
    /// <summary>Нужно ли готовить прокси, чтобы этот движок показал файл.</summary>
    bool NeedsProxy(MediaInfo media);
    /// <summary>Поворот кадра по часовой стрелке (градусы), как его надо показать; 0 — не повёрнут или движок сам повернул.</summary>
    int RotationDegrees { get; }
    /// <summary>Открыть файл и начать с позиции startAt; результат — событие Opened или Failed.</summary>
    void Open(string path, double startAt);
    void Close();
    void Play();
    void Pause();
    /// <summary>Шаг на кадр вперёд/назад на паузе. false — движок не умеет, пусть вызывающий перематывает сам.</summary>
    bool StepFrame(int direction);
    event Action? Opened;
    event Action<Exception?>? Failed;
    event Action? Ended;
}
