using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App.Services;

/// <summary>Создаёт прокси для просмотра тех файлов, которые MediaElement не играет (HEVC, HDR, поворот, не-MP4).</summary>
public sealed class ProxyService
{
    private readonly AppServices _s;
    private readonly Dictionary<string, Task<string>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ProxyService(AppServices s) => _s = s;

    public string? ExistingProxy(MediaInfo media)
    {
        var path = ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public Task<string> EnsureProxyAsync(MediaInfo media, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var path = ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir);
        lock (_inFlight)
        {
            if (_inFlight.TryGetValue(path, out var running)) return running;
            var task = BuildAsync(media, path, progress, ct);
            _inFlight[path] = task;
            _ = task.ContinueWith(_ => { lock (_inFlight) _inFlight.Remove(path); });
            return task;
        }
    }

    private async Task<string> BuildAsync(MediaInfo media, string path, IProgress<ExportProgress>? progress, CancellationToken ct)
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
    public async Task<string?> PosterAsync(MediaInfo media, double atSec, CancellationToken ct)
    {
        var path = Path.Combine(AppPaths.TempDir, Path.GetFileNameWithoutExtension(ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir)) + "_poster.jpg");
        var vf = media.IsHdr
            ? "scale=-2:720,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
            : "scale=-2:720";
        var args = new[] { "-hide_banner", "-y", "-loglevel", "error", "-ss", TimeFormat.Num(atSec), "-i", media.Path, "-frames:v", "1", "-vf", vf, "-q:v", "4", path };
        var r = await ProcessRunner.RunAsync(_s.Ffmpeg.Ffmpeg, args, ct, TimeSpan.FromSeconds(30));
        return r.Success && File.Exists(path) ? path : null;
    }
}
