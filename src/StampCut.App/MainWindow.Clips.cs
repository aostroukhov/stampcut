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
    private string VideoFilter => $"{T("Видео")}|*.mp4;*.mov;*.m4v;*.3gp;*.mkv;*.webm;*.avi;*.mts;*.m2ts;*.wmv;*.ts;*.mpg;*.mpeg;*.flv;*{Core.Presets.ProjectFile.Extension}|{T("Все файлы")}|*.*";

    private UIElement BuildClipsPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(10, 0, 4, 0) };
        var header = Ui.Row(Ui.Label(T("Ролики"), weight: FontWeights.Bold, size: 13));
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var buttons = Ui.Row(
            Ui.SmallButton("▲", () => MoveClip(-1), T("Выше")),
            Ui.SmallButton("▼", () => MoveClip(1), T("Ниже")),
            Ui.SmallButton("✕", RemoveClip, T("Убрать ролик из проекта")));
        _clipButtons = buttons;
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        var hint = Ui.Label(T("Ролики склеиваются по порядку. У каждого — своя обрезка, кроп и ретушь. Оверлей общий."), muted: true, size: 11);
        hint.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(hint, Dock.Bottom);
        panel.Children.Add(hint);

        _clipList.SelectionChanged += (_, _) =>
        {
            if (_clipList.SelectedItem is ListBoxItem { Tag: Clip c } && !ReferenceEquals(c, _current)) SelectClip(c);
        };
        panel.Children.Add(Ui.Card(_clipList));
        return panel;
    }

    private void RefreshClipList()
    {
        _clipList.Items.Clear();
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = _clips[i];
            var segs = c.OrderedSegments();
            var text = new StackPanel();
            text.Children.Add(Ui.Label($"{i + 1}. {c.FileName}", size: 12, weight: FontWeights.SemiBold));
            var info = segs.Count == 1 ? $"{TimeFormat.Format(segs[0].InSec, false)}–{TimeFormat.Format(segs[0].OutSec, false)}" : $"{segs.Count} {T("фрагм.")} {TimeFormat.Format(c.DurationSec, false)}";
            text.Children.Add(Ui.Label($"{info} · {c.Media.Width}×{c.Media.Height}" +
                                       (c.Crop is { IsFull: false } ? $" · {T("кроп")}" : "") + (c.Redactions.Count > 0 ? $" · {T("ретушь")} {c.Redactions.Count}" : "") + (c.Censors.Count > 0 ? $" · {T("пик")} {c.Censors.Count}" : ""), muted: true, size: 11));
            var item = new ListBoxItem { Content = text, Tag = c, Padding = new Thickness(8, 6, 8, 6) };
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
            Pause();
            _clips.Clear();
            _current = null; _activeSegment = null;
            _projectPath = null;
            SetTitle(null);
        }
        Clip? first = null;
        foreach (var file in list)
        {
            try
            {
                _statusInfo.Text = T("Открываю ") + Path.GetFileName(file) + "…";
                var media = await _s.Probe.ProbeAsync(file);
                if (!media.HasVideo) { ShowInfo($"{Path.GetFileName(file)}: {T("в файле нет видео.")}", InfoKind.Warning); continue; }
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
        if (first is not null) SelectClip(first);
        else if (_clips.Count == 0) ClearPreview();
    }

    private void ClearPreview()
    {
        UpdateEnabledState();
        _timer.Stop();
        _mediaReady = false;
        _preview.CurrentClip = null; _preview.Player.Source = null; _preview.ShowPoster(null); _preview.ShowMessage(null); _preview.Refresh();
        _timeline.Duration = 0; _timeline.Segments = new List<Segment>(); _timeline.ActiveSegment = null; _timeline.InvalidateVisual();
        _statusInfo.Text = T("Перетащите видео в окно или нажмите «Открыть»");
        _fragmentText.Text = "";
        _timeText.Text = "00:00.000 / 00:00.000";
    }

    private void SelectClip(Clip clip, double? startAt = null, bool autoplay = true)
    {
        _current = clip;
        UpdateEnabledState();
        _activeSegment = null;
        _selectedRedaction = null;
        RefreshClipList();
        _preview.CurrentClip = clip;
        _preview.SetVideoSize(clip.Media.Width, clip.Media.Height);
        _timeline.Duration = clip.Media.DurationSec;
        _timeline.Segments = clip.Segments;
        _timeline.ActiveSegment = null;
        _timeline.Keyframes = Array.Empty<double>();
        _statusInfo.Text = clip.FileName + " · " + clip.Media.Summary();
        UpdateOutputGeometry();
        SyncCropPanel(); SyncRedactPanel(); SyncCensorPanel(); SyncFiltersPanel(); UpdateTimelineMarkers();
        SetGain(clip.AudioGainDb);
        AfterTrimChanged(light: true);
        Fire(LoadIntoPlayerAsync(clip, startAt, autoplay)); // просмотр готовится в фоне, интерфейс не ждёт прокси
        Fire(LoadKeyframesAsync(clip));
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
