using StampCut.Core.Models;

namespace StampCut.Core.FFmpeg;

public sealed record ExportOutcome(bool Success, string? OutputPath, string Message, TimeSpan Elapsed, string CommandLine);

/// <summary>Полный цикл экспорта: временный файл, список для склейки, запуск, переименование, уборка, журнал.</summary>
public sealed class Exporter
{
    private readonly FFmpegPaths _paths;
    public Exporter(FFmpegPaths paths) => _paths = paths;

    /// <summary>Подбирает свободное имя: file.mp4 → file (2).mp4 → file (3).mp4.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Не удалось подобрать имя файла: " + path);
    }

    public static string DefaultOutputPath(Clip first, ExportPreset preset, string? outputDir)
    {
        var dir = outputDir ?? Path.GetDirectoryName(first.Media.Path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(first.Media.Path) + (preset.OutputSuffix ?? "") + ".mp4";
        return UniquePath(Path.Combine(dir, name));
    }

    public async Task<ExportOutcome> ExportAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct, Action<string>? log = null)
    {
        var finalPath = job.OutputPath;
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir ?? ".", Path.GetFileNameWithoutExtension(finalPath) + ".stampcut-tmp.mp4");

        var tempJob = new ExportJob
        {
            Clips = job.Clips, Overlays = job.Overlays, Preset = job.Preset,
            OutputPath = tempPath, Encoder = job.Encoder, HwAccel = job.HwAccel, NormalizeLoudness = job.NormalizeLoudness,
            TransitionSec = job.TransitionSec, Transition = job.Transition, TempDir = job.TempDir,
        };
        BuildResult build;
        try { build = FFmpegCommandBuilder.Build(tempJob); }
        catch (InvalidOperationException ex) { return new ExportOutcome(false, null, ex.Message, TimeSpan.Zero, ""); }

        var cmd = CommandLine.Render(_paths.Ffmpeg, build.Args);
        try
        {
            if (build.ConcatList is not null) await File.WriteAllTextAsync(build.ConcatListPath, build.ConcatList, ct).ConfigureAwait(false);
            var runner = new FFmpegRunner(_paths);
            // подготовительные проходы (анализ стабилизации): общий прогресс делится по длительности, анализ считаем вдвое дешевле кодирования
            double mainWeight = Math.Max(0.1, job.TotalDurationSec);
            double preWeight = build.PrePasses.Sum(x => Math.Max(0.1, x.DurationSec)) * 0.5;
            double totalWeight = mainWeight + preWeight, doneWeight = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            IProgress<ExportProgress> Scaled(double weight) => new Progress<ExportProgress>(pr =>
            {
                double frac = Math.Clamp((doneWeight + pr.Fraction * weight) / totalWeight, 0, 1);
                TimeSpan? remaining = frac > 0.02 ? TimeSpan.FromSeconds(started.Elapsed.TotalSeconds * (1 - frac) / frac) : null;
                progress?.Report(new ExportProgress(frac, pr.Speed, started.Elapsed, remaining, pr.OutTimeSec));
            });
            foreach (var pre in build.PrePasses)
            {
                if (!string.IsNullOrEmpty(job.TempDir)) Directory.CreateDirectory(job.TempDir);
                log?.Invoke(pre.Title);
                double w = Math.Max(0.1, pre.DurationSec) * 0.5;
                var preResult = await runner.RunAsync(pre.Args, pre.DurationSec, progress is null ? null : Scaled(w), ct, log).ConfigureAwait(false);
                if (!preResult.Success || !File.Exists(pre.OutputFile))
                {
                    TryDelete(tempPath);
                    return new ExportOutcome(false, null, Loc.T("Анализ стабилизации не удался:") + "\n" + MediaProbe.Tail(preResult.StdErr, 8), preResult.Elapsed, CommandLine.Render(_paths.Ffmpeg, pre.Args));
                }
                doneWeight += w;
            }
            var result = await runner.RunAsync(build.Args, job.TotalDurationSec, progress is null || build.PrePasses.Count == 0 ? progress : Scaled(mainWeight), ct, log).ConfigureAwait(false);
            if (!result.Success)
            {
                TryDelete(tempPath);
                return new ExportOutcome(false, null, Loc.T("ffmpeg завершился с ошибкой:") + "\n" + MediaProbe.Tail(result.StdErr, 8), result.Elapsed, cmd);
            }
            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                TryDelete(tempPath);
                return new ExportOutcome(false, null, Loc.T("ffmpeg не создал выходной файл."), result.Elapsed, cmd);
            }
            finalPath = UniquePath(finalPath);
            File.Move(tempPath, finalPath);
            progress?.Report(new ExportProgress(1, 0, result.Elapsed, TimeSpan.Zero, job.TotalDurationSec));
            return new ExportOutcome(true, finalPath, Loc.T("Готово"), result.Elapsed, cmd);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return new ExportOutcome(false, null, Loc.T("Экспорт отменён"), TimeSpan.Zero, cmd);
        }
        finally
        {
            if (build.ConcatList is not null) TryDelete(build.ConcatListPath);
            foreach (var pre in build.PrePasses) TryDelete(pre.OutputFile);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
