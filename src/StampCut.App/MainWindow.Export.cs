using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StampCut.App.Controls;
using StampCut.App.Services;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _exportCts;
    private ComboBox _exportPresetCombo = new();
    private readonly TextBlock _exportTarget = Ui.Label("", muted: true, size: 11);
    private readonly TextBlock _exportDetails = Ui.Label("", muted: true, size: 11);
    private Button _exportButton = new();
    private string? _lastExportedFile;

    private UIElement BuildExportBlock()
    {
        _exportPresetCombo = Ui.Combo(_exportPresets, p => p.Name, _preset, OnExportPresetSelected, width: 250);
        _exportButton = Ui.Button("Экспорт", () => Fire(ExportAsync()), primary: true, tooltip: "Экспортировать (Ctrl+E)", minWidth: 120);
        _exportButton.FontSize = 14; _exportButton.Padding = new Thickness(16, 8, 16, 8);
        var openFolder = Ui.SmallButton("Открыть папку", OpenLastFolder, "Открыть папку с результатом");
        var col = Ui.Column(
            Ui.Header("ЭКСПОРТ"),
            _exportPresetCombo,
            _exportTarget,
            _exportDetails,
            Ui.Row(_exportButton, openFolder));
        _exportTarget.Margin = new Thickness(0, 6, 0, 0);
        _exportDetails.Margin = new Thickness(0, 0, 0, 8);
        return Ui.Card(col);
    }

    private void OnExportPresetSelected(ExportPreset p)
    {
        _preset = p;
        _s.Settings.LastExportPresetId = p.Id;
        try { _s.Settings.Save(); } catch (Exception ex) { Logger.Warn("Настройки: " + ex.Message); }
        UpdateOutputGeometry();
        UpdateExportInfo();
        _timeline.SnapToKeyframes = p.StreamCopy;
        if (p.StreamCopy && _current is not null) Fire(LoadKeyframesAsync(_current));
        _timeline.InvalidateVisual();
    }

    private void RefreshPresetCombos()
    {
        var parent = _exportPresetCombo.Parent as Panel;
        if (parent is null) return;
        int idx = parent.Children.IndexOf(_exportPresetCombo);
        parent.Children.RemoveAt(idx);
        _exportPresetCombo = Ui.Combo(_exportPresets, p => p.Name, _preset, OnExportPresetSelected, width: 250);
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
        if (_clips.Count == 0) { _exportTarget.Text = "Добавьте ролик"; _exportDetails.Text = ""; _exportButton.IsEnabled = false; return; }
        _exportButton.IsEnabled = _exportCts is null;
        var path = BuildOutputPath(_clips[0]);
        _exportTarget.Text = $"→ {Path.GetFileName(path)}\n{Path.GetDirectoryName(path)}";
        try
        {
            var job = new ExportJob { Clips = _clips, Preset = _preset, OutputPath = path, Encoder = HardwareEncoder.None };
            if (_preset.StreamCopy)
                _exportDetails.Text = "Без перекодирования: потоки копируются как есть, секунды.";
            else
            {
                var (w, h) = FFmpegCommandBuilder.ComputeOutputSize(job);
                int fps = FFmpegCommandBuilder.ComputeOutputFps(job);
                double total = job.TotalDurationSec;
                _exportDetails.Text = $"{w}×{h} · {(fps > 0 ? fps + " к/с" : "частота исходника")} · {TimeFormat.Format(total, false)}" + (_clips.Count > 1 ? $" · {_clips.Count} ролика(ов)" : "");
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
            _progress.Visibility = Visibility.Collapsed; _progressText.Text = ""; _cancelButton.Visibility = Visibility.Collapsed;
            _exportButton.IsEnabled = true;
            UpdateExportInfo();
        }
    }

    private async Task ExportCoreAsync(CancellationToken ct)
    {

        // оверлеи с файлами
        var resolved = new List<ResolvedOverlay>();
        foreach (var item in _overlays)
        {
            if (item.ResolvedPath is null || item.PngWidth == 0) continue;
            resolved.Add(new ResolvedOverlay { Settings = item.Settings, Path = item.ResolvedPath, Width = item.PngWidth, Height = item.PngHeight });
        }
        if (_overlays.Count > resolved.Count) ShowInfo("У части оверлеев не выбран PNG-файл — они пропущены.", InfoKind.Warning);

        _statusInfo.Text = "Проверяю GPU…";
        await _s.HardwareReady;
        var encoder = _preset.StreamCopy ? HardwareEncoder.None : _s.ResolveEncoder(_preset);
        var outputPath = BuildOutputPath(_clips[0]);
        var job = new ExportJob
        {
            Clips = _clips.ToList(), Overlays = resolved, Preset = _preset, OutputPath = outputPath,
            Encoder = encoder, HwAccel = _preset.StreamCopy ? "" : _s.ResolveHwAccel(),
        };
        var errors = FFmpegCommandBuilder.Validate(job);
        if (errors.Count > 0) { ShowInfo(string.Join(" ", errors), InfoKind.Error); _statusInfo.Text = ""; return; }

        _progress.Visibility = Visibility.Visible; _progress.Value = 0;
        _cancelButton.Visibility = Visibility.Visible;
        _statusInfo.Text = $"Экспорт: {Path.GetFileName(outputPath)} · {HardwareDetector.DisplayName(encoder)}";
        var sw = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            _progress.Value = p.Fraction * 100;
            _progressText.Text = $"{p.Fraction * 100:0}%" + (p.Remaining is { } r ? $" · осталось {TimeFormat.Format(r.TotalSeconds, false)}" : "") + (p.Speed > 0 ? $" · {p.Speed:0.0}×" : "");
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
            _s.Settings.Save();
            _statusInfo.Text = $"Готово за {TimeFormat.Format(outcome.Elapsed.TotalSeconds, false)}: {Path.GetFileName(outcome.OutputPath)}";
            ShowInfo($"Готово: {Path.GetFileName(outcome.OutputPath)} ({TimeFormat.FormatSize(new FileInfo(outcome.OutputPath).Length)}, {TimeFormat.Format(outcome.Elapsed.TotalSeconds, false)})", InfoKind.Success,
                ("Показать в проводнике", () => RevealInExplorer(outcome.OutputPath)),
                ("Открыть папку", OpenLastFolder),
                ("Скопировать команду ffmpeg", () => { try { Clipboard.SetText(outcome.CommandLine); } catch { } }));
            if (_s.Settings.OpenFolderAfterExport) RevealInExplorer(outcome.OutputPath);
        }
        else
        {
            _statusInfo.Text = outcome.Message.Split('\n')[0];
            Logger.Error("Экспорт не удался: " + outcome.Message + "\n" + outcome.CommandLine);
            ShowInfo(outcome.Message.Replace('\n', ' '), outcome.Message.Contains("отмен", StringComparison.OrdinalIgnoreCase) ? InfoKind.Warning : InfoKind.Error,
                ("Скопировать команду ffmpeg", () => { try { Clipboard.SetText(outcome.CommandLine); } catch { } }),
                ("Открыть журнал", () => TryStart(Logger.CurrentFile)));
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
