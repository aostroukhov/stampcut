using System.Globalization;
using System.Text.Json;
using StampCut.Core.Models;

namespace StampCut.Core.FFmpeg;

/// <summary>Анализ файлов через ffprobe.</summary>
public sealed class MediaProbe
{
    private readonly FFmpegPaths _paths;
    public MediaProbe(FFmpegPaths paths) => _paths = paths;

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден", path);
        var args = new[]
        {
            "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", path
        };
        var r = await ProcessRunner.RunAsync(_paths.Ffprobe, args, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException("ffprobe не смог прочитать файл: " + Tail(r.StdErr));
        return Parse(path, r.StdOut);
    }

    public static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : new List<JsonElement>();

        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPic(x));
        var audio = streams.FirstOrDefault(x => Str(x, "codec_type") == "audio");

        double duration = Dbl(format, "duration");
        if (duration <= 0 && video.ValueKind == JsonValueKind.Object) duration = Dbl(video, "duration");

        int w = 0, h = 0, rotation = 0;
        double fps = 0, avg = 0;
        string vcodec = "", pix = "", trc = "";
        long vbr = 0;
        bool hasVideo = video.ValueKind == JsonValueKind.Object;
        if (hasVideo)
        {
            w = Int(video, "width");
            h = Int(video, "height");
            vcodec = Str(video, "codec_name");
            pix = Str(video, "pix_fmt");
            trc = Str(video, "color_transfer");
            fps = Ratio(Str(video, "r_frame_rate"));
            avg = Ratio(Str(video, "avg_frame_rate"));
            vbr = Long(video, "bit_rate");
            rotation = ReadRotation(video);
            if (((rotation % 180) + 180) % 180 == 90) (w, h) = (h, w);
        }

        long size = Long(format, "size");
        if (size == 0) { try { size = new FileInfo(path).Length; } catch { } }

        return new MediaInfo
        {
            Path = path,
            SizeBytes = size,
            DurationSec = duration,
            HasVideo = hasVideo,
            Width = w,
            Height = h,
            Rotation = rotation,
            VideoCodec = vcodec,
            PixelFormat = pix,
            ColorTransfer = trc,
            Fps = fps,
            AvgFps = avg,
            VideoBitrate = vbr,
            HasAudio = audio.ValueKind == JsonValueKind.Object,
            AudioCodec = audio.ValueKind == JsonValueKind.Object ? Str(audio, "codec_name") : "",
            SampleRate = audio.ValueKind == JsonValueKind.Object ? Int(audio, "sample_rate") : 0,
            Channels = audio.ValueKind == JsonValueKind.Object ? Int(audio, "channels") : 0,
            Container = Str(format, "format_name"),
        };
    }

    /// <summary>Моменты ключевых кадров (секунды) — для «прилипания» маркеров в режиме без перекодирования.</summary>
    public async Task<IReadOnlyList<double>> KeyframesAsync(string path, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path
        };
        var r = await ProcessRunner.RunAsync(_paths.Ffprobe, args, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (!r.Success) return Array.Empty<double>();
        var list = new List<double>();
        foreach (var line in r.StdOut.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2 || !parts[1].Contains('K')) continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) list.Add(t);
        }
        list.Sort();
        return list;
    }

    /// <summary>Размер изображения: PNG читается напрямую, остальное — через ffprobe.</summary>
    public async Task<(int Width, int Height)> ImageSizeAsync(string path, CancellationToken ct = default)
    {
        if (ImageInfo.TryReadPngSize(path, out var w, out var h)) return (w, h);
        var info = await ProbeAsync(path, ct).ConfigureAwait(false);
        if (!info.HasVideo || info.Width == 0) throw new InvalidOperationException("Не удалось определить размер изображения: " + path);
        return (info.Width, info.Height);
    }

    private static bool IsAttachedPic(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;

    private static int ReadRotation(JsonElement video)
    {
        if (video.TryGetProperty("side_data_list", out var sdl) && sdl.ValueKind == JsonValueKind.Array)
            foreach (var sd in sdl.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r))
                {
                    var v = r.ValueKind == JsonValueKind.Number ? r.GetDouble() : double.Parse(r.GetString() ?? "0", CultureInfo.InvariantCulture);
                    return Normalize((int)Math.Round(v));
                }
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rot) &&
            int.TryParse(rot.GetString(), out var deg))
            return Normalize(deg);
        return 0;
    }

    private static int Normalize(int deg) => ((deg % 360) + 360) % 360;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static double Dbl(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        return int.TryParse(v.GetString(), out var i) ? i : 0;
    }

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        return long.TryParse(v.GetString(), out var i) ? i : 0;
    }

    private static double Ratio(string s)
    {
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public static string Tail(string text, int lines = 5)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.TakeLast(lines));
    }
}
