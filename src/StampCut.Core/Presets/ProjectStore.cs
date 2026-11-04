using System.Text.Json;
using System.Text.Json.Serialization;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;

namespace StampCut.Core.Presets;

/// <summary>Проект на диске: ролики с фрагментами, кропом, ретушью и запикиванием, оверлеи, пресет экспорта. JSON, расширение .stampcut.json.</summary>
public sealed class ProjectFile
{
    public const string Extension = ".stampcut.json";
    public int Version { get; set; } = 1;
    public string App { get; set; } = "StampCut";
    public DateTime SavedAtUtc { get; set; }
    public string ExportPresetId { get; set; } = "";
    public List<ClipEntry> Clips { get; set; } = new();
    public List<OverlayPresetFile> Overlays { get; set; } = new();

    public sealed class ClipEntry
    {
        public string Path { get; set; } = "";
        public List<SegmentEntry> Segments { get; set; } = new();
        public CropEntry? Crop { get; set; }
        public List<RedactEntry> Redactions { get; set; } = new();
        public List<CensorEntry> Censors { get; set; } = new();
        public double AudioGainDb { get; set; }
        public FiltersEntry? Filters { get; set; }
    }
    public sealed class FiltersEntry { public int Shadows { get; set; } public int Highlights { get; set; } public int Saturation { get; set; } = 100; public int Brightness { get; set; } public int Contrast { get; set; } }
    public sealed class SegmentEntry { public double In { get; set; } public double Out { get; set; } }
    public sealed class CropEntry { public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }
    public sealed class RedactEntry
    {
        public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; }
        public string Mode { get; set; } = "blur"; public int BlockSize { get; set; } public int BlurRadius { get; set; }
        public double? From { get; set; } public double? To { get; set; }
    }
    public sealed class CensorEntry { public double From { get; set; } public double To { get; set; } public string Mode { get; set; } = "beep"; }
}

public sealed class ProjectLoadResult
{
    public List<Clip> Clips { get; } = new();
    public List<OverlaySettings> Overlays { get; } = new();
    public string ExportPresetId { get; set; } = "";
    public List<string> Warnings { get; } = new();
}

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static bool IsProjectFile(string path) => path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase);

    public static void Save(string path, IEnumerable<Clip> clips, IEnumerable<OverlaySettings> overlays, string exportPresetId)
    {
        var pf = new ProjectFile { SavedAtUtc = DateTime.UtcNow, ExportPresetId = exportPresetId };
        foreach (var c in clips)
        {
            var e = new ProjectFile.ClipEntry { Path = c.Media.Path, AudioGainDb = c.AudioGainDb };
            if (!c.Filters.IsDefault) e.Filters = new ProjectFile.FiltersEntry { Shadows = c.Filters.Shadows, Highlights = c.Filters.Highlights, Saturation = c.Filters.Saturation, Brightness = c.Filters.Brightness, Contrast = c.Filters.Contrast };
            foreach (var s in c.Segments) e.Segments.Add(new ProjectFile.SegmentEntry { In = s.InSec, Out = s.OutSec });
            if (c.Crop is { IsFull: false } cr) e.Crop = new ProjectFile.CropEntry { X = cr.X, Y = cr.Y, W = cr.W, H = cr.H };
            foreach (var r in c.Redactions) e.Redactions.Add(new ProjectFile.RedactEntry { X = r.X, Y = r.Y, W = r.W, H = r.H, Mode = r.Mode == RedactMode.Pixelate ? "pixelate" : "blur", BlockSize = r.BlockSize, BlurRadius = r.BlurRadius, From = r.FromSec, To = r.ToSec });
            foreach (var x in c.Censors) e.Censors.Add(new ProjectFile.CensorEntry { From = x.FromSec, To = x.ToSec, Mode = x.Mode == CensorMode.Mute ? "mute" : "beep" });
            pf.Clips.Add(e);
        }
        foreach (var o in overlays) pf.Overlays.Add(OverlayPresetFile.FromModel(o));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(pf, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<ProjectLoadResult> LoadAsync(string path, MediaProbe probe, CancellationToken ct = default)
    {
        var pf = JsonSerializer.Deserialize<ProjectFile>(await File.ReadAllTextAsync(path, ct), Json) ?? throw new InvalidOperationException(Loc.T("Файл проекта пуст или повреждён."));
        var result = new ProjectLoadResult { ExportPresetId = pf.ExportPresetId };
        var projectDir = Path.GetDirectoryName(path) ?? "";
        foreach (var e in pf.Clips)
        {
            var file = e.Path;
            if (!File.Exists(file))
            {
                // файл переехал: ищем по имени рядом с проектом
                var candidate = Path.Combine(projectDir, Path.GetFileName(file));
                if (File.Exists(candidate)) file = candidate;
                else { result.Warnings.Add($"{Loc.T("Файл не найден:")} {e.Path}"); continue; }
            }
            MediaInfo media;
            try { media = await probe.ProbeAsync(file, ct); }
            catch (Exception ex) { result.Warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); continue; }
            var clip = Clip.FromMedia(media);
            if (e.Segments.Count > 0)
            {
                clip.Segments.Clear();
                foreach (var s in e.Segments) clip.Segments.Add(new Segment { InSec = Math.Clamp(s.In, 0, media.DurationSec), OutSec = Math.Clamp(s.Out, 0, media.DurationSec) });
            }
            if (e.Crop is { } cr) clip.Crop = new CropRect { X = Math.Clamp(cr.X, 0, 1), Y = Math.Clamp(cr.Y, 0, 1), W = Math.Clamp(cr.W, 0.01, 1), H = Math.Clamp(cr.H, 0.01, 1) };
            clip.AudioGainDb = e.AudioGainDb;
            if (e.Filters is { } f) clip.Filters = new VideoFilters { Shadows = f.Shadows, Highlights = f.Highlights, Saturation = f.Saturation, Brightness = f.Brightness, Contrast = f.Contrast };
            foreach (var r in e.Redactions)
            {
                double w = Math.Clamp(r.W, 0.01, 1), h = Math.Clamp(r.H, 0.01, 1);
                clip.Redactions.Add(new RedactRegion { X = Math.Clamp(r.X, 0, 1 - w), Y = Math.Clamp(r.Y, 0, 1 - h), W = w, H = h, Mode = r.Mode == "pixelate" ? RedactMode.Pixelate : RedactMode.Blur, BlockSize = Math.Max(0, r.BlockSize), BlurRadius = Math.Max(0, r.BlurRadius), FromSec = r.From, ToSec = r.To });
            }
            foreach (var x in e.Censors)
                clip.Censors.Add(new AudioCensor { FromSec = x.From, ToSec = x.To, Mode = x.Mode == "mute" ? CensorMode.Mute : CensorMode.Beep });
            result.Clips.Add(clip);
        }
        foreach (var o in pf.Overlays) result.Overlays.Add(o.ToModel(Guid.NewGuid().ToString("N")));
        return result;
    }
}
