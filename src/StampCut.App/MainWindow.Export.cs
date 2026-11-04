using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using static StampCut.App.Lang;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _exportCts;
    private ComboBox _exportPresetCombo = new();
    private readonly TextBlock _exportTarget = Ui.Label("", muted: true, size: Ui.FontCaption);
    private readonly TextBlock _exportDetails = Ui.Label("", muted: true, size: Ui.FontCaption);
    private Button _exportButton = new();
    private string? _lastExportedFile;
    private bool _normalizeLoudness;
    private CheckBox _loudnessCheck = new();
    private double _transitionSec;
    private string _transition = "fade";
    private sealed record TransitionChoice(string Id, string Name);
    private static readonly TransitionChoice[] TransitionChoices =
    {
        new("", "Встык"), new("fade", "Затемнение"), new("dissolve", "Растворение"), new("fadeblack", "Через чёрное"), new("fadewhite", "Через белое"),
        new("wipeleft", "Шторка влево"), new("wiperight", "Шторка вправо"), new("slideleft", "Сдвиг влево"), new("slideright", "Сдвиг вправо"),
        new("smoothleft", "Плавный сдвиг"), new("circleopen", "Круг"), new("pixelize", "Пиксели"),
    };
    private ComboBox _transitionCombo = new();
    private TextBox _transitionBox = new();
    private StackPanel _transitionRow = new();

    /// <summary>Настройки и кнопка экспорта — содержимое панели «Экспорт» слева (без заголовка и карточки).</summary>
    private UIElement BuildExportControls()
    {
        _exportPresetCombo = Ui.Combo(_exportPresets, p => p.Name, _preset, OnExportPresetSelected, width: 0);
        _exportButton = Ui.Button(T("Экспорт"), () => Fire(ExportAsync()), primary: true, tooltip: T("Экспортировать (Ctrl+E)"), minWidth: 120);
        _exportButton.Height = 36; _exportButton.Padding = new Thickness(18, 0, 18, 0);
        var openFolder = Ui.SmallButton(T("Открыть папку"), OpenLastFolder, T("Открыть папку с результатом"));
        _loudnessCheck = Ui.Check(T("Выровнять громкость (−16 LUFS, как у Telegram/YouTube)"), false, v => { if (_syncing) return; _normalizeLoudness = v; ScheduleCommit(); });
        _transitionCombo = Ui.Combo(TransitionChoices, c => T(c.Name), TransitionChoices[0], c =>
        {
            if (_syncing) return;
            _transition = c.Id.Length == 0 ? "fade" : c.Id;
            _transitionSec = c.Id.Length == 0 ? 0 : (_transitionSec > 0 ? _transitionSec : 0.5);
            SetTransition(_transitionSec, _transition);
            UpdateExportInfo(); ScheduleCommit();
        }, width: 150);
        _transitionBox = Ui.NumberBox(0.5, v =>
        {
            if (_syncing) return;
            _transitionSec = Math.Clamp(v, 0, 5);
            if (_transitionSec > 0 && _transitionCombo.SelectedItem is TransitionChoice { Id: "" }) { _syncing = true; _transitionCombo.SelectedItem = TransitionChoices.First(c => c.Id == _transition); _syncing = false; }
            UpdateExportInfo(); ScheduleCommit();
        }, 48, "0.##");
        _transitionRow = Ui.Row(Ui.Label(T("Переходы"), muted: true), _transitionCombo, _transitionBox, Ui.Label(T("сек"), muted: true));
        _transitionRow.ToolTip = T("Плавный переход между соседними фрагментами; каждый переход укорачивает результат на свою длительность.");
        var col = Ui.Column(
            _exportPresetCombo,
            _loudnessCheck,
            _transitionRow,
            _exportTarget,
            _exportDetails,
            Ui.Row(_exportButton, openFolder),
            _exportProgress,
            Ui.Row(_exportStatus, _cancelButton));
        _exportTarget.Margin = new Thickness(0, 4, 0, 0);
        _exportDetails.Margin = new Thickness(0, 0, 0, 8);
        return col;
    }

    private void SetTransition(double sec, string kind)
    {
        _transitionSec = Math.Clamp(sec, 0, 5);
        _transition = ExportJob.Transitions.Contains(kind) ? kind : "fade";
        _syncing = true;
        try
        {
            _transitionCombo.SelectedItem = _transitionSec > 0 ? TransitionChoices.First(c => c.Id == _transition) : TransitionChoices[0];
            Ui.SetNumber(_transitionBox, _transitionSec > 0 ? _transitionSec : 0.5, "0.##");
            _transitionBox.IsEnabled = _transitionSec > 0;
        }
        finally { _syncing = false; }
    }

    private void OnExportPresetSelected(ExportPreset p)
    {
        _preset = p;
        _s.Settings.LastExportPresetId = p.Id;
        try { _s.Settings.Save(); } catch (Exception ex) { Logger.Warn("Настройки: " + ex.Message); }
        UpdateOutputGeometry();
        UpdateExportInfo();
        _timeline.SnapToKeyframes = p.StreamCopy;
        _timeline.InvalidateVisual();
        CommitNow();
    }

    private void RefreshPresetCombos()
    {
        var parent = _exportPresetCombo.Parent as Panel;
        if (parent is null) return;
        int idx = parent.Children.IndexOf(_exportPresetCombo);
        parent.Children.RemoveAt(idx);
        _exportPresetCombo = Ui.Combo(_exportPresets, p => p.Name, _preset, OnExportPresetSelected, width: 0);
        parent.Children.Insert(idx, _exportPresetCombo);
        RefreshOverlayPresetCombo();
        UpdateExportInfo();
    }

    private string? OutputDirFor(Clip first) => _s.Settings.OutputLocation switch
    {
        OutputLocation.Fixed when !string.IsNullOrWhiteSpace(_s.Settings.OutputDir) => _s.Settings.OutputDir,
        OutputLocation.LastUsed => _s.Settings.LastOutputDir ?? Path.GetDirectoryName(first.Media.Path),
        _ => Path.GetDirectoryName(first.Media.Path),
    };

    private string BuildOutputPath(Clip first)
    {
        var dir = OutputDirFor(first) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(first.Media.Path);
        var pattern = string.IsNullOrWhiteSpace(_s.Settings.FileNamePattern) ? "{name}{suffix}" : _s.Settings.FileNamePattern;
        var name = pattern.Replace("{name}", baseName).Replace("{suffix}", _preset.OutputSuffix ?? "")
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd")).Replace("{time}", DateTime.Now.ToString("HHmm"))
            .Replace("{n}", _clips.Count.ToString());
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return Exporter.UniquePath(Path.Combine(dir, name + ".mp4"));
    }

    private void UpdateExportInfo()
    {
        if (_clips.Count == 0) { _exportTarget.Text = T("Добавьте ролик"); _exportDetails.Text = ""; _exportButton.IsEnabled = false; UpdateEnabledState(); return; }
        _exportButton.IsEnabled = _exportCts is null;
        var path = BuildOutputPath(_clips[0]);
        _exportTarget.Text = $"→ {Path.GetFileName(path)}\n{Path.GetDirectoryName(path)}";
        try
        {
            var job = new ExportJob { Clips = _clips, Preset = _preset, OutputPath = path, Encoder = HardwareEncoder.None, TransitionSec = _transitionSec, Transition = _transition };
            if (_preset.StreamCopy)
                _exportDetails.Text = T("Без перекодирования: потоки копируются как есть, секунды.");
            else
            {
                var (w, h) = FFmpegCommandBuilder.ComputeOutputSize(job);
                int fps = FFmpegCommandBuilder.ComputeOutputFps(job);
                double total = job.TotalDurationSec;
                _exportDetails.Text = $"{w}×{h} · {(fps > 0 ? fps + " " + T("к/с") : T("частота исходника"))} · {TimeFormat.Format(total, false)}" + (_clips.Count > 1 ? $" · {_clips.Count} {T("ролика(ов)")}" : "")
                    + (job.EffectiveTransitionSec > 0 ? $" · {T("переходы")} {TimeFormat.Num(job.EffectiveTransitionSec)} {T("с")}" : "")
                    + (_clips.Any(c => c.Stabilize > 0) ? $" · {T("стабилизация")}" : "");
            }
        }
        catch (Exception ex) { _exportDetails.Text = ex.Message; }
    }

    private async Task ExportAsync()
    {
        if (_exportCts is not null || _clips.Count == 0) return;
        _exportCts = new CancellationTokenSource(); // защита от повторного запуска ставится до первого await
        _exportButton.IsEnabled = false;
        HideInfo();
        Pause();
        try
        {
            await ExportCoreAsync(_exportCts.Token);
        }
        finally
        {
            _exportCts = null;
            _exportProgress.Visibility = Visibility.Collapsed; _exportStatus.Text = ""; _cancelButton.Visibility = Visibility.Collapsed;
            _exportButton.IsEnabled = true;
            UpdateExportInfo();
        }
    }

    private async Task ExportCoreAsync(CancellationToken ct)
    {
        var tempPngs = new List<string>();
        try { await ExportInnerAsync(ct, tempPngs); }
        finally { foreach (var f in tempPngs) try { File.Delete(f); } catch { } }
    }

    private async Task ExportInnerAsync(CancellationToken ct, List<string> tempPngs)
    {
        var resolved = new List<ResolvedOverlay>();
        int outW = 0, outH = 0;
        if (!_preset.StreamCopy)
            try { (outW, outH) = FFmpegCommandBuilder.ComputeOutputSize(new ExportJob { Clips = _clips, Preset = _preset, OutputPath = "x", Encoder = HardwareEncoder.None }); } catch { }
        foreach (var item in _overlays)
        {
            if (item.Settings.Text is { } text)
            {
                // текстовая плашка: рисуется под размер выходного кадра во временный PNG
                if (outW == 0 || TextRenderer.Render(text, outW, outH) is not { } bmp) continue;
                var png = Path.Combine(AppPaths.TempDir, $"text-{item.Settings.Id}-{Guid.NewGuid():N}.png");
                TextRenderer.SavePng(bmp, png); tempPngs.Add(png);
                var s = item.Settings.Clone(); s.WidthUnit = SizeUnit.Pixels; s.Width = bmp.PixelWidth;
                resolved.Add(new ResolvedOverlay { Settings = s, Path = png, Width = bmp.PixelWidth, Height = bmp.PixelHeight });
                continue;
            }
            if (item.ResolvedPath is null || item.PngWidth == 0) continue;
            resolved.Add(new ResolvedOverlay { Settings = item.Settings, Path = item.ResolvedPath, Width = item.PngWidth, Height = item.PngHeight });
        }
        if (_overlays.Count > resolved.Count && !_preset.StreamCopy) ShowInfo(T("У части оверлеев не выбран PNG-файл — они пропущены."), InfoKind.Warning);

        Autosave("export");
        if (_clips.Any(c => c.Censors.Any(NeedsLevel)))
        {
            _statusInfo.Text = T("Замеряю громкость дорожки…");
            while (_measuringCensors) await Task.Delay(100, ct);
            foreach (var c in _clips) await MeasureCensorLevelsAsync(c, ct);
        }
        _statusInfo.Text = T("Проверяю GPU…");
        await _s.HardwareReady;
        var encoder = _preset.StreamCopy ? HardwareEncoder.None : _s.ResolveEncoder(_preset);
        var outputPath = BuildOutputPath(_clips[0]);
        var job = new ExportJob
        {
            Clips = _clips.ToList(), Overlays = resolved, Preset = _preset, OutputPath = outputPath,
            Encoder = encoder, HwAccel = _preset.StreamCopy ? "" : _s.ResolveHwAccel(encoder), NormalizeLoudness = _normalizeLoudness,
            TransitionSec = _transitionSec, Transition = _transition, TempDir = AppPaths.TempDir,
        };
        var errors = FFmpegCommandBuilder.Validate(job);
        if (errors.Count > 0) { ShowInfo(string.Join(" ", errors), InfoKind.Error); _statusInfo.Text = ""; return; }

        _exportProgress.Visibility = Visibility.Visible; _exportProgress.Value = 0;
        _cancelButton.Visibility = Visibility.Visible;
        _exportStatus.Text = $"{Path.GetFileName(outputPath)} · {HardwareDetector.DisplayName(encoder)}";
        _statusInfo.Text = $"{T("Экспорт: ")}{Path.GetFileName(outputPath)} · {HardwareDetector.DisplayName(encoder)}";
        var sw = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            _exportProgress.Value = p.Fraction * 100;
            _exportStatus.Text = $"{p.Fraction * 100:0}%" + (p.Remaining is { } r ? $" · {T("осталось")} {TimeFormat.Format(r.TotalSeconds, false)}" : "") + (p.Speed > 0 ? $" · {p.Speed:0.0}×" : "") + $" · {Path.GetFileName(outputPath)}";
        });
        ExportOutcome outcome;
        try
        {
            var exporter = new Exporter(_s.Ffmpeg);
            outcome = await exporter.ExportAsync(job, progress, ct, Logger.Info);
        }
        catch (Exception ex)
        {
            Logger.Error("Экспорт", ex);
            outcome = new ExportOutcome(false, null, ex.Message, sw.Elapsed, "");
        }

        if (outcome.Success && outcome.OutputPath is not null)
        {
            _lastExportedFile = outcome.OutputPath;
            AppSettings.Touch(_s.Settings.RecentOutputDirs, Path.GetDirectoryName(outcome.OutputPath));
            _s.Settings.ExportHistory.Insert(0, new ExportRecord
            {
                Path = outcome.OutputPath, At = DateTime.Now, DurationSec = job.TotalDurationSec, ElapsedSec = outcome.Elapsed.TotalSeconds,
                SizeBytes = new FileInfo(outcome.OutputPath).Length, Project = _projectPath,
            });
            if (_s.Settings.ExportHistory.Count > 50) _s.Settings.ExportHistory.RemoveRange(50, _s.Settings.ExportHistory.Count - 50);
            try { _s.Settings.Save(); } catch { }
            RefreshHistoryList();
            _statusInfo.Text = $"{T("Готово за")} {TimeFormat.Format(outcome.Elapsed.TotalSeconds, false)}: {Path.GetFileName(outcome.OutputPath)}";
            ShowInfo($"{T("Готово")}: {Path.GetFileName(outcome.OutputPath)} ({TimeFormat.FormatSize(new FileInfo(outcome.OutputPath).Length)}, {TimeFormat.Format(outcome.Elapsed.TotalSeconds, false)})", InfoKind.Success,
                (T("Показать в проводнике"), () => RevealInExplorer(outcome.OutputPath)),
                (T("Открыть папку"), OpenLastFolder),
                (T("Скопировать команду ffmpeg"), () => { try { Clipboard.SetText(outcome.CommandLine); } catch { } }));
            if (_s.Settings.OpenFolderAfterExport) RevealInExplorer(outcome.OutputPath);
        }
        else
        {
            _statusInfo.Text = outcome.Message.Split('\n')[0];
            Logger.Error("Экспорт не удался: " + outcome.Message + "\n" + outcome.CommandLine);
            bool cancelled = outcome.Message.Contains(T("Экспорт отменён"), StringComparison.OrdinalIgnoreCase);
            if (cancelled)
                ShowInfo(outcome.Message, InfoKind.Warning);
            else
                ShowInfo(outcome.Message.Replace('\n', ' '), InfoKind.Error,
                    (T("Сообщить о проблеме"), () => ReportProblem(T("Ошибка экспорта"), null, "```\n" + outcome.Message + "\n```\n\nКоманда:\n```\n" + outcome.CommandLine + "\n```")),
                    (T("Скопировать команду ffmpeg"), () => { try { Clipboard.SetText(outcome.CommandLine); } catch { } }),
                    (T("Открыть журнал"), () => TryStart(Logger.CurrentFile)));
        }
    }

    private void CancelExport() => _exportCts?.Cancel();

    private void OpenLastFolder()
    {
        var dir = _lastExportedFile is not null ? Path.GetDirectoryName(_lastExportedFile) : _clips.Count > 0 ? OutputDirFor(_clips[0]) : null;
        if (dir is not null && Directory.Exists(dir)) TryStart(dir);
    }

    private static void RevealInExplorer(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("explorer: " + ex.Message); }
    }

    private static void TryStart(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("open: " + ex.Message); }
    }
}
