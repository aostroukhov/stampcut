using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace StampCut.App.Controls;

/// <summary>Фабрика элементов интерфейса без XAML: единый стиль, минимум кода в окне.</summary>
public static class Ui
{
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED));
    public static readonly Brush AccentDark = new SolidColorBrush(Color.FromRgb(0x1F, 0x4F, 0xB5));
    public static readonly Brush PanelBg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
    public static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0xD6, 0xD9, 0xDE));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    public static readonly Brush PreviewBg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16));
    public static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x3A));
    public static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xCE));
    public static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0xE3, 0xF6, 0xE3));

    static Ui()
    {
        foreach (var b in new[] { Accent, AccentDark, PanelBg, Border, Muted, PreviewBg, Danger, Warning, Success }) b.Freeze();
    }

    public static Button Button(string text, Action onClick, bool primary = false, string? tooltip = null, double minWidth = 0)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0), MinWidth = minWidth,
            ToolTip = tooltip, Cursor = System.Windows.Input.Cursors.Hand, FontSize = 13, Focusable = false,
        };
        if (primary)
        {
            b.Background = Accent; b.Foreground = Brushes.White; b.BorderBrush = AccentDark; b.FontWeight = FontWeights.SemiBold;
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

    public static TextBlock Label(string text, bool muted = false, double size = 12, FontWeight? weight = null)
        => new() { Text = text, FontSize = size, Foreground = muted ? Muted : Brushes.Black, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Header(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Muted, Margin = new Thickness(0, 10, 0, 4) };

    public static StackPanel Row(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
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
        var cb = new ComboBox { Width = width, Padding = new Thickness(6, 4, 6, 4) };
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

    public static Border Card(UIElement content, Brush? bg = null)
        => new() { Child = content, Background = bg ?? Brushes.White, BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 0, 0, 8) };

    public static Expander Section(string title, UIElement content, bool expanded = true)
        => new() { Header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13 }, Content = content, IsExpanded = expanded, Margin = new Thickness(0, 0, 0, 6) };

    public static Thumb Handle(double size, System.Windows.Input.Cursor cursor, Brush? fill = null)
        => new()
        {
            Width = size, Height = size, Cursor = cursor,
            Template = HandleTemplate(fill ?? Brushes.White),
        };

    private static ControlTemplate HandleTemplate(Brush fill)
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        f.SetValue(System.Windows.Shapes.Shape.FillProperty, fill);
        f.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Black);
        f.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
        t.VisualTree = f;
        return t;
    }

    public static Thumb TransparentThumb()
        => new() { Template = TransparentTemplate(), Cursor = System.Windows.Input.Cursors.SizeAll, Background = Brushes.Transparent };

    private static ControlTemplate TransparentTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        f.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.Transparent);
        t.VisualTree = f;
        return t;
    }
}
