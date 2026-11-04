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
    private readonly TextBlock _overlaySizeText = Ui.Label("", muted: true, size: Ui.FontCaption);
    private RadioButton _ovWhole = new(), _ovRange = new();
    private readonly StackPanel _overlayEditor = new();
    private StackPanel _overlayFileRow = new();
    private readonly StackPanel _textEditor = new();
    private readonly TextBox _textBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = Ui.FontBody, Margin = new Thickness(0, 2, 0, 4) };
    private readonly ComboBox _textFontCombo = new() { Width = 160, Height = Ui.ControlHeight };
    private Slider _textSizeSlider = new();
    private readonly TextBlock _textSizeText = Ui.Label("5 %");
    private ToggleButton _textBold = new();
    private readonly ComboBox _textColorCombo = new() { Width = 100, Height = Ui.ControlHeight };
    private readonly ComboBox _textBgCombo = new() { Width = 100, Height = Ui.ControlHeight };
    private readonly ComboBox _textAlignCombo = new() { Width = 100, Height = Ui.ControlHeight };
    private CheckBox _textOutline = new();
    private static readonly (string Name, string Hex)[] TextColors = { ("Белый", "#FFFFFF"), ("Чёрный", "#000000"), ("Жёлтый", "#FFD500"), ("Красный", "#E53935"), ("Синий", "#1E88E5"), ("Зелёный", "#43A047") };
    private static readonly (string Name, string Hex)[] TextBackgrounds = { ("Без подложки", ""), ("Тёмная", "#99000000"), ("Чёрная", "#FF000000"), ("Светлая", "#B3FFFFFF"), ("Белая", "#FFFFFFFF"), ("Красная", "#CCC62828"), ("Синяя", "#CC1565C0"), ("Жёлтая", "#CCFFD500") };
    private static readonly (string Name, string Id)[] TextAligns = { ("По центру", "center"), ("Слева", "left"), ("Справа", "right") };

    // --- стабилизация
    private Slider _stabSlider = new();
    private readonly TextBlock _stabValue = Ui.Label("", muted: true);

    // --- поиск лиц
    private FaceFinder? _faceFinder;
    private FaceFinder Faces => _faceFinder ??= new FaceFinder(_s);
    private CancellationTokenSource? _faceCts;
    private Button _faceStop = new();
    private readonly TextBlock _faceStatus = Ui.Label("", muted: true, size: Ui.FontCaption);

    // --- кроп
    private ToggleButton _cropToggle = new();
    private readonly TextBlock _cropText = Ui.Label("", muted: true, size: Ui.FontCaption);

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
    private readonly TextBlock _redactText = Ui.Label("", muted: true, size: Ui.FontCaption);
    private readonly TextBlock _redactMotionText = Ui.Label("", muted: true, size: Ui.FontCaption);
    private readonly TextBlock _overlayMotionText = Ui.Label("", muted: true, size: Ui.FontCaption);

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
        // правая колонка — инспектор свойств выбранного ролика (как Effect Controls / Inspector в больших редакторах)
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    }

    // ------------------------------------------------------------------ оверлей

    private UIElement BuildOverlaySection()
    {
        _overlayPresetCombo = Ui.Combo(_overlayPresets, p => p.Name, null, AddOverlayFromPreset, width: 200);
        var presetRow = Ui.Row(_overlayPresetCombo, Ui.SmallButton("+", () => AddOverlayFromPreset(null), T("Добавить пустой оверлей")), Ui.SmallButton(T("+ Текст"), AddTextOverlay, T("Текстовая плашка: заголовок, подпись, источник")));
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
                ScheduleCommit();
            }
        };
        _opacitySlider = Ui.Slider(0, 100, 80, v => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.Opacity = v / 100.0; _opacityText.Text = $"{v:0} %"; _preview.Refresh(); ScheduleCommit(); }, width: 0);
        _ovWhole = new RadioButton { Content = T("весь ролик"), IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovRange = new RadioButton { Content = T("с"), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _ovFrom = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = v; _ovRange.IsChecked = true; _preview.Refresh(); ScheduleCommit(); }, 52, "0.#");
        _ovTo = Ui.NumberBox(5, v => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = v; _ovRange.IsChecked = true; _preview.Refresh(); ScheduleCommit(); }, 52, "0.#");
        _ovWhole.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec = null; _selectedOverlay.ToSec = null; _preview.Refresh(); ScheduleCommit(); };
        _ovRange.Checked += (_, _) => { if (_selectedOverlay is null || _syncing) return; _selectedOverlay.FromSec ??= 0; _selectedOverlay.ToSec ??= 5; SyncOverlayPanel(); _preview.Refresh(); ScheduleCommit(); };
        _fadeIn = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeInSec = Math.Max(0, v); ScheduleCommit(); }, 44, "0.#");
        _fadeOut = Ui.NumberBox(0, v => { if (_selectedOverlay is null) return; _selectedOverlay.FadeOutSec = Math.Max(0, v); ScheduleCommit(); }, 44, "0.#");

        _overlayFileRow = Ui.Row(Ui.Label(T("Файл"), muted: true), _overlayFileCombo, Ui.SmallButton("…", BrowseOverlayFile, T("Выбрать PNG")));
        _overlayEditor.Children.Add(_overlayFileRow);
        BuildTextEditor();
        _overlayEditor.Children.Add(_textEditor);
        _overlayEditor.Children.Add(Ui.Label(T("Перетащите оверлей мышью, размер меняйте за любую сторону или угол."), muted: true, size: Ui.FontCaption));
        _overlayEditor.Children.Add(_overlaySizeText);
        _overlayEditor.Children.Add(Ui.FormRow(T("Прозрачность"), _opacitySlider, _opacityText, trailingWidth: 44));
        _overlayEditor.Children.Add(Ui.Row(Ui.Label(T("Показ"), muted: true), _ovWhole, _ovRange, _ovFrom, Ui.Label(T("по"), muted: true), _ovTo, Ui.Label(T("сек"), muted: true)));
        _overlayEditor.Children.Add(Ui.Row(Ui.SmallButton(T("от текущей"), () => { if (_selectedOverlay is null) return; _selectedOverlay.FromSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); ScheduleCommit(); }),
                                           Ui.SmallButton(T("до текущей"), () => { if (_selectedOverlay is null) return; _selectedOverlay.ToSec = Math.Round(OutputTime(), 2); _ovRange.IsChecked = true; SyncOverlayPanel(); _preview.Refresh(); ScheduleCommit(); }),
                                           Ui.Label(T("Плавно:"), muted: true), _fadeIn, _fadeOut, Ui.Label(T("сек"), muted: true)));
        _overlayEditor.Children.Add(Ui.Row(Ui.Label(T("Движение"), muted: true),
            Ui.SmallButton(T("⬥ Точка здесь"), () =>
            {
                if (_selectedOverlay is null) return;
                var item = ItemOf(_selectedOverlay);
                var r = Core.Geometry.OverlayGeometry.Compute(_preview.OutputWidth, _preview.OutputHeight, _selectedOverlay, item.PngWidth, item.PngHeight);
                var (x, y) = _selectedOverlay.Motion.Count == 0 ? ((double)r.X / _preview.OutputWidth, (double)r.Y / _preview.OutputHeight)
                    : Core.Models.Motion.At(_selectedOverlay.Motion, OutputTime(), (double)r.X / _preview.OutputWidth, (double)r.Y / _preview.OutputHeight);
                Core.Models.Motion.Upsert(_selectedOverlay.Motion, Math.Round(OutputTime(), 2), x, y);
                SyncOverlayPanel(); _preview.Layout(); ScheduleCommit();
            }, T("Запомнить положение оверлея в этот момент; между точками он движется по прямой")),
            Ui.SmallButton(T("− Точка"), () =>
            {
                if (_selectedOverlay is null || _selectedOverlay.Motion.Count == 0) return;
                var near = _selectedOverlay.Motion.OrderBy(k => Math.Abs(k.TimeSec - OutputTime())).First();
                _selectedOverlay.Motion.Remove(near);
                SyncOverlayPanel(); _preview.Layout(); ScheduleCommit();
            }, T("Удалить ближайшую точку")),
            Ui.SmallButton(T("Без движения"), () => { if (_selectedOverlay is null) return; _selectedOverlay.Motion.Clear(); SyncOverlayPanel(); _preview.Layout(); ScheduleCommit(); }),
            _overlayMotionText));
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
        var earlier = _clips.TakeWhile(c => !ReferenceEquals(c, _current)).ToList();
        double before = earlier.Sum(c => c.DurationSec);
        int partsBefore = earlier.Sum(c => c.OrderedSegments().Count);
        double pos = Position, inner = 0;
        foreach (var s in _current.OrderedSegments())
        {
            if (pos >= s.OutSec) { inner += s.DurationSec; partsBefore++; }
            else if (pos > s.InSec) { inner += pos - s.InSec; break; }
            else break;
        }
        // переходы накладывают фрагменты друг на друга: каждый предыдущий стык укорачивает ленту
        double tr = _transitionSec > 0 ? new ExportJob { Clips = _clips, Preset = _preset, OutputPath = "x", TransitionSec = _transitionSec }.EffectiveTransitionSec : 0;
        return Math.Max(0, before + inner - tr * partsBefore);
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
        ScheduleCommit();
        if (item.ResolvedPath is null && !s.IsText) BrowseOverlayFile();
    }

    // ------------------------------------------------------------------ текстовая плашка

    private void AddTextOverlay()
    {
        if (_overlays.Count >= 3) { ShowInfo(T("Не больше трёх оверлеев."), InfoKind.Warning); return; }
        var s = new OverlaySettings
        {
            Name = T("Текст"), Anchor = Anchor.BottomCenter, OffsetX = 0, OffsetY = 6, OffsetUnit = OffsetUnit.Percent, Opacity = 1, WidthUnit = SizeUnit.Pixels,
            Text = new TextStyle { Text = T("Заголовок") },
        };
        var item = new OverlayItem { Settings = s };
        LoadOverlayImage(item);
        _overlays.Add(item);
        _selectedOverlay = s;
        _preview.Overlays.Clear(); _preview.Overlays.AddRange(_overlays);
        _preview.SelectedOverlay = s;
        SyncOverlayPanel();
        _preview.Refresh();
        ScheduleCommit();
        _textBox.Focus(); _textBox.SelectAll();
    }

    /// <summary>Нарисовать плашку под текущий размер выходного кадра; ширина оверлея всегда равна ширине картинки (в пикселях выхода).</summary>
    private void RenderTextOverlay(OverlayItem item)
    {
        if (item.Settings.Text is not { } t) return;
        int w = _preview.OutputWidth > 0 ? _preview.OutputWidth : 1920, h = _preview.OutputHeight > 0 ? _preview.OutputHeight : 1080;
        item.ResolvedPath = null;
        var bmp = TextRenderer.Render(t, w, h);
        item.Image = bmp;
        item.PngWidth = bmp?.PixelWidth ?? 0; item.PngHeight = bmp?.PixelHeight ?? 0;
        item.Settings.WidthUnit = SizeUnit.Pixels;
        item.Settings.Width = Math.Max(1, item.PngWidth);
    }

    private void RefreshTextOverlays()
    {
        bool any = false;
        foreach (var item in _overlays) if (item.Settings.IsText) { RenderTextOverlay(item); any = true; }
        if (any) _preview.Layout();
    }

    /// <summary>Растягивание плашки за рукоятки меняет размер шрифта, а не масштаб картинки — текст остаётся чётким.</summary>
    private void OnOverlayResized(OverlaySettings s)
    {
        if (s.Text is not { } t) return;
        var item = ItemOf(s);
        if (item.PngWidth <= 0 || Math.Abs(s.Width - item.PngWidth) < 0.5) return;
        t.SizePercent = Math.Clamp(t.SizePercent * s.Width / item.PngWidth, 1, 30);
        RenderTextOverlay(item);
        _syncing = true; _textSizeSlider.Value = t.SizePercent; _textSizeText.Text = $"{t.SizePercent:0.#} %"; _syncing = false;
    }

    private void BuildTextEditor()
    {
        _textBox.TextChanged += (_, _) => ApplyTextChange(t => t.Text = _textBox.Text, renameOverlay: true);
        foreach (var f in TextRenderer.FontFamilies()) _textFontCombo.Items.Add(new ComboBoxItem { Content = f, Tag = f, FontFamily = new FontFamily(f) });
        _textFontCombo.SelectionChanged += (_, _) => { if (_textFontCombo.SelectedItem is ComboBoxItem { Tag: string f }) ApplyTextChange(t => t.FontFamily = f); };
        _textSizeSlider = Ui.Slider(1, 30, 5, v => { _textSizeText.Text = $"{v:0.#} %"; ApplyTextChange(t => t.SizePercent = v); }, width: 0);
        _textBold = Ui.Toggle(T("Жирный"), true, v => ApplyTextChange(t => t.Bold = v));
        FillHexCombo(_textColorCombo, TextColors, "#FFFFFF");
        _textColorCombo.SelectionChanged += (_, _) => { if (_textColorCombo.SelectedItem is ComboBoxItem { Tag: string hex }) ApplyTextChange(t => t.Color = hex); };
        FillHexCombo(_textBgCombo, TextBackgrounds, "#99000000");
        _textBgCombo.SelectionChanged += (_, _) => { if (_textBgCombo.SelectedItem is ComboBoxItem { Tag: string hex }) ApplyTextChange(t => t.Background = hex); };
        foreach (var (name, id) in TextAligns) _textAlignCombo.Items.Add(new ComboBoxItem { Content = T(name), Tag = id });
        _textAlignCombo.SelectedIndex = 0;
        _textAlignCombo.SelectionChanged += (_, _) => { if (_textAlignCombo.SelectedItem is ComboBoxItem { Tag: string id }) ApplyTextChange(t => t.Align = id); };
        _textOutline = Ui.Check(T("Обводка"), false, v => ApplyTextChange(t => t.Outline = v));

        _textEditor.Children.Add(_textBox);
        _textEditor.Children.Add(Ui.Row(Ui.Label(T("Шрифт"), muted: true), _textFontCombo, _textBold));
        _textEditor.Children.Add(Ui.FormRow(T("Размер"), _textSizeSlider, _textSizeText, trailingWidth: 48));
        _textEditor.Children.Add(Ui.Row(Ui.Label(T("Цвет"), muted: true), _textColorCombo, Ui.Label(T("Подложка"), muted: true), _textBgCombo));
        _textEditor.Children.Add(Ui.Row(Ui.Label(T("Строки"), muted: true), _textAlignCombo, _textOutline));
        _textEditor.Visibility = Visibility.Collapsed;
    }

    private void FillHexCombo(ComboBox cb, (string Name, string Hex)[] items, string current)
    {
        cb.Items.Clear();
        ComboBoxItem? selected = null;
        foreach (var (name, hex) in items)
        {
            var ci = new ComboBoxItem { Content = T(name), Tag = hex };
            if (string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)) selected = ci;
            cb.Items.Add(ci);
        }
        if (selected is null) { selected = new ComboBoxItem { Content = current, Tag = current }; cb.Items.Add(selected); }
        cb.SelectedItem = selected;
    }

    private void ApplyTextChange(Action<TextStyle> change, bool renameOverlay = false)
    {
        if (_syncing || _selectedOverlay?.Text is not { } t) return;
        change(t);
        if (renameOverlay)
        {
            var first = t.Text.Split('\n')[0].Trim();
            _selectedOverlay.Name = first.Length == 0 ? T("Текст") : (first.Length > 24 ? first[..24] + "…" : first);
            foreach (ListBoxItem li in _overlayList.Items) if (ReferenceEquals(li.Tag, _selectedOverlay)) li.Content = OverlayListLabel(ItemOf(_selectedOverlay));
        }
        RenderTextOverlay(ItemOf(_selectedOverlay));
        _overlaySizeText.Text = $"{T("Шрифт")}: {t.SizePercent:0.#} % {T("высоты кадра")}";
        _preview.Layout();
        ScheduleCommit();
    }

    private string OverlayListLabel(OverlayItem item)
    {
        var s = item.Settings;
        if (s.IsText) return "T  " + (s.Name.Length > 0 ? s.Name : T("Текст"));
        return (s.Name.Length > 0 ? s.Name : T("Оверлей")) + (item.ResolvedPath is null ? "  " + T("(файл не выбран)") : "");
    }

    private void SyncTextEditor(OverlaySettings o)
    {
        if (o.Text is not { } t) return;
        _textBox.Text = t.Text;
        foreach (ComboBoxItem ci in _textFontCombo.Items) if (string.Equals(ci.Tag as string, t.FontFamily, StringComparison.OrdinalIgnoreCase)) { _textFontCombo.SelectedItem = ci; break; }
        if (_textFontCombo.SelectedItem is not ComboBoxItem { Tag: string tag } || !string.Equals(tag, t.FontFamily, StringComparison.OrdinalIgnoreCase))
        { var ci = new ComboBoxItem { Content = t.FontFamily, Tag = t.FontFamily }; _textFontCombo.Items.Insert(0, ci); _textFontCombo.SelectedItem = ci; }
        _textSizeSlider.Value = t.SizePercent; _textSizeText.Text = $"{t.SizePercent:0.#} %";
        _textBold.IsChecked = t.Bold;
        FillHexCombo(_textColorCombo, TextColors, t.Color);
        FillHexCombo(_textBgCombo, TextBackgrounds, t.Background);
        foreach (ComboBoxItem ci in _textAlignCombo.Items) if ((string)ci.Tag == t.Align) _textAlignCombo.SelectedItem = ci;
        _textOutline.IsChecked = t.Outline;
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
        ScheduleCommit();
    }

    private void ApplyDefaultOverlayIfNeeded()
    {
        if (_overlays.Count > 0 || string.IsNullOrEmpty(_s.Settings.DefaultOverlayPresetId)) return;
        var preset = _overlayPresets.FirstOrDefault(p => p.Id == _s.Settings.DefaultOverlayPresetId);
        if (preset is not null && !string.IsNullOrEmpty(preset.File)) AddOverlayFromPreset(preset);
    }

    private void LoadOverlayImage(OverlayItem item)
    {
        if (item.Settings.IsText) { RenderTextOverlay(item); return; }
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
        ScheduleCommit();
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
        _overlaySizeText.Text = o.Text is { } t ? $"{T("Шрифт")}: {t.SizePercent:0.#} % {T("высоты кадра")}" : $"{T("Ширина")}: {o.Width:0.#}% {T("кадра")}";
        ScheduleCommit();
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
                var li = new ListBoxItem { Content = OverlayListLabel(item), Tag = s, Padding = new Thickness(6, 2, 6, 2) };
                _overlayList.Items.Add(li);
                if (ReferenceEquals(s, _selectedOverlay)) _overlayList.SelectedItem = li;
            }
            _overlayList.Visibility = _overlays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var o = _selectedOverlay;
            _overlayEditor.Visibility = o is null ? Visibility.Collapsed : Visibility.Visible;
            if (o is null) return;
            _overlayFileRow.Visibility = o.IsText ? Visibility.Collapsed : Visibility.Visible;
            _textEditor.Visibility = o.IsText ? Visibility.Visible : Visibility.Collapsed;
            if (o.IsText) SyncTextEditor(o);

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
            _overlayMotionText.Text = o.Motion.Count == 0 ? T("по якорю") : $"{T("точек")}: {o.Motion.Count}";
        }
        finally { _syncing = false; }
    }

    private void SaveOverlayPreset()
    {
        if (_selectedOverlay is null) return;
        var name = InputWindow.Ask(this, T("Пресет оверлея"), T("Название пресета:"), _selectedOverlay.Name);
        if (name is null) return;
        var p = _selectedOverlay.Clone();
        p.Motion.Clear(); // движение относится к конкретному проекту, не к пресету
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
        ScheduleCommit();
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
        _stabSlider = Ui.Slider(0, Core.Models.Clip.MaxStabilize, 0, v =>
        {
            if (_syncing || _current is null) return;
            _current.Stabilize = (int)Math.Round(v);
            SyncStabLabel(); RefreshClipList(); UpdateExportInfo(); ScheduleCommit();
        }, width: 0);
        _stabSlider.TickFrequency = 1; _stabSlider.IsSnapToTickEnabled = true;
        _stabSlider.MouseDoubleClick += (_, _) => _stabSlider.Value = 0;
        _stabValue.TextAlignment = TextAlignment.Right;
        col.Children.Add(Ui.FormRow(T("Стабилизация"), _stabSlider, _stabValue));
        col.Children.Add(Ui.Label(T("Стабилизация убирает дрожание камеры при экспорте (два прохода, дольше); на превью не видна."), muted: true, size: Ui.FontCaption));
        var filtersHint = Ui.Label(ColorFilterEffect.Available ? T("Фильтры применяются при воспроизведении. Двойной клик по ползунку — сброс.") : T("Результат виден на паузе; при воспроизведении показывается исходник. Двойной клик по ползунку — сброс."), muted: true, size: Ui.FontCaption);
        col.Children.Add(filtersHint);
        col.Children.Add(Ui.Row(
            Ui.SmallButton(T("Сбросить"), () => { if (_current is null) return; _current.Filters = new VideoFilters(); SyncFiltersPanel(); _preview.SetFilters(_current.Filters); RefreshFilteredFrameIfPaused(); ScheduleCommit(); }),
            Ui.SmallButton(T("Ко всем роликам"), () => { if (_current is null) return; foreach (var c in _clips) if (!ReferenceEquals(c, _current)) c.Filters = _current.Filters.Clone(); _statusInfo.Text = T("Фильтры применены ко всем роликам"); RefreshClipList(); CommitNow(); })));
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
        _preview.SetFilters(f);
        RefreshFilteredFrameIfPaused();
        ScheduleCommit();
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
            _stabSlider.Value = _current.Stabilize; SyncStabLabel();
        }
        finally { _syncing = false; }
    }

    private void SyncStabLabel() => _stabValue.Text = _current is null || _current.Stabilize == 0 ? T("выкл") : _current.Stabilize.ToString();

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
        ScheduleCommit();
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
            SyncEffectLabel(); _preview.Layout(); ScheduleCommit();
        }, width: 0);
        _redactModeCombo = new ComboBox { Width = 110, Height = Ui.ControlHeight };
        _redactModeCombo.Items.Add(T("Размытие")); _redactModeCombo.Items.Add(T("Мозаика"));
        _redactModeCombo.SelectionChanged += (_, _) => { if (_selectedRedaction is null || _syncing) return; _selectedRedaction.Mode = _redactModeCombo.SelectedIndex == 1 ? RedactMode.Pixelate : RedactMode.Blur; SyncRedactPanel(); _preview.Refresh(); RefreshClipList(); ScheduleCommit(); };
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
        _redactEditor.Children.Add(Ui.Row(Ui.Label(T("Движение"), muted: true),
            Ui.SmallButton(T("⬥ Точка здесь"), () =>
            {
                if (_selectedRedaction is null) return;
                var (x, y) = _selectedRedaction.PositionAt(Position);
                Core.Models.Motion.Upsert(_selectedRedaction.Motion, Math.Round(Position, 2), x, y);
                SyncRedactPanel(); UpdateTimelineMarkers(); _preview.Layout();
            }, T("Запомнить положение области в этот момент; между точками область движется по прямой")),
            Ui.SmallButton(T("− Точка"), () =>
            {
                if (_selectedRedaction is null || _selectedRedaction.Motion.Count == 0) return;
                var near = _selectedRedaction.Motion.OrderBy(k => Math.Abs(k.TimeSec - Position)).First();
                _selectedRedaction.Motion.Remove(near);
                if (_selectedRedaction.Motion.Count == 0) { (_selectedRedaction.X, _selectedRedaction.Y) = (near.X, near.Y); }
                SyncRedactPanel(); UpdateTimelineMarkers(); _preview.Layout();
            }, T("Удалить ближайшую точку")),
            Ui.SmallButton(T("Без движения"), () =>
            {
                if (_selectedRedaction is null) return;
                var (x, y) = _selectedRedaction.PositionAt(Position);
                _selectedRedaction.Motion.Clear(); _selectedRedaction.X = x; _selectedRedaction.Y = y;
                SyncRedactPanel(); UpdateTimelineMarkers(); _preview.Layout();
            }),
            _redactMotionText));
        _redactEditor.Visibility = Visibility.Collapsed;

        _faceStop = Ui.SmallButton(T("Стоп"), () => _faceCts?.Cancel(), T("Остановить поиск лиц"));
        _faceStop.Visibility = Visibility.Collapsed;
        var faceRow = Ui.Row(Ui.Label(T("Лица"), muted: true),
            Ui.SmallButton(T("Найти в кадре"), () => Fire(FindFacesAsync(scan: false)), T("Найти лица в текущем кадре и размыть их")),
            Ui.SmallButton(T("Найти по ролику"), () => Fire(FindFacesAsync(scan: true)), T("Пройти по активному фрагменту (или всему ролику), найти лица и размыть их с движением")),
            _faceStop, _faceStatus);
        var content = Ui.Column(
            Ui.Row(_redactToggle, Ui.SmallButton(T("+ Размытие"), () => AddRedaction(RedactMode.Blur)), Ui.SmallButton(T("+ Мозаика"), () => AddRedaction(RedactMode.Pixelate))),
            faceRow,
            Ui.Label(T("Лица, номера, недопустимый контент. Область можно двигать и растягивать на превью."), muted: true, size: Ui.FontCaption),
            _redactText,
            _redactEditor);
        return Ui.Card(Ui.Column(Ui.Header(T("РЕТУШЬ")), content));
    }

    /// <summary>Поиск лиц встроенным детектором Windows: один кадр — неподвижные области, проход по ролику — области с движением.</summary>
    private async Task FindFacesAsync(bool scan)
    {
        if (_current is null || _faceCts is not null) return;
        if (!FaceFinder.Supported) { ShowInfo(T("Поиск лиц недоступен в этой версии Windows."), InfoKind.Warning); return; }
        Pause();
        var clip = _current;
        double from, to, step;
        if (scan && !clip.Media.IsImage)
        {
            var segs = clip.OrderedSegments();
            var seg = ActiveSegment ?? (segs.Count == 1 ? segs[0] : null);
            from = seg?.InSec ?? 0; to = seg?.OutSec ?? clip.Media.DurationSec; step = FaceFinder.DefaultStepSec;
        }
        else { from = Position; to = Position; step = 0; }
        _faceCts = new CancellationTokenSource();
        _faceStop.Visibility = Visibility.Visible;
        _faceStatus.Text = T("Ищу лица…");
        try
        {
            var progress = new Progress<double>(p => _faceStatus.Text = $"{T("Ищу лица…")} {p * 100:0}%");
            var found = await Faces.DetectAsync(clip.Media, from, to, step, progress, _faceCts.Token);
            var regions = FaceFinder.ToRegions(found, step, (double)clip.Media.Width / Math.Max(1, clip.Media.Height), step > 0 ? from : null, step > 0 ? to : null);
            if (!ReferenceEquals(clip, _current)) return;
            if (regions.Count == 0) { _faceStatus.Text = T("Лица не найдены"); return; }
            clip.Redactions.AddRange(regions);
            _selectedRedaction = regions[0]; _preview.SelectedRedaction = regions[0];
            SetEditMode(EditMode.Redact);
            SyncRedactPanel(); UpdateTimelineMarkers(); RefreshClipList(); _preview.Refresh(); CommitNow();
            _faceStatus.Text = $"{T("Найдено лиц")}: {regions.Count}";
        }
        catch (OperationCanceledException) { _faceStatus.Text = T("Поиск остановлен"); }
        catch (Exception ex)
        {
            Logger.Error("Поиск лиц", ex);
            _faceStatus.Text = "";
            ShowInfo(T("Поиск лиц не удался: ") + ex.Message, InfoKind.Error);
        }
        finally { _faceCts = null; _faceStop.Visibility = Visibility.Collapsed; }
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
        ScheduleCommit();
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
        ScheduleCommit();
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
            _redactMotionText.Text = r.Motion.Count == 0 ? T("неподвижно") : $"{T("точек")}: {r.Motion.Count} ({TimeFormat.Format(r.Motion.Min(k => k.TimeSec), false)}–{TimeFormat.Format(r.Motion.Max(k => k.TimeSec), false)})";
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

    private readonly System.Windows.Threading.DispatcherTimer _censorMeasureTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _measuringCensors;

    private static bool NeedsLevel(AudioCensor c) => c.Mode == CensorMode.Beep && !c.LevelIsCurrent && c.ToSec > c.FromSec;

    /// <summary>Громкость дорожки в интервалах «пика» замеряется в фоне: тон ставится на уровень речи, не громче и не тише.</summary>
    private void ScheduleCensorMeasure()
    {
        if (_current is null || !_current.Media.HasAudio || !_current.Censors.Any(NeedsLevel)) return;
        _censorMeasureTimer.Stop();
        _censorMeasureTimer.Start();
    }

    private async Task MeasureCensorLevelsAsync(Clip clip, CancellationToken ct = default)
    {
        if (_measuringCensors || !clip.Media.HasAudio || clip.Media.IsImage) return;
        _measuringCensors = true;
        try
        {
            foreach (var c in clip.Censors.Where(NeedsLevel).ToList())
            {
                double f = c.FromSec, t = c.ToSec;
                var lv = await _s.Probe.MeanVolumeAsync(clip.Media.Path, f, t, ct);
                if (Math.Abs(c.FromSec - f) > 0.011 || Math.Abs(c.ToSec - t) > 0.011) continue; // интервал успели сдвинуть — замерим заново
                c.SetLevel(lv ?? AudioCensor.DefaultLevelDb);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn("Замер громкости: " + ex.Message); }
        finally { _measuringCensors = false; }
        // замер — служебные данные: он не делает проект несохранённым и не становится шагом Undo
        CommitNow(); _history.Amend(Snapshot()); UpdateDirtyTitle();
        if (ReferenceEquals(clip, _current) && clip.Censors.Any(NeedsLevel)) ScheduleCensorMeasure();
    }

    private UIElement BuildCensorSection()
    {
        _censorMeasureTimer.Tick += (_, _) => { _censorMeasureTimer.Stop(); if (_current is not null) Fire(MeasureCensorLevelsAsync(_current)); };
        _censorList.SelectionChanged += (_, _) => UpdateTimelineMarkers();
        var modeCombo = new ComboBox { Width = 90, Height = Ui.ControlHeight };
        modeCombo.Items.Add(T("пик")); modeCombo.Items.Add(T("тишина"));
        modeCombo.SelectedIndex = 0;
        modeCombo.SelectionChanged += (_, _) => { if (SelectedCensor is { } c) { c.Mode = modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep; SyncCensorPanel(); } };
        var content = Ui.Column(
            Ui.Row(Ui.SmallButton(T("+ Запикать здесь"), () => AddCensor(modeCombo.SelectedIndex == 1 ? CensorMode.Mute : CensorMode.Beep), T("Новый интервал от текущей позиции на 1 с")), modeCombo),
            Ui.Label(T("Выделите интервал: «Начало здесь» и «Конец здесь» по ходу воспроизведения."), muted: true, size: Ui.FontCaption),
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
        ScheduleCommit();
        ScheduleCensorMeasure();
        if (_timeline.IsDragging) return; // во время перетаскивания границы маркеры не пересоздаём
        _timeline.Markers.Clear();
        if (_current is not null)
        {
            foreach (var r in _current.Redactions.Where(r => r.FromSec is not null || r.ToSec is not null || r.Motion.Count > 0))
                _timeline.Markers.Add(new TimelineMarker { Tag = r, From = r.FromSec ?? 0, To = r.ToSec ?? _current.Media.DurationSec, Brush = RedactBrush, Lane = 0, KeyTimes = r.Motion.Select(k => k.TimeSec).ToList() });
            foreach (var c in _current.Censors)
                _timeline.Markers.Add(new TimelineMarker { Tag = c, From = c.FromSec, To = c.ToSec, Brush = CensorBrush, Lane = 1 });
            _timeline.SelectedMarkerTag = (object?)SelectedCensor ?? _selectedRedaction;
        }
        _timeline.InvalidateVisual();
    }
}
