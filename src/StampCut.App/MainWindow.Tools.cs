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
using static StampCut.App.Lang;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private bool _syncing;

    // --- оверлей
    private OverlaySettings? _selectedOverlay;
    private ComboBox _overlayPresetCombo = new();
    private readonly ListBox _overlayList = new() { MaxHeight = 70, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _overlayFileCombo = new() { Width = 190, Height = Ui.ControlHeight };
    private TextBox _fadeIn = new(), _fadeOut = new(), _ovFrom = new(), _ovTo = new();
    private Slider _opacitySlider = new();
    private readonly TextBlock _opacityText = Ui.Label("80 %");
    private readonly TextBlock _overlaySizeText = Ui.Label("", muted: true, size: 11);
    private RadioButton _ovWhole = new(), _ovRange = new();
    private readonly StackPanel _overlayEditor = new();

    // --- кроп
    private ToggleButton _cropToggle = new();
    private readonly TextBlock _cropText = Ui.Label("", muted: true, size: 11);

    // --- ретушь
    private RedactRegion? _selectedRedaction;
    private ToggleButton _redactToggle = new();
    private readonly StackPanel _redactEditor = new();
    private Slider _effectSlider = new();
    private readonly TextBlock _effectLabel = Ui.Label("", muted: true);
    private readonly TextBlock _effectValue = Ui.Label("");
    private ComboBox _redactModeCombo = new();
    private RadioButton _rdWhole = new(), _rdRange = new();
    private TextBox _rdFrom = new(), _rdTo = new();
    private readonly TextBlock _redactText = Ui.Label("", muted: true, size: 11);

    // --- запикать
    private readonly ListBox _censorList = new() { MaxHeight = 90, Margin = new Thickness(0, 4, 0, 4) };
    private AudioCensor? SelectedCensor => _censorList.SelectedItem is ListBoxItem { Tag: AudioCensor c } ? c : null;

    private UIElement BuildToolsPanel()
    {
        var stack = new StackPanel { Margin = new Thickness(4, 0, 10, 0) };
        _toolsStack = stack;
        stack.RequestBringIntoView += (_, e) => e.Handled = true; // фокус в поле не должен прокручивать панель
        stack.Children.Add(BuildOverlaySection());
        stack.Children.Add(BuildFiltersSection());
        stack.Children.Add(BuildCropSection());
        stack.Children.Add(BuildRedactSection());
        stack.Children.Add(BuildCensorSection());
        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var dock = new DockPanel();
        var export = BuildExportBlock();
        ((FrameworkElement)export).Margin = new Thickness(4, 0, 10, 0);
        DockPanel.SetDock(export, Dock.Bottom);
        dock.Children.Add(export);
        dock.Children.Add(scroll);
        return dock;
    }

    // ------------------------------------------------------------------ оверлей

    private UIElement BuildOverlaySection()
    {
        _overlayPresetCombo = Ui.Combo(_overlayPresets, p => p.Name, null, AddOverlayFromPreset, width: 200);
        var presetRow = Ui.Row(_overlayPresetCombo, Ui.SmallButton("+", () => AddOverlayFromPreset(null), T("Добавить пустой оверлей")));
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
        _opacitySlider = Ui.Slider(0, 100, 80, v => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.Opacity = v / 100.0; _opacityText.Text = $"{v:0} %"; _preview.Refresh(); }, width: 0);
        _ovWhole = new RadioButton { Content = T("весь ролик"), IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovRange = new RadioButton { Content = T("с"), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovFrom = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = v; _ovRange.IsChecked = true; _preview.Refresh(); }, 52, "0.#");
        _ovTo = Ui.NumberBox(5, v => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = v; _ovRange.IsChecked = true; _preview.Refresh(); }, 52, "0.#");
        _ovWhole.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec = null; _selectedOverlay.ToSec = null; _preview.Refresh(); };
        _ovRange.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec ??= 0; _selectedOverlay.ToSec ??= 5; SyncOverlayPanel(); _preview.Refresh(); };
        _fadeIn = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeInSec = Math.Max(0, v); }, 44, "0.#");
        _fadeOut = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeOutSec = Math.Max(0, v); }, 44, "0.#");

        _overlayEditor.Children.Add(Ui.Row(Ui.Label(T("Файл"), muted: true), _overlayFileCombo, Ui.SmallButton("…", BrowseOverlayFile, T("Выбрать PNG"))));
        _overlayEditor.Children.Add(Ui.Label(T("Перетащите оверлей мышью, размер меняйте за любую сторону или угол."), muted: true, size: 11));
        _overlayEditor.Children.Add(_overlaySizeText);
        _overlayEditor.Children.Add(Ui.FormRow(T("Прозрачность"), _opacitySlider, _opacityText, trailingWidth: 44));
        _overlayEditor.Children.Add(Ui.Row(Ui.Label(T("Показ"), muted: true), _ovWhole, _ovRange, _ovFrom, Ui.Label(T("по"), muted: true), _ovTo, Ui.Label(T("сек"), muted: true)));
        _overlayEditor.Children.Add(Ui.Row(Ui.SmallButton(T("от текущей"), () => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); }),
                                           Ui.SmallButton(T("до текущей"), () => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); }),
                                           Ui.Label(T("Плавно:"), muted: true), _fadeIn, _fadeOut, Ui.Label(T("сек"), muted: true)));
        _overlayEditor.Children.Add(Ui.Row(
            Ui.SmallButton(T("Сохранить как пресет"), SaveOverlayPreset),
            Ui.SmallButton(T("По умолчанию"), SetDefaultOverlay, T("Применять этот пресет ко всем новым проектам")),
            Ui.SmallButton(T("✕ Убрать"), RemoveSelectedOverlay)));
        _overlayEditor.Visibility = Visibility.Collapsed;

        return Ui.Card(Ui.Column(Ui.Header(T("ОВЕРЛЕЙ")), presetRow, _overlayList, _overlayEditor));
    }

    /// <summary>Время на выходной ленте: сумма предыдущих роликов + положение внутри фрагментов текущего.</summary>
    private double OutputTime()
    {
        if (_current is null) return 0;
        double before = _clips.TakeWhile(c => !ReferenceEquals(c, _current)).Sum(c => c.DurationSec);
        double pos = Position, inner = 0;
        foreach (var s in _current.OrderedSegments())
        {
            if (pos >= s.OutSec) inner += s.DurationSec;
            else if (pos > s.InSec) { inner += pos - s.InSec; break; }
            else break;
        }
        return before + inner;
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
        if (_overlays.Count >= 3) { ShowInfo(T("Не больше трёх оверлеев."), InfoKind.Warning); return; }
        var s = preset?.Clone() ?? new OverlaySettings { Name = T("Оверлей") };
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
            ShowInfo(T("Не удалось загрузить изображение оверлея: ") + ex.Message, InfoKind.Error);
        }
    }

    private void BrowseOverlayFile()
    {
        if (_selectedOverlay is null) return;
        var dlg = new OpenFileDialog { Filter = $"{T("PNG с прозрачностью")}|*.png|{T("Изображения")}|*.png;*.webp;*.jpg;*.jpeg;*.bmp", Title = T("Выбрать оверлей") };
        var dir = _s.Settings.LibraryDir is not null && Directory.Exists(Path.Combine(_s.Settings.LibraryDir, "overlays")) ? Path.Combine(_s.Settings.LibraryDir, "overlays") : AppPaths.OverlaysDir;
        dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;
        _selectedOverlay.File = dlg.FileName;
        if (string.IsNullOrEmpty(_selectedOverlay.Name) || _selectedOverlay.Name == T("Оверлей")) _selectedOverlay.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
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

    /// <summary>Во время перетаскивания: только подпись размера, без перестроения списков и чтения папок.</summary>
    private void SyncOverlayNumbers()
    {
        var o = _selectedOverlay;
        if (o is null) return;
        _overlaySizeText.Text = $"{T("Ширина")}: {o.Width:0.#}% {T("кадра")}";
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
                var li = new ListBoxItem { Content = (s.Name.Length > 0 ? s.Name : T("Оверлей")) + (item.ResolvedPath is null ? "  " + T("(файл не выбран)") : ""), Tag = s, Padding = new Thickness(6, 2, 6, 2) };
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
            _overlayFileCombo.Items.Add(new ComboBoxItem { Content = T("Выбрать файл…"), Tag = "*browse*" });

            SyncOverlayNumbers();
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
        var name = InputWindow.Ask(this, T("Пресет оверлея"), T("Название пресета:"), _selectedOverlay.Name);
        if (name is null) return;
        var p = _selectedOverlay.Clone();
        p.Name = name; p.Id = PresetStore.SafeId(name).ToLowerInvariant();
        var resolved = ItemOf(_selectedOverlay).ResolvedPath;
        if (resolved is not null && _s.Presets.OverlayDirs(AppPaths.OverlaysDir).Any(d => string.Equals(Path.GetDirectoryName(resolved), d, StringComparison.OrdinalIgnoreCase)))
            p.File = Path.GetFileName(resolved);
        bool toLibrary = _s.Settings.LibraryDir is not null && MessageBox.Show(this, T("Сохранить в общую библиотеку редакции (да) или только на этом компьютере (нет)?"), T("Пресет оверлея"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _s.Presets.SaveOverlayPreset(p, toLibrary);
        _overlayPresets = _s.Presets.LoadOverlayPresets();
        RefreshOverlayPresetCombo();
        _selectedOverlay.Name = name;
        SyncOverlayPanel();
        _statusInfo.Text = T("Пресет оверлея сохранён: ") + name;
    }

    private void SetDefaultOverlay()
    {
        if (_selectedOverlay is null) return;
        var preset = _overlayPresets.FirstOrDefault(p => p.Name == _selectedOverlay.Name);
        if (preset is null) { SaveOverlayPreset(); preset = _overlayPresets.FirstOrDefault(p => p.Name == _selectedOverlay.Name); }
        if (preset is null) return;
        _s.Settings.DefaultOverlayPresetId = preset.Id;
        try { _s.Settings.Save(); } catch { }
        _statusInfo.Text = $"«{preset.Name}» {T("будет применяться ко всем новым проектам")}";
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

    // ------------------------------------------------------------------ фильтры видео

    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _filterSliders = new();

    private UIElement BuildFiltersSection()
    {
        var col = Ui.Column(Ui.Header(T("ВИДЕО")));
        (string Key, string Name, int Min, int Max, int Default)[] defs =
        {
            ("shadows", "Тени", -100, 100, 0), ("highlights", "Светлые участки", -100, 100, 0), ("saturation", "Насыщенность", 0, 200, 100),
            ("brightness", "Яркость", -100, 100, 0), ("contrast", "Контраст", -100, 100, 0),
        };
        foreach (var (key, name, min, max, def) in defs)
        {
            var value = Ui.Label(def.ToString(), muted: true); value.TextAlignment = TextAlignment.Right;
            var slider = Ui.Slider(min, max, def, v => { if (_syncing || _current is null) return; SetFilter(key, (int)Math.Round(v)); }, width: 0);
            slider.TickFrequency = 1; slider.IsSnapToTickEnabled = true;
            slider.MouseDoubleClick += (_, _) => slider.Value = def;
            _filterSliders[key] = (slider, value);
            col.Children.Add(Ui.FormRow(T(name), slider, value));
        }
        col.Children.Add(Ui.Label(T("Результат виден на паузе; при воспроизведении показывается исходник. Двойной клик по ползунку — сброс."), muted: true, size: 11));
        col.Children.Add(Ui.Row(
            Ui.SmallButton(T("Сбросить"), () => { if (_current is null) return; _current.Filters = new VideoFilters(); SyncFiltersPanel(); RefreshFilteredFrameIfPaused(); }),
            Ui.SmallButton(T("Ко всем роликам"), () => { if (_current is null) return; foreach (var c in _clips) if (!ReferenceEquals(c, _current)) c.Filters = _current.Filters.Clone(); _statusInfo.Text = T("Фильтры применены ко всем роликам"); })));
        return Ui.Card(col);
    }

    private void SetFilter(string key, int v)
    {
        if (_current is null) return;
        var f = _current.Filters;
        switch (key)
        {
            case "shadows": f.Shadows = v; break;
            case "highlights": f.Highlights = v; break;
            case "saturation": f.Saturation = v; break;
            case "brightness": f.Brightness = v; break;
            case "contrast": f.Contrast = v; break;
        }
        _filterSliders[key].Value.Text = v.ToString();
        RefreshFilteredFrameIfPaused();
    }

    private void SyncFiltersPanel()
    {
        if (_current is null) return;
        var f = _current.Filters;
        _syncing = true;
        try
        {
            void Set(string key, int v) { _filterSliders[key].Slider.Value = v; _filterSliders[key].Value.Text = v.ToString(); }
            Set("shadows", f.Shadows); Set("highlights", f.Highlights); Set("saturation", f.Saturation); Set("brightness", f.Brightness); Set("contrast", f.Contrast);
        }
        finally { _syncing = false; }
    }

    // ------------------------------------------------------------------ кроп

    private static readonly (string Name, double? Aspect)[] Aspects =
    {
        ("Свободно", null), ("9:16 вертикаль", 9.0 / 16), ("1:1 квадрат", 1), ("4:5", 0.8), ("16:9", 16.0 / 9), ("4:3", 4.0 / 3), ("3:4", 0.75),
    };
    private double? _cropAspect;

    private UIElement BuildCropSection()
    {
        _cropToggle = Ui.Toggle(T("Изменить кроп"), false, v => { if (_syncing) return; if (v) SetEditMode(EditMode.Crop); else if (_preview.Mode == EditMode.Crop) SetEditMode(EditMode.View); });
        var aspectCombo = new ComboBox { Width = 130, Height = Ui.ControlHeight };
        foreach (var (name, _) in Aspects) aspectCombo.Items.Add(T(name));
        aspectCombo.SelectedIndex = 0;
        aspectCombo.SelectionChanged += (_, _) =>
        {
            _cropAspect = Aspects[Math.Max(0, aspectCombo.SelectedIndex)].Aspect;
            _preview.CropAspect = _cropAspect;
            if (_current is not null && _cropAspect is { } a) { _current.Crop = CropRect.Centered(_current.Media.Width, _current.Media.Height, a); AfterCropChanged(); }
        };
        var content = Ui.Column(
            Ui.Row(_cropToggle, Ui.Label(T("Пропорции"), muted: true), aspectCombo),
            Ui.Row(Ui.SmallButton(T("По центру"), () => { if (_current is null) return; _current.Crop = _cropAspect is { } a ? CropRect.Centered(_current.Media.Width, _current.Media.Height, a) : null; AfterCropChanged(); }),
                   Ui.SmallButton(T("Сбросить"), () => { if (_current is null) return; _current.Crop = null; AfterCropChanged(); }),
                   Ui.SmallButton(T("Ко всем роликам"), () => { if (_current is null) return; foreach (var c in _clips) if (!ReferenceEquals(c, _current)) c.Crop = _current.Crop?.Clone(); AfterCropChanged(); }, T("Скопировать кроп на все ролики"))),
            _cropText);
        return Ui.Card(Ui.Column(Ui.Header(T("КРОП")), content));
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
            _cropText.Text = $"{T("Кадр")} {w}×{h} {T("из")} {_current.Media.Width}×{_current.Media.Height} ({T("смещение")} {x}, {y})";
        }
        else _cropText.Text = T("Без кропа: весь кадр");
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
        _redactToggle = Ui.Toggle(T("Изменить области"), false, v => { if (_syncing) return; if (v) SetEditMode(EditMode.Redact); else if (_preview.Mode == EditMode.Redact) SetEditMode(EditMode.View); });
        _effectSlider = Ui.Slider(0, 200, 0, v =>
        {
            if (_selectedRedaction is null || _syncing) return;
            int px = (int)Math.Round(v);
            if (_selectedRedaction.Mode == RedactMode.Pixelate) _selectedRedaction.BlockSize = px; else _selectedRedaction.BlurRadius = px;
            SyncEffectLabel(); _preview.Layout();
        }, width: 0);
        _redactModeCombo = new ComboBox { Width = 110, Height = Ui.ControlHeight };
        _redactModeCombo.Items.Add(T("Размытие")); _redactModeCombo.Items.Add(T("Мозаика"));
        _redactModeCombo.SelectionChanged += (_, _) => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.Mode = _redactModeCombo.SelectedIndex == 1 ? RedactMode.Pixelate : RedactMode.Blur; SyncRedactPanel(); _preview.Refresh(); RefreshClipList(); };
        _rdWhole = new RadioButton { Content = T("весь ролик"), IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _rdRange = new RadioButton { Content = T("с"), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _rdFrom = Ui.NumberBox(0, v => { if (_selectedRedaction is null) return; _selectedRedaction.FromSec = v; _rdRange.IsChecked = true; UpdateTimelineMarkers(); }, 52, "0.#");
        _rdTo = Ui.NumberBox(0, v => { if (_selectedRedaction is null) return; _selectedRedaction.ToSec = v; _rdRange.IsChecked = true; UpdateTimelineMarkers(); }, 52, "0.#");
        _rdWhole.Checked += (_, _) => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.FromSec = null; _selectedRedaction.ToSec = null; UpdateTimelineMarkers(); _preview.Layout(); };
        _rdRange.Checked += (_, _) => { if (_selectedRedaction is null || _syncing || _current is null) return; _selectedRedaction.FromSec ??= ActiveSegment?.InSec ?? 0; _selectedRedaction.ToSec ??= ActiveSegment?.OutSec ?? _current.Media.DurationSec; SyncRedactPanel(); UpdateTimelineMarkers(); };

        _redactEditor.Children.Add(Ui.FormRow(T("Эффект"), _redactModeCombo));
        var effectRow = Ui.FormRow("", _effectSlider, _effectValue, trailingWidth: 84);
        effectRow.Children.Remove(effectRow.Children[0]); Grid.SetColumn(_effectLabel, 0); _effectLabel.VerticalAlignment = VerticalAlignment.Center; effectRow.Children.Add(_effectLabel);
        _redactEditor.Children.Add(effectRow);
        _redactEditor.Children.Add(Ui.Row(Ui.Label(T("Показ"), muted: true), _rdWhole, _rdRange, _rdFrom, Ui.Label(T("по"), muted: true), _rdTo, Ui.Label(T("сек"), muted: true)));
        _redactEditor.Children.Add(Ui.Row(
            Ui.SmallButton(T("от текущей"), () => { if (_selectedRedaction is null) return; _selectedRedaction.FromSec = Math.Round(Position, 2); _selectedRedaction.ToSec ??= _current?.Media.DurationSec; SyncRedactPanel(); UpdateTimelineMarkers(); }),
            Ui.SmallButton(T("до текущей"), () => { if (_selectedRedaction is null) return; _selectedRedaction.ToSec = Math.Round(Position, 2); _selectedRedaction.FromSec ??= 0; SyncRedactPanel(); UpdateTimelineMarkers(); }),
            Ui.SmallButton(T("✕ Удалить область"), RemoveSelectedRedaction)));
        _redactEditor.Visibility = Visibility.Collapsed;

        var content = Ui.Column(
            Ui.Row(_redactToggle, Ui.SmallButton(T("+ Размытие"), () => AddRedaction(RedactMode.Blur)), Ui.SmallButton(T("+ Мозаика"), () => AddRedaction(RedactMode.Pixelate))),
            Ui.Label(T("Лица, номера, недопустимый контент. Область можно двигать и растягивать на превью."), muted: true, size: 11),
            _redactText,
            _redactEditor);
        return Ui.Card(Ui.Column(Ui.Header(T("РЕТУШЬ")), content));
    }

    private void SyncEffectLabel()
    {
        var r = _selectedRedaction;
        if (r is null || _current is null) return;
        bool pix = r.Mode == RedactMode.Pixelate;
        _effectLabel.Text = pix ? T("Квадратик") : T("Радиус");
        int value = pix ? r.BlockSize : r.BlurRadius;
        int px = pix ? r.EffectiveBlockSize(_current.Media.Height) : r.EffectiveBlurRadius((int)(r.W * _current.Media.Width), (int)(r.H * _current.Media.Height));
        _effectValue.Text = value > 0 ? $"{px} px" : $"{px} px ({T("авто")})";
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
            _redactText.Text = _current.Redactions.Count == 0 ? T("Областей нет") : T("Областей: ") + _current.Redactions.Count + (_selectedRedaction is null ? T(" (кликните область на превью)") : "");
            var r = _selectedRedaction;
            _preview.SelectedRedaction = r;
            _redactEditor.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
            if (r is null) return;
            _redactModeCombo.SelectedIndex = r.Mode == RedactMode.Pixelate ? 1 : 0;
            _effectSlider.Maximum = r.Mode == RedactMode.Pixelate ? 200 : 300;
            _effectSlider.Value = r.Mode == RedactMode.Pixelate ? r.BlockSize : r.BlurRadius;
            SyncEffectLabel();
            bool range = r.FromSec is not null || r.ToSec is not null;
            _rdWhole.IsChecked = !range; _rdRange.IsChecked = range;
            Ui.SetNumber(_rdFrom, r.FromSec ?? 0, "0.#"); Ui.SetNumber(_rdTo, r.ToSec ?? _current.Media.DurationSec, "0.#");
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
        var modeCombo = new ComboBox { Width = 90, Height = Ui.ControlHeight };
        modeCombo.Items.Add(T("пик")); modeCombo.Items.Add(T("тишина"));
        modeCombo.SelectedIndex = 0;
        modeCombo.SelectionChanged += (_, _) => { if (SelectedCensor is { } c) { c.Mode = modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep; SyncCensorPanel(); } };
        var content = Ui.Column(
            Ui.Row(Ui.SmallButton(T("+ Запикать здесь"), () => AddCensor(modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep), T("Новый интервал от текущей позиции на 1 с")), modeCombo),
            Ui.Label(T("Выделите интервал: «Начало здесь» и «Конец здесь» по ходу воспроизведения."), muted: true, size: 11),
            _censorList,
            Ui.Row(
                Ui.SmallButton(T("Начало здесь"), () => { if (SelectedCensor is { } c) { c.FromSec = Math.Round(Position, 2); if (c.ToSec <= c.FromSec) c.ToSec = c.FromSec + 0.5; SyncCensorPanel(); } }),
                Ui.SmallButton(T("Конец здесь"), () => { if (SelectedCensor is { } c) { c.ToSec = Math.Round(Position, 2); if (c.ToSec <= c.FromSec) c.FromSec = Math.Max(0, c.ToSec - 0.5); SyncCensorPanel(); } }),
                Ui.SmallButton("✕", RemoveCensor, T("Удалить интервал"))));
        return Ui.Card(Ui.Column(Ui.Header(T("ЗАПИКАТЬ ЗВУК")), content));
    }

    private void AddCensor(CensorMode mode)
    {
        if (_current is null) return;
        if (!_current.Media.HasAudio) { ShowInfo(T("В этом ролике нет звуковой дорожки."), InfoKind.Warning); return; }
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
            var li = new ListBoxItem { Content = $"{TimeFormat.Format(c.FromSec)} – {TimeFormat.Format(c.ToSec)}  {(c.Mode == CensorMode.Beep ? T("пик") : T("тишина"))}", Tag = c, Padding = new Thickness(6, 2, 6, 2) };
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
