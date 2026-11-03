using StampCut.Core.Models;

namespace StampCut.Core.FFmpeg;

public sealed class HardwareCapabilities
{
    public string FfmpegVersion { get; init; } = "";
    public List<HardwareEncoder> Encoders { get; init; } = new();
    public List<string> HwAccels { get; init; } = new();
    public bool HasFilter_Zscale { get; init; }
    public bool HasFilter_Tonemap { get; init; }

    /// <summary>Лучший доступный аппаратный декодер для этой ОС.</summary>
    public string PreferredHwAccel =>
        OperatingSystem.IsWindows()
            ? (HwAccels.Contains("d3d11va") ? "d3d11va" : HwAccels.Contains("dxva2") ? "dxva2" : "")
            : (HwAccels.Contains("vaapi") ? "vaapi" : "");

    public HardwareEncoder Resolve(HardwareEncoder requested)
    {
        if (requested != HardwareEncoder.Auto)
            return requested == HardwareEncoder.None || Encoders.Contains(requested) ? requested : HardwareEncoder.None;
        foreach (var e in new[] { HardwareEncoder.Nvenc, HardwareEncoder.Qsv, HardwareEncoder.Amf })
            if (Encoders.Contains(e)) return e;
        return HardwareEncoder.None;
    }
}

/// <summary>Определяет, какие аппаратные кодеры реально работают: не по списку ffmpeg, а тестовым кодированием.</summary>
public sealed class HardwareDetector
{
    private readonly FFmpegPaths _paths;
    public HardwareDetector(FFmpegPaths paths) => _paths = paths;

    public static string EncoderName(HardwareEncoder e) => e switch
    {
        HardwareEncoder.Nvenc => "h264_nvenc",
        HardwareEncoder.Qsv => "h264_qsv",
        HardwareEncoder.Amf => "h264_amf",
        HardwareEncoder.MediaFoundation => "h264_mf",
        _ => "libx264",
    };

    public static string DisplayName(HardwareEncoder e) => e switch
    {
        HardwareEncoder.Nvenc => "NVIDIA NVENC",
        HardwareEncoder.Qsv => "Intel Quick Sync",
        HardwareEncoder.Amf => "AMD AMF",
        HardwareEncoder.MediaFoundation => "Media Foundation",
        HardwareEncoder.Auto => "автоматически",
        _ => "CPU (libx264)",
    };

    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(_paths.Ffmpeg, new[] { "-hide_banner", "-version" }, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException("ffmpeg не запускается: " + MediaProbe.Tail(r.StdErr));
        return r.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    public async Task<HardwareCapabilities> DetectAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        var version = await VersionAsync(ct).ConfigureAwait(false);
        var enc = await ProcessRunner.RunAsync(_paths.Ffmpeg, new[] { "-hide_banner", "-encoders" }, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var hw = await ProcessRunner.RunAsync(_paths.Ffmpeg, new[] { "-hide_banner", "-hwaccels" }, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var filters = await ProcessRunner.RunAsync(_paths.Ffmpeg, new[] { "-hide_banner", "-filters" }, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var listed = new List<HardwareEncoder>();
        foreach (var e in new[] { HardwareEncoder.Nvenc, HardwareEncoder.Qsv, HardwareEncoder.Amf, HardwareEncoder.MediaFoundation })
            if (enc.StdOut.Contains(" " + EncoderName(e) + " ", StringComparison.Ordinal)) listed.Add(e);

        var working = new List<HardwareEncoder>();
        foreach (var e in listed)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await TestEncoderAsync(EncoderName(e), ct).ConfigureAwait(false);
            log?.Invoke($"{EncoderName(e)}: {(ok ? "работает" : "недоступен")}");
            if (ok) working.Add(e);
        }

        var listedAccels = hw.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Hardware", StringComparison.Ordinal)).ToList();
        // аппаратное декодирование проверяем реальным декодированием: в списке может быть vaapi/cuda без устройства
        var accels = new List<string>();
        var candidates = OperatingSystem.IsWindows() ? new[] { "d3d11va", "dxva2", "cuda" } : new[] { "vaapi", "cuda" };
        string? sample = null;
        try
        {
            foreach (var name in candidates.Where(listedAccels.Contains))
            {
                ct.ThrowIfCancellationRequested();
                sample ??= await MakeSampleAsync(ct).ConfigureAwait(false);
                if (sample is null) break;
                var ok = await TestHwAccelAsync(name, sample, ct).ConfigureAwait(false);
                log?.Invoke($"hwaccel {name}: {(ok ? "работает" : "недоступен")}");
                if (ok) accels.Add(name);
            }
        }
        finally
        {
            if (sample is not null) try { File.Delete(sample); } catch { }
        }

        return new HardwareCapabilities
        {
            FfmpegVersion = version,
            Encoders = working,
            HwAccels = accels,
            HasFilter_Zscale = filters.StdOut.Contains(" zscale ", StringComparison.Ordinal),
            HasFilter_Tonemap = filters.StdOut.Contains(" tonemap ", StringComparison.Ordinal),
        };
    }

    private async Task<string?> MakeSampleAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "stampcut-hwtest-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-t", "0.5",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
        };
        try
        {
            var r = await ProcessRunner.RunAsync(_paths.Ffmpeg, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return r.Success && File.Exists(path) ? path : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<bool> TestHwAccelAsync(string hwaccel, string sample, CancellationToken ct)
    {
        var args = new[] { "-hide_banner", "-loglevel", "error", "-hwaccel", hwaccel, "-i", sample, "-f", "null", "-" };
        try
        {
            var r = await ProcessRunner.RunAsync(_paths.Ffmpeg, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            // ffmpeg может молча откатиться на CPU и вернуть 0 — это тоже «работает» для нас, но без ошибок в stderr
            return r.Success && !r.StdErr.Contains("Error", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<bool> TestEncoderAsync(string encoder, CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-t", "0.3",
            "-c:v", encoder, "-f", "null", "-"
        };
        try
        {
            var r = await ProcessRunner.RunAsync(_paths.Ffmpeg, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return r.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
