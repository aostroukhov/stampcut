using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using StampCut.Core.Presets;

namespace StampCut.App.Services;

/// <summary>Общие сервисы приложения: настройки, пути, ffmpeg, пресеты, автоопределение железа.</summary>
public sealed class AppServices
{
    public AppSettings Settings { get; }
    public FFmpegPaths Ffmpeg { get; private set; }
    public MediaProbe Probe { get; private set; }
    public PresetStore Presets { get; private set; }
    public HardwareCapabilities? Hardware { get; private set; }
    public Task HardwareReady { get; private set; } = Task.CompletedTask;

    public event Action? HardwareDetected;
    /// <summary>Поднимать при изменении логики проверки GPU, чтобы кэш в настройках пересчитался.</summary>
    private const int DetectorVersion = 2;
    /// <summary>Рекомендация по драйверу (из свежей проверки или из кэша).</summary>
    public string CachedRecommendation { get; private set; } = "";

    public AppServices(AppSettings? settings = null)
    {
        Settings = settings ?? AppSettings.Load();
        Ffmpeg = FFmpegPaths.Locate(Settings.FfmpegDir);
        Probe = new MediaProbe(Ffmpeg);
        Presets = new PresetStore(AppPaths.PresetsDir, Settings.LibraryDir);
        AppPaths.Cleanup(TimeSpan.FromDays(30), TimeSpan.FromDays(14));
    }

    public void ReloadAfterSettingsChange()
    {
        Ffmpeg = FFmpegPaths.Locate(Settings.FfmpegDir);
        Probe = new MediaProbe(Ffmpeg);
        Presets = new PresetStore(AppPaths.PresetsDir, Settings.LibraryDir);
        Settings.Save();
    }

    /// <summary>Автоопределение GPU в фоне. Результат кэшируется в настройках по версии ffmpeg.</summary>
    public void StartHardwareDetection(bool force = false)
    {
        HardwareReady = Task.Run(async () =>
        {
            try
            {
                var detector = new HardwareDetector(Ffmpeg);
                var version = await detector.VersionAsync();
                var cache = Settings.Hardware;
                if (!force && cache is not null && cache.DetectorVersion == DetectorVersion && cache.FfmpegVersion == version && (DateTime.UtcNow - cache.DetectedAtUtc) < TimeSpan.FromDays(30))
                {
                    Hardware = new HardwareCapabilities
                    {
                        FfmpegVersion = version, Encoders = cache.Encoders.ToList(),
                        HwAccels = string.IsNullOrEmpty(cache.HwAccel) ? new List<string>() : new List<string> { cache.HwAccel },
                        HasFilter_Tonemap = true, HasFilter_Zscale = true,
                        GpuName = cache.GpuName, NvidiaDriver = cache.NvidiaDriver,
                        NvencFailure = string.IsNullOrEmpty(cache.Recommendation) ? "" : "см. кэш",
                        EncodersWithoutHwDecode = cache.EncodersWithoutHwDecode.ToList(),
                    };
                    CachedRecommendation = cache.Recommendation;
                }
                else
                {
                    Hardware = await detector.DetectAsync(default, Logger.Info);
                    Settings.Hardware = new HardwareCache
                    {
                        DetectorVersion = DetectorVersion, FfmpegVersion = version, Encoders = Hardware.Encoders.ToList(), HwAccel = Hardware.PreferredHwAccel, DetectedAtUtc = DateTime.UtcNow,
                        Recommendation = Hardware.Recommendation, GpuName = Hardware.GpuName, NvidiaDriver = Hardware.NvidiaDriver,
                        EncodersWithoutHwDecode = Hardware.EncodersWithoutHwDecode.ToList(),
                    };
                    CachedRecommendation = Hardware.Recommendation;
                    // сохранение — в обработчике HardwareDetected на UI-потоке: настройки правятся только там
                }
                Logger.Info($"GPU: кодеры [{string.Join(", ", Hardware.Encoders)}], декодирование {Hardware.PreferredHwAccel}");
            }
            catch (Exception ex)
            {
                Logger.Error("Автоопределение железа не удалось", ex);
                Hardware = new HardwareCapabilities();
            }
            HardwareDetected?.Invoke();
        });
    }

    public HardwareEncoder ResolveEncoder(ExportPreset preset)
    {
        var requested = Settings.Encoder == HardwareEncoder.Auto ? preset.HardwareEncoder : Settings.Encoder;
        return Hardware?.Resolve(requested) ?? HardwareEncoder.None;
    }

    public string ResolveHwAccel() => Settings.HardwareDecode ? Hardware?.PreferredHwAccel ?? "" : "";
    /// <summary>Аппаратное декодирование для конкретного кодера: пусто, если их связка не работает.</summary>
    public string ResolveHwAccel(HardwareEncoder encoder) => Settings.HardwareDecode ? Hardware?.HwAccelFor(encoder) ?? "" : "";

    public string HardwareSummary()
    {
        if (Hardware is null) return "GPU: проверка…";
        var enc = HardwareDetector.DisplayName(Hardware.Resolve(Settings.Encoder));
        var dec = ResolveHwAccel();
        return $"Кодирование: {enc} · декодирование: {(dec == "" ? "CPU" : dec.ToUpperInvariant())}";
    }
}
