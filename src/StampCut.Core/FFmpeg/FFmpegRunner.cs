using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StampCut.Core.FFmpeg;

public sealed record ExportProgress(double Fraction, double Speed, TimeSpan Elapsed, TimeSpan? Remaining, double OutTimeSec);

public sealed record RunResult(int ExitCode, string StdErr, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Запуск ffmpeg с разбором «-progress pipe:1», отменой и сбором stderr.</summary>
public sealed class FFmpegRunner
{
    private readonly FFmpegPaths _paths;
    public FFmpegRunner(FFmpegPaths paths) => _paths = paths;

    public async Task<RunResult> RunAsync(IReadOnlyList<string> args, double totalDurationSec, IProgress<ExportProgress>? progress, CancellationToken ct, Action<string>? log = null)
    {
        log?.Invoke(CommandLine.Render(_paths.Ffmpeg, args));
        using var p = new Process { StartInfo = ProcessRunner.CreateStartInfo(_paths.Ffmpeg, args) };
        var stderr = new StringBuilder();
        var sw = Stopwatch.StartNew();
        double outTime = 0, speed = 0;

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) { stderr.AppendLine(e.Data); }
            log?.Invoke("ffmpeg: " + e.Data);
        };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            var key = line[..eq];
            var val = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "out_time_us":
                    if (long.TryParse(val, out var us) && us > 0) outTime = us / 1_000_000.0;
                    break;
                case "out_time":
                    if (outTime <= 0 && TimeSpan.TryParse(val, CultureInfo.InvariantCulture, out var ts)) outTime = ts.TotalSeconds;
                    break;
                case "speed":
                    double.TryParse(val.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                    break;
                case "progress":
                    Report();
                    break;
            }
        };

        void Report()
        {
            if (progress is null) return;
            double frac = totalDurationSec > 0 ? Math.Clamp(outTime / totalDurationSec, 0, 1) : 0;
            TimeSpan? remaining = null;
            if (frac > 0.02 && sw.Elapsed.TotalSeconds > 1)
                remaining = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - frac) / frac);
            progress.Report(new ExportProgress(frac, speed, sw.Elapsed, remaining, outTime));
        }

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(p);
            try { await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
        // дождаться, пока дочитаются потоки
        p.WaitForExit();
        sw.Stop();
        string err;
        lock (stderr) { err = stderr.ToString(); }
        return new RunResult(p.ExitCode, err, sw.Elapsed);
    }
}
