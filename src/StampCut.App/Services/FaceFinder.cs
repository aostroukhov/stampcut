using System.Runtime.InteropServices.WindowsRuntime;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace StampCut.App.Services;

/// <summary>
/// Поиск лиц встроенным детектором Windows (Windows.Media.FaceAnalysis): ffmpeg выдаёт уменьшенные серые кадры,
/// детектор отмечает лица, одинаковые лица на соседних кадрах связываются в дорожки, из дорожек получаются области ретуши с движением.
/// </summary>
public sealed class FaceFinder
{
    /// <summary>Лицо на кадре: время (с, по ролику) и прямоугольник в долях кадра.</summary>
    public sealed record Found(double TimeSec, double X, double Y, double W, double H)
    {
        public double Cx => X + W / 2;
        public double Cy => Y + H / 2;
    }

    public const double DefaultStepSec = 0.5;
    private const int FrameWidth = 512;

    private readonly AppServices _s;
    public FaceFinder(AppServices s) => _s = s;

    public static bool Supported
    {
        get { try { return FaceDetector.IsSupported; } catch { return false; } }
    }

    /// <summary>Кадры с fromSec по toSec с шагом stepSec (stepSec ≤ 0 — один кадр в fromSec). Прогресс — доля обработанных кадров.</summary>
    public async Task<List<Found>> DetectAsync(MediaInfo media, double fromSec, double toSec, double stepSec, IProgress<double>? progress, CancellationToken ct)
    {
        int w = Math.Min(FrameWidth, Math.Max(32, media.Width)); w -= w % 2;
        int h = Math.Max(32, (int)Math.Round((double)w * media.Height / Math.Max(1, media.Width))); h -= h % 2;
        bool single = stepSec <= 0 || media.IsImage;

        var formats = FaceDetector.GetSupportedBitmapPixelFormats();
        BitmapPixelFormat fmt = formats.Contains(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8
            : formats.Contains(BitmapPixelFormat.Nv12) ? BitmapPixelFormat.Nv12
            : throw new NotSupportedException("FaceDetector: нет подходящего формата кадра");
        string pixFmt = fmt == BitmapPixelFormat.Gray8 ? "gray" : "nv12";
        int frameBytes = fmt == BitmapPixelFormat.Gray8 ? w * h : w * h * 3 / 2;

        var raw = Path.Combine(AppPaths.TempDir, $"faces-{Guid.NewGuid():N}.raw");
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            var hw = _s.ResolveHwAccel();
            if (hw != "" && !media.IsImage) args.AddRange(new[] { "-hwaccel", hw });
            if (!media.IsImage)
            {
                if (fromSec > 0.01) args.AddRange(new[] { "-ss", TimeFormat.Num(fromSec) });
                if (!single && toSec < media.DurationSec - 0.01) args.AddRange(new[] { "-to", TimeFormat.Num(toSec) });
            }
            args.AddRange(new[] { "-i", media.Path });
            string vf = (single ? "" : $"fps={TimeFormat.Num(1 / stepSec)},") + $"scale={w}:{h}";
            if (single) args.AddRange(new[] { "-frames:v", "1" });
            args.AddRange(new[] { "-vf", vf, "-pix_fmt", pixFmt, "-f", "rawvideo", raw });
            var r = await ProcessRunner.RunAsync(_s.Ffmpeg.Ffmpeg, args, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            if (!r.Success || !File.Exists(raw)) throw new InvalidOperationException(Loc.T("Не удалось извлечь кадры:") + " " + MediaProbe.Tail(r.StdErr, 3));

            var detector = await FaceDetector.CreateAsync();
            var found = new List<Found>();
            long total = new FileInfo(raw).Length;
            int frames = (int)(total / frameBytes);
            var buf = new byte[frameBytes];
            await using var fs = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            for (int k = 0; k < frames; k++)
            {
                ct.ThrowIfCancellationRequested();
                await fs.ReadExactlyAsync(buf, 0, frameBytes, ct).ConfigureAwait(false);
                using var bmp = new SoftwareBitmap(fmt, w, h, BitmapAlphaMode.Ignore);
                bmp.CopyFromBuffer(buf.AsBuffer());
                var faces = await detector.DetectFacesAsync(bmp);
                double t = single ? fromSec : fromSec + k * stepSec;
                foreach (var f in faces)
                {
                    var b = f.FaceBox;
                    found.Add(new Found(t, b.X / (double)w, b.Y / (double)h, b.Width / (double)w, b.Height / (double)h));
                }
                progress?.Report((k + 1) / (double)frames);
            }
            return found;
        }
        finally
        {
            try { if (File.Exists(raw)) File.Delete(raw); } catch { }
        }
    }

    /// <summary>
    /// Лица → области ретуши. Один кадр: неподвижные области на весь ролик. Проход по ролику: дорожки лиц (сопоставление по близости
    /// центров на соседних кадрах, допускается пропуск до 3 кадров) → область с ключевыми точками движения и временем показа.
    /// </summary>
    public static List<RedactRegion> ToRegions(IReadOnlyList<Found> found, double stepSec, double frameAspect, double? rangeFrom, double? rangeTo)
    {
        var regions = new List<RedactRegion>();
        const double grow = 1.45; // лицо детектора — от бровей до подбородка; берём с запасом на волосы и шею
        if (stepSec <= 0)
        {
            foreach (var f in found.OrderBy(f => f.X))
            {
                var (x, y, w, h) = Box(f.Cx, f.Cy, f.W * grow, f.H * grow);
                regions.Add(new RedactRegion { Mode = RedactMode.Blur, X = x, Y = y, W = w, H = h });
            }
            return regions;
        }

        var tracks = new List<List<Found>>();
        foreach (var group in found.GroupBy(f => f.TimeSec).OrderBy(g => g.Key))
        {
            double t = group.Key;
            var candidates = tracks.Where(tr => t - tr[^1].TimeSec <= stepSec * 3.5 + 1e-6 && tr[^1].TimeSec < t - 1e-6).ToList();
            foreach (var f in group.OrderByDescending(f => f.W * f.H))
            {
                List<Found>? best = null; double bestD = double.MaxValue;
                foreach (var tr in candidates)
                {
                    var last = tr[^1];
                    double d = Math.Sqrt(Math.Pow((f.Cx - last.Cx), 2) + Math.Pow((f.Cy - last.Cy) / frameAspect, 2));
                    double limit = Math.Max(f.W, last.W) * 1.2;
                    if (d < limit && d < bestD) { best = tr; bestD = d; }
                }
                if (best is null) { best = new List<Found>(); tracks.Add(best); }
                else candidates.Remove(best);
                best.Add(f);
            }
        }

        foreach (var tr in tracks.OrderBy(tr => tr[0].TimeSec))
        {
            double w = tr.Max(f => f.W) * grow, h = tr.Max(f => f.H) * grow;
            w = Math.Clamp(w, 0.02, 1); h = Math.Clamp(h, 0.02, 1);
            var first = tr[0];
            var (x0, y0, bw, bh) = Box(first.Cx, first.Cy, w, h);
            var region = new RedactRegion { Mode = RedactMode.Blur, X = x0, Y = y0, W = bw, H = bh };
            double from = Math.Max(rangeFrom ?? 0, first.TimeSec - stepSec / 2), to = tr[^1].TimeSec + stepSec / 2;
            if (rangeTo is { } rt) to = Math.Min(rt, to);
            region.FromSec = Math.Round(from, 2); region.ToSec = Math.Round(Math.Max(from + stepSec, to), 2);
            if (tr.Count > 1)
                foreach (var f in tr)
                {
                    var (x, y, _, _) = Box(f.Cx, f.Cy, bw, bh);
                    Motion.Upsert(region.Motion, Math.Round(f.TimeSec, 2), x, y);
                }
            regions.Add(region);
        }
        return regions.Take(30).ToList();
    }

    private static (double X, double Y, double W, double H) Box(double cx, double cy, double w, double h)
    {
        w = Math.Clamp(w, 0.02, 1); h = Math.Clamp(h, 0.02, 1);
        double x = Math.Clamp(cx - w / 2, 0, 1 - w), y = Math.Clamp(cy - h / 2, 0, 1 - h);
        return (x, y, w, h);
    }
}
