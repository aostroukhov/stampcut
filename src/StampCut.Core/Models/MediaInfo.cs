namespace StampCut.Core.Models;

/// <summary>Результат анализа исходного файла (ffprobe). Ширина и высота — уже с учётом поворота из метаданных.</summary>
public sealed class MediaInfo
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public double DurationSec { get; init; }

    public bool HasVideo { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Поворот из метаданных в градусах (0, 90, 180, 270). ffmpeg применяет его автоматически.</summary>
    public int Rotation { get; init; }
    public string VideoCodec { get; init; } = "";
    public string PixelFormat { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    /// <summary>Номинальная частота кадров (r_frame_rate).</summary>
    public double Fps { get; init; }
    /// <summary>Средняя частота кадров (avg_frame_rate).</summary>
    public double AvgFps { get; init; }
    public long VideoBitrate { get; init; }
    public bool IsVfr => Fps > 0 && AvgFps > 0 && Math.Abs(Fps - AvgFps) / Fps > 0.005;
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    public bool Is10Bit => PixelFormat.Contains("10", StringComparison.Ordinal) || PixelFormat.Contains("12", StringComparison.Ordinal);

    public bool HasAudio { get; init; }
    public string AudioCodec { get; init; } = "";
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    public string Container { get; init; } = "";

    /// <summary>Нужен ли прокси для просмотра средствами Windows (WPF MediaElement уверенно играет только H.264 8 бит в MP4 без поворота).</summary>
    public bool NeedsProxy =>
        !HasVideo ||
        VideoCodec != "h264" ||
        PixelFormat != "yuv420p" ||
        IsHdr ||
        Rotation != 0 ||
        !(Container.Contains("mp4", StringComparison.Ordinal) && System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".mp4" or ".m4v");

    public string Summary()
    {
        var parts = new List<string>();
        if (HasVideo)
        {
            var codec = VideoCodec.ToUpperInvariant();
            if (Is10Bit) codec += " 10-bit";
            if (IsHdr) codec += " HDR";
            parts.Add(codec);
            parts.Add($"{Width}×{Height}");
            parts.Add($"{Fps:0.##} {Loc.T("к/с")}" + (IsVfr ? Loc.T(" (переменная)") : ""));
        }
        else parts.Add(Loc.T("без видео"));
        parts.Add(HasAudio ? $"{AudioCodec.ToUpperInvariant()} {Channels}ch" : Loc.T("без звука"));
        parts.Add(TimeFormat.Format(DurationSec));
        parts.Add(TimeFormat.FormatSize(SizeBytes));
        return string.Join(" · ", parts);
    }
}
