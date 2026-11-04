using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
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
    public AppServices Services => _s;
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
    private readonly TextBlock _timeText = Ui.Label("00:00.000 / 00:00.000");
    private readonly TextBlock _fragmentText = Ui.Label("", muted: true);
    private readonly TextBlock _statusInfo = Ui.Label("", muted: true);
    private readonly TextBlock _statusHw = Ui.Label("", muted: true);
    private readonly Button _cancelButton;
    private readonly Button _playButton;
    private Slider _volume = new();
    private readonly TextBlock _gainText = Ui.Label("0 дБ", muted: true);
    private readonly Border _infoBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6) };
    private readonly WrapPanel _infoContent = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private UIElement _transport = new Grid();
    private readonly TextBlock _titleText = Ui.Label("StampCut");
    private UIElement _toolsStack = new Grid();
    private UIElement _clipButtons = new Grid();

    public static string AppVersion => typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public MainWindow(IReadOnlyList<string> filesToOpen, AppSettings settings)
    {
        _s = new AppServices(settings);
        _proxy = new ProxyService(_s);
        _waveform = new WaveformService(_s);
        _exportPresets = _s.Presets.LoadExportPresets();
        _preset = _exportPresets.FirstOrDefault(p => p.Id == _s.Settings.LastExportPresetId) ?? _exportPresets[0];
        _overlayPresets = _s.Presets.LoadOverlayPresets();

        Title = "StampCut " + AppVersion;
        MinWidth = 1100; MinHeight = 680;
        Width = Math.Max(1100, _s.Settings.WindowWidth); Height = Math.Max(680, _s.Settings.WindowHeight);
        if (_s.Settings.WindowMaximized) WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowDrop = true;
        Ui.ApplyWindowTheme(this);
        try { if (Ui.AppIcon() is { } ico) Icon = ico.Frame; } catch (Exception ex) { Logger.Warn("Иконка: " + ex.Message); }
        // панель кнопок живёт в строке заголовка: системные кнопки рисует Windows, остальное — наше
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true,
            ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(0),
        });

        _cancelButton = Ui.SmallButton(T("Отмена"), CancelExport);
        _cancelButton.Visibility = Visibility.Collapsed;
        _playButton = Ui.Button("▶", TogglePlay, tooltip: T("Воспроизведение / пауза (Пробел)"), minWidth: 48);
        _playButton.Padding = new Thickness(14, 0, 14, 0);

        Content = BuildLayout();
        WireEvents();
        InitHistory();
        UpdateEnabledState();
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
            else await OfferAutosaveRestoreAsync(null);
            OfferPendingCrashReport();
            Fire(CheckUpdatesAsync(manual: false));
        };
        Closing += (_, e) =>
        {
            if (_exportCts is not null && MessageBox.Show(this, T("Идёт экспорт. Прервать и выйти?"), "StampCut", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            if (!ConfirmDiscardChanges()) { e.Cancel = true; return; }
            if (!_history.IsDirty) DeleteAutosave(_projectPath);
            _timer.Stop();
            _autosaveTimer.Stop();
            _exportCts?.Cancel();
            _proxyCts?.Cancel();
            _waveformCts?.Cancel();
            _s.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { _s.Settings.WindowWidth = Width; _s.Settings.WindowHeight = Height; }
            try { _s.Settings.Save(); } catch { }
        };
    }

    // ------------------------------------------------------------------ компоновка

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();

        // строка заголовка: иконка, название, кнопки; справа оставляем место под системные кнопки окна
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 150, 4), Height = 34 };
        if (Icon is not null) toolbar.Children.Add(new Image { Source = Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        _titleText.Margin = new Thickness(0, 0, 14, 0);
        _titleText.FontWeight = FontWeights.SemiBold;
        toolbar.Children.Add(_titleText);
        var recentButton = Ui.Button(T("Недавние ▾"), () => { }, tooltip: T("Недавние файлы и папки"));
        recentButton.Click += (_, _) => ShowRecentMenu(recentButton);
        var projectButton = Ui.Button(T("Проект ▾"), () => { }, tooltip: T("Сохранить или открыть проект"));
        projectButton.Click += (_, _) => ShowProjectMenu(projectButton);
        var helpButton = Ui.Button("?", () => { }, tooltip: T("О программе, обновления, отчёт о проблеме"), minWidth: 34);
        helpButton.Click += (_, _) => ShowHelpMenu(helpButton);
        foreach (var b in new[]
        {
            Ui.Button(T("Открыть…"), () => OpenFiles(replace: true), tooltip: T("Открыть видео (Ctrl+O)")),
            recentButton,
            Ui.Button(T("+ Добавить ролик"), () => OpenFiles(replace: false), tooltip: T("Добавить ролик для склейки")),
            projectButton,
            Ui.Button(T("Настройки"), OpenSettings),
            helpButton,
        })
        {
            b.Height = 30; b.Padding = new Thickness(10, 0, 10, 0);
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            toolbar.Children.Add(b);
        }
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);

        var status = new Grid { Margin = new Thickness(10, 4, 10, 6) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusInfo, 0); Grid.SetColumn(_statusHw, 2);
        _statusHw.Margin = new Thickness(16, 0, 0, 0);
        status.Children.Add(_statusInfo); status.Children.Add(_statusHw);
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);

        // три колонки с разделителями: ширина боковых панелей меняется мышью и запоминается
        var columns = new Grid();
        var leftCol = new ColumnDefinition { Width = new GridLength(Math.Clamp(_s.Settings.LeftPanelWidth, 160, 600)), MinWidth = 160 };
        var rightCol = new ColumnDefinition { Width = new GridLength(Math.Clamp(_s.Settings.RightPanelWidth, 280, 700)), MinWidth = 280 };
        columns.ColumnDefinitions.Add(leftCol);
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(rightCol);

        var left = BuildClipsPanel();
        Grid.SetColumn(left, 0); columns.Children.Add(left);
        var splitLeft = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ShowsPreview = false, Focusable = false };
        Grid.SetColumn(splitLeft, 1); columns.Children.Add(splitLeft);
        var right = BuildToolsPanel();
        Grid.SetColumn(right, 4); columns.Children.Add(right);
        var splitRight = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ShowsPreview = false, Focusable = false };
        Grid.SetColumn(splitRight, 3); columns.Children.Add(splitRight);
        splitLeft.DragCompleted += (_, _) => { _s.Settings.LeftPanelWidth = leftCol.ActualWidth; };
        splitRight.DragCompleted += (_, _) => { _s.Settings.RightPanelWidth = rightCol.ActualWidth; };
        dock.Children.Add(columns);

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
        _transport = transport;
        Grid.SetRow(transport, 2); center.Children.Add(transport);
        Grid.SetRow(_timeline, 3); center.Children.Add(_timeline);
        _timeline.Margin = new Thickness(0, 0, 0, 2);
        Grid.SetColumn(center, 2); columns.Children.Add(center);
        return dock;
    }

    private UIElement BuildTransport()
    {
        // строка 1: плеер слева, обрезка справа; строка 2: режим, громкость, сведения о фрагментах
        var line1 = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
        var trim = Ui.Row(
            Ui.Button(T("[ Вход"), SetInHere, tooltip: T("Точка входа = текущая позиция (I)")),
            Ui.Button(T("Выход ]"), SetOutHere, tooltip: T("Точка выхода = текущая позиция (O)")),
            Ui.SmallButton(T("+ Фрагмент"), AddSegmentHere, T("Новый фрагмент от текущей позиции")),
            Ui.SmallButton(T("✕ Фрагмент"), RemoveActiveSegment, T("Удалить выбранный фрагмент")),
            Ui.SmallButton(T("Сброс"), ResetTrim, T("Убрать обрезку")));
        trim.Margin = new Thickness(0);
        DockPanel.SetDock(trim, Dock.Right);
        line1.Children.Add(trim);
        _timeText.FontFamily = new FontFamily("Consolas");
        var play = Ui.Row(
            Ui.Button("⏮", () => Seek(ActiveSegment?.InSec ?? 0), tooltip: T("К точке входа (Home)"), minWidth: 36),
            Ui.Button("◁", () => StepFrame(-1), tooltip: T("Кадр назад (,)"), minWidth: 36),
            _playButton,
            Ui.Button("▷", () => StepFrame(1), tooltip: T("Кадр вперёд (.)"), minWidth: 36),
            Ui.Button("⏭", () => Seek(ActiveSegment?.OutSec ?? 0), tooltip: T("К точке выхода (End)"), minWidth: 36),
            _timeText);
        play.Margin = new Thickness(0);
        _timeText.Margin = new Thickness(8, 0, 0, 0);
        line1.Children.Add(play);

        _volume = Ui.Slider(Core.Models.Clip.MuteDb, Core.Models.Clip.MaxGainDb, 0, v => { if (_syncing) return; SetGain(Math.Round(v)); }, width: 150);
        _volume.TickFrequency = 1; _volume.IsSnapToTickEnabled = true;
        _volume.ToolTip = T("Громкость ролика (↑ ↓): так он играет здесь и с таким уровнем уйдёт в экспорт");
        var vol = Ui.Label("🔊");
        vol.Margin = new Thickness(10, 0, 2, 0);
        _gainText.Width = 72;
        var line2 = Ui.Row(
            Ui.Toggle(T("По склейке"), true, v => _followEdit = v, T("Проигрывать только фрагменты ролика по кругу")),
            Ui.Toggle(T("Все ролики"), false, v => _playAllClips = v, T("После последнего фрагмента переходить к следующему ролику — как в результате")),
            vol, _volume, _gainText, _fragmentText);
        line2.Margin = new Thickness(0, 0, 0, 2);
        return Ui.Column(line1, line2);
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
        _preview.EditCompleted += () => { if (_preview.Mode == EditMode.Crop) AfterCropChanged(); else ScheduleCommit(); };
        _timeline.SeekRequested += Seek;
        _timeline.SegmentEdgeChanged += (seg, isIn, v) => { if (isIn) seg.InSec = v; else seg.OutSec = v; _activeSegment = seg; AfterTrimChanged(light: true); };
        _timeline.ActiveSegmentChanged += seg => { _activeSegment = seg; AfterTrimChanged(light: true); };
        _timeline.DragCompleted += () => { AfterTrimChanged(); SyncRedactPanel(); SyncCensorPanel(); };
        _timeline.ContextRequested += ShowTimelineMenu;
        _timeline.MarkerEdgeChanged += (m, isFrom, t) =>
        {
            t = Math.Round(t, 2);
            var live = _timeline.Markers.FirstOrDefault(x => ReferenceEquals(x.Tag, m.Tag)) ?? m;
            switch (m.Tag)
            {
                case AudioCensor c: if (isFrom) c.FromSec = t; else c.ToSec = t; live.From = m.From = c.FromSec; live.To = m.To = c.ToSec; break;
                case RedactRegion r: if (isFrom) r.FromSec = t; else r.ToSec = t; live.From = m.From = r.FromSec ?? 0; live.To = m.To = r.ToSec ?? (_current?.Media.DurationSec ?? 0); break;
            }
            _timeline.InvalidateVisual();
            _preview.Layout();
        };
        _timeline.MarkerSelected += m =>
        {
            switch (m?.Tag)
            {
                case AudioCensor c: SyncCensorPanel(c); break;
                case RedactRegion r: _selectedRedaction = r; _preview.SelectedRedaction = r; SyncRedactPanel(); _preview.Refresh(); break;
            }
        };

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
            case Key.N when ctrl: NewProject(); break;
            case Key.Z when ctrl && !shift: Undo(); break;
            case Key.Z when ctrl && shift: Redo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.V when ctrl: Fire(PasteAsync()); break;
            case Key.E when ctrl: Fire(ExportAsync()); break;
            case Key.Left: Seek(Position - step); break;
            case Key.Right: Seek(Position + step); break;
            case Key.OemComma: StepFrame(shift ? -10 : -1); break;
            case Key.OemPeriod: StepFrame(shift ? 10 : 1); break;
            case Key.OemOpenBrackets: if (_timeline.PrevKeyframe(Position) is { } pk) { Pause(); Seek(pk); } break;
            case Key.OemCloseBrackets: if (_timeline.NextKeyframe(Position) is { } nk) { Pause(); Seek(nk); } break;
            case Key.D0 when ctrl: _timeline.ResetView(); break;
            case Key.OemPlus when ctrl: _timeline.Zoom(1.5, Position); break;
            case Key.OemMinus when ctrl: _timeline.Zoom(1 / 1.5, Position); break;
            case Key.Up: SetGain((_current?.AudioGainDb ?? 0) + 1); break;
            case Key.Down: SetGain((_current?.AudioGainDb ?? 0) - 1); break;
            case Key.M: ToggleMute(); break;
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
        db = Math.Clamp(Math.Round(db), Core.Models.Clip.MuteDb, Core.Models.Clip.MaxGainDb);
        _current.AudioGainDb = db;
        ApplyGainToPlayer();
        _syncing = true; _volume.Value = db; _syncing = false;
        _gainText.Text = _current.IsMuted ? T("без звука") : $"{(db > 0 ? "+" : "")}{db:0} {T("дБ")}";
        _timeline.Muted = _current.IsMuted; _timeline.InvalidateVisual();
        ScheduleCommit();
    }

    private double _gainBeforeMute;

    /// <summary>M: заглушить ролик (и в экспорте) / вернуть прежнюю громкость.</summary>
    private void ToggleMute()
    {
        if (_current is null) return;
        if (_current.IsMuted) SetGain(_gainBeforeMute);
        else { _gainBeforeMute = _current.AudioGainDb; SetGain(Core.Models.Clip.MuteDb); }
    }

    private void ApplyGainToPlayer()
    {
        double db = _current?.AudioGainDb ?? 0;
        _preview.Player.Volume = _current?.IsMuted == true ? 0 : Math.Clamp(0.5 * Math.Pow(10, db / 20.0), 0, 1);
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
        Item(T("Отменить") + " (Ctrl+Z)", Undo, _history.CanUndo);
        Item(T("Повторить") + " (Ctrl+Y)", Redo, _history.CanRedo);
        menu.Items.Add(new Separator());
        Item(T("Новый проект") + " (Ctrl+N)", NewProject, _clips.Count > 0 || _overlays.Count > 0);
        Item(T("Сохранить проект") + " (Ctrl+S)", () => SaveProject(saveAs: false), _clips.Count > 0);
        Item(T("Сохранить проект как…"), () => SaveProject(saveAs: true), _clips.Count > 0);
        Item(T("Открыть проект…"), OpenProjectDialog);
        if (_projectPath is not null) { menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = Path.GetFileName(_projectPath), IsEnabled = false }); }
        menu.IsOpen = true;
    }

    private bool SaveProject(bool saveAs)
    {
        if (_clips.Count == 0) return false;
        CommitNow();
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
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
            if (!Core.Presets.ProjectStore.IsProjectFile(path)) path += Core.Presets.ProjectFile.Extension;
        }
        try
        {
            Core.Presets.ProjectStore.Save(path, _clips, _overlays.Select(o => o.Settings), _preset.Id);
            DeleteAutosave(_projectPath);
            _projectPath = path;
            _history.MarkSaved();
            DeleteAutosave(path);
            AppSettings.Touch(_s.Settings.RecentFiles, path);
            _statusInfo.Text = $"{T("Проект сохранён")}: {Path.GetFileName(path)}";
            UpdateDirtyTitle();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Сохранение проекта", ex);
            ShowInfo(T("Не удалось сохранить проект: ") + ex.Message, InfoKind.Error);
            return false;
        }
    }

    private void OpenProjectDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = $"{T("Проект StampCut")}|*{Core.Presets.ProjectFile.Extension}" };
        var dir = _s.Settings.LastOpenDir;
        if (dir is not null) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == true) Fire(OpenProjectAsync(dlg.FileName));
    }

    /// <summary>asAutosaveOf — путь исходного проекта ("" для безымянного), если открываем его резервную копию: проект считается несохранённым.</summary>
    private async Task OpenProjectAsync(string path, string? asAutosaveOf = null)
    {
        if (asAutosaveOf is null && !ConfirmDiscardChanges()) return;
        if (asAutosaveOf is null && FindAutosave(path) is not null)
        {
            var when = File.GetLastWriteTime(AutosavePath(path));
            if (MessageBox.Show(this, $"{T("Найдена резервная копия проекта от")} {when:g}. {T("Восстановить её?")}", "StampCut", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await OpenProjectAsync(AutosavePath(path), asAutosaveOf: path);
                return;
            }
            DeleteAutosave(path);
        }
        _statusInfo.Text = T("Открываю проект…");
        var result = await Core.Presets.ProjectStore.LoadAsync(path, _s.Probe);
        foreach (var c in result.Clips) _mediaCache[c.Media.Path] = c.Media;
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
        _projectPath = asAutosaveOf is null ? path : (asAutosaveOf.Length == 0 ? null : asAutosaveOf);
        if (_projectPath is not null) { AppSettings.Touch(_s.Settings.RecentFiles, _projectPath); AppSettings.Touch(_s.Settings.RecentOpenDirs, Path.GetDirectoryName(_projectPath)); }
        _history.Reset(Snapshot(), saved: asAutosaveOf is null);
        UpdateDirtyTitle();
        RefreshClipList();
        SyncOverlayPanel();
        UpdateExportInfo();
        if (_clips.Count > 0) SelectClip(_clips[0]); else ClearPreview();
        if (result.Warnings.Count > 0) ShowInfo(string.Join(" ", result.Warnings), InfoKind.Warning);
        else _statusInfo.Text = $"{T("Проект открыт")}: {Path.GetFileName(path)}";
    }

    /// <summary>Файлы с командной строки, из drag-and-drop и «Недавних»: проекты открываются как проекты, остальное — как видео.
    /// Если проекта ещё нет и рядом с видео лежит «имя-видео.stampcut.json» — открывается он.</summary>
    private async Task OpenAnyAsync(IEnumerable<string> files, bool replace, bool ignoreSiblingProject = false)
    {
        var list = files.ToList();
        var project = list.FirstOrDefault(Core.Presets.ProjectStore.IsProjectFile);
        if (project is not null) { await OpenProjectAsync(project); list.Remove(project); replace = false; }
        var videos = list.Where(f => IsVideoFile(f) || IsImageFile(f) || File.Exists(f)).Where(f => !Core.Presets.ProjectStore.IsProjectFile(f)).ToList();
        if (videos.Count == 0) return;
        if (replace && !ignoreSiblingProject && _projectPath is null && videos.Count == 1)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(videos[0]) ?? "", Path.GetFileNameWithoutExtension(videos[0]) + Core.Presets.ProjectFile.Extension);
            if (File.Exists(sibling))
            {
                var video = videos[0];
                await OpenProjectAsync(sibling);
                ShowInfo($"{T("Рядом с видео найден проект")} {Path.GetFileName(sibling)} — {T("открыт он.")}", InfoKind.Info,
                    (T("Открыть только видео"), () => Fire(OpenAnyAsync(new[] { video }, replace: true, ignoreSiblingProject: true))));
                return;
            }
        }
        await AddFilesAsync(videos, replace);
    }

    /// <summary>Новый проект: убрать все ролики и оверлеи, сбросить историю (с вопросом о сохранении).</summary>
    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        Pause();
        _proxyCts?.Cancel();
        DeleteAutosave(_projectPath);
        _clips.Clear(); _current = null; _activeSegment = null; _selectedRedaction = null;
        _overlays.Clear(); _selectedOverlay = null;
        _preview.Overlays.Clear(); _preview.SelectedOverlay = null;
        _projectPath = null;
        HideInfo();
        RefreshClipList(); SyncOverlayPanel(); SyncRedactPanel(); SyncCensorPanel(); UpdateExportInfo();
        ClearPreview();
        _history.Reset(Snapshot(), saved: true);
        SetTitle(null);
    }

    private void SetTitle(string? project, bool dirty = false)
    {
        var mark = dirty ? "• " : "";
        Title = project is null ? $"{mark}StampCut {AppVersion}" : $"{mark}{project} — StampCut {AppVersion}";
        _titleText.Text = mark + (project is null ? "StampCut" : project);
    }

    // ------------------------------------------------------------------ справка, отчёты, обновления

    private void ShowHelpMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string text, Action action) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => action(); menu.Items.Add(mi); }
        Item(T("О программе…"), () => new AboutWindow(_s, () => CheckUpdatesAsync(manual: true), () => ReportProblem(T("Проблема в StampCut"), null, null)) { Owner = this }.ShowDialog());
        Item(T("Проверить обновления"), () => Fire(CheckUpdatesAsync(manual: true)));
        Item(T("Сообщить о проблеме…"), () => ReportProblem(T("Проблема в StampCut"), null, null));
        Item(T("Страница релизов"), () => TryStart(CrashReporter.ReleasesUrl));
        Item(T("Открыть папку журналов"), () => TryStart(Logger.CurrentFile));
        menu.IsOpen = true;
    }

    /// <summary>Отчёт о проблеме: текст в буфер обмена и форма issue на GitHub с заполненным описанием.</summary>
    private void ReportProblem(string title, Exception? ex, string? extra)
    {
        var report = CrashReporter.Build(title, ex, _s, extra);
        CrashReporter.OpenIssue($"[{AppVersion}] {title}", report);
        _statusInfo.Text = T("Отчёт скопирован в буфер обмена и открыт в браузере");
    }

    private void OfferPendingCrashReport()
    {
        var pending = CrashReporter.PendingReport();
        if (pending is null) return;
        ShowInfo(T("В прошлый раз программа завершилась с ошибкой. Отправить отчёт разработчику? Он откроется в браузере, вы увидите его текст."), InfoKind.Warning,
            (T("Отправить отчёт"), () => { try { CrashReporter.OpenIssue($"[{AppVersion}] {T("Сбой приложения")}", File.ReadAllText(pending)); } catch { } CrashReporter.MarkReported(pending); HideInfo(); }),
            (T("Открыть папку отчётов"), () => TryStart(CrashReporter.CrashDir)),
            (T("Не отправлять"), () => { CrashReporter.MarkReported(pending); HideInfo(); }));
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (!manual)
        {
            if (!_s.Settings.CheckUpdates || (DateTime.UtcNow - _s.Settings.LastUpdateCheckUtc) < TimeSpan.FromHours(20)) return;
        }
        try
        {
            var info = await UpdateChecker.CheckAsync(CancellationToken.None);
            _s.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            try { _s.Settings.Save(); } catch { }
            if (info is null) { if (manual) ShowInfo($"{T("У вас последняя версия")} ({AppVersion}).", InfoKind.Success); return; }
            if (!manual && _s.Settings.SkippedUpdateVersion == info.Version) return;
            ShowInfo($"{T("Доступна новая версия")} {info.Version} ({T("у вас")} {AppVersion}).", InfoKind.Info,
                (T("Скачать"), () => TryStart(info.Url)),
                (T("Пропустить эту версию"), () => { _s.Settings.SkippedUpdateVersion = info.Version; try { _s.Settings.Save(); } catch { } HideInfo(); }));
        }
        catch (Exception ex)
        {
            Logger.Warn("Проверка обновлений: " + ex.Message);
            if (manual) ShowInfo(T("Не удалось проверить обновления: ") + ex.Message, InfoKind.Warning, (T("Страница релизов"), () => TryStart(CrashReporter.ReleasesUrl)));
        }
    }

    // ------------------------------------------------------------------ вставка из буфера

    /// <summary>Ctrl+V: файлы из проводника — как ролики (или проект), картинка — сохраняется в PNG и добавляется как фото.</summary>
    private async Task PasteAsync()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
                if (files.Count > 0) { await OpenAnyAsync(files, replace: _clips.Count == 0); return; }
            }
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img is null) return;
                var dir = AppPaths.Ensure(Path.Combine(AppPaths.Local, "pasted"));
                var path = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(path)) enc.Save(fs);
                await AddFilesAsync(new[] { path }, replace: _clips.Count == 0);
                return;
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText().Trim().Trim('"');
                if (File.Exists(text)) { await OpenAnyAsync(new[] { text }, replace: _clips.Count == 0); return; }
            }
            _statusInfo.Text = T("В буфере обмена нет файлов или изображения");
        }
        catch (Exception ex) { Logger.Error("Вставка", ex); ShowInfo(T("Ошибка: ") + ex.Message, InfoKind.Error); }
    }

    /// <summary>Пока нет ни одного ролика, инструменты и управление воспроизведением неактивны.</summary>
    private void UpdateEnabledState()
    {
        bool hasClip = _current is not null;
        _transport.IsEnabled = hasClip;
        _toolsStack.IsEnabled = hasClip;
        _timeline.IsEnabled = hasClip;
        _timeline.Opacity = hasClip ? 1 : 0.5;
        _clipButtons.IsEnabled = _clips.Count > 0;
        _exportButton.IsEnabled = _clips.Count > 0 && _exportCts is null;
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
        var tb = Ui.Label(text);
        tb.Margin = new Thickness(0, 0, 12, 4);
        tb.MaxWidth = 760;
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
