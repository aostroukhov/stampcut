using System.Windows.Media.Imaging;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App.Services;

/// <summary>Полоска кадров для ленты: ffmpeg собирает N кадров в одну картинку (tile), кэш рядом с прокси.</summary>
public sealed class FilmstripService
{
    public const int Frames = 40;
    public const int FrameHeight = 40;
    private readonly AppServices _s;
    public FilmstripService(AppServices s) => _s = s;

    public async Task<BitmapSource?> GetAsync(MediaInfo media, CancellationToken ct)
    {
        if (!media.HasVideo || media.IsImage || media.DurationSec <= 0) return null;
        var path = Path.Combine(AppPaths.ProxyDir, Path.GetFileNameWithoutExtension(ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir)) + "_strip.jpg");
        if (!File.Exists(path))
        {
            // N кадров через равные промежутки; tile собирает их в ряд. Длинные ролики — только по ключевым кадрам (быстро, без полного декодирования),
            // короткие — полное декодирование, чтобы кадры легли точно по времени.
            double step = media.DurationSec / Frames;
            string tonemap = media.IsHdr ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p," : "";
            string selectVf = $"{tonemap}select='isnan(prev_selected_t)+gte(t-prev_selected_t\\,{TimeFormat.Num(step)})',scale=-2:{FrameHeight},tile={Frames}x1";
            string fpsVf = $"{tonemap}fps={TimeFormat.Num(Frames / media.DurationSec)},scale=-2:{FrameHeight},tile={Frames}x1";
            var hw = _s.ResolveHwAccel();
            var baseArgs = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            bool keyframesOnly = media.DurationSec >= 30;
            var args = new List<string>(baseArgs);
            if (keyframesOnly) args.AddRange(new[] { "-skip_frame", "nokey" });
            if (hw != "") args.AddRange(new[] { "-hwaccel", hw });
            args.AddRange(new[] { "-i", media.Path, "-vf", keyframesOnly ? selectVf : fpsVf, "-vsync", "vfr", "-frames:v", "1", "-q:v", "5", path });
            var r = await ProcessRunner.RunAsync(_s.Ffmpeg.Ffmpeg, args, ct, TimeSpan.FromMinutes(3));
            if (!r.Success || !File.Exists(path))
            {
                args = new List<string>(baseArgs);
                args.AddRange(new[] { "-i", media.Path, "-vf", fpsVf, "-frames:v", "1", "-q:v", "5", path });
                r = await ProcessRunner.RunAsync(_s.Ffmpeg.Ffmpeg, args, ct, TimeSpan.FromMinutes(5));
                if (!r.Success || !File.Exists(path)) return null;
            }
        }
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bmp.UriSource = Controls.Ui.FileUri(path); bmp.EndInit(); bmp.Freeze();
        return bmp;
    }
}
