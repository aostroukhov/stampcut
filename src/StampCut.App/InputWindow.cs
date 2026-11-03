using System.Windows;
using System.Windows.Controls;
using StampCut.App.Controls;

namespace StampCut.App;

/// <summary>Маленький диалог ввода строки (имя пресета).</summary>
public sealed class InputWindow : Window
{
    private readonly TextBox _box = new() { Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
    public string Value => _box.Text.Trim();

    public InputWindow(string title, string prompt, string initial = "")
    {
        Title = title; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box.Text = initial;
        var ok = Ui.Button("OK", () => { DialogResult = true; Close(); }, primary: true, minWidth: 80);
        var cancel = Ui.Button("Отмена", () => { DialogResult = false; Close(); }, minWidth: 80);
        ok.IsDefault = true; cancel.IsCancel = true;
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children = { Ui.Label(prompt), _box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } } },
        };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var w = new InputWindow(title, prompt, initial) { Owner = owner };
        return w.ShowDialog() == true && w.Value.Length > 0 ? w.Value : null;
    }
}
