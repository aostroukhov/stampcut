using System.Windows;
using System.Windows.Controls;
using StampCut.App.Controls;
using StampCut.App.Services;
using static StampCut.App.Lang;

namespace StampCut.App;

/// <summary>Окно после необработанной ошибки: текст отчёта, «Скопировать», «Отправить на GitHub», «Продолжить».</summary>
public sealed class CrashDialog : Window
{
    public static void Show(string title, Exception ex, MainWindow? owner)
    {
        var report = CrashReporter.Build(title, ex, owner?.Services);
        var path = CrashReporter.Save(report);
        var w = new CrashDialog(title, ex, report, path);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        w.ShowDialog();
    }

    private CrashDialog(string title, Exception ex, string report, string path)
    {
        Title = "StampCut — " + title; Width = 640; Height = 520; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ApplyWindowTheme(this, solidBackground: true);
        var box = new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = Ui.FontCaption,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var send = Ui.Button(T("Отправить на GitHub"), () => { CrashReporter.OpenIssue($"[{MainWindow.AppVersion}] {title}: {ex.GetType().Name}", report); CrashReporter.MarkReported(path); Close(); }, primary: true);
        var copy = Ui.Button(T("Скопировать отчёт"), () => { try { Clipboard.SetText(report); } catch { } });
        var cont = Ui.Button(T("Продолжить работу"), () => { CrashReporter.MarkReported(path); Close(); });
        cont.IsCancel = true;
        var dock = new DockPanel { Margin = new Thickness(16) };
        var head = Ui.Label(T("Произошла ошибка. Отчёт сохранён; отправка откроет форму на GitHub с этим текстом — вы увидите, что уходит, и сможете дописать, что делали."), muted: true);
        head.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        var msg = Ui.Label(ex.Message, weight: FontWeights.SemiBold); msg.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(msg, Dock.Top); dock.Children.Add(msg);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { send, copy, cont } };
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        dock.Children.Add(box);
        Content = dock;
    }
}
