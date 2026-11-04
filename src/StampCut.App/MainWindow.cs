using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using static StampCut.App.Lang;

namespace StampCut.App;

/// <summary>Главное и единственное окно: ролики слева, превью и лента по центру, инструменты справа.</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _s;
    private readonly ProxyService _proxy;

    private readonly List<Clip> _clips = new();
    private Clip? _current;
    private readonly List<OverlayItem> _overlays = new();
    private List<ExportPreset> _exportPresets = new();
    private ExportPreset _preset;
    private List<OverlaySettings> _overlayPresets = new();
    private string? _projectPath;

    private readonly PreviewControl _preview = new();
    private readonly TimelineControl _timeline = new();
    private readonly ListBox _clipList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _timeText = Ui.Label("00:00.000 / 00:00.000", size: 13);
    private readonly TextBlock _fragmentText = Ui.Label("", muted: true);
    private readonly TextBlock _statusInfo = Ui.Label("", muted: true);
    private readonly TextBlock _statusHw = Ui.Label("", muted: true);
    private readonly ProgressBar _progress = new() { Width = 220, Height = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = Ui.Label("", muted: true);
    private readonly Button _cancelButton;
    private readonly Button _playButton;
    private Slider _volume = new();
    private readonly TextBlock _gainText = Ui.Label("0 дБ", muted: true);
    private readonly Border _infoBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6) };
    private readonly StackPanel _infoContent = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public static string AppVersion => typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public MainWindow(IReadOnlyList<string> filesToOpen, AppSettings settings)
    {
        _s = new AppServices(settings);
        _proxy = new ProxyService(_s);
        _exportPresets = _s.Presets.LoadExportPresets();
        _preset = _exportPresets.FirstOrDefault(p => p.Id == _s.Settings.LastExportPresetId) ?? _exportPresets[0];
        _overlayPresets = _s.Presets.LoadOverlayPresets();

        Title = "StampCut " + AppVersion;
        MinWidth = 1100; MinHeight = 680; Width = 1380; Height = 840;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowDrop = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon.ico")); } catch (Exception ex) { Logger.Warn("Иконка: " + ex.Message); }

        _cancelButton = Ui.SmallButton(T("Отмена"), CancelExport);
        _cancelButton.Visibility = Visibility.Collapsed;
        _playButton = Ui.Button("▶", TogglePlay, tooltip: T("Воспроизведение / пауза (Пробел)"), minWidth: 48);
        _playButton.FontSize = 15;

        Content = BuildLayout();
        WireEvents();
        _timeline.SnapToKeyframes = _preset.StreamCopy;
        _statusInfo.Text = T("Перетащите видео в окно или нажмите «Открыть»");

        _s.HardwareDetected += () => Dispatcher.Invoke(() =>
        {
            _statusHw.Text = _s.HardwareSummary();
            try { _s.Settings.Save(); } catch (Exception ex) { Logger.Warn("Настройки: " + ex.Message); }
            ShowDriverRecommendation();
        });
        _statusHw.Text = _s.HardwareSummary();
        _s.StartHardwareDetection();
        if (!_s.Ffmpeg.IsResolved) ShowInfo(T("FFmpeg не найден. Положите ffmpeg.exe и ffprobe.exe в папку tools\\ffmpeg рядом с программой или укажите путь в настройках."), InfoKind.Error);

        Loaded += async (_, _) =>
        {
            if (filesToOpen.Count > 0) await OpenAnyAsync(filesToOpen, replace: true);
        };
        Closing += (_, _) =>
        {
            _timer.Stop();
            _exportCts?.Cancel();
            _proxyCts?.Cancel();
            try { _s.Settings.Save(); } catch { }
        };
    }

    // ------------------------------------------------------------------ компоновка

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 6) };
        toolbar.Children.Add(Ui.Button(T("Открыть…"), () => OpenFiles(replace: true), tooltip: T("Открыть видео (Ctrl+O)")));
        var recentButton = Ui.Button(T("Недавние ▾"), () => { }, tooltip: T("Недавние файлы и папки"));
        recentButton.Click += (_, _) => ShowRecentMenu(recentButton);
        toolbar.Children.Add(recentButton);
        toolbar.Children.Add(Ui.Button(T("+ Добавить ролик"), () => OpenFiles(replace: false), tooltip: T("Добавить ролик для склейки")));
        var projectButton = Ui.Button(T("Проект ▾"), () => { }, tooltip: T("Сохранить или открыть проект"));
        projectButton.Click += (_, _) => ShowProjectMenu(projectButton);
        toolbar.Children.Add(projectButton);
        toolbar.Children.Add(new Separator { Width = 12, Opacity = 0 });
        toolbar.Children.Add(Ui.Button(T("Настройки"), OpenSettings));
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);

        var status = new Grid { Margin = new Thickness(10, 4, 10, 6) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var progressRow = Ui.Row(_progress, _progressText, _cancelButton);
        Grid.SetColumn(_statusInfo, 0); Grid.SetColumn(progressRow, 1); Grid.SetColumn(_statusHw, 2);
        _statusHw.Margin = new Thickness(16, 0, 0, 0);
        status.Children.Add(_statusInfo); status.Children.Add(progressRow); status.Children.Add(_statusHw);
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);

        var left = BuildClipsPanel();
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        var right = BuildToolsPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _infoBar.Child = _infoContent;
        Grid.SetRow(_infoBar, 0); center.Children.Add(_infoBar);
        var previewBorder = new Border { Child = _preview, Background = Ui.PreviewBg, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), ClipToBounds = true };
        Grid.SetRow(previewBorder, 1); center.Children.Add(previewBorder);
        var transport = BuildTransport();
        Grid.SetRow(transport, 2); center.Children.Add(transport);
        Grid.SetRow(_timeline, 3); center.Children.Add(_timeline);
        _timeline.Margin = new Thickness(0, 0, 0, 2);
        dock.Children.Add(center);
        return dock;
    }

    private UIElement BuildTransport()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(Ui.Button("⏮", () => Seek(ActiveSegment?.InSec ?? 0), tooltip: T("К точке входа (Home)"), minWidth: 36));
        row.Children.Add(Ui.Button("◁", () => StepFrame(-1), tooltip: T("Кадр назад (,)"), minWidth: 36));
        row.Children.Add(_playButton);
        row.Children.Add(Ui.Button("▷", () => StepFrame(1), tooltip: T("Кадр вперёд (.)"), minWidth: 36));
        row.Children.Add(Ui.Button("⏭", () => Seek(ActiveSegment?.OutSec ?? 0), tooltip: T("К точке выхода (End)"), minWidth: 36));
        _timeText.Margin = new Thickness(10, 0, 12, 0);
        _timeText.FontFamily = new FontFamily("Consolas");
        row.Children.Add(_timeText);
        row.Children.Add(Ui.Button(T("[ Вход"), SetInHere, tooltip: T("Точка входа = текущая позиция (I)")));
        row.Children.Add(Ui.Button(T("Выход ]"), SetOutHere, tooltip: T("Точка выхода = текущая позиция (O)")));
        row.Children.Add(Ui.SmallButton(T("+ Фрагмент"), AddSegmentHere, T("Новый фрагмент от текущей позиции")));
        row.Children.Add(Ui.SmallButton(T("✕ Фрагмент"), RemoveActiveSegment, T("Удалить выбранный фрагмент")));
        row.Children.Add(Ui.SmallButton(T("Сброс"), ResetTrim, T("Убрать обрезку")));
        row.Children.Add(Ui.Toggle(T("По склейке"), true, v => _followEdit = v, T("Проигрывать только фрагменты и переходить к следующему ролику — как в результате")));
        var vol = Ui.Label("🔊", size: 13);
        vol.Margin = new Thickness(8, 0, 2, 0);
        _volume = Ui.Slider(-20, 20, 0, v => { if (_syncing) return; SetGain(Math.Round(v)); }, width: 110);
        _volume.TickFrequency = 1; _volume.IsSnapToTickEnabled = true;
        _volume.ToolTip = T("Громкость ролика (↑ ↓): так он играет здесь и с таким уровнем уйдёт в экспорт");
        row.Children.Add(vol);
        row.Children.Add(_volume);
        row.Children.Add(_gainText);
        _fragmentText.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(_fragmentText);
        return row;
    }

    private void WireEvents()
    {
        _timer.Tick += (_, _) => OnTick();
        _preview.Player.MediaOpened += (_, _) => OnMediaOpened();
        _preview.Player.MediaFailed += (_, e) => OnMediaFailed(e.ErrorException);
        _preview.Player.MediaEnded += (_, _) => { if (_followEdit) OnTick(); else Pause(); };
        _preview.OverlayChanged += _ => SyncOverlayNumbers();
        _preview.OverlaySelected += o => { _selectedOverlay = o; SyncOverlayPanel(); };
        _preview.CropChanged += () => { SyncCropPanel(); UpdateOutputGeometry(rebuild: false); };
        _preview.RedactionChanged += _ => SyncRedactPanel();
        _preview.RedactionSelected += r => { _selectedRedaction = r; SyncRedactPanel(); };
        _timeline.SeekRequested += Seek;
        _timeline.SegmentEdgeChanged += (seg, isIn, v) => { if (isIn) seg.InSec = v; else seg.OutSec = v; _activeSegment = seg; AfterTrimChanged(light: true); };
        _timeline.ActiveSegmentChanged += seg => { _activeSegment = seg; AfterTrimChanged(light: true); };
        _timeline.DragCompleted += () => AfterTrimChanged();
        _timeline.ContextRequested += ShowTimelineMenu;

        DragEventHandler dragCheck = (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragEnter += dragCheck;
        DragOver += dragCheck;
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var png in files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) AddOverlayFromFile(png);
                var rest = files.Where(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count > 0) await OpenAnyAsync(rest, replace: _clips.Count == 0);
            }
        };
        PreviewKeyDown += OnKeyDown;
    }

    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm", ".avi", ".mts", ".m2ts", ".wmv", ".ts", ".mpg", ".mpeg", ".flv" };
    private static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double step = ctrl ? 30 : shift ? 1 : 5; // как в плеерах: ←/→ 5 с, Shift — 1 с, Ctrl — 30 с
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: SetInHere(); break;
            case Key.O when !ctrl: SetOutHere(); break;
            case Key.O when ctrl: OpenFiles(replace: true); break;
            case Key.S when ctrl: SaveProject(saveAs: shift); break;
            case Key.E when ctrl: Fire(ExportAsync()); break;
            case Key.Left: Seek(Position - step); break;
            case Key.Right: Seek(Position + step); break;
            case Key.OemComma: StepFrame(-1); break;
            case Key.OemPeriod: StepFrame(1); break;
            case Key.Up: SetGain((_current?.AudioGainDb ?? 0) + 1); break;
            case Key.Down: SetGain((_current?.AudioGainDb ?? 0) - 1); break;
            case Key.M: _preview.Player.IsMuted = !_preview.Player.IsMuted; break;
            case Key.Home: Seek(ActiveSegment?.InSec ?? 0); break;
            case Key.End: Seek(ActiveSegment?.OutSec ?? 0); break;
            case Key.Escape: if (_exportCts is not null) CancelExport(); else SetEditMode(EditMode.View); break;
            case Key.Delete: DeleteSelectedTool(); break;
            case Key.PageUp: SelectClipOffset(-1); break;
            case Key.PageDown: SelectClipOffset(1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Громкость ролика в дБ: превью играет с ней (0 дБ = половина шкалы, чтобы +6 дБ было слышно), экспорт применяет её к дорожке.</summary>
    private void SetGain(double db)
    {
        if (_current is null) return;
        db = Math.Clamp(Math.Round(db), -20, 20);
        _current.AudioGainDb = db;
        ApplyGainToPlayer();
        _syncing = true; _volume.Value = db; _syncing = false;
        _gainText.Text = $"{(db > 0 ? "+" : "")}{db:0} {T("дБ")}";
    }

    private void ApplyGainToPlayer()
    {
        double db = _current?.AudioGainDb ?? 0;
        _preview.Player.Volume = Math.Clamp(0.5 * Math.Pow(10, db / 20.0), 0, 1);
    }

    // ------------------------------------------------------------------ контекстное меню ленты

    private void ShowTimelineMenu(double t)
    {
        if (_current is null) return;
        var seg = _current.Segments.FirstOrDefault(s => s.Contains(t)) ?? ActiveSegment;
        bool hasKf = _timeline.Keyframes.Count > 0;
        var menu = new ContextMenu();
        void Item(string text, Action action, bool enabled = true) { var mi = new MenuItem { Header = text, IsEnabled = enabled }; mi.Click += (_, _) => action(); menu.Items.Add(mi); }
        Item(T("Вход здесь"), () => { if (seg is null) return; seg.InSec = Math.Min(_timeline.Snap(t), seg.OutSec - 0.05); _activeSegment = seg; AfterTrimChanged(); });
        Item(T("Выход здесь"), () => { if (seg is null) return; seg.OutSec = Math.Max(_timeline.Snap(t), seg.InSec + 0.05); _activeSegment = seg; AfterTrimChanged(); });
        Item(T("Новый фрагмент отсюда"), () => { Seek(t); AddSegmentHere(); });
        Item(T("Удалить фрагмент"), () => { _activeSegment = seg; RemoveActiveSegment(); }, seg is not null && _current.Segments.Count > 1);
        menu.Items.Add(new Separator());
        Item(T("Вход → ближайший ключевой кадр"), () => { if (seg is null) return; seg.InSec = Math.Min(_timeline.NearestKeyframe(seg.InSec), seg.OutSec - 0.05); AfterTrimChanged(); }, hasKf && seg is not null);
        Item(T("Выход → ближайший ключевой кадр"), () => { if (seg is null) return; seg.OutSec = Math.Max(_timeline.NearestKeyframe(seg.OutSec), seg.InSec + 0.05); AfterTrimChanged(); }, hasKf && seg is not null);
        Item(T("Все фрагменты → ключевые кадры"), () =>
        {
            foreach (var s in _current.Segments) { s.InSec = _timeline.NearestKeyframe(s.InSec); s.OutSec = Math.Max(_timeline.NearestKeyframe(s.OutSec), s.InSec + 0.05); }
            AfterTrimChanged();
        }, hasKf);
        Item(T("К предыдущему ключевому кадру"), () => { if (_timeline.PrevKeyframe(t) is { } k) Seek(k); }, hasKf);
        Item(T("К следующему ключевому кадру"), () => { if (_timeline.NextKeyframe(t) is { } k) Seek(k); }, hasKf);
        var show = new MenuItem { Header = T("Показывать ключевые кадры"), IsCheckable = true, IsChecked = _timeline.ShowKeyframes };
        show.Click += (_, _) => { _timeline.ShowKeyframes = show.IsChecked; _timeline.InvalidateVisual(); };
        menu.Items.Add(show);
        if (!hasKf) menu.Items.Add(new MenuItem { Header = T("Ключевые кадры ещё загружаются"), IsEnabled = false });
        menu.PlacementTarget = _timeline;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ проект

    private void ShowProjectMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string text, Action action, bool enabled = true) { var mi = new MenuItem { Header = text, IsEnabled = enabled }; mi.Click += (_, _) => action(); menu.Items.Add(mi); }
        Item(T("Сохранить проект") + " (Ctrl+S)", () => SaveProject(saveAs: false), _clips.Count > 0);
        Item(T("Сохранить проект как…"), () => SaveProject(saveAs: true), _clips.Count > 0);
        Item(T("Открыть проект…"), OpenProjectDialog);
        if (_projectPath is not null) { menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = Path.GetFileName(_projectPath), IsEnabled = false }); }
        menu.IsOpen = true;
    }

    private void SaveProject(bool saveAs)
    {
        if (_clips.Count == 0) return;
        var path = _projectPath;
        if (saveAs || path is null)
        {
            var first = _clips[0].Media.Path;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = $"{T("Проект StampCut")}|*{Core.Presets.ProjectFile.Extension}",
                FileName = Path.GetFileNameWithoutExtension(first) + Core.Presets.ProjectFile.Extension,
                InitialDirectory = Path.GetDirectoryName(first),
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
            if (!Core.Presets.ProjectStore.IsProjectFile(path)) path += Core.Presets.ProjectFile.Extension;
        }
        try
        {
            Core.Presets.ProjectStore.Save(path, _clips, _overlays.Select(o => o.Settings), _preset.Id);
            _projectPath = path;
            AppSettings.Touch(_s.Settings.RecentFiles, path);
            _statusInfo.Text = $"{T("Проект сохранён")}: {Path.GetFileName(path)}";
            Title = $"StampCut {AppVersion} — {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Logger.Error("Сохранение проекта", ex);
            ShowInfo(T("Не удалось сохранить проект: ") + ex.Message, InfoKind.Error);
        }
    }

    private void OpenProjectDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = $"{T("Проект StampCut")}|*{Core.Presets.ProjectFile.Extension}" };
        var dir = _s.Settings.LastOpenDir;
        if (dir is not null) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) Fire(OpenProjectAsync(dlg.FileName));
    }

    private async Task OpenProjectAsync(string path)
    {
        _statusInfo.Text = T("Открываю проект…");
        var result = await Core.Presets.ProjectStore.LoadAsync(path, _s.Probe);
        Pause();
        _clips.Clear(); _current = null; _activeSegment = null;
        _clips.AddRange(result.Clips);
        _overlays.Clear();
        foreach (var o in result.Overlays)
        {
            var item = new OverlayItem { Settings = o };
            LoadOverlayImage(item);
            _overlays.Add(item);
        }
        _selectedOverlay = _overlays.LastOrDefault()?.Settings;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = _selectedOverlay;
        if (_exportPresets.FirstOrDefault(p => p.Id == result.ExportPresetId) is { } preset) { _preset = preset; RefreshPresetCombos(); }
        _projectPath = path;
        AppSettings.Touch(_s.Settings.RecentFiles, path);
        AppSettings.Touch(_s.Settings.RecentOpenDirs, Path.GetDirectoryName(path));
        Title = $"StampCut {AppVersion} — {Path.GetFileName(path)}";
        RefreshClipList();
        SyncOverlayPanel();
        UpdateExportInfo();
        if (_clips.Count > 0) SelectClip(_clips[0]);
        if (result.Warnings.Count > 0) ShowInfo(string.Join(" ", result.Warnings), InfoKind.Warning);
        else _statusInfo.Text = $"{T("Проект открыт")}: {Path.GetFileName(path)}";
    }

    /// <summary>Файлы с командной строки, из drag-and-drop и «Недавних»: проекты открываются как проекты, остальное — как видео.</summary>
    private async Task OpenAnyAsync(IEnumerable<string> files, bool replace)
    {
        var list = files.ToList();
        var project = list.FirstOrDefault(Core.Presets.ProjectStore.IsProjectFile);
        if (project is not null) { await OpenProjectAsync(project); list.Remove(project); replace = false; }
        var videos = list.Where(f => IsVideoFile(f) || !Path.HasExtension(f) || File.Exists(f)).Where(f => !Core.Presets.ProjectStore.IsProjectFile(f)).ToList();
        if (videos.Count > 0) await AddFilesAsync(videos, replace);
    }

    /// <summary>Запуск задачи «в фоне» так, чтобы её ошибка попала в журнал и в полосу уведомлений, а не потерялась.</summary>
    private void Fire(Task task)
    {
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException();
            if (ex is OperationCanceledException) return;
            Logger.Error("Фоновая операция", ex);
            Dispatcher.BeginInvoke(() => ShowInfo(T("Ошибка: ") + ex?.Message, InfoKind.Error, (T("Открыть журнал"), () => TryStart(Logger.CurrentFile))));
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // ------------------------------------------------------------------ информационная полоса

    private enum InfoKind { Info, Success, Warning, Error }

    private void ShowInfo(string text, InfoKind kind, params (string Label, Action Action)[] buttons)
    {
        _infoContent.Children.Clear();
        var tb = Ui.Label(text, size: 13);
        tb.Margin = new Thickness(0, 0, 12, 0);
        tb.MaxWidth = 700;
        _infoContent.Children.Add(tb);
        foreach (var (label, action) in buttons) _infoContent.Children.Add(Ui.SmallButton(label, action));
        _infoContent.Children.Add(Ui.SmallButton("✕", HideInfo, T("Скрыть")));
        var (key, fallback) = kind switch
        {
            InfoKind.Success => ("SystemFillColorSuccessBackgroundBrush", Color.FromRgb(0xDF, 0xF6, 0xDD)),
            InfoKind.Warning => ("SystemFillColorCautionBackgroundBrush", Color.FromRgb(0xFF, 0xF4, 0xCE)),
            InfoKind.Error => ("SystemFillColorCriticalBackgroundBrush", Color.FromRgb(0xFD, 0xE7, 0xE9)),
            _ => ("SystemFillColorAttentionBackgroundBrush", Color.FromRgb(0xF6, 0xFA, 0xFE)),
        };
        Ui.Themed(_infoBar, Border.BackgroundProperty, key, new SolidColorBrush(fallback));
        _infoBar.Visibility = Visibility.Visible;
    }

    private void HideInfo() => _infoBar.Visibility = Visibility.Collapsed;

    private void ShowDriverRecommendation()
    {
        var rec = _s.CachedRecommendation;
        if (string.IsNullOrEmpty(rec) || _infoBar.Visibility == Visibility.Visible) return;
        var key = (_s.Hardware?.NvidiaDriver ?? "") + "|" + (_s.Hardware?.FfmpegVersion ?? "");
        if (_s.Settings.DismissedDriverWarning == key) return;
        ShowInfo(rec, InfoKind.Warning,
            (T("Скачать драйвер NVIDIA"), () => TryStart("https://www.nvidia.com/Download/index.aspx")),
            (T("Проверить снова"), () => { _s.StartHardwareDetection(force: true); _statusHw.Text = T("GPU: проверка…"); HideInfo(); }),
            (T("Больше не напоминать"), () => { _s.Settings.DismissedDriverWarning = key; try { _s.Settings.Save(); } catch { } HideInfo(); }));
    }

    private void OpenSettings()
    {
        var oldLang = _s.Settings.Language;
        var w = new SettingsWindow(_s) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _s.ReloadAfterSettingsChange();
            Application.Current.ThemeMode = Program.ThemeFor(_s.Settings.Theme);
            _exportPresets = _s.Presets.LoadExportPresets();
            _preset = _exportPresets.FirstOrDefault(p => p.Id == _preset.Id) ?? _exportPresets[0];
            _overlayPresets = _s.Presets.LoadOverlayPresets();
            RefreshPresetCombos();
            _statusHw.Text = _s.HardwareSummary();
            if (w.RedetectRequested) { _s.StartHardwareDetection(force: true); _statusHw.Text = T("GPU: проверка…"); }
            if (_s.Settings.Language != oldLang &&
                MessageBox.Show(this, T("Язык изменится после перезапуска. Перезапустить сейчас?"), "StampCut", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch (Exception ex) { Logger.Error("Перезапуск", ex); }
                Close();
            }
        }
    }
}
