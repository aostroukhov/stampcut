using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App;

/// <summary>Настройки: всё техническое спрятано сюда, чтобы главное окно оставалось простым.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppServices _s;
    public bool RedetectRequested { get; private set; }

    public SettingsWindow(AppServices s)
    {
        _s = s;
        Title = "Настройки StampCut"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var st = s.Settings;

        // экспорт
        var rbNext = new RadioButton { GroupName = "outloc", Content = "рядом с исходником", IsChecked = st.OutputLocation == OutputLocation.NextToSource, Margin = new Thickness(0, 2, 0, 2) };
        var rbLast = new RadioButton { GroupName = "outloc", Content = "в последнюю использованную папку", IsChecked = st.OutputLocation == OutputLocation.LastUsed, Margin = new Thickness(0, 2, 0, 2) };
        var rbFixed = new RadioButton { GroupName = "outloc", Content = "всегда в папку:", IsChecked = st.OutputLocation == OutputLocation.Fixed, Margin = new Thickness(0, 2, 0, 2) };
        var fixedDir = new TextBox { Text = st.OutputDir ?? "", Width = 330, Padding = new Thickness(4, 3, 4, 3) };
        var pattern = new TextBox { Text = st.FileNamePattern, Width = 200, Padding = new Thickness(4, 3, 4, 3) };
        var openAfter = Ui.Check("После экспорта показать файл в проводнике", st.OpenFolderAfterExport, _ => { });

        // GPU
        var encoders = new[] { HardwareEncoder.Auto, HardwareEncoder.None, HardwareEncoder.Nvenc, HardwareEncoder.Qsv, HardwareEncoder.Amf, HardwareEncoder.MediaFoundation };
        var encCombo = new ComboBox { Width = 220 };
        foreach (var e in encoders) encCombo.Items.Add(HardwareDetector.DisplayName(e));
        encCombo.SelectedIndex = Array.IndexOf(encoders, st.Encoder);
        var hwDecode = Ui.Check("Аппаратное декодирование (D3D11VA)", st.HardwareDecode, _ => { });
        var alwaysProxy = Ui.Check("Всегда готовить прокси для просмотра (если видео не играет)", st.AlwaysUseProxy, _ => { });
        var hwInfo = Ui.Label(s.Hardware is null ? "GPU ещё проверяется…" : $"Найдено: {(s.Hardware.Encoders.Count == 0 ? "только CPU" : string.Join(", ", s.Hardware.Encoders.Select(HardwareDetector.DisplayName)))}; декодирование: {(s.Hardware.PreferredHwAccel == "" ? "CPU" : s.Hardware.PreferredHwAccel)}", muted: true, size: 11);

        // библиотека и ffmpeg
        var libDir = new TextBox { Text = st.LibraryDir ?? "", Width = 330, Padding = new Thickness(4, 3, 4, 3) };
        var ffDir = new TextBox { Text = st.FfmpegDir ?? "", Width = 330, Padding = new Thickness(4, 3, 4, 3) };

        var ok = Ui.Button("Сохранить", () =>
        {
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
        var cancel = Ui.Button("Отмена", () => { DialogResult = false; Close(); }, minWidth: 90);
        cancel.IsCancel = true;

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    Ui.Header("КУДА СОХРАНЯТЬ"),
                    rbNext, rbLast, Ui.Row(rbFixed, fixedDir, Ui.SmallButton("…", () => PickFolder(fixedDir))),
                    Ui.Row(Ui.Label("Имя файла:", muted: true), pattern, Ui.Label("{name} {suffix} {date} {time}", muted: true, size: 11)),
                    openAfter,
                    Ui.Header("GPU"),
                    Ui.Row(Ui.Label("Кодирование:", muted: true), encCombo, Ui.SmallButton("Проверить заново", () => { RedetectRequested = true; hwInfo.Text = "Проверка запустится после сохранения."; })),
                    hwDecode, alwaysProxy, hwInfo,
                    Ui.Header("ОБЩАЯ БИБЛИОТЕКА РЕДАКЦИИ"),
                    Ui.Label("Папка (можно сетевую или в облаке) с подпапками overlays и presets — у всех сотрудников одинаковые оверлеи и пресеты.", muted: true, size: 11),
                    Ui.Row(libDir, Ui.SmallButton("…", () => PickFolder(libDir))),
                    Ui.Header("FFMPEG"),
                    Ui.Label($"Сейчас: {s.Ffmpeg.Ffmpeg}", muted: true, size: 11),
                    Ui.Row(ffDir, Ui.SmallButton("…", () => PickFolder(ffDir))),
                    Ui.Row(Ui.SmallButton("Открыть папку журналов", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true }); } catch { } }),
                           Ui.SmallButton("Открыть папку пресетов", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.PresetsDir) { UseShellExecute = true }); } catch { } })),
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
    }

    private void PickFolder(TextBox target)
    {
        var dlg = new OpenFolderDialog { Title = "Выбрать папку" };
        if (Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
        if (dlg.ShowDialog(this) == true) target.Text = dlg.FolderName;
    }
}
