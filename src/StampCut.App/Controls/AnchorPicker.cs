using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using StampCut.Core.Models;

namespace StampCut.App.Controls;

/// <summary>Сетка 3×3 для выбора якоря оверлея.</summary>
public sealed class AnchorPicker : UniformGrid
{
    private readonly ToggleButton[] _buttons = new ToggleButton[9];
    private bool _updating;
    public event Action<Anchor>? AnchorChanged;

    public AnchorPicker()
    {
        Rows = 3; Columns = 3; Width = 66; Height = 66;
        for (int i = 0; i < 9; i++)
        {
            var b = new ToggleButton { Margin = new Thickness(1), Tag = (Anchor)i, ToolTip = Title((Anchor)i), Focusable = false };
            int idx = i;
            b.Checked += (_, _) => { if (!_updating) Set((Anchor)idx, true); };
            b.Unchecked += (_, _) => { if (!_updating) Set((Anchor)idx, true); };
            _buttons[i] = b;
            Children.Add(b);
        }
    }

    public Anchor Value { get; private set; } = Anchor.BottomRight;

    public void Set(Anchor a, bool raise = false)
    {
        _updating = true;
        Value = a;
        for (int i = 0; i < 9; i++) _buttons[i].IsChecked = i == (int)a;
        _updating = false;
        if (raise) AnchorChanged?.Invoke(a);
    }

    public static string Title(Anchor a) => a switch
    {
        Anchor.TopLeft => "Слева вверху", Anchor.TopCenter => "Сверху по центру", Anchor.TopRight => "Справа вверху",
        Anchor.MiddleLeft => "Слева по центру", Anchor.Center => "По центру", Anchor.MiddleRight => "Справа по центру",
        Anchor.BottomLeft => "Слева внизу", Anchor.BottomCenter => "Снизу по центру", _ => "Справа внизу",
    };
}
