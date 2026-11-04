using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App.Services;

/// <summary>Создаёт прокси для просмотра тех файлов, которые MediaElement не играет (HEVC, HDR, поворот, не-MP4).</summary>
public sealed class ProxyService
{
    private readonly AppServices _s;
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Одна сборка прокси на файл: ждать могут несколько вызовов, отмена — когда отказались все.</summary>
    private sealed class InFlight
    {
        public Task<string> Task = null!;
        public readonly CancellationTokenSource Cts = new();
        public readonly MultiProgress Progress = new();
        public int Waiters;
    }

    private sealed class MultiProgress : IProgress<ExportProgress>
    {
        private readonly List<IProgress<ExportProgress>> _targets = new();
        public void Add(IProgress<ExportProgress> p) { lock (_targets) _targets.Add(p); }
        public void Report(ExportProgress value) { IProgress<ExportProgress>[] copy; lock (_targets) copy = _targets.ToArray(); foreach (var t in copy) t.Report(value); }
    }

    public ProxyService(AppServices s) => _s = s;

    public string? ExistingProxy(MediaInfo media)
    {
        var path = ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public Task<string> EnsureProxyAsync(MediaInfo media, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var path = ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir);
        InFlight f;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(path, out f!))
            {
                f = new InFlight();
                var local = f;
                f.Task = BuildAsync(media, path, local.Progress, local.Cts.Token);
                _inFlight[path] = f;
                _ = f.Task.ContinueWith(_ => { lock (_inFlight) if (_inFlight.TryGetValue(path, out var cur) && ReferenceEquals(cur, local)) _inFlight.Remove(path); });
            }
            f.Waiters++;
            if (progress is not null) f.Progress.Add(progress);
        }
        return WaitAsync(f, ct);
    }

    private async Task<string> WaitAsync(InFlight f, CancellationToken ct)
    {
        using var reg = ct.Register(() =>
        {
            lock (_inFlight) { if (--f.Waiters <= 0) f.Cts.Cancel(); }
        });
        try
        {
            return await f.Task.WaitAsync(ct);
        }
        finally
        {
            if (!ct.IsCancellationRequested) lock (_inFlight) f.Waiters--;
        }
    }

    private async Task<string> BuildAsync(MediaInfo media, string path, IProgress<ExportProgress> progress, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        await _s.HardwareReady;
        var encoder = _s.Hardware?.Resolve(HardwareEncoder.Auto) ?? HardwareEncoder.None;
        var tmp = path + ".part.mp4";
        var args = ProxyBuilder.BuildArgs(media, tmp, _s.Settings.ProxyLongSide, encoder, _s.ResolveHwAccel());
        Logger.Info("Прокси: " + CommandLine.Render(_s.Ffmpeg.Ffmpeg, args));
        var runner = new FFmpegRunner(_s.Ffmpeg);
        var r = await runner.RunAsync(args, media.DurationSec, progress, ct);
        if (!r.Success && encoder != HardwareEncoder.None)
        {
            Logger.Warn("Прокси через GPU не удался, пробую CPU: " + MediaProbe.Tail(r.StdErr, 3));
            args = ProxyBuilder.BuildArgs(media, tmp, _s.Settings.ProxyLongSide, HardwareEncoder.None, "");
            r = await runner.RunAsync(args, media.DurationSec, progress, ct);
        }
        if (!r.Success)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("Не удалось подготовить просмотр: " + MediaProbe.Tail(r.StdErr, 3));
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Кадр для мгновенного показа, пока готовится прокси или открывается плеер.</summary>
    public async Task<string?> PosterAsync(MediaInfo media, double atSec, CancellationToken ct, VideoFilters? filters = null)
    {
        var path = Path.Combine(AppPaths.TempDir, Path.GetFileNameWithoutExtension(ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir)) + (filters is null ? "_poster.jpg" : "_filtered.jpg"));
        var vf = media.IsHdr
            ? "scale=-2:720,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
            : "scale=-2:720,format=yuv420p";
        if (filters is not null && !filters.IsDefault) vf += "," + string.Join(",", filters.ToFfmpeg());
        var args = new[] { "-hide_banner", "-y", "-loglevel", "error", "-ss", TimeFormat.Num(atSec), "-i", media.Path, "-frames:v", "1", "-vf", vf, "-q:v", "4", path };
        var r = await ProcessRunner.RunAsync(_s.Ffmpeg.Ffmpeg, args, ct, TimeSpan.FromSeconds(30));
        return r.Success && File.Exists(path) ? path : null;
    }
}
