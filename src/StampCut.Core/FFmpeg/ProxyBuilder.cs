using System.Security.Cryptography;
using System.Text;
using StampCut.Core.Models;

namespace StampCut.Core.FFmpeg;

/// <summary>Лёгкая H.264-копия исходника для просмотра: поворот и HDR уже «запечены», ключевой кадр каждые полсекунды.</summary>
public static class ProxyBuilder
{
    public static string ProxyPath(MediaInfo media, string proxyDir)
    {
        var fi = new FileInfo(media.Path);
        var key = $"{media.Path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(proxyDir, hash + ".mp4");
    }

    public static List<string> BuildArgs(MediaInfo media, string proxyPath, int longSide, HardwareEncoder encoder, string hwAccel)
    {
        var a = new List<string> { "-hide_banner", "-y", "-nostats", "-loglevel", "error", "-progress", "pipe:1" };
        if (!string.IsNullOrEmpty(hwAccel)) a.AddRange(new[] { "-hwaccel", hwAccel });
        a.AddRange(new[] { "-i", media.Path });

        var vf = new List<string>();
        double k = Math.Min(1.0, longSide / (double)Math.Max(1, Math.Max(media.Width, media.Height)));
        int w = Clip.Even((int)Math.Round(media.Width * k)), h = Clip.Even((int)Math.Round(media.Height * k));
        if (k < 1) vf.Add($"scale={w}:{h}");
        if (media.IsHdr) vf.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        vf.Add("format=yuv420p");
        if (media.Fps > 30.5) vf.Add("fps=30");

        a.AddRange(new[] { "-vf", string.Join(",", vf), "-map", "0:v:0", "-map", "0:a?" });
        var preset = new ExportPreset { AutoQuality = false, Crf = 26, X264Preset = "ultrafast", Profile = "main", Level = "4.0" };
        a.AddRange(FFmpegCommandBuilder.EncoderArgs(encoder, preset));
        a.AddRange(new[] { "-g", "15", "-c:a", "aac", "-b:a", "96k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", proxyPath });
        return a;
    }
}
