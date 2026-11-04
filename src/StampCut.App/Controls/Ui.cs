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

    /// <summary>Динамическая ссылка на кисть темы Fluent (ключи проверены по PresentationFramework.Fluent); запасная — если темы нет.</summary>
    public static void Themed(FrameworkElement el, DependencyProperty prop, string key, Brush fallback)
    {
        if (Application.Current?.ThemeMode != ThemeMode.None) el.SetResourceReference(prop, key);
        else el.SetValue(prop, fallback);
    }

    public static Brush ThemeBrush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>Цвет кисти темы (для рисования: кисти темы нельзя клонировать и замораживать).</summary>
    public static Color ThemeColor(string key, Color fallback) => Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    public static SolidColorBrush Solid(Color c, double opacity = 1) { var b = new SolidColorBrush(c) { Opacity = opacity }; b.Freeze(); return b; }

    /// <summary>Основной цвет текста окна: TextBlock наследует его, иначе в тёмной теме текст остаётся чёрным.</summary>
    public static void ApplyWindowTheme(Window w)
    {
        Themed(w, Control.ForegroundProperty, "TextFillColorPrimaryBrush", Brushes.Black);
    }

    public static Icon? AppIcon()
    {
        try
        {
            using var stream = typeof(Ui).Assembly.GetManifestResourceStream("StampCut.icon.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch { return null; }
    }

    public sealed class Icon : IDisposable
    {
        public System.Windows.Media.Imaging.BitmapFrame Frame { get; }
        public Icon(Stream s) { Frame = System.Windows.Media.Imaging.BitmapFrame.Create(s, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad); }
        public void Dispose() { }
    }

    public static Button Button(string text, Action onClick, bool primary = false, string? tooltip = null, double minWidth = 0)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 0, 12, 0), Height = ControlHeight, Margin = new Thickness(0, 0, 6, 0), MinWidth = minWidth,
            ToolTip = tooltip, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false, VerticalContentAlignment = VerticalAlignment.Center,
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

    /// <summary>Единая высота кнопок, переключателей и полей — как у элементов Windows 11.</summary>
    public const double ControlHeight = 32;

    public static Button SmallButton(string text, Action onClick, string? tooltip = null)
    {
        var b = Button(text, onClick, tooltip: tooltip);
        b.Padding = new Thickness(9, 0, 9, 0); b.Margin = new Thickness(0, 0, 4, 0);
        return b;
    }

    public static ToggleButton Toggle(string text, bool isChecked, Action<bool> onChange, string? tooltip = null)
    {
        var t = new ToggleButton { Content = text, IsChecked = isChecked, Padding = new Thickness(10, 0, 10, 0), Height = ControlHeight, Margin = new Thickness(0, 0, 6, 0), ToolTip = tooltip, Focusable = false, VerticalContentAlignment = VerticalAlignment.Center };
        t.Checked += (_, _) => onChange(true);
        t.Unchecked += (_, _) => onChange(false);
        return t;
    }

    public static TextBlock Label(string text, bool muted = false, double size = 12, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Themed(tb, TextBlock.ForegroundProperty, muted ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush", muted ? Muted : Brushes.Black);
        return tb;
    }

    public static TextBlock Header(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
        Themed(tb, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush", Muted);
        return tb;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe)
            {
                if (fe.Margin == default) fe.Margin = new Thickness(0, 0, 6, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            sp.Children.Add(c);
        }
        return sp;
    }

    /// <summary>Строка формы «подпись — элемент — значение» с фиксированными колонками: ничего не скачет и не обрезается.</summary>
    public static Grid FormRow(string label, UIElement control, UIElement? trailing = null, double labelWidth = 104, double trailingWidth = 40)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(trailing is null ? 0 : trailingWidth) });
        var l = Label(label, muted: true);
        l.TextTrimming = TextTrimming.CharacterEllipsis; l.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(l, 0); g.Children.Add(l);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Stretch; }
        Grid.SetColumn(control, 1); g.Children.Add(control);
        if (trailing is not null)
        {
            if (trailing is FrameworkElement tf) { tf.VerticalAlignment = VerticalAlignment.Center; tf.HorizontalAlignment = HorizontalAlignment.Right; tf.Margin = new Thickness(6, 0, 0, 0); }
            Grid.SetColumn(trailing, 2); g.Children.Add(trailing);
        }
        return g;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static TextBox NumberBox(double value, Action<double> onChange, double width = 56, string format = "0.##")
    {
        var tb = new TextBox { Text = value.ToString(format, System.Globalization.CultureInfo.CurrentCulture), Width = width, Height = ControlHeight, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
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
        var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Focusable = false };
        if (width > 0) s.Width = width;
        s.ValueChanged += (_, e) => onChange(e.NewValue);
        return s;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, Func<T, string> display, T? selected, Action<T> onSelect, double width = 180) where T : class
    {
        var cb = new ComboBox { Height = ControlHeight, VerticalContentAlignment = VerticalAlignment.Center };
        if (width > 0) cb.Width = width; else cb.HorizontalAlignment = HorizontalAlignment.Stretch;
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
        var b = new Border { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
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
