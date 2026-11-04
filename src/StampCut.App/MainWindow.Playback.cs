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
    private bool _followEdit = true;
    private double _lastPosition;
    private double _pendingSeek = -1;
    private bool _autoplayOnOpen = true;
    private CancellationTokenSource? _proxyCts;
    private Segment? _activeSegment;

    private double Position => _mediaReady ? _preview.Player.Position.TotalSeconds : _lastPosition;

    /// <summary>Фрагмент, с которым работают I/O и кнопки: выбранный на ленте или содержащий позицию, или первый.</summary>
    private Segment? ActiveSegment
    {
        get
        {
            if (_current is null) return null;
            if (_activeSegment is not null && _current.Segments.Contains(_activeSegment)) return _activeSegment;
            _activeSegment = _current.Segments.FirstOrDefault(s => s.Contains(Position)) ?? _current.Segments.FirstOrDefault();
            return _activeSegment;
        }
    }

    private async Task LoadIntoPlayerAsync(Clip clip, double? startAt = null, bool autoplay = true)
    {
        _proxyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proxyCts = cts;
        Pause();
        _mediaReady = false;
        double start = startAt ?? clip.InSec;
        _lastPosition = start;
        _autoplayOnOpen = autoplay;
        _preview.Player.Source = null;
        _preview.ShowMessage(null);
        _ = ShowPosterAsync(clip, start, cts.Token);

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
        OpenInPlayer(path, start);
    }

    private void OpenInPlayer(string path, double startAt)
    {
        _mediaReady = false;
        _pendingSeek = startAt;
        _preview.Player.Source = Ui.FileUri(path);
        _preview.Player.Play(); // MediaElement открывает файл только после Play()
        _playing = true;
        _playButton.Content = "⏸";
    }

    private readonly System.Windows.Threading.DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _filterCts;

    /// <summary>На паузе показываем кадр с фильтрами (его считает ffmpeg), при воспроизведении — исходник.</summary>
    private void RequestFilteredFrame()
    {
        _filterTimer.Stop();
        if (_current is null || _playing || !_mediaReady) return;
        _preview.HidePoster(); // старый кадр с фильтрами не должен перекрывать новую позицию
        if (_current.Filters.IsDefault) return;
        _filterTimer.Tick -= OnFilterTimer; _filterTimer.Tick += OnFilterTimer;
        _filterTimer.Start();
    }

    private void OnFilterTimer(object? sender, EventArgs e)
    {
        _filterTimer.Stop();
        if (_current is null || _playing) return;
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        var clip = _current;
        double at = Position;
        Fire(Task.Run(async () =>
        {
            var path = await _proxy.PosterAsync(clip.Media, at, cts.Token, clip.Filters.Clone());
            if (path is null || cts.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(clip, _current) || _playing || cts.IsCancellationRequested) return;
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bmp.UriSource = Ui.FileUri(path); bmp.EndInit(); bmp.Freeze();
                _preview.ShowPoster(bmp);
            });
        }));
    }

    private void OnMediaOpened()
    {
        _mediaReady = true;
        ApplyGainToPlayer();
        _preview.HidePoster();
        if (_pendingSeek > 0.01) _preview.Player.Position = TimeSpan.FromSeconds(_pendingSeek);
        _pendingSeek = -1;
        _timer.Start();
        _preview.Refresh();
        if (!_autoplayOnOpen) Pause();
    }

    private void RefreshFilteredFrameIfPaused() { if (!_playing) RequestFilteredFrame(); }

    private void OnMediaFailed(Exception? ex)
    {
        Logger.Warn("MediaElement не открыл файл: " + ex?.Message);
        _mediaReady = false;
        _timer.Stop();
        if (_current is null) return;
        if (!_usingProxy)
        {
            _preview.ShowMessage("Готовлю просмотр…");
            var clip = _current;
            double start = _lastPosition;
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
                        OpenInPlayer(path, start);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception e2)
                {
                    Logger.Error("Прокси после сбоя плеера", e2);
                    await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(clip, _current)) _preview.ShowMessage("Не удалось открыть видео для просмотра.\n" + e2.Message); });
                }
            });
        }
        else _preview.ShowMessage("Не удалось открыть видео для просмотра.\n" + ex?.Message);
    }

    private async Task ShowPosterAsync(Clip clip, double atSec, CancellationToken ct)
    {
        try
        {
            var path = await _proxy.PosterAsync(clip.Media, atSec, ct);
            if (path is null || ct.IsCancellationRequested || _mediaReady) return;
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bmp.UriSource = Ui.FileUri(path); bmp.EndInit(); bmp.Freeze();
            if (!_mediaReady && ReferenceEquals(clip, _current)) _preview.ShowPoster(bmp);
        }
        catch (Exception ex) { Logger.Warn("Постер: " + ex.Message); }
    }

    private double _jumpTarget = -1;
    private DateTime _jumpAt;

    private void OnTick()
    {
        if (!_mediaReady || _current is null) return;
        double pos = _preview.Player.Position.TotalSeconds;
        _lastPosition = pos;
        if (_playing && !_followEdit && pos >= _current.Media.DurationSec - 0.02) { Pause(); UpdateTimeUi(pos); return; }
        if (_playing && _followEdit && !_timeline.IsDragging && !FollowEditTick(pos)) return;
        UpdateTimeUi(pos);
    }

    /// <summary>Режим «По склейке». Возвращает false, если позиция изменилась или ролик сменился (интерфейс обновлять не нужно).</summary>
    private bool FollowEditTick(double pos)
    {
        if (_current is null) return true;
        // только что прыгнули — даём плееру доехать до цели, иначе будем дёргать его каждые 33 мс
        if (_jumpTarget >= 0 && pos < _jumpTarget + 0.5 && (DateTime.UtcNow - _jumpAt).TotalMilliseconds < 400) return true;
        _jumpTarget = -1;
        var segs = _current.OrderedSegments();
        var inside = segs.FirstOrDefault(s => s.Contains(pos) || Math.Abs(pos - s.InSec) < 0.1);
        if (inside is not null)
        {
            if (!ReferenceEquals(inside, _activeSegment)) _activeSegment = inside;
            if (pos < inside.OutSec - 0.02) return true;
            int i = segs.IndexOf(inside);
            var next = i + 1 < segs.Count ? segs[i + 1] : null;
            if (next is not null)
            {
                _activeSegment = next;
                if (next.InSec > pos + 0.02) { JumpTo(next.InSec); return false; }
                return true; // смежный фрагмент: просто едем дальше
            }
            return NextClipOrPause(inside.OutSec);
        }
        var after = segs.FirstOrDefault(s => s.InSec > pos);
        if (after is not null) { _activeSegment = after; JumpTo(after.InSec); return false; }
        return NextClipOrPause(null);
    }

    private void JumpTo(double t)
    {
        _jumpTarget = t; _jumpAt = DateTime.UtcNow;
        Seek(t);
    }

    private bool NextClipOrPause(double? stopAt)
    {
        if (_current is null) return true;
        int idx = _clips.IndexOf(_current);
        if (idx >= 0 && idx + 1 < _clips.Count)
        {
            var nc = _clips[idx + 1];
            SelectClip(nc, startAt: nc.OrderedSegments().FirstOrDefault()?.InSec ?? 0, autoplay: true);
            return false;
        }
        Pause();
        if (stopAt is { } t) Seek(t);
        return false;
    }

    private void UpdateTimeUi(double pos)
    {
        if (_current is null) return;
        _timeline.Position = pos;
        _timeline.ActiveSegment = ActiveSegment;
        _timeline.InvalidateVisual();
        _preview.CurrentTimeSec = pos;
        _preview.OutputTimeSec = OutputTime();
        _preview.Layout();
        _timeText.Text = $"{TimeFormat.Format(pos)} / {TimeFormat.Format(_current.Media.DurationSec)}";
    }

    private void Seek(double t)
    {
        if (_current is null) return;
        if (_jumpTarget >= 0 && Math.Abs(t - _jumpTarget) > 0.001) _jumpTarget = -1;
        t = Math.Clamp(t, 0, _current.Media.DurationSec);
        _lastPosition = t;
        if (_mediaReady) _preview.Player.Position = TimeSpan.FromSeconds(t);
        else _pendingSeek = t;
        UpdateTimeUi(t);
        RefreshFilteredFrameIfPaused();
    }

    private void TogglePlay()
    {
        if (_current is null) return;
        if (_playing) { Pause(); return; }
        if (!_mediaReady) return;
        if (_followEdit)
        {
            var segs = _current.OrderedSegments();
            var inside = segs.FirstOrDefault(s => s.Contains(Position) && Position < s.OutSec - 0.05);
            if (inside is null)
            {
                var next = segs.FirstOrDefault(s => s.InSec > Position) ?? segs.FirstOrDefault();
                if (next is not null) { _activeSegment = next; Seek(next.InSec); }
            }
        }
        else if (Position >= _current.Media.DurationSec - 0.05) Seek(0);
        _filterTimer.Stop();
        _filterCts?.Cancel();
        _preview.HidePoster();
        _preview.Player.Play();
        _playing = true;
        _playButton.Content = "⏸";
    }

    private void Pause()
    {
        if (_mediaReady) _preview.Player.Pause();
        _playing = false;
        _playButton.Content = "▶";
        RequestFilteredFrame();
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
        if (ActiveSegment is not { } seg) return;
        double t = _timeline.Snap(Position);
        seg.InSec = Math.Min(t, seg.OutSec - 0.05);
        AfterTrimChanged();
    }

    private void SetOutHere()
    {
        if (ActiveSegment is not { } seg) return;
        double t = _timeline.Snap(Position);
        seg.OutSec = Math.Max(t, seg.InSec + 0.05);
        AfterTrimChanged();
    }

    /// <summary>Новый фрагмент от текущей позиции до начала следующего фрагмента (или до конца ролика).</summary>
    private void AddSegmentHere()
    {
        if (_current is null) return;
        double t = _timeline.Snap(Position);
        var seg = new Segment { InSec = t, OutSec = _current.Media.DurationSec };
        var next = _current.OrderedSegments().FirstOrDefault(s => s.InSec > t + 0.05);
        if (next is not null) seg.OutSec = next.InSec;
        var inside = _current.Segments.FirstOrDefault(s => s.Contains(t) && s.OutSec - t > 0.1);
        if (inside is not null) { seg.OutSec = inside.OutSec; inside.OutSec = t; } // режем текущий фрагмент пополам
        if (seg.DurationSec < 0.05) { ShowInfo("Здесь нет места для нового фрагмента.", InfoKind.Warning); return; }
        _current.Segments.Add(seg);
        _activeSegment = seg;
        AfterTrimChanged();
    }

    private void RemoveActiveSegment()
    {
        if (_current is null || ActiveSegment is not { } seg) return;
        _current.Segments.Remove(seg);
        if (_current.Segments.Count == 0) _current.Segments.Add(new Segment { InSec = 0, OutSec = _current.Media.DurationSec });
        _activeSegment = null;
        AfterTrimChanged();
    }

    private void ResetTrim()
    {
        if (_current is null) return;
        _current.Segments.Clear();
        _current.Segments.Add(new Segment { InSec = 0, OutSec = _current.Media.DurationSec });
        _activeSegment = null;
        AfterTrimChanged();
    }

    /// <summary>light = во время перетаскивания маркера: только лента и подпись, без перестроения списков.</summary>
    private void AfterTrimChanged(bool light = false)
    {
        if (_current is null) return;
        _timeline.Segments = _current.Segments;
        _timeline.ActiveSegment = ActiveSegment;
        _timeline.InvalidateVisual();
        var segs = _current.OrderedSegments();
        _fragmentText.Text = !_current.IsTrimmed ? "весь ролик"
            : segs.Count == 1 ? $"фрагмент {TimeFormat.Format(segs[0].InSec, false)}–{TimeFormat.Format(segs[0].OutSec, false)} ({TimeFormat.Format(_current.DurationSec, false)})"
            : $"фрагментов: {segs.Count}, итого {TimeFormat.Format(_current.DurationSec, false)}";
        if (light) return;
        RefreshClipList();
        UpdateExportInfo();
    }
}
