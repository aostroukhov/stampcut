using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.App.Services;

/// <summary>Огибающая звука для ленты: ffmpeg отдаёт моно 16 бит, мы берём пик на каждые 1/Rate секунды. Кэш на диске рядом с прокси.</summary>
public sealed class WaveformService
{
    public const int Rate = 100; // значений в секунду
    private const int SampleRate = 8000;
    private readonly AppServices _s;
    public WaveformService(AppServices s) => _s = s;

    public async Task<float[]?> GetAsync(MediaInfo media, CancellationToken ct)
    {
        if (!media.HasAudio || media.IsImage) return null;
        var cache = Path.Combine(AppPaths.ProxyDir, Path.GetFileNameWithoutExtension(ProxyBuilder.ProxyPath(media, AppPaths.ProxyDir)) + ".wave");
        try
        {
            if (File.Exists(cache))
            {
                var bytes = await File.ReadAllBytesAsync(cache, ct);
                var cached = new float[bytes.Length];
                for (int i = 0; i < bytes.Length; i++) cached[i] = bytes[i] / 255f;
                return cached;
            }
        }
        catch { }

        var args = new[] { "-hide_banner", "-loglevel", "error", "-i", media.Path, "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "s16le", "-" };
        var psi = ProcessRunner.CreateStartInfo(_s.Ffmpeg.Ffmpeg, args);
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        p.Start();
        p.BeginErrorReadLine();
        var peaks = new List<float>((int)(media.DurationSec * Rate) + 16);
        int perBin = SampleRate / Rate;
        var buf = new byte[perBin * 2];
        var stream = p.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(got, buf.Length - got), ct);
                    if (n == 0) break;
                    got += n;
                }
                if (got == 0) break;
                int peak = 0;
                for (int i = 0; i + 1 < got; i += 2)
                {
                    int v = Math.Abs((short)(buf[i] | (buf[i + 1] << 8)));
                    if (v > peak) peak = v;
                }
                peaks.Add(peak / 32768f);
                if (got < buf.Length) break;
            }
        }
        catch (OperationCanceledException) { ProcessRunner.TryKill(p); throw; }
        try { await p.WaitForExitAsync(ct); } catch { ProcessRunner.TryKill(p); }
        if (peaks.Count == 0) return null;
        // нормировка: тихие ролики тоже должны быть видны
        float max = peaks.Max();
        var result = peaks.ToArray();
        if (max > 0.01f) for (int i = 0; i < result.Length; i++) result[i] = Math.Min(1f, result[i] / max);
        try { await File.WriteAllBytesAsync(cache, result.Select(v => (byte)Math.Round(v * 255)).ToArray(), ct); } catch { }
        return result;
    }
}
