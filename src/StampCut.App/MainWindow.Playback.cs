using System.Windows;
using System.Windows.Media.Imaging;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private bool _mediaReady;
    private bool _playing;
    private bool _usingProxy;
    private double _lastPosition;
    private double _pendingSeek = -1;
    private CancellationTokenSource? _proxyCts;

    private double Position => _mediaReady ? _preview.Player.Position.TotalSeconds : _lastPosition;

    private async Task LoadIntoPlayerAsync(Clip clip)
    {
        _proxyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proxyCts = cts;
        Pause();
        _mediaReady = false;
        _lastPosition = clip.InSec;
        _preview.Player.Source = null;
        _preview.ShowMessage(null);
        _ = ShowPosterAsync(clip, cts.Token);

        string path;
        bool needProxy = clip.Media.NeedsProxy || _s.Settings.AlwaysUseProxy;
        if (!needProxy) path = clip.Media.Path;
        else
        {
            var existing = _proxy.ExistingProxy(clip.Media);
            if (existing is not null) path = existing;
            else
            {
                try
                {
                    _preview.ShowMessage("Готовлю просмотр…");
                    var progress = new Progress<ExportProgress>(p => _preview.ShowMessage($"Готовлю просмотр… {p.Fraction * 100:0}%"));
                    path = await _proxy.EnsureProxyAsync(clip.Media, progress, cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Logger.Error("Прокси", ex);
                    _preview.ShowMessage("Не удалось подготовить просмотр.\n" + ex.Message);
                    return;
                }
            }
        }
        if (cts.IsCancellationRequested) return;
        _preview.ShowMessage(null);
        _usingProxy = path != clip.Media.Path;
        OpenInPlayer(path, clip.InSec);
    }

    private void OpenInPlayer(string path, double startAt)
    {
        _mediaReady = false;
        _pendingSeek = startAt;
        _preview.Player.Source = new Uri(path);
        _preview.Player.Play(); // MediaElement открывает файл только после Play()
        _playing = true;
        _playButton.Content = "⏸";
    }

    private void OnMediaOpened()
    {
        _mediaReady = true;
        _preview.HidePoster();
        if (_pendingSeek > 0.01) { _preview.Player.Position = TimeSpan.FromSeconds(_pendingSeek); }
        _pendingSeek = -1;
        _timer.Start();
        _preview.Refresh();
    }

    private void OnMediaFailed(Exception? ex)
    {
        Logger.Warn("MediaElement не открыл файл: " + ex?.Message);
        _mediaReady = false;
        _timer.Stop();
        if (_current is null) return;
        if (!_usingProxy)
        {
            // плеер Windows не справился — делаем прокси, как для HEVC
            _preview.ShowMessage("Готовлю просмотр…");
            var clip = _current;
            _ = Task.Run(async () =>
            {
                try
                {
                    var cts = _proxyCts ?? new CancellationTokenSource();
                    var progress = new Progress<ExportProgress>(p => Dispatcher.Invoke(() => _preview.ShowMessage($"Готовлю просмотр… {p.Fraction * 100:0}%")));
                    var path = await _proxy.EnsureProxyAsync(clip.Media, progress, cts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(clip, _current)) return;
                        _preview.ShowMessage(null);
                        _usingProxy = true;
                        OpenInPlayer(path, clip.InSec);
                    });
                }
                catch (Exception e2)
                {
                    Logger.Error("Прокси после сбоя плеера", e2);
                    await Dispatcher.InvokeAsync(() => _preview.ShowMessage("Не удалось открыть видео для просмотра.\n" + e2.Message));
                }
            });
        }
        else _preview.ShowMessage("Не удалось открыть видео для просмотра.\n" + ex?.Message);
    }

    private async Task ShowPosterAsync(Clip clip, CancellationToken ct)
    {
        try
        {
            var path = await _proxy.PosterAsync(clip.Media, clip.InSec, ct);
            if (path is null || ct.IsCancellationRequested || _mediaReady) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            if (!_mediaReady && ReferenceEquals(clip, _current)) _preview.ShowPoster(bmp);
        }
        catch (Exception ex) { Logger.Warn("Постер: " + ex.Message); }
    }

    private void OnTick()
    {
        if (!_mediaReady || _current is null) return;
        double pos = _preview.Player.Position.TotalSeconds;
        _lastPosition = pos;
        if (_playing && pos >= _current.OutSec - 0.02 && _current.OutSec < _current.Media.DurationSec - 0.02)
        {
            Pause();
            Seek(_current.OutSec);
            return;
        }
        UpdateTimeUi(pos);
    }

    private void UpdateTimeUi(double pos)
    {
        if (_current is null) return;
        _timeline.Position = pos;
        _timeline.InvalidateVisual();
        _preview.CurrentTimeSec = pos;
        _preview.Layout();
        _timeText.Text = $"{TimeFormat.Format(pos)} / {TimeFormat.Format(_current.Media.DurationSec)}";
    }

    private void Seek(double t)
    {
        if (_current is null) return;
        t = Math.Clamp(t, 0, _current.Media.DurationSec);
        _lastPosition = t;
        if (_mediaReady) _preview.Player.Position = TimeSpan.FromSeconds(t);
        else _pendingSeek = t;
        UpdateTimeUi(t);
    }

    private void TogglePlay()
    {
        if (_current is null) return;
        if (_playing) { Pause(); return; }
        if (!_mediaReady) return;
        if (Position >= _current.OutSec - 0.02) Seek(_current.InSec);
        _preview.Player.Play();
        _playing = true;
        _playButton.Content = "⏸";
    }

    private void Pause()
    {
        if (_mediaReady) _preview.Player.Pause();
        _playing = false;
        _playButton.Content = "▶";
    }

    private void StepFrame(int direction)
    {
        if (_current is null) return;
        Pause();
        double fps = _current.Media.Fps > 0 ? _current.Media.Fps : 30;
        Seek(Position + direction / fps);
    }

    private void SetInHere()
    {
        if (_current is null) return;
        double t = _timeline.Snap(Position);
        _current.InSec = Math.Min(t, _current.OutSec - 0.05);
        AfterTrimChanged();
    }

    private void SetOutHere()
    {
        if (_current is null) return;
        double t = _timeline.Snap(Position);
        _current.OutSec = Math.Max(t, _current.InSec + 0.05);
        AfterTrimChanged();
    }

    private void ResetTrim()
    {
        if (_current is null) return;
        _current.InSec = 0; _current.OutSec = _current.Media.DurationSec;
        AfterTrimChanged();
    }

    private void AfterTrimChanged()
    {
        if (_current is null) return;
        _timeline.InPoint = _current.InSec; _timeline.OutPoint = _current.OutSec;
        _timeline.InvalidateVisual();
        bool trimmed = _current.InSec > 0.01 || _current.OutSec < _current.Media.DurationSec - 0.01;
        _fragmentText.Text = trimmed ? $"фрагмент {TimeFormat.Format(_current.InSec, false)}–{TimeFormat.Format(_current.OutSec, false)} ({TimeFormat.Format(_current.DurationSec, false)})" : "";
        RefreshClipList();
        UpdateExportInfo();
    }
}
