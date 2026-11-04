using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using Xunit;
using static StampCut.Core.Tests.TestData;

namespace StampCut.Core.Tests;

public class CommandBuilderTests
{
    [Fact]
    public void SingleClip_KeepsSizeAndFps_AndTrimsOnInput()
    {
        var clip = Clip.FromMedia(Media());
        clip.InSec = 1.5; clip.OutSec = 10;
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Equal(1920, r.OutputWidth);
        Assert.Equal(1080, r.OutputHeight);
        Assert.Equal(0, r.OutputFps);
        var cmd = string.Join(" ", r.Args);
        Assert.Contains("-ss 1.5 -to 10 -i C:/v/a.mp4", cmd);
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains("-movflags +faststart", cmd);
        Assert.Contains("-level 4.1", cmd);
        Assert.DoesNotContain("scale=", r.FilterGraph);
        Assert.DoesNotContain("fps=", r.FilterGraph);
    }

    [Fact]
    public void FourK60_IsDownscaledToMaxLongSide_FpsKept_Level42()
    {
        var clip = Clip.FromMedia(Media(w: 2160, h: 3840, fps: 59.94, bitrate: 50_000_000));
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Equal(1080, r.OutputWidth);
        Assert.Equal(1920, r.OutputHeight);
        Assert.Contains("scale=1080:1920", r.FilterGraph);
        Assert.Equal(0, r.OutputFps);
        Assert.Contains("-level 4.2", string.Join(" ", r.Args));
    }

    [Fact]
    public void Fps120_IsCappedByPreset()
    {
        var clip = Clip.FromMedia(Media(fps: 120));
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Equal(60, r.OutputFps);
        Assert.Contains("fps=60", r.FilterGraph);
    }

    [Fact]
    public void Hdr_GetsTonemap()
    {
        var clip = Clip.FromMedia(Media(codec: "hevc", pix: "yuv420p10le", trc: "smpte2084"));
        var g = Graph(Job(Telegram(), clip));
        Assert.Contains("zscale=t=linear", g);
        Assert.Contains("tonemap=", g);
    }

    [Fact]
    public void Crop_ProducesEvenCropAndOutputSizeFromCroppedFrame()
    {
        var clip = Clip.FromMedia(Media());
        clip.Crop = new CropRect { X = 0.25, Y = 0, W = 0.3, H = 1 }; // 576×1080 → 576 even
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Contains("crop=576:1080:480:0", r.FilterGraph);
        Assert.Equal(576, r.OutputWidth);
        Assert.Equal(1080, r.OutputHeight);
    }

    [Fact]
    public void Redaction_Blur_And_Pixelate_WithTimeRangeRelativeToInPoint()
    {
        var clip = Clip.FromMedia(Media());
        clip.InSec = 10;
        clip.Redactions.Add(new RedactRegion { X = 0.5, Y = 0.5, W = 0.1, H = 0.1, Mode = RedactMode.Blur, BlurRadius = 20, FromSec = 12, ToSec = 15 });
        clip.Redactions.Add(new RedactRegion { X = 0, Y = 0, W = 0.2, H = 0.2, Mode = RedactMode.Pixelate, BlockSize = 24 });
        var g = Graph(Job(Telegram(), clip));
        Assert.Contains("split[c0_m1][c0_r1]", g);
        Assert.Contains("crop=192:108:960:540,boxblur=lr=20:lp=2:cr=10:cp=2", g);
        Assert.Contains("overlay=x=960:y=540:enable='between(t,2,5)'", g);
        Assert.Contains("crop=384:216:0:0,scale=16:9:flags=area,scale=384:216:flags=neighbor", g);
    }

    [Fact]
    public void Overlay_PositionedByGeometry_WithOpacityAndTimeRange()
    {
        var clip = Clip.FromMedia(Media());
        var job = Job(Telegram(), clip);
        job.Overlays.Add(new ResolvedOverlay
        {
            Settings = new OverlaySettings { Anchor = Anchor.BottomRight, OffsetX = 3, OffsetY = 3, Width = 25, Opacity = 0.8, FromSec = 0, ToSec = 5 },
            Path = "C:/o/logo.png", Width = 1000, Height = 400,
        });
        var r = FFmpegCommandBuilder.Build(job);
        Assert.Contains("-i C:/o/logo.png", string.Join(" ", r.Args));
        Assert.Contains("[1:v]format=rgba,scale=w=480:h=-1,colorchannelmixer=aa=0.8[ov0]", r.FilterGraph);
        // x = 1920 - 480 - 57.6 = 1382; y = 1080 - 192 - 32.4 = 856
        Assert.Contains("overlay=x=1382:y=856:format=auto:enable='between(t,0,5)'", r.FilterGraph);
        Assert.EndsWith("format=yuv420p[vout]", r.FilterGraph);
    }

    [Fact]
    public void Overlay_WithFade_LoopsImageInput()
    {
        var clip = Clip.FromMedia(Media(dur: 20));
        var job = Job(Telegram(), clip);
        job.Overlays.Add(new ResolvedOverlay { Settings = new OverlaySettings { FadeInSec = 0.5, FadeOutSec = 0.5 }, Path = "C:/o/logo.png", Width = 100, Height = 50 });
        var r = FFmpegCommandBuilder.Build(job);
        Assert.Contains("-loop 1 -framerate 30 -t 20 -i C:/o/logo.png", string.Join(" ", r.Args));
        Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", r.FilterGraph);
        Assert.Contains("fade=t=out:st=19.5:d=0.5:alpha=1", r.FilterGraph);
    }

    [Fact]
    public void Join_TwoClips_ConcatWithPadForSecond_AndSilenceForClipWithoutAudio()
    {
        var a = Clip.FromMedia(Media("C:/v/a.mp4", 1920, 1080, 30, 10));
        var b = Clip.FromMedia(Media("C:/v/b.mp4", 1080, 1920, 25, 5, audio: false));
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), a, b));
        var cmd = string.Join(" ", r.Args);
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", cmd);
        Assert.Contains("-t 5 -i anullsrc", cmd);
        Assert.Contains("concat=n=2:v=1:a=1[vc][ac]", r.FilterGraph);
        Assert.Contains("[1:v:0]format=yuv420p[c1_0];[c1_0]scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=30[c1_end]", r.FilterGraph);
        Assert.Contains("[2:a:0]aresample=48000", r.FilterGraph);
        Assert.Equal(30, r.OutputFps);
    }

    [Fact]
    public void MultipleSegments_OfOneClip_BecomeSeparateInputsAndConcat()
    {
        var clip = Clip.FromMedia(Media(dur: 60));
        clip.Segments.Clear();
        clip.Segments.Add(new Segment { InSec = 30, OutSec = 35 });
        clip.Segments.Add(new Segment { InSec = 5, OutSec = 10 }); // порядок — по времени
        clip.Censors.Add(new AudioCensor { FromSec = 31, ToSec = 32, Mode = CensorMode.Beep });
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        var cmd = string.Join(" ", r.Args);
        Assert.Contains("-ss 5 -to 10 -i C:/v/a.mp4", cmd);
        Assert.Contains("-ss 30 -to 35 -i C:/v/a.mp4", cmd);
        Assert.Contains("concat=n=2:v=1:a=1", r.FilterGraph);
        Assert.DoesNotContain("fps=", r.FilterGraph);
        Assert.Contains("[1:a:0]", r.FilterGraph);
        Assert.Contains("between(t,1,2)", r.FilterGraph); // пик относительно начала второго фрагмента (30 с)
        Assert.Equal(10, clip.DurationSec, 6);

        clip.Censors.Clear(); // без перекодирования «пик» недоступен
        var ll = FFmpegCommandBuilder.Build(Job(Lossless(), clip));
        Assert.Contains("inpoint 5\noutpoint 10\n", ll.ConcatList);
        Assert.Contains("inpoint 30\noutpoint 35\n", ll.ConcatList);
    }

    [Fact]
    public void VerticalPreset_PadBlur_ForHorizontalSource()
    {
        var clip = Clip.FromMedia(Media());
        var r = FFmpegCommandBuilder.Build(Job(Vertical(), clip));
        Assert.Equal((1080, 1920), (r.OutputWidth, r.OutputHeight));
        Assert.Contains("split[c0_fg][c0_bg]", r.FilterGraph);
        Assert.Contains("boxblur=", r.FilterGraph);
        Assert.Contains("[c0_bgb][c0_fgs]overlay=x=(W-w)/2:y=(H-h)/2[c0_fit]", r.FilterGraph);
    }

    [Fact]
    public void Beep_And_Mute_GateVolumeAndMixTone()
    {
        var clip = Clip.FromMedia(Media());
        clip.InSec = 2;
        clip.Censors.Add(new AudioCensor { FromSec = 3, ToSec = 4, Mode = CensorMode.Beep });
        clip.Censors.Add(new AudioCensor { FromSec = 6, ToSec = 6.5, Mode = CensorMode.Mute });
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Contains("sine=frequency=1000:sample_rate=48000", string.Join(" ", r.Args));
        Assert.Contains("volume=volume='if(between(t,1,2)+between(t,4,4.5),0,1)':eval=frame[a0_src]", r.FilterGraph);
        Assert.Contains("volume=volume='if(between(t,1,2),0.25,0)':eval=frame[a0_beep]", r.FilterGraph);
        Assert.Contains("amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a0]", r.FilterGraph);
    }

    [Fact]
    public void ClipGain_AndFilters_GoIntoChains()
    {
        var clip = Clip.FromMedia(Media());
        clip.AudioGainDb = 6;
        clip.Filters = new VideoFilters { Shadows = 50, Highlights = 100, Saturation = 140, Brightness = 10, Contrast = -20 };
        var g = Graph(Job(Telegram(), clip));
        Assert.Contains("volume=6dB", g);
        Assert.Contains("curves=all='0/0 0.25/0.325 0.5/0.5 0.75/0.6 1/1'", g);
        Assert.Contains("eq=saturation=1.4:brightness=0.03:contrast=0.9", g);
        Assert.True(new VideoFilters().IsDefault);
        Assert.Empty(new VideoFilters().ToFfmpeg());
    }

    [Fact]
    public void MutedClip_LosesAudioTrack_AndSilenceIsUsedInJoin()
    {
        var a = Clip.FromMedia(Media("C:/v/a.mp4", dur: 5)); a.AudioGainDb = Clip.MuteDb;
        var single = FFmpegCommandBuilder.Build(Job(Telegram(), a));
        Assert.Contains("-an", single.Args);
        var b = Clip.FromMedia(Media("C:/v/b.mp4", dur: 5));
        var join = FFmpegCommandBuilder.Build(Job(Telegram(), a, b));
        Assert.Contains("anullsrc", string.Join(" ", join.Args));
        Assert.Contains("concat=n=2:v=1:a=1", join.FilterGraph);
    }

    [Fact]
    public void AutoQuality_CapsBitrateFromSource()
    {
        var clip = Clip.FromMedia(Media(bitrate: 8_000_000));
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip));
        Assert.Equal(18, r.Quality.Crf);
        Assert.Equal(9_200_000, r.Quality.MaxRateBps);
        Assert.Contains("-crf 18 -profile:v high -level 4.1 -maxrate 9200k -bufsize 18400k", string.Join(" ", r.Args));
    }

    [Fact]
    public void AutoQuality_ScalesCapWhenDownsizing()
    {
        var clip = Clip.FromMedia(Media(w: 3840, h: 2160, fps: 30, bitrate: 40_000_000));
        var r = FFmpegCommandBuilder.Build(Job(Telegram(), clip)); // 4K → 1080p: 1/4 пикселей
        Assert.Equal(11_500_000, r.Quality.MaxRateBps);
    }

    [Fact]
    public void Nvenc_EncoderArgs()
    {
        var clip = Clip.FromMedia(Media());
        var job = new ExportJob { Clips = { clip }, Preset = Telegram(), OutputPath = "o.mp4", Encoder = HardwareEncoder.Nvenc, HwAccel = "d3d11va" };
        var cmd = string.Join(" ", FFmpegCommandBuilder.Build(job).Args);
        Assert.Contains("-hwaccel d3d11va -i C:/v/a.mp4", cmd);
        Assert.Contains("-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 18 -b:v 0", cmd);
        Assert.Contains("-maxrate 9200k", cmd);
        Assert.Contains("format=nv12[vout]", cmd); // NVENC через устройство D3D11 принимает только NV12
    }

    [Fact]
    public void StreamCopy_SingleClip()
    {
        var clip = Clip.FromMedia(Media());
        clip.InSec = 1; clip.OutSec = 4.5;
        var r = FFmpegCommandBuilder.Build(Job(Lossless(), clip));
        Assert.True(r.StreamCopy);
        Assert.Contains("-ss 1 -to 4.5 -i C:/v/a.mp4 -map 0:v:0 -map 0:a? -c copy -movflags +faststart -f mp4 C:/out/x.mp4", string.Join(" ", r.Args));
        Assert.Null(r.ConcatList);
    }

    [Fact]
    public void StreamCopy_Join_UsesConcatDemuxerWithInOutPoints()
    {
        var a = Clip.FromMedia(Media("C:/v/a.mp4", dur: 10)); a.InSec = 2;
        var b = Clip.FromMedia(Media("C:/v/b.mp4", dur: 10)); b.OutSec = 5;
        var r = FFmpegCommandBuilder.Build(Job(Lossless(), a, b));
        Assert.NotNull(r.ConcatList);
        Assert.Contains("file 'C:/v/a.mp4'\ninpoint 2\n", r.ConcatList);
        Assert.Contains("file 'C:/v/b.mp4'\noutpoint 5\n", r.ConcatList);
        Assert.Contains("-f concat -safe 0 -i C:/out/x.mp4.concat.txt", string.Join(" ", r.Args));
    }

    [Fact]
    public void StreamCopy_Join_RejectsIncompatibleClips_AndOverlay()
    {
        var a = Clip.FromMedia(Media("C:/v/a.mp4"));
        var b = Clip.FromMedia(Media("C:/v/b.mp4", w: 1280, h: 720));
        var errors = FFmpegCommandBuilder.Validate(Job(Lossless(), a, b));
        Assert.Contains(errors, e => e.Contains("разный размер кадра"));

        var job = Job(Lossless(), a);
        job.Overlays.Add(new ResolvedOverlay { Settings = new OverlaySettings(), Path = "x.png", Width = 1, Height = 1 });
        Assert.Contains(FFmpegCommandBuilder.Validate(job), e => e.Contains("Оверлей"));
    }

    [Fact]
    public void Validate_RejectsAutoEncoderAndEmptyJob()
    {
        var job = new ExportJob { Preset = Telegram(), OutputPath = "o.mp4", Encoder = HardwareEncoder.Auto };
        var errors = FFmpegCommandBuilder.Validate(job);
        Assert.Contains(errors, e => e.Contains("ни одного ролика"));
        Assert.Contains(errors, e => e.Contains("Auto"));
    }
}

public class ImageTests
{
    [Fact]
    public void Photo_BecomesLoopedInput_WithSilence_AndFps30()
    {
        var photo = new MediaInfo { Path = "C:/p/a.jpg", HasVideo = true, Width = 4000, Height = 3000, IsImage = true, DurationSec = MediaInfo.ImageMaxDurationSec, VideoCodec = "mjpeg", PixelFormat = "yuvj420p", SizeBytes = 2_000_000, Container = "image2" };
        var clip = Clip.FromMedia(photo);
        Assert.Equal(5, clip.DurationSec, 6);
        var video = Clip.FromMedia(TestData.Media("C:/v/b.mp4", 1920, 1080, 25, 10));
        var r = FFmpegCommandBuilder.Build(TestData.Job(TestData.Telegram(), clip, video));
        var cmd = string.Join(" ", r.Args);
        Assert.Contains("-loop 1 -framerate 25 -t 5 -i C:/p/a.jpg", cmd);
        Assert.Contains("anullsrc", cmd);
        Assert.Equal(25, r.OutputFps);
        Assert.Equal((1920, 1440), (r.OutputWidth, r.OutputHeight));
        Assert.Contains("concat=n=2", r.FilterGraph);
    }

    [Fact]
    public void Probe_DetectsJpegAsImage()
    {
        var json = """
        { "streams": [ { "codec_type": "video", "codec_name": "mjpeg", "width": 4032, "height": 3024, "pix_fmt": "yuvj420p", "r_frame_rate": "25/1", "avg_frame_rate": "25/1" } ],
          "format": { "format_name": "image2", "size": "3000000" } }
        """;
        var m = MediaProbe.Parse("C:/p/IMG_1.JPG", json);
        Assert.True(m.IsImage);
        Assert.True(m.NeedsProxy);
        Assert.Equal(MediaInfo.ImageMaxDurationSec, m.DurationSec);
        Assert.Contains("фото", m.Summary());
    }
}
