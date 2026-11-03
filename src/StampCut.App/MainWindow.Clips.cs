using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.Models;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private UIElement BuildClipsPanel()
    {
        var panel = new DockPanel { Width = 230, Margin = new Thickness(10, 0, 8, 0) };
        var header = Ui.Row(Ui.Label("Ролики", weight: FontWeights.Bold, size: 13));
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var buttons = Ui.Row(
            Ui.SmallButton("▲", () => MoveClip(-1), "Выше"),
            Ui.SmallButton("▼", () => MoveClip(1), "Ниже"),
            Ui.SmallButton("✕", RemoveClip, "Убрать ролик из проекта"));
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        var hint = Ui.Label("Ролики склеиваются по порядку. У каждого — своя обрезка, кроп и ретушь. Оверлей общий.", muted: true, size: 11);
        hint.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(hint, Dock.Bottom);
        panel.Children.Add(hint);

        _clipList.SelectionChanged += (_, _) =>
        {
            if (_clipList.SelectedItem is ListBoxItem { Tag: Clip c } && !ReferenceEquals(c, _current)) _ = SelectClipAsync(c);
        };
        panel.Children.Add(new Border { Child = _clipList, Background = Brushes.White, BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
        return panel;
    }

    private void RefreshClipList()
    {
        _clipList.Items.Clear();
        for (int i = 0; i < _clips.Count; i++)
        {
            var c = _clips[i];
            var text = new StackPanel();
            text.Children.Add(Ui.Label($"{i + 1}. {c.FileName}", size: 12, weight: FontWeights.SemiBold));
            text.Children.Add(Ui.Label($"{TimeFormat.Format(c.InSec, false)}–{TimeFormat.Format(c.OutSec, false)} · {c.Media.Width}×{c.Media.Height}" +
                                       (c.Crop is { IsFull: false } ? " · кроп" : "") + (c.Redactions.Count > 0 ? $" · ретушь {c.Redactions.Count}" : "") + (c.Censors.Count > 0 ? $" · пик {c.Censors.Count}" : ""), muted: true, size: 11));
            var item = new ListBoxItem { Content = text, Tag = c, Padding = new Thickness(8, 6, 8, 6) };
            _clipList.Items.Add(item);
            if (ReferenceEquals(c, _current)) _clipList.SelectedItem = item;
        }
    }

    private void OpenFiles(bool replace)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Видео|*.mp4;*.mov;*.m4v;*.3gp;*.mkv;*.webm;*.avi;*.mts;*.m2ts;*.wmv;*.ts;*.mpg;*.mpeg;*.flv|Все файлы|*.*",
            Title = replace ? "Открыть видео" : "Добавить ролик",
        };
        var dir = _s.Settings.LastOpenDir;
        if (dir is not null) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) _ = AddFilesAsync(dlg.FileNames, replace);
    }

    private void ShowRecentMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var files = _s.Settings.RecentFiles.Where(File.Exists).Take(8).ToList();
        var dirs = _s.Settings.RecentOpenDirs.Where(Directory.Exists).Take(8).ToList();
        if (files.Count == 0 && dirs.Count == 0) menu.Items.Add(new MenuItem { Header = "Пока пусто", IsEnabled = false });
        if (files.Count > 0) menu.Items.Add(new MenuItem { Header = "Файлы", IsEnabled = false, FontWeight = FontWeights.Bold });
        foreach (var f in files)
        {
            var mi = new MenuItem { Header = Path.GetFileName(f), ToolTip = f };
            mi.Click += (_, _) => _ = AddFilesAsync(new[] { f }, replace: _clips.Count == 0);
            menu.Items.Add(mi);
        }
        if (dirs.Count > 0) { menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "Папки", IsEnabled = false, FontWeight = FontWeights.Bold }); }
        foreach (var d in dirs)
        {
            var mi = new MenuItem { Header = d.Length > 48 ? "…" + d[^46..] : d, ToolTip = d };
            mi.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog { Multiselect = true, InitialDirectory = d, Filter = "Видео|*.mp4;*.mov;*.m4v;*.3gp;*.mkv;*.webm;*.avi;*.mts;*.m2ts;*.wmv;*.ts|Все файлы|*.*" };
                if (dlg.ShowDialog(this) == true) _ = AddFilesAsync(dlg.FileNames, replace: _clips.Count == 0);
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
            _current = null;
        }
        Clip? first = null;
        foreach (var file in list)
        {
            try
            {
                _statusInfo.Text = "Открываю " + Path.GetFileName(file) + "…";
                var media = await _s.Probe.ProbeAsync(file);
                if (!media.HasVideo) { ShowInfo($"{Path.GetFileName(file)}: в файле нет видео.", InfoKind.Warning); continue; }
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
        _s.Settings.Save();
        ApplyDefaultOverlayIfNeeded();
        RefreshClipList();
        if (first is not null) await SelectClipAsync(first);
        UpdateExportInfo();
    }

    private async Task SelectClipAsync(Clip clip)
    {
        _current = clip;
        _selectedRedaction = null;
        RefreshClipList();
        _preview.CurrentClip = clip;
        _preview.SetVideoSize(clip.Media.Width, clip.Media.Height);
        _timeline.Duration = clip.Media.DurationSec;
        _timeline.InPoint = clip.InSec; _timeline.OutPoint = clip.OutSec;
        _timeline.Keyframes = Array.Empty<double>();
        _statusInfo.Text = clip.FileName + " · " + clip.Media.Summary();
        UpdateOutputGeometry();
        SyncCropPanel(); SyncRedactPanel(); SyncCensorPanel(); UpdateTimelineMarkers();
        await LoadIntoPlayerAsync(clip);
        if (_preset.StreamCopy) _ = LoadKeyframesAsync(clip);
    }

    private void SelectClipOffset(int delta)
    {
        if (_current is null || _clips.Count < 2) return;
        int i = Math.Clamp(_clips.IndexOf(_current) + delta, 0, _clips.Count - 1);
        _ = SelectClipAsync(_clips[i]);
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
        if (_clips.Count > 0) _ = SelectClipAsync(_clips[Math.Clamp(i, 0, _clips.Count - 1)]);
        else
        {
            _preview.CurrentClip = null; _preview.Player.Source = null; _preview.ShowPoster(null); _preview.Refresh();
            _timeline.Duration = 0; _timeline.InvalidateVisual();
            _statusInfo.Text = "Перетащите видео в окно или нажмите «Открыть»";
        }
        UpdateExportInfo();
    }

    /// <summary>Размер выходного кадра для превью (WYSIWYG оверлея) — тот же расчёт, что при экспорте.</summary>
    private void UpdateOutputGeometry()
    {
        if (_clips.Count == 0 || _current is null) return;
        var job = new ExportJob { Clips = _clips, Preset = _preset, OutputPath = "x", Encoder = HardwareEncoder.None };
        var (w, h) = _preset.StreamCopy ? (_current.Media.Width, _current.Media.Height) : Core.FFmpeg.FFmpegCommandBuilder.ComputeOutputSize(job);
        _preview.OutputWidth = w; _preview.OutputHeight = h;
        bool isFirst = ReferenceEquals(_clips[0], _current);
        _preview.Fit = _preset.StreamCopy ? FitMode.Keep : (_preset.HasFixedSize ? _preset.Fit : (isFirst ? FitMode.Keep : FitMode.PadBlack));
        _preview.Refresh();
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
