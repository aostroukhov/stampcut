using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App;

/// <summary>Главное и единственное окно: ролики слева, превью и лента по центру, инструменты справа.</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppServices _s = new();
    private readonly ProxyService _proxy;

    // проект
    private readonly List<Clip> _clips = new();
    private Clip? _current;
    private readonly List<OverlayItem> _overlays = new();
    private List<ExportPreset> _exportPresets = new();
    private ExportPreset _preset;
    private List<OverlaySettings> _overlayPresets = new();

    // элементы
    private readonly PreviewControl _preview = new();
    private readonly TimelineControl _timeline = new();
    private readonly ListBox _clipList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _timeText = Ui.Label("00:00.000 / 00:00.000", size: 13);
    private readonly TextBlock _fragmentText = Ui.Label("", muted: true);
    private readonly TextBlock _statusInfo = Ui.Label("Перетащите видео в окно или нажмите «Открыть»", muted: true);
    private readonly TextBlock _statusHw = Ui.Label("", muted: true);
    private readonly ProgressBar _progress = new() { Width = 220, Height = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = Ui.Label("", muted: true);
    private readonly Button _cancelButton;
    private readonly Button _playButton;
    private readonly Border _infoBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4) };
    private readonly StackPanel _infoContent = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public MainWindow(IReadOnlyList<string> filesToOpen)
    {
        _proxy = new ProxyService(_s);
        _exportPresets = _s.Presets.LoadExportPresets();
        _preset = _exportPresets.FirstOrDefault(p => p.Id == _s.Settings.LastExportPresetId) ?? _exportPresets[0];
        _overlayPresets = _s.Presets.LoadOverlayPresets();

        Title = "StampCut " + AppVersion;
        MinWidth = 1100; MinHeight = 680; Width = 1360; Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.PanelBg;
        AllowDrop = true;
        FontFamily = new FontFamily("Segoe UI");

        _cancelButton = Ui.SmallButton("Отмена", CancelExport);
        _cancelButton.Visibility = Visibility.Collapsed;
        _playButton = Ui.Button("▶", TogglePlay, tooltip: "Воспроизведение / пауза (Пробел)", minWidth: 44);

        Content = BuildLayout();
        WireEvents();
        _timeline.SnapToKeyframes = _preset.StreamCopy;

        _s.HardwareDetected += () => Dispatcher.Invoke(() => { _statusHw.Text = _s.HardwareSummary(); try { _s.Settings.Save(); } catch (Exception ex) { Logger.Warn("Настройки: " + ex.Message); } });
        _statusHw.Text = _s.HardwareSummary();
        _s.StartHardwareDetection();
        if (!_s.Ffmpeg.IsResolved) ShowInfo("FFmpeg не найден. Положите ffmpeg.exe и ffprobe.exe в папку tools\\ffmpeg рядом с программой или укажите путь в настройках.", InfoKind.Error);

        Loaded += async (_, _) =>
        {
            if (filesToOpen.Count > 0) await AddFilesAsync(filesToOpen, replace: true);
        };
        Closing += (_, _) =>
        {
            _timer.Stop();
            _exportCts?.Cancel();
            _proxyCts?.Cancel();
            _s.Settings.Save();
        };
    }

    public static string AppVersion => typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // ------------------------------------------------------------------ компоновка

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();

        // верхняя панель
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 6) };
        var openButton = Ui.Button("Открыть…", () => OpenFiles(replace: true), tooltip: "Открыть видео (Ctrl+O)");
        var recentButton = Ui.Button("Недавние ▾", () => { }, tooltip: "Недавние файлы и папки");
        recentButton.Click += (_, _) => ShowRecentMenu(recentButton);
        toolbar.Children.Add(openButton);
        toolbar.Children.Add(recentButton);
        toolbar.Children.Add(Ui.Button("+ Добавить ролик", () => OpenFiles(replace: false), tooltip: "Добавить ролик для склейки"));
        toolbar.Children.Add(new Separator { Width = 12, Opacity = 0 });
        toolbar.Children.Add(Ui.Button("Настройки", OpenSettings));
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);

        // строка состояния
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

        // слева — ролики
        var left = BuildClipsPanel();
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        // справа — инструменты
        var right = BuildToolsPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        // центр — превью, транспорт, лента
        var center = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _infoBar.Child = _infoContent;
        Grid.SetRow(_infoBar, 0); center.Children.Add(_infoBar);
        var previewBorder = new Border { Child = _preview, Background = Ui.PreviewBg, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 6) };
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
        row.Children.Add(Ui.Button("⏮", () => Seek(_current?.InSec ?? 0), tooltip: "К точке входа (Home)", minWidth: 36));
        row.Children.Add(Ui.Button("◀", () => StepFrame(-1), tooltip: "Кадр назад (←)", minWidth: 36));
        row.Children.Add(_playButton);
        row.Children.Add(Ui.Button("▶", () => StepFrame(1), tooltip: "Кадр вперёд (→)", minWidth: 36));
        row.Children.Add(Ui.Button("⏭", () => Seek(_current?.OutSec ?? 0), tooltip: "К точке выхода (End)", minWidth: 36));
        _timeText.Margin = new Thickness(10, 0, 16, 0);
        _timeText.FontFamily = new FontFamily("Consolas");
        row.Children.Add(_timeText);
        row.Children.Add(Ui.Button("[ Вход", SetInHere, tooltip: "Точка входа = текущая позиция (I)"));
        row.Children.Add(Ui.Button("Выход ]", SetOutHere, tooltip: "Точка выхода = текущая позиция (O)"));
        row.Children.Add(Ui.SmallButton("Сброс", ResetTrim, "Убрать обрезку"));
        _fragmentText.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(_fragmentText);
        return row;
    }

    private void WireEvents()
    {
        _timer.Tick += (_, _) => OnTick();
        _preview.Player.MediaOpened += (_, _) => OnMediaOpened();
        _preview.Player.MediaFailed += (_, e) => OnMediaFailed(e.ErrorException);
        _preview.Player.MediaEnded += (_, _) => { Pause(); };
        _preview.OverlayChanged += _ => { SyncOverlayNumbers(); };
        _preview.OverlaySelected += o => { _selectedOverlay = o; SyncOverlayPanel(); };
        _preview.CropChanged += () => { SyncCropPanel(); UpdateOutputGeometry(rebuild: false); };
        _preview.RedactionChanged += _ => SyncRedactPanel();
        _preview.RedactionSelected += r => { _selectedRedaction = r; SyncRedactPanel(); };
        _timeline.SeekRequested += Seek;
        _timeline.InPointChanged += t => { if (_current is null) return; _current.InSec = t; AfterTrimChanged(light: true); };
        _timeline.OutPointChanged += t => { if (_current is null) return; _current.OutSec = t; AfterTrimChanged(light: true); };
        _timeline.DragCompleted += () => AfterTrimChanged();

        DragEventHandler dragCheck = (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragEnter += dragCheck;
        DragOver += dragCheck;
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var videos = files.Where(IsVideoFile).ToList();
                var pngs = files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var png in pngs) AddOverlayFromFile(png);
                if (videos.Count > 0) await AddFilesAsync(videos, replace: _clips.Count == 0);
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
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: SetInHere(); break;
            case Key.O when !ctrl: SetOutHere(); break;
            case Key.O when ctrl: OpenFiles(replace: true); break;
            case Key.E when ctrl: Fire(ExportAsync()); break;
            case Key.Left: if (shift) Seek(Position - 1); else StepFrame(-1); break;
            case Key.Right: if (shift) Seek(Position + 1); else StepFrame(1); break;
            case Key.Home: Seek(_current?.InSec ?? 0); break;
            case Key.End: Seek(_current?.OutSec ?? 0); break;
            case Key.Escape: if (_exportCts is not null) CancelExport(); else SetEditMode(EditMode.View); break;
            case Key.Delete: DeleteSelectedTool(); break;
            case Key.PageUp: SelectClipOffset(-1); break;
            case Key.PageDown: SelectClipOffset(1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Запуск задачи «в фоне» так, чтобы её ошибка попала в журнал и в полосу уведомлений, а не потерялась.</summary>
    private void Fire(Task task)
    {
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException();
            if (ex is OperationCanceledException) return;
            Logger.Error("Фоновая операция", ex);
            Dispatcher.BeginInvoke(() => ShowInfo("Ошибка: " + ex?.Message, InfoKind.Error, ("Открыть журнал", () => TryStart(Logger.CurrentFile))));
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
        _infoContent.Children.Add(Ui.SmallButton("✕", HideInfo, "Скрыть"));
        _infoBar.Background = kind switch
        {
            InfoKind.Success => Ui.Success,
            InfoKind.Warning => Ui.Warning,
            InfoKind.Error => new SolidColorBrush(Color.FromRgb(0xFD, 0xE2, 0xE2)),
            _ => new SolidColorBrush(Color.FromRgb(0xE4, 0xEE, 0xFB)),
        };
        _infoBar.Visibility = Visibility.Visible;
    }

    private void HideInfo() => _infoBar.Visibility = Visibility.Collapsed;

    private void OpenSettings()
    {
        var w = new SettingsWindow(_s) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _s.ReloadAfterSettingsChange();
            _exportPresets = _s.Presets.LoadExportPresets();
            _preset = _exportPresets.FirstOrDefault(p => p.Id == _preset.Id) ?? _exportPresets[0];
            _overlayPresets = _s.Presets.LoadOverlayPresets();
            RefreshPresetCombos();
            _statusHw.Text = _s.HardwareSummary();
            if (w.RedetectRequested) { _s.StartHardwareDetection(force: true); _statusHw.Text = "GPU: проверка…"; }
        }
    }
}
