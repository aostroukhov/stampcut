using System.Text.Json;
using System.Text.Json.Serialization;
using StampCut.Core.Models;

namespace StampCut.Core.Presets;

/// <summary>Чтение и запись пресетов в формате JSON из docs/TZ.md (раздел «Модель данных»). Встроенные + локальные + общая библиотека.</summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _localDir;
    private readonly string? _libraryDir;

    public PresetStore(string localPresetsDir, string? libraryDir)
    {
        _localDir = localPresetsDir;
        _libraryDir = libraryDir;
    }

    public string LocalExportDir => Path.Combine(_localDir, "export");
    public string LocalOverlayDir => Path.Combine(_localDir, "overlay");

    public List<ExportPreset> LoadExportPresets(List<string>? errors = null)
    {
        var result = BuiltInPresets.Export();
        foreach (var dir in Dirs("export"))
            foreach (var file in JsonFiles(dir))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<ExportPresetFile>(File.ReadAllText(file), Json);
                    if (dto is null) continue;
                    var preset = dto.ToModel(Path.GetFileNameWithoutExtension(file));
                    result.RemoveAll(p => p.Id == preset.Id);
                    result.Add(preset);
                }
                catch (Exception ex) { errors?.Add($"{file}: {ex.Message}"); }
            }
        return result;
    }

    public List<OverlaySettings> LoadOverlayPresets(List<string>? errors = null)
    {
        var result = new List<OverlaySettings>();
        foreach (var dir in Dirs("overlay"))
            foreach (var file in JsonFiles(dir))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<OverlayPresetFile>(File.ReadAllText(file), Json);
                    if (dto is null) continue;
                    var o = dto.ToModel(Path.GetFileNameWithoutExtension(file));
                    result.RemoveAll(p => p.Id == o.Id);
                    result.Add(o);
                }
                catch (Exception ex) { errors?.Add($"{file}: {ex.Message}"); }
            }
        return result;
    }

    public string SaveExportPreset(ExportPreset p, bool toLibrary = false)
    {
        var dir = toLibrary && _libraryDir is not null ? Path.Combine(_libraryDir, "presets", "export") : LocalExportDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SafeId(p.Id) + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(ExportPresetFile.FromModel(p), Json));
        return file;
    }

    public string SaveOverlayPreset(OverlaySettings o, bool toLibrary = false)
    {
        var dir = toLibrary && _libraryDir is not null ? Path.Combine(_libraryDir, "presets", "overlay") : LocalOverlayDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SafeId(o.Id) + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(OverlayPresetFile.FromModel(o), Json));
        return file;
    }

    public bool DeleteOverlayPreset(string id)
    {
        bool any = false;
        foreach (var dir in Dirs("overlay"))
        {
            var f = Path.Combine(dir, SafeId(id) + ".json");
            if (File.Exists(f)) { File.Delete(f); any = true; }
        }
        return any;
    }

    /// <summary>Папки с PNG-оверлеями: локальная и общая. Относительное имя файла ищется в них по порядку.</summary>
    public IEnumerable<string> OverlayDirs(string localOverlaysDir)
    {
        yield return localOverlaysDir;
        if (_libraryDir is not null) yield return Path.Combine(_libraryDir, "overlays");
        if (_libraryDir is not null) yield return _libraryDir;
        // оверлеи из комплекта программы (папка overlays рядом с exe)
        yield return Path.Combine(AppContext.BaseDirectory, "overlays");
    }

    public string? ResolveOverlayFile(string file, string localOverlaysDir)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
        if (File.Exists(file)) return Path.GetFullPath(file); // относительно текущей папки (командная строка)
        foreach (var dir in OverlayDirs(localOverlaysDir))
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string SafeId(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray();
        var s = new string(chars);
        return string.IsNullOrWhiteSpace(s) ? Guid.NewGuid().ToString("N")[..8] : s;
    }

    private IEnumerable<string> Dirs(string kind)
    {
        // сначала общая библиотека, затем локальные — локальные пресеты с тем же id побеждают
        if (_libraryDir is not null) yield return Path.Combine(_libraryDir, "presets", kind);
        yield return Path.Combine(_localDir, kind);
    }

    private static IEnumerable<string> JsonFiles(string dir)
    {
        if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
        try { return Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return Enumerable.Empty<string>(); }
    }
}

// ------------------------------------------------------------------ формат файлов

public sealed class ExportPresetFile
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Container { get; set; }
    public bool? StreamCopy { get; set; }
    public VideoSection? Video { get; set; }
    public AudioSection? Audio { get; set; }
    public bool? FastStart { get; set; }
    public string? OutputSuffix { get; set; }

    public sealed class VideoSection
    {
        public string? Codec { get; set; }
        public int? MaxLongSide { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? Fit { get; set; }
        public int? MaxFps { get; set; }
        public bool? AutoQuality { get; set; }
        public int? Crf { get; set; }
        public string? Preset { get; set; }
        public string? Profile { get; set; }
        public string? Level { get; set; }
        public string? PixelFormat { get; set; }
        public bool? HdrToSdr { get; set; }
        public string? HardwareEncoder { get; set; }
    }

    public sealed class AudioSection
    {
        public string? Codec { get; set; }
        public int? BitrateKbps { get; set; }
        public int? SampleRate { get; set; }
        public bool? LoudnessNormalize { get; set; }
    }

    public ExportPreset ToModel(string fallbackId)
    {
        var p = new ExportPreset
        {
            Id = string.IsNullOrWhiteSpace(Id) ? fallbackId : Id!,
            Name = string.IsNullOrWhiteSpace(Name) ? fallbackId : Name!,
            Description = Description ?? "",
            StreamCopy = StreamCopy ?? false,
            FastStart = FastStart ?? true,
            OutputSuffix = OutputSuffix ?? "_tg",
        };
        if (Video is { } v)
        {
            p.MaxLongSide = v.Width is > 0 && v.Height is > 0 ? null : (v.MaxLongSide ?? 1920);
            p.Width = v.Width; p.Height = v.Height;
            p.Fit = ParseFit(v.Fit);
            p.MaxFps = v.MaxFps ?? 30;
            p.AutoQuality = v.AutoQuality ?? true;
            p.Crf = v.Crf ?? 20;
            p.X264Preset = v.Preset ?? "veryfast";
            p.Profile = v.Profile ?? "high";
            p.Level = v.Level ?? "auto";
            p.HdrToSdr = v.HdrToSdr ?? true;
            p.HardwareEncoder = ParseEncoder(v.HardwareEncoder);
        }
        if (Audio is { } au)
        {
            p.AudioBitrateKbps = au.BitrateKbps ?? 128;
            p.SampleRate = au.SampleRate ?? 48000;
            p.LoudnessNormalize = au.LoudnessNormalize ?? false;
        }
        return p;
    }

    public static ExportPresetFile FromModel(ExportPreset p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description, Container = "mp4",
        StreamCopy = p.StreamCopy ? true : null,
        Video = p.StreamCopy ? null : new VideoSection
        {
            Codec = "h264", MaxLongSide = p.HasFixedSize ? null : p.MaxLongSide, Width = p.Width, Height = p.Height,
            Fit = FitName(p.Fit), MaxFps = p.MaxFps, AutoQuality = p.AutoQuality, Crf = p.Crf, Preset = p.X264Preset, Profile = p.Profile, Level = p.Level,
            PixelFormat = "yuv420p", HdrToSdr = p.HdrToSdr, HardwareEncoder = EncoderName(p.HardwareEncoder),
        },
        Audio = p.StreamCopy ? null : new AudioSection { Codec = "aac", BitrateKbps = p.AudioBitrateKbps, SampleRate = p.SampleRate, LoudnessNormalize = p.LoudnessNormalize },
        FastStart = p.FastStart, OutputSuffix = p.OutputSuffix,
    };

    public static FitMode ParseFit(string? s) => s?.ToLowerInvariant() switch
    {
        "pad-black" or "pad" => FitMode.PadBlack,
        "pad-blur" or "blur" => FitMode.PadBlur,
        "crop" or "fill" => FitMode.Crop,
        _ => FitMode.Keep,
    };

    public static string FitName(FitMode f) => f switch
    {
        FitMode.PadBlack => "pad-black", FitMode.PadBlur => "pad-blur", FitMode.Crop => "crop", _ => "keep",
    };

    public static HardwareEncoder ParseEncoder(string? s) => s?.ToLowerInvariant() switch
    {
        "none" or "cpu" or "libx264" => HardwareEncoder.None,
        "nvenc" => HardwareEncoder.Nvenc,
        "qsv" => HardwareEncoder.Qsv,
        "amf" => HardwareEncoder.Amf,
        "mf" or "mediafoundation" => HardwareEncoder.MediaFoundation,
        _ => HardwareEncoder.Auto,
    };

    public static string EncoderName(HardwareEncoder e) => e switch
    {
        HardwareEncoder.None => "none", HardwareEncoder.Nvenc => "nvenc", HardwareEncoder.Qsv => "qsv",
        HardwareEncoder.Amf => "amf", HardwareEncoder.MediaFoundation => "mf", _ => "auto",
    };
}

public sealed class OverlayPresetFile
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? File { get; set; }
    public string? Anchor { get; set; }
    public OffsetSection? Offset { get; set; }
    public SizeSection? Size { get; set; }
    public double? Opacity { get; set; }
    public TimeRangeSection? TimeRange { get; set; }
    public double? FadeInSec { get; set; }
    public double? FadeOutSec { get; set; }
    public List<ProjectFile.MotionEntry>? Motion { get; set; }

    public sealed class OffsetSection { public double X { get; set; } public double Y { get; set; } public string? Unit { get; set; } }
    public sealed class SizeSection { public double Width { get; set; } public string? Unit { get; set; } }
    public sealed class TimeRangeSection { public double FromSec { get; set; } public double ToSec { get; set; } }

    public OverlaySettings ToModel(string fallbackId)
    {
        var o = ToModelCore(fallbackId);
        if (Motion is not null) foreach (var k in Motion.OrderBy(k => k.T)) o.Motion.Add(new MotionKey { TimeSec = k.T, X = k.X, Y = k.Y });
        return o;
    }

    private OverlaySettings ToModelCore(string fallbackId) => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? fallbackId : Id!,
        Name = string.IsNullOrWhiteSpace(Name) ? fallbackId : Name!,
        File = File ?? "",
        Anchor = ParseAnchor(Anchor),
        OffsetX = Offset?.X ?? 3, OffsetY = Offset?.Y ?? 3,
        OffsetUnit = Offset?.Unit?.ToLowerInvariant() == "px" ? OffsetUnit.Pixels : OffsetUnit.Percent,
        Width = Size?.Width ?? 25,
        WidthUnit = Size?.Unit?.ToLowerInvariant() == "px" ? SizeUnit.Pixels : SizeUnit.PercentOfVideoWidth,
        Opacity = Opacity ?? 1,
        FromSec = TimeRange?.FromSec, ToSec = TimeRange?.ToSec,
        FadeInSec = FadeInSec ?? 0, FadeOutSec = FadeOutSec ?? 0,
    };

    public static OverlayPresetFile FromModel(OverlaySettings o) => new()
    {
        Id = o.Id, Name = o.Name, File = o.File, Anchor = AnchorName(o.Anchor),
        Offset = new OffsetSection { X = o.OffsetX, Y = o.OffsetY, Unit = o.OffsetUnit == OffsetUnit.Pixels ? "px" : "percent" },
        Size = new SizeSection { Width = o.Width, Unit = o.WidthUnit == SizeUnit.Pixels ? "px" : "percentOfVideoWidth" },
        Opacity = o.Opacity,
        TimeRange = o.FromSec is not null || o.ToSec is not null ? new TimeRangeSection { FromSec = o.FromSec ?? 0, ToSec = o.ToSec ?? 0 } : null,
        FadeInSec = o.FadeInSec, FadeOutSec = o.FadeOutSec,
        Motion = o.Motion.Count == 0 ? null : o.Motion.Select(k => new ProjectFile.MotionEntry { T = k.TimeSec, X = k.X, Y = k.Y }).ToList(),
    };

    public static Anchor ParseAnchor(string? s) => s?.ToLowerInvariant().Replace("_", "-") switch
    {
        "top-left" => Models.Anchor.TopLeft, "top-center" or "top" => Models.Anchor.TopCenter, "top-right" => Models.Anchor.TopRight,
        "middle-left" or "left" => Models.Anchor.MiddleLeft, "center" or "middle" => Models.Anchor.Center, "middle-right" or "right" => Models.Anchor.MiddleRight,
        "bottom-left" => Models.Anchor.BottomLeft, "bottom-center" or "bottom" => Models.Anchor.BottomCenter,
        _ => Models.Anchor.BottomRight,
    };

    public static string AnchorName(Anchor a) => a switch
    {
        Models.Anchor.TopLeft => "top-left", Models.Anchor.TopCenter => "top-center", Models.Anchor.TopRight => "top-right",
        Models.Anchor.MiddleLeft => "middle-left", Models.Anchor.Center => "center", Models.Anchor.MiddleRight => "middle-right",
        Models.Anchor.BottomLeft => "bottom-left", Models.Anchor.BottomCenter => "bottom-center", _ => "bottom-right",
    };
}
