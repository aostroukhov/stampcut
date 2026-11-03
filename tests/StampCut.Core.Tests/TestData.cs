using StampCut.Core.Models;

namespace StampCut.Core.Tests;

internal static class TestData
{
    public static MediaInfo Media(string path = "C:/v/a.mp4", int w = 1920, int h = 1080, double fps = 30, double dur = 60,
        string codec = "h264", string pix = "yuv420p", string trc = "bt709", bool audio = true, long bitrate = 8_000_000, int rotation = 0)
        => new()
        {
            Path = path, Width = w, Height = h, Fps = fps, AvgFps = fps, DurationSec = dur, HasVideo = true,
            VideoCodec = codec, PixelFormat = pix, ColorTransfer = trc, HasAudio = audio, AudioCodec = audio ? "aac" : "",
            SampleRate = audio ? 48000 : 0, Channels = audio ? 2 : 0, VideoBitrate = bitrate, SizeBytes = (long)(bitrate / 8 * dur),
            Container = "mov,mp4,m4a,3gp,3g2,mj2", Rotation = rotation,
        };

    public static ExportPreset Telegram() => Presets.BuiltInPresets.Export().First(p => p.Id == "telegram-1080p");
    public static ExportPreset Vertical() => Presets.BuiltInPresets.Export().First(p => p.Id == "vertical-9x16");
    public static ExportPreset Lossless() => Presets.BuiltInPresets.Export().First(p => p.Id == "lossless-cut");

    public static ExportJob Job(ExportPreset preset, params Clip[] clips) => new()
    {
        Clips = clips.ToList(), Preset = preset, OutputPath = "C:/out/x.mp4", Encoder = HardwareEncoder.None,
    };

    public static string Graph(ExportJob job) => FFmpeg.FFmpegCommandBuilder.Build(job).FilterGraph;
    public static string Cmd(ExportJob job) => string.Join(" ", FFmpeg.FFmpegCommandBuilder.Build(job).Args);
}
