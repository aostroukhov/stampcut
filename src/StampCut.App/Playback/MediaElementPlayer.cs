using System.Windows;
using System.Windows.Controls;
using StampCut.App.Controls;
using StampCut.Core.Models;

namespace StampCut.App.Playback;

/// <summary>Прежний движок: WPF MediaElement (Media Foundation). Играет только H.264 в MP4 без поворота и HDR, остальное — через прокси.</summary>
public sealed class MediaElementPlayer : IVideoPlayer
{
    private readonly MediaElement _me = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = System.Windows.Media.Stretch.Uniform,
    };

    public MediaElementPlayer()
    {
        _me.MediaOpened += (_, _) => { IsOpen = true; Opened?.Invoke(); };
        _me.MediaFailed += (_, e) => { IsOpen = false; Failed?.Invoke(e.ErrorException); };
        _me.MediaEnded += (_, _) => Ended?.Invoke();
    }

    public UIElement View => _me;
    public string Name => "Windows Media Foundation";
    public bool IsOpen { get; private set; }
    public TimeSpan Position { get => _me.Position; set => _me.Position = value; }
    public double Volume { get => _me.Volume; set => _me.Volume = value; }
    public bool NeedsProxy(MediaInfo media) => media.NeedsProxy;
    public int RotationDegrees => 0;

    public void Open(string path, double startAt)
    {
        IsOpen = false;
        _me.Source = Ui.FileUri(path);
        _me.Play(); // MediaElement открывает файл только после Play()
    }

    public void Close() { IsOpen = false; _me.Source = null; }
    public void Play() => _me.Play();
    public void Pause() => _me.Pause();
    public bool StepFrame(int direction) => false;

    public event Action? Opened;
    public event Action<Exception?>? Failed;
    public event Action? Ended;

    public void Dispose() { try { _me.Close(); } catch { } }
}
