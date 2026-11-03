using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using StampCut.Core.Presets;
using Xunit;

namespace StampCut.Core.Tests;

public class ProbeAndPresetTests
{
    private const string IPhoneJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "hevc", "width": 3840, "height": 2160, "pix_fmt": "yuv420p10le",
          "color_transfer": "arib-std-b67", "r_frame_rate": "60/1", "avg_frame_rate": "59940/1001", "bit_rate": "52000000",
          "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] },
        { "codec_type": "audio", "codec_name": "aac", "sample_rate": "44100", "channels": 2 }
      ],
      "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.345", "size": "80000000" }
    }
    """;

    [Fact]
    public void Parse_IPhoneHdr_RotatedPortrait()
    {
        var m = MediaProbe.Parse("C:/v/IMG_1.MOV", IPhoneJson);
        Assert.Equal((2160, 3840), (m.Width, m.Height));
        Assert.Equal(270, m.Rotation);
        Assert.True(m.IsHdr);
        Assert.True(m.Is10Bit);
        Assert.True(m.IsVfr == false || m.IsVfr); // 60 vs 59.94 — в пределах 0,5 %
        Assert.False(m.IsVfr);
        Assert.True(m.NeedsProxy);
        Assert.Equal(44100, m.SampleRate);
        Assert.Equal(12.345, m.DurationSec, 3);
    }

    [Fact]
    public void Parse_PlainMp4_NoProxy()
    {
        var json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720, "pix_fmt": "yuv420p", "r_frame_rate": "30000/1001", "avg_frame_rate": "30000/1001" } ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "3" } }
        """;
        var m = MediaProbe.Parse("C:/v/a.mp4", json);
        Assert.False(m.NeedsProxy);
        Assert.False(m.HasAudio);
        Assert.InRange(m.Fps, 29.9, 30);
    }

    [Fact]
    public void PresetStore_RoundTripsExportAndOverlay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stampcut-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PresetStore(dir, null);
            var p = new ExportPreset { Id = "my", Name = "Мой", Width = 1080, Height = 1080, Fit = FitMode.Crop, Crf = 19, AutoQuality = false, OutputSuffix = "_sq", HardwareEncoder = HardwareEncoder.Nvenc };
            store.SaveExportPreset(p);
            var loaded = store.LoadExportPresets().Single(x => x.Id == "my");
            Assert.Equal((1080, 1080, FitMode.Crop, 19, false, "_sq", HardwareEncoder.Nvenc), (loaded.Width, loaded.Height, loaded.Fit, loaded.Crf, loaded.AutoQuality, loaded.OutputSuffix, loaded.HardwareEncoder));
            Assert.Null(loaded.MaxLongSide);

            var o = new OverlaySettings { Id = "ov", Name = "Лого", File = "logo.png", Anchor = Anchor.TopCenter, OffsetX = 1, OffsetY = 2, Width = 40, Opacity = 0.5, FromSec = 0, ToSec = 5, FadeOutSec = 0.5 };
            store.SaveOverlayPreset(o);
            var lo = store.LoadOverlayPresets().Single();
            Assert.Equal((Anchor.TopCenter, 1.0, 2.0, 40.0, 0.5, 0.0, 5.0, 0.5), (lo.Anchor, lo.OffsetX, lo.OffsetY, lo.Width, lo.Opacity, lo.FromSec, lo.ToSec, lo.FadeOutSec));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void PresetStore_ReadsRepositoryExamples()
    {
        // presets/ в корне репозитория: формат из ТЗ
        var root = FindRepoRoot();
        if (root is null) return;
        var store = new PresetStore(Path.Combine(root, "presets"), null);
        var errors = new List<string>();
        var exports = store.LoadExportPresets(errors);
        var overlays = store.LoadOverlayPresets(errors);
        Assert.Empty(errors);
        Assert.Contains(exports, p => p.Id == "telegram-vertical-9x16" && p.Fit == FitMode.PadBlur && p.Width == 1080);
        Assert.Contains(exports, p => p.Id == "lossless-cut" && p.StreamCopy);
        Assert.Contains(overlays, o => o.Id == "plashka-top-full-width" && o.ToSec == 5 && o.Anchor == Anchor.TopCenter);
    }

    [Fact]
    public void BuiltInPresets_AreUniqueAndSane()
    {
        var all = BuiltInPresets.Export();
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count());
        Assert.All(all.Where(p => !p.StreamCopy), p => Assert.True(p.MaxFps > 0));
    }

    internal static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "StampCut.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
