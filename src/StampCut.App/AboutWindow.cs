using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using StampCut.App.Controls;
using StampCut.App.Services;
using static StampCut.App.Lang;

namespace StampCut.App;

/// <summary>О программе: версия, ссылки, окружение, обновления, отчёт о проблеме, клавиши.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(AppServices s, Func<Task> checkUpdates, Action reportProblem)
    {
        Title = T("О программе"); Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ApplyWindowTheme(this, solidBackground: true);
        try { if (Ui.AppIcon() is { } ico) Icon = ico.Frame; } catch { }

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icon is not null) head.Children.Add(new Image { Source = Icon, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) });
        head.Children.Add(Ui.Column(
            Ui.Label("StampCut " + MainWindow.AppVersion, size: 20, weight: FontWeights.SemiBold),
            Ui.Label(T("Быстрый видеоредактор и плеер для редакции: обрезка, склейка, оверлей, кроп, ретушь, экспорт для Telegram."), muted: true)));

        var links = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        links.Inlines.Add(Link(T("Релизы и загрузки"), CrashReporter.ReleasesUrl));
        links.Inlines.Add("   ·   ");
        links.Inlines.Add(Link(T("Исходный код"), CrashReporter.RepoUrl));
        links.Inlines.Add("   ·   ");
        links.Inlines.Add(Link(T("Сообщить о проблеме"), CrashReporter.IssuesUrl));

        var env = Ui.Label(
            $"{(s.Hardware is null ? "" : s.Hardware.FfmpegVersion + "\n")}" +
            $"{s.HardwareSummary()}" +
            (s.Hardware is { NvidiaDriver.Length: > 0 } hw ? $"\n{hw.GpuName}, {T("драйвер NVIDIA")} {hw.NvidiaDriver}" : "") +
            $"\n{T("Фильтры на видеокарте")}: {(ColorFilterEffect.Available ? T("да") : T("нет"))}", muted: true, size: Ui.FontCaption);
        env.Margin = new Thickness(0, 10, 0, 0);

        var status = Ui.Label("", muted: true, size: Ui.FontCaption);
        var updateButton = Ui.Button(T("Проверить обновления"), () => { });
        updateButton.Click += async (_, _) =>
        {
            updateButton.IsEnabled = false; status.Text = T("Проверяю…");
            try { await checkUpdates(); status.Text = ""; }
            catch (Exception ex) { status.Text = T("Не удалось проверить: ") + ex.Message; }
            finally { updateButton.IsEnabled = true; }
        };
        var buttons = Ui.Row(updateButton, Ui.Button(T("Сообщить о проблеме"), reportProblem),
            Ui.Button(T("Открыть папку журналов"), () => { try { Process.Start(new ProcessStartInfo(Core.AppPaths.LogDir) { UseShellExecute = true }); } catch { } }));
        buttons.Margin = new Thickness(0, 12, 0, 0);

        var keys = Ui.Label(T("Клавиши: Пробел — пуск/пауза · I, O — вход/выход · , . — кадр (Shift — 10 кадров) · [ ] — ключевые кадры · ←→ — 5 с (Shift 1 с, Ctrl 30 с) · ↑↓ — громкость · M — без звука · Ctrl+колесо — масштаб ленты, Ctrl+0 — весь ролик · Ctrl+Z/Y — отмена/повтор · Ctrl+S — проект · Ctrl+E — экспорт · Ctrl+V — вставить файлы · Delete — удалить область/интервал"), muted: true, size: Ui.FontCaption);
        keys.Margin = new Thickness(0, 12, 0, 0);

        var ok = Ui.Button("OK", Close, primary: true, minWidth: 90);
        ok.IsDefault = true; ok.IsCancel = true;
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { head, links, env, buttons, status, keys, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok } } },
        };
    }

    private static Hyperlink Link(string text, string url)
    {
        var h = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
        h.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); } catch { } e.Handled = true; };
        return h;
    }
}
