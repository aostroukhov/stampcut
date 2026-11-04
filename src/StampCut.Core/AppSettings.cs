using System.Text.Json;
using System.Text.Json.Serialization;
using StampCut.Core.Models;

namespace StampCut.Core;

public sealed class AppSettings
{
    public string? FfmpegDir { get; set; }
    /// <summary>Общая библиотека редакции (оверлеи + пресеты). null — только локальные.</summary>
    public string? LibraryDir { get; set; }
    /// <summary>Папка экспорта. null — рядом с исходником.</summary>
    public string? OutputDir { get; set; }
    public string LastExportPresetId { get; set; } = "telegram-1080p";
    public string? LastOverlayPresetId { get; set; }
    public HardwareEncoder Encoder { get; set; } = HardwareEncoder.Auto;
    public bool HardwareDecode { get; set; } = true;
    public bool AlwaysUseProxy { get; set; }
    public int ProxyLongSide { get; set; } = 960;
    public string Language { get; set; } = "ru";

    // --- «меньше кликов»: недавние папки и автоматические имена
    /// <summary>Куда класть экспорт: рядом с исходником, в последнюю использованную папку или в фиксированную (OutputDir).</summary>
    public OutputLocation OutputLocation { get; set; } = OutputLocation.NextToSource;
    public List<string> RecentOpenDirs { get; set; } = new();
    public List<string> RecentOutputDirs { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>Шаблон имени: {name} — имя исходника, {suffix} — суффикс пресета, {date} — ГГГГ-ММ-ДД, {time} — ЧЧММ, {n} — номер.</summary>
    public string FileNamePattern { get; set; } = "{name}{suffix}";
    /// <summary>Оверлей по умолчанию, который применяется к каждому новому проекту.</summary>
    public string? DefaultOverlayPresetId { get; set; }
    public bool OpenFolderAfterExport { get; set; }
    /// <summary>Пользователь закрыл предупреждение про драйвер для этой версии драйвера.</summary>
    public string? DismissedDriverWarning { get; set; }
    public const int RecentLimit = 12;

    public static void Touch(List<string> list, string? value, int limit = RecentLimit)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
    }

    public string? LastOpenDir => RecentOpenDirs.FirstOrDefault(Directory.Exists);
    public string? LastOutputDir => RecentOutputDirs.FirstOrDefault(Directory.Exists);

    /// <summary>Кэш автоопределения железа; сбрасывается при смене версии ffmpeg.</summary>
    public HardwareCache? Hardware { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettings Load(string? file = null)
    {
        file ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions) ?? new AppSettings();
        }
        catch { /* битый файл — начинаем с настроек по умолчанию */ }
        return new AppSettings();
    }

    private static readonly object SaveGate = new();

    public void Save(string? file = null)
    {
        file ??= AppPaths.SettingsFile;
        lock (SaveGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, file, overwrite: true);
        }
    }
}

public enum OutputLocation { NextToSource, LastUsed, Fixed }

public sealed class HardwareCache
{
    public string FfmpegVersion { get; set; } = "";
    public List<HardwareEncoder> Encoders { get; set; } = new();
    public string HwAccel { get; set; } = "";
    public DateTime DetectedAtUtc { get; set; }
    public string Recommendation { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string NvidiaDriver { get; set; } = "";
}
