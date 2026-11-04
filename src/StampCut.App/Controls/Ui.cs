using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace StampCut.App.Controls;

/// <summary>
/// Фабрика элементов без XAML. Цвета берутся из темы Fluent (Windows 11) по ключам ресурсов,
/// чтобы светлая и тёмная темы выглядели правильно; при отсутствии ключа — запасной цвет.
/// </summary>
public static class Ui
{
    public static readonly Brush Accent = Frozen(Color.FromRgb(0x00, 0x67, 0xC0));
    public static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    public static readonly Brush PreviewBg = Frozen(Color.FromRgb(0x14, 0x14, 0x16));
    public static readonly Brush Danger = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    public static readonly Brush CardBg = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    public static readonly Brush CardBorder = Frozen(Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>file:-URI для пути с «#» и «%»: new Uri(path) считает «#» началом фрагмента и теряет хвост имени.</summary>
    public static Uri FileUri(string path) => new(path.Replace("%", "%25").Replace("#", "%23"));

    /// <summary>Ресурс темы, если он есть, иначе запасная кисть.</summary>
    public static void Themed(FrameworkElement el, DependencyProperty prop, string key, Brush fallback)
    {
        if (Application.Current?.TryFindResource(key) is not null) el.SetResourceReference(prop, key);
        else el.SetValue(prop, fallback);
    }

    public static Brush ThemeBrush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static Button Button(string text, Action onClick, bool primary = false, string? tooltip = null, double minWidth = 0)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), MinWidth = minWidth,
            ToolTip = tooltip, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false,
        };
        if (primary)
        {
            if (Application.Current?.TryFindResource("AccentButtonStyle") is Style accent) b.Style = accent;
            else { b.Background = Accent; b.Foreground = Brushes.White; b.BorderBrush = Accent; }
            b.FontWeight = FontWeights.SemiBold;
        }
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button SmallButton(string text, Action onClick, string? tooltip = null)
    {
        var b = Button(text, onClick, tooltip: tooltip);
        b.Padding = new Thickness(8, 3, 8, 3); b.FontSize = 12; b.Margin = new Thickness(0, 0, 4, 0);
        return b;
    }

    public static ToggleButton Toggle(string text, bool isChecked, Action<bool> onChange, string? tooltip = null)
    {
        var t = new ToggleButton { Content = text, IsChecked = isChecked, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = tooltip, Focusable = false };
        t.Checked += (_, _) => onChange(true);
        t.Unchecked += (_, _) => onChange(false);
        return t;
    }

    public static TextBlock Label(string text, bool muted = false, double size = 12, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (muted) Themed(tb, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush", Muted);
        return tb;
    }

    public static TextBlock Header(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4) };
        Themed(tb, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush", Muted);
        return tb;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(c);
        }
        return sp;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static TextBox NumberBox(double value, Action<double> onChange, double width = 56, string format = "0.##")
    {
        var tb = new TextBox { Text = value.ToString(format, System.Globalization.CultureInfo.CurrentCulture), Width = width, Padding = new Thickness(4, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center };
        void Commit()
        {
            var text = tb.Text.Replace(',', '.');
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) onChange(v);
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
        return tb;
    }

    public static void SetNumber(TextBox tb, double value, string format = "0.##")
    {
        var text = value.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
        if (tb.Text != text && !tb.IsKeyboardFocused) tb.Text = text;
    }

    public static Slider Slider(double min, double max, double value, Action<double> onChange, double width = 140)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = width, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Focusable = false };
        s.ValueChanged += (_, e) => onChange(e.NewValue);
        return s;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, Func<T, string> display, T? selected, Action<T> onSelect, double width = 180) where T : class
    {
        var cb = new ComboBox { Width = width };
        foreach (var it in items) cb.Items.Add(new ComboBoxItem { Content = display(it), Tag = it });
        if (selected is not null)
            foreach (ComboBoxItem it in cb.Items) if (ReferenceEquals(it.Tag, selected)) { cb.SelectedItem = it; break; }
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T t }) onSelect(t); };
        return cb;
    }

    public static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var c = new CheckBox { Content = text, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3), Focusable = false };
        c.Checked += (_, _) => onChange(true);
        c.Unchecked += (_, _) => onChange(false);
        return c;
    }

    public static Border Card(UIElement content)
    {
        var b = new Border { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
        Themed(b, Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush", CardBg);
        Themed(b, Border.BorderBrushProperty, "CardStrokeColorDefaultBrush", CardBorder);
        return b;
    }

    public static Thumb Handle(double size, System.Windows.Input.Cursor cursor, Brush? fill = null)
        => new() { Width = size, Height = size, Cursor = cursor, Template = HandleTemplate(fill ?? Brushes.White), Focusable = false };

    private static ControlTemplate HandleTemplate(Brush fill)
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        f.SetValue(System.Windows.Shapes.Shape.FillProperty, fill);
        f.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Black);
        f.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
        t.VisualTree = f;
        return t;
    }

    public static Thumb TransparentThumb(System.Windows.Input.Cursor? cursor = null)
        => new() { Template = TransparentTemplate(), Cursor = cursor ?? System.Windows.Input.Cursors.SizeAll, Background = Brushes.Transparent, Focusable = false };

    private static ControlTemplate TransparentTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        f.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.Transparent);
        t.VisualTree = f;
        return t;
    }
}
