namespace StampCut.Core.FFmpeg;

/// <summary>Где лежат ffmpeg и ffprobe. Порядок поиска: явный путь → tools/ffmpeg рядом с приложением → PATH.</summary>
public sealed class FFmpegPaths
{
    public required string Ffmpeg { get; init; }
    public required string Ffprobe { get; init; }

    public static FFmpegPaths Locate(string? configuredDir = null)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredDir)) candidates.Add(configuredDir);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(AppContext.BaseDirectory);
        foreach (var dir in candidates)
        {
            var f = Path.Combine(dir, "ffmpeg" + exe);
            var p = Path.Combine(dir, "ffprobe" + exe);
            if (File.Exists(f) && File.Exists(p)) return new FFmpegPaths { Ffmpeg = f, Ffprobe = p };
        }
        return new FFmpegPaths { Ffmpeg = "ffmpeg" + exe, Ffprobe = "ffprobe" + exe };
    }

    public bool IsResolved => Path.IsPathRooted(Ffmpeg);
}
