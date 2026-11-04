using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using static StampCut.App.Lang;

namespace StampCut.App;

/// <summary>Настройки: всё техническое спрятано сюда, чтобы главное окно оставалось простым.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppServices _s;
    public bool RedetectRequested { get; private set; }

    public SettingsWindow(AppServices s)
    {
        _s = s;
        Title = T("Настройки StampCut"); Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ApplyWindowTheme(this);
        var st = s.Settings;

        // внешний вид
        var themes = new[] { ("system", T("Как в Windows")), ("light", T("Светлая")), ("dark", T("Тёмная")) };
        var themeCombo = new ComboBox { Width = 180, Height = Ui.ControlHeight };
        foreach (var (_, name) in themes) themeCombo.Items.Add(name);
        themeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(themes, t => t.Item1 == st.Theme));
        var langs = new[] { ("ru", "Русский"), ("en", "English") };
        var langCombo = new ComboBox { Width = 120, Height = Ui.ControlHeight };
        foreach (var (_, name) in langs) langCombo.Items.Add(name);
        langCombo.SelectedIndex = Math.Max(0, Array.FindIndex(langs, l => l.Item1 == st.Language));

        // экспорт
        var rbNext = new RadioButton { GroupName = "outloc", Content = T("рядом с исходником"), IsChecked = st.OutputLocation == OutputLocation.NextToSource, Margin = new Thickness(0, 2, 0, 2) };
        var rbLast = new RadioButton { GroupName = "outloc", Content = T("в последнюю использованную папку"), IsChecked = st.OutputLocation == OutputLocation.LastUsed, Margin = new Thickness(0, 2, 0, 2) };
        var rbFixed = new RadioButton { GroupName = "outloc", Content = T("всегда в папку:"), IsChecked = st.OutputLocation == OutputLocation.Fixed, Margin = new Thickness(0, 2, 0, 2) };
        var fixedDir = new TextBox { Text = st.OutputDir ?? "", Width = 330, Height = Ui.ControlHeight, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var pattern = new TextBox { Text = st.FileNamePattern, Width = 200, Height = Ui.ControlHeight, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var openAfter = Ui.Check(T("После экспорта показать файл в проводнике"), st.OpenFolderAfterExport, _ => { });

        // GPU
        var encoders = new[] { HardwareEncoder.Auto, HardwareEncoder.None, HardwareEncoder.Nvenc, HardwareEncoder.Qsv, HardwareEncoder.Amf, HardwareEncoder.MediaFoundation };
        var encCombo = new ComboBox { Width = 220, Height = Ui.ControlHeight };
        foreach (var e in encoders) encCombo.Items.Add(HardwareDetector.DisplayName(e));
        encCombo.SelectedIndex = Array.IndexOf(encoders, st.Encoder);
        var hwDecode = Ui.Check(T("Аппаратное декодирование (D3D11VA)"), st.HardwareDecode, _ => { });
        var alwaysProxy = Ui.Check(T("Всегда готовить прокси для просмотра (если видео не играет)"), st.AlwaysUseProxy, _ => { });
        var hwInfo = Ui.Label(s.Hardware is null ? T("GPU ещё проверяется…") : $"{T("Найдено: ")}{(s.Hardware.Encoders.Count == 0 ? T("только CPU") : string.Join(", ", s.Hardware.Encoders.Select(HardwareDetector.DisplayName)))}; декодирование: {(s.Hardware.PreferredHwAccel == "" ? "CPU" : s.Hardware.PreferredHwAccel)}"
            + (string.IsNullOrEmpty(s.Hardware.NvidiaDriver) ? "" : $"\n{s.Hardware.GpuName}, {T("драйвер NVIDIA")} {s.Hardware.NvidiaDriver}")
            + (string.IsNullOrEmpty(s.CachedRecommendation) ? "" : "\n⚠ " + s.CachedRecommendation), muted: true, size: 11);

        var libDir = new TextBox { Text = st.LibraryDir ?? "", Width = 330, Height = Ui.ControlHeight, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var ffDir = new TextBox { Text = st.FfmpegDir ?? "", Width = 330, Height = Ui.ControlHeight, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };

        var ok = Ui.Button(T("Сохранить"), () =>
        {
            st.Theme = themes[Math.Max(0, themeCombo.SelectedIndex)].Item1;
            st.Language = langs[Math.Max(0, langCombo.SelectedIndex)].Item1;
            st.OutputLocation = rbFixed.IsChecked == true ? OutputLocation.Fixed : rbLast.IsChecked == true ? OutputLocation.LastUsed : OutputLocation.NextToSource;
            st.OutputDir = string.IsNullOrWhiteSpace(fixedDir.Text) ? null : fixedDir.Text.Trim();
            st.FileNamePattern = string.IsNullOrWhiteSpace(pattern.Text) ? "{name}{suffix}" : pattern.Text.Trim();
            st.Encoder = encoders[Math.Max(0, encCombo.SelectedIndex)];
            st.OpenFolderAfterExport = openAfter.IsChecked == true;
            st.HardwareDecode = hwDecode.IsChecked == true;
            st.AlwaysUseProxy = alwaysProxy.IsChecked == true;
            st.LibraryDir = string.IsNullOrWhiteSpace(libDir.Text) ? null : libDir.Text.Trim();
            st.FfmpegDir = string.IsNullOrWhiteSpace(ffDir.Text) ? null : ffDir.Text.Trim();
            DialogResult = true; Close();
        }, primary: true, minWidth: 100);
        var cancel = Ui.Button(T("Отмена"), () => { DialogResult = false; Close(); }, minWidth: 90);
        cancel.IsCancel = true;

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    Ui.Header(T("ВНЕШНИЙ ВИД")),
                    Ui.Row(Ui.Label(T("Тема:"), muted: true), themeCombo, Ui.Label(T("Язык:"), muted: true), langCombo),
                    Gap(), Ui.Header(T("КУДА СОХРАНЯТЬ")),
                    rbNext, rbLast, Ui.Row(rbFixed, fixedDir, Ui.SmallButton("…", () => PickFolder(fixedDir))),
                    Ui.Row(Ui.Label(T("Имя файла:"), muted: true), pattern, Ui.Label("{name} {suffix} {date} {time}", muted: true, size: 11)),
                    openAfter,
                    Gap(), Ui.Header("GPU"),
                    Ui.Row(Ui.Label(T("Кодирование:"), muted: true), encCombo, Ui.SmallButton(T("Проверить заново"), () => { RedetectRequested = true; hwInfo.Text = T("Проверка запустится после сохранения."); })),
                    hwDecode, alwaysProxy, hwInfo,
                    Gap(), Ui.Header(T("ОБЩАЯ БИБЛИОТЕКА РЕДАКЦИИ")),
                    Ui.Label(T("Папка (можно сетевую или в облаке) с подпапками overlays и presets — у всех сотрудников одинаковые оверлеи и пресеты."), muted: true, size: 11),
                    Ui.Row(libDir, Ui.SmallButton("…", () => PickFolder(libDir))),
                    Gap(), Ui.Header("FFMPEG"),
                    Ui.Label($"{T("Сейчас: ")}{s.Ffmpeg.Ffmpeg}", muted: true, size: 11),
                    Ui.Row(ffDir, Ui.SmallButton("…", () => PickFolder(ffDir))),
                    Ui.Row(Ui.SmallButton(T("Открыть папку журналов"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true }); } catch { } }),
                           Ui.SmallButton(T("Открыть папку пресетов"), () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.PresetsDir) { UseShellExecute = true }); } catch { } })),
                    Ui.Label($"StampCut {MainWindow.AppVersion}", muted: true, size: 11),
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
    }

    private static FrameworkElement Gap() => new Border { Height = 10 };

    private void PickFolder(TextBox target)
    {
        var dlg = new OpenFolderDialog { Title = T("Выбрать папку") };
        if (Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
        if (dlg.ShowDialog(this) == true) target.Text = dlg.FolderName;
    }
}
