using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using StampCut.Core.Presets;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private bool _syncing;

    // --- оверлей
    private OverlaySettings? _selectedOverlay;
    private ComboBox _overlayPresetCombo = new();
    private readonly ListBox _overlayList = new() { MaxHeight = 70, BorderBrush = Ui.Border, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _overlayFileCombo = new() { Width = 200 };
    private readonly AnchorPicker _anchorPicker = new();
    private TextBox _offsetX = new(), _offsetY = new(), _widthBox = new(), _fadeIn = new(), _fadeOut = new(), _ovFrom = new(), _ovTo = new();
    private Slider _opacitySlider = new();
    private readonly TextBlock _opacityText = Ui.Label("80 %");
    private RadioButton _ovWhole = new(), _ovRange = new();
    private readonly StackPanel _overlayEditor = new();

    // --- кроп
    private ToggleButton _cropToggle = new();
    private readonly TextBlock _cropText = Ui.Label("", muted: true, size: 11);

    // --- ретушь
    private RedactRegion? _selectedRedaction;
    private ToggleButton _redactToggle = new();
    private readonly StackPanel _redactEditor = new();
    private Slider _strengthSlider = new();
    private ComboBox _redactModeCombo = new();
    private RadioButton _rdWhole = new(), _rdRange = new();
    private TextBox _rdFrom = new(), _rdTo = new();
    private readonly TextBlock _redactText = Ui.Label("", muted: true, size: 11);

    // --- запикать
    private readonly ListBox _censorList = new() { MaxHeight = 90, BorderBrush = Ui.Border, Margin = new Thickness(0, 4, 0, 4) };
    private AudioCensor? SelectedCensor => _censorList.SelectedItem is ListBoxItem { Tag: AudioCensor c } ? c : null;

    private UIElement BuildToolsPanel()
    {
        var stack = new StackPanel { Width = 300, Margin = new Thickness(8, 0, 10, 0) };
        stack.Children.Add(BuildOverlaySection());
        stack.Children.Add(BuildCropSection());
        stack.Children.Add(BuildRedactSection());
        stack.Children.Add(BuildCensorSection());
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dock = new DockPanel { Width = 320 };
        var export = BuildExportBlock();
        ((FrameworkElement)export).Margin = new Thickness(8, 0, 10, 0);
        DockPanel.SetDock(export, Dock.Bottom);
        dock.Children.Add(export);
        dock.Children.Add(scroll);
        return dock;
    }

    // ------------------------------------------------------------------ оверлей

    private UIElement BuildOverlaySection()
    {
        _overlayPresetCombo = Ui.Combo(_overlayPresets, p => p.Name, null, AddOverlayFromPreset, width: 200);
        var presetRow = Ui.Row(_overlayPresetCombo, Ui.SmallButton("+", () => AddOverlayFromPreset(null), "Добавить пустой оверлей"));
        _overlayList.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_overlayList.SelectedItem is ListBoxItem { Tag: OverlaySettings o }) { _selectedOverlay = o; _preview.SelectedOverlay = o; SyncOverlayPanel(); _preview.Refresh(); }
        };

        _overlayFileCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing || _selectedOverlay is null) return;
            if (_overlayFileCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                if (tag == "*browse*") { _overlayFileCombo.SelectedItem = null; BrowseOverlayFile(); return; }
                _selectedOverlay.File = tag;
                LoadOverlayImage(ItemOf(_selectedOverlay));
                _preview.Refresh();
            }
        };
        _anchorPicker.AnchorChanged += a => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.Anchor = a; _preview.Refresh(); };
        _offsetX = Ui.NumberBox(3, v => { if (_selectedOverlay is null) return; _selectedOverlay.OffsetX = v; _preview.Refresh(); });
        _offsetY = Ui.NumberBox(3, v => { if (_selectedOverlay is null) return; _selectedOverlay.OffsetY = v; _preview.Refresh(); });
        _widthBox = Ui.NumberBox(25, v => { if (_selectedOverlay is null) return; _selectedOverlay.Width = Math.Clamp(v, 1, 100); _preview.Refresh(); });
        _opacitySlider = Ui.Slider(0, 100, 80, v => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.Opacity = v / 100.0; _opacityText.Text = $"{v:0} %"; _preview.Refresh(); }, width: 150);
        _ovWhole = new RadioButton { Content = "весь ролик", IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovRange = new RadioButton { Content = "с", VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovFrom = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = v; _ovRange.IsChecked = true; _preview.Refresh(); }, 52, "0.#");
        _ovTo = Ui.NumberBox(5, v => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = v; _ovRange.IsChecked = true; _preview.Refresh(); }, 52, "0.#");
        _ovWhole.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec = null; _selectedOverlay.ToSec = null; _preview.Refresh(); };
        _ovRange.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec ??= 0; _selectedOverlay.ToSec ??= 5; SyncOverlayPanel(); _preview.Refresh(); };
        _fadeIn = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeInSec = Math.Max(0, v); }, 44, "0.#");
        _fadeOut = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeOutSec = Math.Max(0, v); }, 44, "0.#");

        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal };
        anchorRow.Children.Add(_anchorPicker);
        var offsets = Ui.Column(
            Ui.Row(Ui.Label("Отступ X", muted: true), _offsetX, Ui.Label("%", muted: true)),
            Ui.Row(Ui.Label("Отступ Y", muted: true), _offsetY, Ui.Label("%", muted: true)),
            Ui.Row(Ui.Label("Ширина", muted: true), _widthBox, Ui.Label("% кадра", muted: true)));
        offsets.Margin = new Thickness(10, 0, 0, 0);
        anchorRow.Children.Add(offsets);

        _overlayEditor.Children.Add(Ui.Row(Ui.Label("Файл", muted: true), _overlayFileCombo, Ui.SmallButton("…", BrowseOverlayFile, "Выбрать PNG")));
        _overlayEditor.Children.Add(anchorRow);
        _overlayEditor.Children.Add(Ui.Row(Ui.Label("Прозрачность", muted: true), _opacitySlider, _opacityText));
        _overlayEditor.Children.Add(Ui.Row(Ui.Label("Показ", muted: true), _ovWhole, _ovRange, _ovFrom, Ui.Label("по", muted: true), _ovTo, Ui.Label("с", muted: true)));
        _overlayEditor.Children.Add(Ui.Row(Ui.SmallButton("от текущей", () => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); }),
                                           Ui.SmallButton("до текущей", () => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); }),
                                           Ui.Label("Плавно:", muted: true), _fadeIn, _fadeOut, Ui.Label("с", muted: true)));
        _overlayEditor.Children.Add(Ui.Row(
            Ui.SmallButton("Сохранить как пресет", SaveOverlayPreset),
            Ui.SmallButton("По умолчанию", SetDefaultOverlay, "Применять этот пресет ко всем новым проектам"),
            Ui.SmallButton("✕ Убрать", RemoveSelectedOverlay)));
        _overlayEditor.Visibility = Visibility.Collapsed;

        var content = Ui.Column(presetRow, _overlayList, _overlayEditor);
        return Ui.Card(Ui.Column(Ui.Header("ОВЕРЛЕЙ"), content));
    }

    /// <summary>Время на выходной ленте (для интервалов оверлея): сумма предыдущих роликов + позиция в текущем от точки входа.</summary>
    private double OutputTime()
    {
        if (_current is null) return 0;
        double before = _clips.TakeWhile(c => !ReferenceEquals(c, _current)).Sum(c => c.DurationSec);
        return before + Math.Max(0, Position - _current.InSec);
    }

    private OverlayItem ItemOf(OverlaySettings s) => _overlays.First(i => ReferenceEquals(i.Settings, s));

    private void RefreshOverlayPresetCombo()
    {
        var parent = _overlayPresetCombo.Parent as Panel;
        if (parent is null) return;
        int idx = parent.Children.IndexOf(_overlayPresetCombo);
        parent.Children.RemoveAt(idx);
        _overlayPresetCombo = Ui.Combo(_overlayPresets, p => p.Name, null, AddOverlayFromPreset, width: 200);
        parent.Children.Insert(idx, _overlayPresetCombo);
    }

    private void AddOverlayFromPreset(OverlaySettings? preset)
    {
        Dispatcher.BeginInvoke(() => { _syncing = true; _overlayPresetCombo.SelectedItem = null; _syncing = false; });
        if (_overlays.Count >= 3) { ShowInfo("Не больше трёх оверлеев.", InfoKind.Warning); return; }
        var s = preset?.Clone() ?? new OverlaySettings { Name = "Оверлей" };
        s.Id = Guid.NewGuid().ToString("N");
        if (preset is not null) { s.Name = preset.Name; _s.Settings.LastOverlayPresetId = preset.Id; }
        var item = new OverlayItem { Settings = s };
        LoadOverlayImage(item);
        _overlays.Add(item);
        _selectedOverlay = s;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = s;
        SyncOverlayPanel();
        _preview.Refresh();
        if (item.ResolvedPath is null) BrowseOverlayFile();
    }

    private void AddOverlayFromFile(string png)
    {
        if (_overlays.Count >= 3) return;
        var s = new OverlaySettings { Name = Path.GetFileNameWithoutExtension(png), File = png };
        var item = new OverlayItem { Settings = s };
        LoadOverlayImage(item);
        _overlays.Add(item);
        _selectedOverlay = s;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = s;
        SyncOverlayPanel();
        _preview.Refresh();
    }

    private void ApplyDefaultOverlayIfNeeded()
    {
        if (_overlays.Count > 0 || string.IsNullOrEmpty(_s.Settings.DefaultOverlayPresetId)) return;
        var preset = _overlayPresets.FirstOrDefault(p => p.Id == _s.Settings.DefaultOverlayPresetId);
        if (preset is not null && !string.IsNullOrEmpty(preset.File)) AddOverlayFromPreset(preset);
    }

    private void LoadOverlayImage(OverlayItem item)
    {
        item.ResolvedPath = _s.Presets.ResolveOverlayFile(item.Settings.File, AppPaths.OverlaysDir);
        item.Image = null; item.PngWidth = item.PngHeight = 0;
        if (item.ResolvedPath is null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = Ui.FileUri(item.ResolvedPath); bmp.EndInit(); bmp.Freeze();
            item.Image = bmp;
            if (!ImageInfo.TryReadPngSize(item.ResolvedPath, out var w, out var h)) { w = bmp.PixelWidth; h = bmp.PixelHeight; }
            item.PngWidth = w; item.PngHeight = h;
        }
        catch (Exception ex)
        {
            Logger.Warn("Оверлей не загрузился: " + ex.Message);
            ShowInfo("Не удалось загрузить изображение оверлея: " + ex.Message, InfoKind.Error);
        }
    }

    private void BrowseOverlayFile()
    {
        if (_selectedOverlay is null) return;
        var dlg = new OpenFileDialog { Filter = "PNG с прозрачностью|*.png|Изображения|*.png;*.webp;*.jpg;*.jpeg;*.bmp", Title = "Выбрать оверлей" };
        var dir = _s.Settings.LibraryDir is not null && Directory.Exists(Path.Combine(_s.Settings.LibraryDir, "overlays")) ? Path.Combine(_s.Settings.LibraryDir, "overlays") : AppPaths.OverlaysDir;
        dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        _selectedOverlay.File = dlg.FileName;
        if (string.IsNullOrEmpty(_selectedOverlay.Name) || _selectedOverlay.Name == "Оверлей") _selectedOverlay.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        LoadOverlayImage(ItemOf(_selectedOverlay));
        SyncOverlayPanel();
        _preview.Refresh();
    }

    private IEnumerable<string> OverlayLibraryFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _s.Presets.OverlayDirs(AppPaths.OverlaysDir))
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.png"); } catch { continue; }
            foreach (var f in files) if (seen.Add(Path.GetFileName(f))) yield return f;
        }
    }

    /// <summary>Во время перетаскивания: только числа, без перестроения списков и чтения папок.</summary>
    private void SyncOverlayNumbers()
    {
        var o = _selectedOverlay;
        if (o is null) return;
        _syncing = true;
        try
        {
            _anchorPicker.Set(o.Anchor);
            Ui.SetNumber(_offsetX, o.OffsetX); Ui.SetNumber(_offsetY, o.OffsetY); Ui.SetNumber(_widthBox, o.Width);
        }
        finally { _syncing = false; }
    }

    private void SyncOverlayPanel()
    {
        _syncing = true;
        try
        {
            _overlayList.Items.Clear();
            foreach (var item in _overlays)
            {
                var s = item.Settings;
                var li = new ListBoxItem { Content = (s.Name.Length > 0 ? s.Name : "Оверлей") + (item.ResolvedPath is null ? "  (файл не выбран)" : ""), Tag = s, Padding = new Thickness(6, 2, 6, 2) };
                _overlayList.Items.Add(li);
                if (ReferenceEquals(s, _selectedOverlay)) _overlayList.SelectedItem = li;
            }
            _overlayList.Visibility = _overlays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var o = _selectedOverlay;
            _overlayEditor.Visibility = o is null ? Visibility.Collapsed : Visibility.Visible;
            if (o is null) return;

            _overlayFileCombo.Items.Clear();
            var files = OverlayLibraryFiles().ToList();
            string? resolved = ItemOf(o).ResolvedPath;
            if (resolved is not null && !files.Any(f => string.Equals(f, resolved, StringComparison.OrdinalIgnoreCase))) files.Insert(0, resolved);
            foreach (var f in files)
            {
                var ci = new ComboBoxItem { Content = Path.GetFileName(f), Tag = f, ToolTip = f };
                _overlayFileCombo.Items.Add(ci);
                if (string.Equals(f, resolved, StringComparison.OrdinalIgnoreCase)) _overlayFileCombo.SelectedItem = ci;
            }
            _overlayFileCombo.Items.Add(new ComboBoxItem { Content = "Выбрать файл…", Tag = "*browse*" });

            _anchorPicker.Set(o.Anchor);
            Ui.SetNumber(_offsetX, o.OffsetX); Ui.SetNumber(_offsetY, o.OffsetY); Ui.SetNumber(_widthBox, o.Width);
            _opacitySlider.Value = o.Opacity * 100; _opacityText.Text = $"{o.Opacity * 100:0} %";
            bool range = o.FromSec is not null || o.ToSec is not null;
            _ovWhole.IsChecked = !range; _ovRange.IsChecked = range;
            Ui.SetNumber(_ovFrom, o.FromSec ?? 0, "0.#"); Ui.SetNumber(_ovTo, o.ToSec ?? 5, "0.#");
            Ui.SetNumber(_fadeIn, o.FadeInSec, "0.#"); Ui.SetNumber(_fadeOut, o.FadeOutSec, "0.#");
        }
        finally { _syncing = false; }
    }

    private void SaveOverlayPreset()
    {
        if (_selectedOverlay is null) return;
        var name = InputWindow.Ask(this, "Пресет оверлея", "Название пресета:", _selectedOverlay.Name);
        if (name is null) return;
        var p = _selectedOverlay.Clone();
        p.Name = name; p.Id = PresetStore.SafeId(name).ToLowerInvariant();
        // файл из библиотеки сохраняем по имени, чтобы пресет работал на других компьютерах редакции
        var resolved = ItemOf(_selectedOverlay).ResolvedPath;
        if (resolved is not null && _s.Presets.OverlayDirs(AppPaths.OverlaysDir).Any(d => string.Equals(Path.GetDirectoryName(resolved), d, StringComparison.OrdinalIgnoreCase)))
            p.File = Path.GetFileName(resolved);
        bool toLibrary = _s.Settings.LibraryDir is not null && MessageBox.Show(this, "Сохранить в общую библиотеку редакции (да) или только на этом компьютере (нет)?", "Пресет оверлея", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _s.Presets.SaveOverlayPreset(p, toLibrary);
        _overlayPresets = _s.Presets.LoadOverlayPresets();
        RefreshOverlayPresetCombo();
        _selectedOverlay.Name = name;
        SyncOverlayPanel();
        _statusInfo.Text = "Пресет оверлея сохранён: " + name;
    }

    private void SetDefaultOverlay()
    {
        if (_selectedOverlay is null) return;
        var preset = _overlayPresets.FirstOrDefault(p => p.Name == _selectedOverlay.Name);
        if (preset is null) { SaveOverlayPreset(); preset = _overlayPresets.FirstOrDefault(p => p.Name == _selectedOverlay.Name); }
        if (preset is null) return;
        _s.Settings.DefaultOverlayPresetId = preset.Id;
        _s.Settings.Save();
        _statusInfo.Text = $"«{preset.Name}» будет применяться ко всем новым проектам";
    }

    private void RemoveSelectedOverlay()
    {
        if (_selectedOverlay is null) return;
        _overlays.RemoveAll(i => ReferenceEquals(i.Settings, _selectedOverlay));
        _selectedOverlay = _overlays.LastOrDefault()?.Settings;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = _selectedOverlay;
        SyncOverlayPanel();
        _preview.Refresh();
    }

    // ------------------------------------------------------------------ кроп

    private static readonly (string Name, double? Aspect)[] Aspects =
    {
        ("Свободно", null), ("9:16 вертикаль", 9.0 / 16), ("1:1 квадрат", 1), ("4:5", 0.8), ("16:9", 16.0 / 9), ("4:3", 4.0 / 3), ("3:4", 0.75),
    };
    private double? _cropAspect;

    private UIElement BuildCropSection()
    {
        _cropToggle = new ToggleButton { Content = "Изменить кроп", Padding = new Thickness(10, 4, 10, 4), Focusable = false };
        _cropToggle.Checked += (_, _) => { if (!_syncing) SetEditMode(EditMode.Crop); };
        _cropToggle.Unchecked += (_, _) => { if (!_syncing && _preview.Mode == EditMode.Crop) SetEditMode(EditMode.View); };
        var aspectCombo = new ComboBox { Width = 130 };
        foreach (var (name, _) in Aspects) aspectCombo.Items.Add(name);
        aspectCombo.SelectedIndex = 0;
        aspectCombo.SelectionChanged += (_, _) =>
        {
            _cropAspect = Aspects[Math.Max(0, aspectCombo.SelectedIndex)].Aspect;
            _preview.CropAspect = _cropAspect;
            if (_current is not null && _cropAspect is { } a) { _current.Crop = CropRect.Centered(_current.Media.Width, _current.Media.Height, a); AfterCropChanged(); }
        };
        var content = Ui.Column(
            Ui.Row(_cropToggle, Ui.Label("Пропорции", muted: true), aspectCombo),
            Ui.Row(Ui.SmallButton("По центру", () => { if (_current is null) return; _current.Crop = _cropAspect is { } a ? CropRect.Centered(_current.Media.Width, _current.Media.Height, a) : null; AfterCropChanged(); }),
                   Ui.SmallButton("Сбросить", () => { if (_current is null) return; _current.Crop = null; AfterCropChanged(); }),
                   Ui.SmallButton("Ко всем роликам", () => { if (_current is null) return; foreach (var c in _clips) if (!ReferenceEquals(c, _current)) c.Crop = _current.Crop?.Clone(); AfterCropChanged(); }, "Скопировать кроп на все ролики")),
            _cropText);
        return Ui.Card(Ui.Column(Ui.Header("КРОП"), content));
    }

    private void AfterCropChanged()
    {
        SyncCropPanel();
        UpdateOutputGeometry();
        RefreshClipList();
        UpdateExportInfo();
    }

    private void SyncCropPanel()
    {
        if (_current is null) { _cropText.Text = ""; return; }
        if (_current.Crop is { IsFull: false } c)
        {
            var (x, y, w, h) = c.ToPixels(_current.Media.Width, _current.Media.Height);
            _cropText.Text = $"Кадр {w}×{h} из {_current.Media.Width}×{_current.Media.Height} (смещение {x}, {y})";
        }
        else _cropText.Text = "Без кропа: весь кадр";
    }

    private void SetEditMode(EditMode mode)
    {
        _syncing = true;
        _cropToggle.IsChecked = mode == EditMode.Crop;
        _redactToggle.IsChecked = mode == EditMode.Redact;
        _syncing = false;
        _preview.SetMode(mode);
        if (mode == EditMode.Crop) AfterCropChanged();
    }

    // ------------------------------------------------------------------ ретушь

    private UIElement BuildRedactSection()
    {
        _redactToggle = new ToggleButton { Content = "Изменить области", Padding = new Thickness(10, 4, 10, 4), Focusable = false };
        _redactToggle.Checked += (_, _) => { if (!_syncing) SetEditMode(EditMode.Redact); };
        _redactToggle.Unchecked += (_, _) => { if (!_syncing && _preview.Mode == EditMode.Redact) SetEditMode(EditMode.View); };

        _strengthSlider = Ui.Slider(1, 10, 6, v => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.Strength = (int)Math.Round(v); _preview.Refresh(); }, width: 120);
        _strengthSlider.TickFrequency = 1; _strengthSlider.IsSnapToTickEnabled = true;
        _redactModeCombo = new ComboBox { Width = 110 };
        _redactModeCombo.Items.Add("Размытие"); _redactModeCombo.Items.Add("Мозаика");
        _redactModeCombo.SelectionChanged += (_, _) => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.Mode = _redactModeCombo.SelectedIndex == 1 ? RedactMode.Pixelate : RedactMode.Blur; _preview.Refresh(); RefreshClipList(); };
        _rdWhole = new RadioButton { Content = "весь ролик", IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _rdRange = new RadioButton { Content = "с", VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _rdFrom = Ui.NumberBox(0, v => { if (_selectedRedaction is null) return; _selectedRedaction.FromSec = v; _rdRange.IsChecked = true; UpdateTimelineMarkers(); }, 52, "0.#");
        _rdTo = Ui.NumberBox(0, v => { if (_selectedRedaction is null) return; _selectedRedaction.ToSec = v; _rdRange.IsChecked = true; UpdateTimelineMarkers(); }, 52, "0.#");
        _rdWhole.Checked += (_, _) => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.FromSec = null; _selectedRedaction.ToSec = null; UpdateTimelineMarkers(); _preview.Layout(); };
        _rdRange.Checked += (_, _) => { if (_selectedRedaction is null || _syncing || _current is null) return; _selectedRedaction.FromSec ??= _current.InSec; _selectedRedaction.ToSec ??= _current.OutSec; SyncRedactPanel(); UpdateTimelineMarkers(); };

        _redactEditor.Children.Add(Ui.Row(Ui.Label("Эффект", muted: true), _redactModeCombo, Ui.Label("Сила", muted: true), _strengthSlider));
        _redactEditor.Children.Add(Ui.Row(Ui.Label("Показ", muted: true), _rdWhole, _rdRange, _rdFrom, Ui.Label("по", muted: true), _rdTo, Ui.Label("с", muted: true)));
        _redactEditor.Children.Add(Ui.Row(
            Ui.SmallButton("от текущей", () => { if (_selectedRedaction is null) return; _selectedRedaction.FromSec = Math.Round(Position, 2); _selectedRedaction.ToSec ??= _current?.OutSec; SyncRedactPanel(); UpdateTimelineMarkers(); }),
            Ui.SmallButton("до текущей", () => { if (_selectedRedaction is null) return; _selectedRedaction.ToSec = Math.Round(Position, 2); _selectedRedaction.FromSec ??= _current?.InSec; SyncRedactPanel(); UpdateTimelineMarkers(); }),
            Ui.SmallButton("✕ Удалить область", RemoveSelectedRedaction)));
        _redactEditor.Visibility = Visibility.Collapsed;

        var content = Ui.Column(
            Ui.Row(_redactToggle, Ui.SmallButton("+ Размытие", () => AddRedaction(RedactMode.Blur)), Ui.SmallButton("+ Мозаика", () => AddRedaction(RedactMode.Pixelate))),
            Ui.Label("Лица, номера, недопустимый контент. Область можно двигать и растягивать на превью.", muted: true, size: 11),
            _redactText,
            _redactEditor);
        return Ui.Card(Ui.Column(Ui.Header("РЕТУШЬ"), content));
    }

    private void AddRedaction(RedactMode mode)
    {
        if (_current is null) return;
        var r = new RedactRegion { Mode = mode, X = 0.4, Y = 0.35, W = 0.2, H = 0.2 * _current.Media.Width / Math.Max(1, _current.Media.Height) };
        r.H = Math.Clamp(r.H, 0.05, 0.6);
        _current.Redactions.Add(r);
        _selectedRedaction = r;
        _preview.SelectedRedaction = r;
        SetEditMode(EditMode.Redact);
        SyncRedactPanel();
        UpdateTimelineMarkers();
        RefreshClipList();
    }

    private void RemoveSelectedRedaction()
    {
        if (_current is null || _selectedRedaction is null) return;
        _current.Redactions.Remove(_selectedRedaction);
        _selectedRedaction = _current.Redactions.LastOrDefault();
        _preview.SelectedRedaction = _selectedRedaction;
        _preview.Refresh();
        SyncRedactPanel();
        UpdateTimelineMarkers();
        RefreshClipList();
    }

    private void SyncRedactPanel()
    {
        _syncing = true;
        try
        {
            if (_current is null) { _redactText.Text = ""; _redactEditor.Visibility = Visibility.Collapsed; return; }
            _redactText.Text = _current.Redactions.Count == 0 ? "Областей нет" : $"Областей: {_current.Redactions.Count}" + (_selectedRedaction is null ? " (кликните область на превью)" : "");
            var r = _selectedRedaction;
            _preview.SelectedRedaction = r;
            _redactEditor.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
            if (r is null) return;
            _redactModeCombo.SelectedIndex = r.Mode == RedactMode.Pixelate ? 1 : 0;
            _strengthSlider.Value = r.Strength;
            bool range = r.FromSec is not null || r.ToSec is not null;
            _rdWhole.IsChecked = !range; _rdRange.IsChecked = range;
            Ui.SetNumber(_rdFrom, r.FromSec ?? _current.InSec, "0.#"); Ui.SetNumber(_rdTo, r.ToSec ?? _current.OutSec, "0.#");
        }
        finally { _syncing = false; }
    }

    private void DeleteSelectedTool()
    {
        if (_preview.Mode == EditMode.Redact && _selectedRedaction is not null) RemoveSelectedRedaction();
        else if (SelectedCensor is not null) RemoveCensor();
    }

    // ------------------------------------------------------------------ запикать

    private UIElement BuildCensorSection()
    {
        _censorList.SelectionChanged += (_, _) => UpdateTimelineMarkers();
        var modeCombo = new ComboBox { Width = 90 };
        modeCombo.Items.Add("пик"); modeCombo.Items.Add("тишина");
        modeCombo.SelectedIndex = 0;
        modeCombo.SelectionChanged += (_, _) => { if (SelectedCensor is { } c) { c.Mode = modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep; SyncCensorPanel(); } };
        var content = Ui.Column(
            Ui.Row(Ui.SmallButton("+ Запикать здесь", () => AddCensor(modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep), "Новый интервал от текущей позиции на 1 с"), modeCombo),
            Ui.Label("Выделите интервал: «Начало здесь» и «Конец здесь» по ходу воспроизведения.", muted: true, size: 11),
            _censorList,
            Ui.Row(
                Ui.SmallButton("Начало здесь", () => { if (SelectedCensor is { } c) { c.FromSec = Math.Round(Position, 2); if (c.ToSec <= c.FromSec) c.ToSec = c.FromSec + 0.5; SyncCensorPanel(); } }),
                Ui.SmallButton("Конец здесь", () => { if (SelectedCensor is { } c) { c.ToSec = Math.Round(Position, 2); if (c.ToSec <= c.FromSec) c.FromSec = Math.Max(0, c.ToSec - 0.5); SyncCensorPanel(); } }),
                Ui.SmallButton("✕", RemoveCensor, "Удалить интервал")));
        return Ui.Card(Ui.Column(Ui.Header("ЗАПИКАТЬ ЗВУК"), content));
    }

    private void AddCensor(CensorMode mode)
    {
        if (_current is null) return;
        if (!_current.Media.HasAudio) { ShowInfo("В этом ролике нет звуковой дорожки.", InfoKind.Warning); return; }
        var c = new AudioCensor { FromSec = Math.Round(Position, 2), ToSec = Math.Min(_current.Media.DurationSec, Math.Round(Position, 2) + 1), Mode = mode };
        _current.Censors.Add(c);
        SyncCensorPanel(c);
        RefreshClipList();
    }

    private void RemoveCensor()
    {
        if (_current is null || SelectedCensor is not { } c) return;
        _current.Censors.Remove(c);
        SyncCensorPanel();
        RefreshClipList();
    }

    private void SyncCensorPanel(AudioCensor? select = null)
    {
        select ??= SelectedCensor;
        _censorList.Items.Clear();
        if (_current is null) return;
        foreach (var c in _current.Censors.OrderBy(x => x.FromSec))
        {
            var li = new ListBoxItem { Content = $"{TimeFormat.Format(c.FromSec)} – {TimeFormat.Format(c.ToSec)}  {(c.Mode == CensorMode.Beep ? "пик" : "тишина")}", Tag = c, Padding = new Thickness(6, 2, 6, 2) };
            _censorList.Items.Add(li);
            if (ReferenceEquals(c, select)) _censorList.SelectedItem = li;
        }
        UpdateTimelineMarkers();
    }

    private static readonly Brush CensorBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x3A));
    private static readonly Brush RedactBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x00));

    private void UpdateTimelineMarkers()
    {
        _timeline.Markers.Clear();
        if (_current is not null)
        {
            foreach (var r in _current.Redactions.Where(r => r.FromSec is not null || r.ToSec is not null))
                _timeline.Markers.Add(new TimelineMarker(r.FromSec ?? 0, r.ToSec ?? _current.Media.DurationSec, RedactBrush, 0));
            foreach (var c in _current.Censors)
                _timeline.Markers.Add(new TimelineMarker(c.FromSec, c.ToSec, CensorBrush, 1));
        }
        _timeline.InvalidateVisual();
    }
}
