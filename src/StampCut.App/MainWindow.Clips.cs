using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.Models;
using static StampCut.App.Lang;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private string VideoFilter => $"{T("Видео и фото")}|*.mp4;*.mov;*.m4v;*.3gp;*.mkv;*.webm;*.avi;*.mts;*.m2ts;*.wmv;*.ts;*.mpg;*.mpeg;*.flv;*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.heic;*{Core.Presets.ProjectFile.Extension}|{T("Все файлы")}|*.*";
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff", ".gif" };
    private static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // --- панель экспорта слева
    private readonly ListBox _historyList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ProgressBar _exportProgress = new() { Height = 10, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _exportStatus = Ui.Label("", muted: true, size: Ui.FontCaption);
    private UIElement _historyButtons = new Grid();

    private UIElement BuildClipsPanel()
    {
        var grid = new Grid { Margin = new Thickness(10, 0, 4, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });

        // ИМПОРТ
        var importDock = new DockPanel();
        var header = Ui.Header(T("ИМПОРТ"));
        DockPanel.SetDock(header, Dock.Top);
        importDock.Children.Add(header);
        var buttons = Ui.Row(
            Ui.SmallButton("▲", () => MoveClip(-1), T("Выше")),
            Ui.SmallButton("▼", () => MoveClip(1), T("Ниже")),
            Ui.SmallButton("✕", RemoveClip, T("Убрать ролик из проекта")),
            Ui.SmallButton("📁", () => { if (_current is not null) RevealInExplorer(_current.Media.Path); }, T("Показать файл в проводнике")),
            Ui.SmallButton("▶", () => { if (_current is not null) TryStart(_current.Media.Path); }, T("Открыть файл программой по умолчанию")));
        _clipButtons = buttons;
        DockPanel.SetDock(buttons, Dock.Bottom);
        importDock.Children.Add(buttons);
        var hint = Ui.Label(T("Ролики и фото склеиваются по порядку. Перетащите файлы сюда или вставьте Ctrl+V."), muted: true, size: Ui.FontCaption);
        hint.Margin = new Thickness(0, 4, 0, 4);
        DockPanel.SetDock(hint, Dock.Bottom);
        importDock.Children.Add(hint);
        _clipList.SelectionChanged += (_, _) =>
        {
            if (_clipList.SelectedItem is ListBoxItem { Tag: Clip c } && !ReferenceEquals(c, _current)) SelectClip(c);
        };
        _clipList.MouseDoubleClick += (_, _) => { if (_current is not null) RevealInExplorer(_current.Media.Path); };
        importDock.Children.Add(new Border { Child = _clipList, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent });
        var importCard = Ui.Card(importDock);
        importCard.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(importCard, 0); grid.Children.Add(importCard);

        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ShowsPreview = false, Focusable = false };
        Grid.SetRow(splitter, 1); grid.Children.Add(splitter);

        // ЭКСПОРТ: прогресс и история
        var exportDock = new DockPanel();
        var eh = Ui.Header(T("ЭКСПОРТ"));
        DockPanel.SetDock(eh, Dock.Top); exportDock.Children.Add(eh);
        var progressRow = Ui.Column(_exportProgress, Ui.Row(_exportStatus, _cancelButton));
        DockPanel.SetDock(progressRow, Dock.Top); exportDock.Children.Add(progressRow);
        var hb = Ui.Row(
            Ui.SmallButton("📁", () => { if (SelectedHistory is { } h) RevealInExplorer(h.Path); }, T("Показать в проводнике")),
            Ui.SmallButton("▶", () => { if (SelectedHistory is { } h) TryStart(h.Path); }, T("Открыть файл")),
            Ui.SmallButton("✕", DeleteHistoryItem, T("Удалить запись и сам файл")));
        _historyButtons = hb;
        DockPanel.SetDock(hb, Dock.Bottom); exportDock.Children.Add(hb);
        _historyList.MouseDoubleClick += (_, _) => { if (SelectedHistory is { } h) RevealInExplorer(h.Path); };
        exportDock.Children.Add(new Border { Child = _historyList, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent });
        var exportCard = Ui.Card(exportDock);
        exportCard.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(exportCard, 2); grid.Children.Add(exportCard);
        RefreshHistoryList();
        return grid;
    }

    private ExportRecord? SelectedHistory => _historyList.SelectedItem is ListBoxItem { Tag: ExportRecord r } ? r : null;

    private void RefreshHistoryList()
    {
        _historyList.Items.Clear();
        foreach (var r in _s.Settings.ExportHistory.OrderByDescending(x => x.At).Take(50))
        {
            var text = new StackPanel();
            bool exists = File.Exists(r.Path);
            var name = Ui.Label(Path.GetFileName(r.Path), weight: FontWeights.SemiBold);
            if (!exists) name.Opacity = 0.5;
            text.Children.Add(name);
            text.Children.Add(Ui.Label($"{r.At:dd.MM HH:mm} · {TimeFormat.Format(r.DurationSec, false)} · {TimeFormat.FormatSize(r.SizeBytes)}" + (exists ? "" : $" · {T("файла нет")}"), muted: true, size: Ui.FontCaption));
            _historyList.Items.Add(new ListBoxItem { Content = text, Tag = r, Padding = new Thickness(8, 4, 8, 4), ToolTip = r.Path });
        }
        _historyButtons.IsEnabled = _historyList.Items.Count > 0;
    }

    private void DeleteHistoryItem()
    {
        if (SelectedHistory is not { } r) return;
        bool exists = File.Exists(r.Path);
        if (exists && MessageBox.Show(this, $"{T("Удалить файл")} {Path.GetFileName(r.Path)}?", "StampCut", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { if (exists) File.Delete(r.Path); }
        catch (Exception ex) { ShowInfo(T("Не удалось удалить файл: ") + ex.Message, InfoKind.Error); return; }
        _s.Settings.ExportHistory.Remove(r);
        try { _s.Settings.Save(); } catch { }
        RefreshHistoryList();
    }

    private void RefreshClipList()
    {
        _clipList.Items.Clear();
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = _clips[i];
            var segs = c.OrderedSegments();
            var text = new StackPanel();
            text.Children.Add(Ui.Label($"{i + 1}. {(c.Media.IsImage ? "🖼 " : "")}{c.FileName}", weight: FontWeights.SemiBold));
            var info = segs.Count == 1 ? $"{TimeFormat.Format(segs[0].InSec, false)}–{TimeFormat.Format(segs[0].OutSec, false)}" : $"{segs.Count} {T("фрагм.")} {TimeFormat.Format(c.DurationSec, false)}";
            text.Children.Add(Ui.Label($"{info} · {c.Media.Width}×{c.Media.Height}" +
                                       (c.Crop is { IsFull: false } ? $" · {T("кроп")}" : "") + (c.Redactions.Count > 0 ? $" · {T("ретушь")} {c.Redactions.Count}" : "") + (c.Censors.Count > 0 ? $" · {T("пик")} {c.Censors.Count}" : "")
                                       + (c.IsMuted ? $" · {T("без звука")}" : "") + (!c.Filters.IsDefault ? $" · {T("фильтры")}" : ""), muted: true, size: Ui.FontCaption));
            var item = new ListBoxItem { Content = text, Tag = c, Padding = new Thickness(8, 6, 8, 6), ToolTip = c.Media.Path };
            _clipList.Items.Add(item);
            if (ReferenceEquals(c, _current)) _clipList.SelectedItem = item;
        }
    }

    private void OpenFiles(bool replace)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = VideoFilter, Title = replace ? T("Открыть видео") : T("Добавить ролик") };
        var dir = _s.Settings.LastOpenDir;
        if (dir is not null) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) Fire(OpenAnyAsync(dlg.FileNames, replace));
    }

    private void ShowRecentMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var files = _s.Settings.RecentFiles.Where(File.Exists).Take(10).ToList();
        var dirs = _s.Settings.RecentOpenDirs.Where(Directory.Exists).Take(8).ToList();
        if (files.Count == 0 && dirs.Count == 0) menu.Items.Add(new MenuItem { Header = T("Пока пусто"), IsEnabled = false });
        if (files.Count > 0) menu.Items.Add(new MenuItem { Header = T("Файлы"), IsEnabled = false, FontWeight = FontWeights.Bold });
        foreach (var f in files)
        {
            var mi = new MenuItem { Header = Path.GetFileName(f), ToolTip = f };
            mi.Click += (_, _) => Fire(OpenAnyAsync(new[] { f }, replace: _clips.Count == 0));
            menu.Items.Add(mi);
        }
        if (dirs.Count > 0) { menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = T("Папки"), IsEnabled = false, FontWeight = FontWeights.Bold }); }
        foreach (var d in dirs)
        {
            var mi = new MenuItem { Header = d.Length > 48 ? "…" + d[^46..] : d, ToolTip = d };
            mi.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog { Multiselect = true, InitialDirectory = d, Filter = VideoFilter };
                if (dlg.ShowDialog(this) == true) Fire(OpenAnyAsync(dlg.FileNames, replace: _clips.Count == 0));
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private async Task AddFilesAsync(IEnumerable<string> files, bool replace)
    {
        var list = files.Where(File.Exists).ToList();
        if (list.Count == 0) return;
        if (replace)
        {
            if (!ConfirmDiscardChanges()) return;
            Pause();
            _clips.Clear();
            _current = null; _activeSegment = null;
            _projectPath = null;
            SetTitle(null);
        }
        else Autosave("import");
        Clip? first = null;
        foreach (var file in list)
        {
            try
            {
                _statusInfo.Text = T("Открываю ") + Path.GetFileName(file) + "…";
                var media = await _s.Probe.ProbeAsync(file);
                if (!media.HasVideo) { ShowInfo($"{Path.GetFileName(file)}: {T("в файле нет видео.")}", InfoKind.Warning); continue; }
                _mediaCache[media.Path] = media;
                var clip = Core.Models.Clip.FromMedia(media);
                _clips.Add(clip);
                first ??= clip;
                AppSettings.Touch(_s.Settings.RecentFiles, file);
                AppSettings.Touch(_s.Settings.RecentOpenDirs, Path.GetDirectoryName(file));
                Logger.Info($"Открыт {file}: {media.Summary()}");
            }
            catch (Exception ex)
            {
                Logger.Error("Не удалось открыть " + file, ex);
                ShowInfo($"{Path.GetFileName(file)}: {ex.Message}", InfoKind.Error);
            }
        }
        try { _s.Settings.Save(); } catch (Exception ex) { Logger.Warn("Настройки: " + ex.Message); }
        ApplyDefaultOverlayIfNeeded();
        RefreshClipList();
        UpdateExportInfo();
        if (replace) _history.Reset(Snapshot(), saved: false); else CommitNow();
        UpdateDirtyTitle();
        if (first is not null) SelectClip(first);
        else if (_clips.Count == 0) ClearPreview();
    }

    private void ClearPreview()
    {
        UpdateEnabledState();
        _timer.Stop();
        _mediaReady = false; _isImageClip = false;
        _preview.CurrentClip = null; _preview.Player.Source = null; _preview.ShowPoster(null); _preview.ShowMessage(null); _preview.Refresh();
        _timeline.SetDuration(0); _timeline.Segments = new List<Segment>(); _timeline.ActiveSegment = null; _timeline.Waveform = null; _timeline.Filmstrip = null; _timeline.InvalidateVisual();
        _statusInfo.Text = T("Перетащите видео в окно или нажмите «Открыть»");
        _fragmentText.Text = "";
        _timeText.Text = "00:00.000 / 00:00.000";
    }

    /// <summary>keepPlayer — тот же файл уже открыт в плеере: не перезагружать, только панели.</summary>
    private void SelectClip(Clip clip, double? startAt = null, bool autoplay = true, bool keepPlayer = false)
    {
        _current = clip;
        UpdateEnabledState();
        _activeSegment = null;
        _selectedRedaction = null;
        RefreshClipList();
        _preview.CurrentClip = clip;
        _preview.SetVideoSize(clip.Media.Width, clip.Media.Height);
        _preview.SetFilters(clip.Filters);
        if (!keepPlayer) { _timeline.SetDuration(clip.Media.DurationSec); _timeline.Waveform = null; _timeline.Filmstrip = null; }
        _timeline.Segments = clip.Segments;
        _timeline.ActiveSegment = null;
        _timeline.Muted = clip.IsMuted;
        _statusInfo.Text = clip.FileName + " · " + clip.Media.Summary();
        UpdateOutputGeometry();
        SyncCropPanel(); SyncRedactPanel(); SyncCensorPanel(); SyncFiltersPanel(); UpdateTimelineMarkers();
        SetGain(clip.AudioGainDb);
        AfterTrimChanged(light: true);
        if (keepPlayer && _mediaReady)
        {
            if (startAt is { } t) Seek(t);
            _preview.Refresh();
            return;
        }
        _timeline.Keyframes = Array.Empty<double>();
        Fire(LoadIntoPlayerAsync(clip, startAt, autoplay)); // просмотр готовится в фоне, интерфейс не ждёт прокси
        if (!clip.Media.IsImage) { Fire(LoadKeyframesAsync(clip)); Fire(LoadWaveformAsync(clip)); Fire(LoadFilmstripAsync(clip)); }
    }

    private readonly FilmstripService _filmstrip;

    private async Task LoadFilmstripAsync(Clip clip)
    {
        try
        {
            var strip = await _filmstrip.GetAsync(clip.Media, _waveformCts?.Token ?? CancellationToken.None);
            if (ReferenceEquals(clip, _current)) { _timeline.Filmstrip = strip; _timeline.FilmstripFrames = FilmstripService.Frames; _timeline.InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn("Полоска кадров: " + ex.Message); }
    }

    private readonly WaveformService _waveform;
    private CancellationTokenSource? _waveformCts;

    private async Task LoadWaveformAsync(Clip clip)
    {
        _waveformCts?.Cancel();
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        try
        {
            var wf = await _waveform.GetAsync(clip.Media, cts.Token);
            if (ReferenceEquals(clip, _current) && !cts.IsCancellationRequested) { _timeline.Waveform = wf; _timeline.WaveformRate = WaveformService.Rate; _timeline.InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn("Волна звука: " + ex.Message); }
    }

    private void SelectClipOffset(int delta)
    {
        if (_current is null || _clips.Count < 2) return;
        int cur = _clips.IndexOf(_current);
        int i = Math.Clamp(cur + delta, 0, _clips.Count - 1);
        if (i != cur) SelectClip(_clips[i]);
    }

    private void MoveClip(int delta)
    {
        if (_current is null) return;
        int i = _clips.IndexOf(_current), j = i + delta;
        if (j < 0 || j >= _clips.Count) return;
        (_clips[i], _clips[j]) = (_clips[j], _clips[i]);
        RefreshClipList();
        UpdateOutputGeometry();
        UpdateExportInfo();
        CommitNow();
    }

    private void RemoveClip()
    {
        if (_current is null) return;
        int i = _clips.IndexOf(_current);
        _clips.Remove(_current);
        Pause();
        _current = null;
        RefreshClipList();
        if (_clips.Count > 0) SelectClip(_clips[Math.Clamp(i, 0, _clips.Count - 1)]);
        else ClearPreview();
        UpdateExportInfo();
        CommitNow();
    }

    /// <summary>Размер выходного кадра для превью (WYSIWYG оверлея) — тот же расчёт, что при экспорте.</summary>
    private void UpdateOutputGeometry(bool rebuild = true)
    {
        if (_clips.Count == 0 || _current is null) return;
        var job = new ExportJob { Clips = _clips, Preset = _preset, OutputPath = "x", Encoder = HardwareEncoder.None };
        var (w, h) = _preset.StreamCopy ? (_current.Media.Width, _current.Media.Height) : Core.FFmpeg.FFmpegCommandBuilder.ComputeOutputSize(job);
        _preview.OutputWidth = w; _preview.OutputHeight = h;
        bool isFirst = ReferenceEquals(_clips[0], _current);
        _preview.Fit = _preset.StreamCopy ? FitMode.Keep : (_preset.HasFixedSize ? _preset.Fit : (isFirst ? FitMode.Keep : FitMode.PadBlack));
        if (rebuild) _preview.Refresh(); else _preview.Layout(); // во время перетаскивания слой перестраивать нельзя
    }

    private async Task LoadKeyframesAsync(Clip clip)
    {
        try
        {
            var kf = await _s.Probe.KeyframesAsync(clip.Media.Path);
            if (ReferenceEquals(clip, _current)) { _timeline.Keyframes = kf; _timeline.InvalidateVisual(); }
        }
        catch (Exception ex) { Logger.Warn("Ключевые кадры: " + ex.Message); }
    }
}
