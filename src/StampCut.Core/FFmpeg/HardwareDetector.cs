using StampCut.Core.Models;

namespace StampCut.Core.FFmpeg;

public sealed class HardwareCapabilities
{
    public string FfmpegVersion { get; init; } = "";
    public List<HardwareEncoder> Encoders { get; init; } = new();
    public List<string> HwAccels { get; init; } = new();
    public bool HasFilter_Zscale { get; init; }
    public bool HasFilter_Tonemap { get; init; }

    /// <summary>Видеокарта NVIDIA и версия драйвера (из nvidia-smi), если есть.</summary>
    public string GpuName { get; init; } = "";
    public string NvidiaDriver { get; init; } = "";
    /// <summary>Почему NVENC не заработал (последние строки ffmpeg), пусто — заработал или кодера нет.</summary>
    public string NvencFailure { get; init; } = "";
    /// <summary>Минимальный драйвер NVIDIA, который требует эта сборка ffmpeg для NVENC (из сообщения об ошибке).</summary>
    public string NvencRequiredDriver { get; init; } = "";

    /// <summary>Человеческая рекомендация, что сделать, чтобы включить GPU-кодирование. Пусто — всё в порядке.</summary>
    public string Recommendation
    {
        get
        {
            if (Encoders.Contains(HardwareEncoder.Nvenc) || string.IsNullOrEmpty(NvencFailure)) return "";
            bool driverIssue = NvencFailure.Contains("nvenc API version", StringComparison.OrdinalIgnoreCase) ||
                               NvencFailure.Contains("driver", StringComparison.OrdinalIgnoreCase);
            var gpu = string.IsNullOrEmpty(GpuName) ? "видеокарта NVIDIA" : GpuName;
            var have = string.IsNullOrEmpty(NvidiaDriver) ? "" : $" (установлен драйвер {NvidiaDriver})";
            if (driverIssue)
                return $"{gpu}{have}: кодер NVENC не запускается, нужен драйвер NVIDIA {(string.IsNullOrEmpty(NvencRequiredDriver) ? "посвежее" : NvencRequiredDriver + " или новее")}. Обновите драйвер — экспорт станет в несколько раз быстрее.";
            if (!string.IsNullOrEmpty(GpuName) || !string.IsNullOrEmpty(NvidiaDriver))
                return $"{gpu}{have}: кодер NVENC не запускается ({NvencFailure}). Проверьте драйвер NVIDIA.";
            return "";
        }
    }

    /// <summary>Лучший доступный аппаратный декодер для этой ОС.</summary>
    public string PreferredHwAccel =>
        OperatingSystem.IsWindows()
            ? (HwAccels.Contains("d3d11va") ? "d3d11va" : HwAccels.Contains("dxva2") ? "dxva2" : "")
            : (HwAccels.Contains("vaapi") ? "vaapi" : "");

    public HardwareEncoder Resolve(HardwareEncoder requested)
    {
        if (requested != HardwareEncoder.Auto)
            return requested == HardwareEncoder.None || Encoders.Contains(requested) ? requested : HardwareEncoder.None;
        // Media Foundation — последним из аппаратных: на машинах без NVENC/QSV/AMF это обычно всё равно GPU-кодер драйвера
        foreach (var e in new[] { HardwareEncoder.Nvenc, HardwareEncoder.Qsv, HardwareEncoder.Amf, HardwareEncoder.MediaFoundation })
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
        string nvencFailure = "", nvencRequired = "";
        foreach (var e in listed)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, why) = await TestEncoderAsync(EncoderName(e), ct).ConfigureAwait(false);
            log?.Invoke($"{EncoderName(e)}: {(ok ? "работает" : "недоступен — " + why)}");
            if (ok) working.Add(e);
            else if (e == HardwareEncoder.Nvenc)
            {
                nvencFailure = why;
                // ffmpeg пишет: "Minimum required Nvidia driver for nvenc is 570.0 or newer"
                var m = System.Text.RegularExpressions.Regex.Match(why, @"driver for nvenc is (\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) nvencRequired = m.Groups[1].Value;
            }
        }
        var (gpuName, driver) = await NvidiaInfoAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(driver)) log?.Invoke($"NVIDIA: {gpuName}, драйвер {driver}");

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
            GpuName = gpuName, NvidiaDriver = driver, NvencFailure = nvencFailure, NvencRequiredDriver = nvencRequired,
        };
    }

    /// <summary>Имя видеокарты и версия драйвера NVIDIA через nvidia-smi (ставится вместе с драйвером). Нет NVIDIA — пустые строки.</summary>
    public static async Task<(string Gpu, string Driver)> NvidiaInfoAsync(CancellationToken ct)
    {
        var candidates = new List<string> { "nvidia-smi" };
        if (OperatingSystem.IsWindows())
        {
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            candidates.Insert(0, Path.Combine(sysRoot, "System32", "nvidia-smi.exe"));
            var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            candidates.Add(Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        }
        foreach (var exe in candidates)
        {
            if (Path.IsPathRooted(exe) && !File.Exists(exe)) continue;
            try
            {
                var r = await ProcessRunner.RunAsync(exe, new[] { "--query-gpu=name,driver_version", "--format=csv,noheader" }, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (!r.Success) continue;
                var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                if (line is null) continue;
                var parts = line.Split(',', 2);
                return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* нет nvidia-smi — значит, нет NVIDIA или старый драйвер */ }
        }
        return ("", "");
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

    private async Task<(bool Ok, string Why)> TestEncoderAsync(string encoder, CancellationToken ct)
    {
        // кадр как в реальной работе: 1280x720 yuv420p, чтобы не упереться в минимальные размеры кодера
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "0.3",
            "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"
        };
        try
        {
            var r = await ProcessRunner.RunAsync(_paths.Ffmpeg, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return (r.Success, r.Success ? "" : MediaProbe.Tail(r.StdErr, 2).Replace('\n', ' '));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
