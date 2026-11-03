using System.Text;
using StampCut.Core.Geometry;
using StampCut.Core.Models;
using static StampCut.Core.TimeFormat;

namespace StampCut.Core.FFmpeg;

public sealed class BuildResult
{
    public List<string> Args { get; } = new();
    public string FilterGraph { get; set; } = "";
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public int OutputFps { get; set; }
    /// <summary>Содержимое списка для concat-демуксера (режим без перекодирования, несколько роликов); null — не нужен.</summary>
    public string? ConcatList { get; set; }
    public string ConcatListPath { get; set; } = "";
    public bool StreamCopy { get; set; }
    public QualityPlan Quality { get; set; } = new(20, null);
}

/// <summary>Что решили по качеству: CRF/CQ и потолок битрейта (бит/с), если он нужен.</summary>
public sealed record QualityPlan(int Crf, long? MaxRateBps);

/// <summary>Превращает ExportJob в аргументы ffmpeg. Чистая функция: без процессов и файловой системы, поэтому легко тестируется.</summary>
public static class FFmpegCommandBuilder
{
    private const double Eps = 0.01;

    /// <summary>Проверка задания перед сборкой. Возвращает человеческие сообщения об ошибках (пусто — всё в порядке).</summary>
    public static List<string> Validate(ExportJob job)
    {
        var errors = new List<string>();
        if (job.Clips.Count == 0) errors.Add("Не добавлено ни одного ролика.");
        foreach (var c in job.Clips)
        {
            if (!c.Media.HasVideo) errors.Add($"{c.FileName}: нет видеодорожки.");
            if (c.DurationSec < 0.05) errors.Add($"{c.FileName}: фрагмент короче 0,05 с, проверьте точки входа и выхода.");
        }
        if (job.Preset.StreamCopy)
        {
            if (job.Overlays.Count > 0) errors.Add("Оверлей невозможен без перекодирования: выберите другой пресет экспорта.");
            if (job.Clips.Any(c => c.Crop is { IsFull: false })) errors.Add("Кроп невозможен без перекодирования: выберите другой пресет экспорта.");
            if (job.Clips.Any(c => c.Redactions.Count > 0)) errors.Add("Ретушь невозможна без перекодирования: выберите другой пресет экспорта.");
            if (job.Clips.Any(c => c.Censors.Count > 0)) errors.Add("«Запикивание» невозможно без перекодирования: выберите другой пресет экспорта.");
            if (job.Clips.Count > 1 && !AreCopyCompatible(job.Clips, out var why))
                errors.Add("Склейка без перекодирования невозможна: " + why + " Выберите пресет с перекодированием.");
        }
        if (job.Encoder == HardwareEncoder.Auto) errors.Add("Кодер не определён (Auto): сначала выполните автоопределение.");
        return errors;
    }

    public static BuildResult Build(ExportJob job)
    {
        var errors = Validate(job);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        return job.Preset.StreamCopy ? BuildStreamCopy(job) : BuildTranscode(job);
    }

    // ----------------------------------------------------------------- без перекодирования

    private static BuildResult BuildStreamCopy(ExportJob job)
    {
        var r = new BuildResult { StreamCopy = true };
        var a = r.Args;
        a.AddRange(new[] { "-hide_banner", "-y", "-nostats", "-loglevel", "error", "-progress", "pipe:1" });
        if (job.Clips.Count == 1)
        {
            var c = job.Clips[0];
            AddTrim(a, c);
            a.AddRange(new[] { "-i", c.Media.Path });
        }
        else
        {
            var sb = new StringBuilder("ffconcat version 1.0\n");
            foreach (var c in job.Clips)
            {
                sb.Append("file '").Append(c.Media.Path.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                if (c.InSec > Eps) sb.Append("inpoint ").Append(Num(c.InSec)).Append('\n');
                if (c.OutSec < c.Media.DurationSec - Eps) sb.Append("outpoint ").Append(Num(c.OutSec)).Append('\n');
            }
            r.ConcatList = sb.ToString();
            r.ConcatListPath = job.OutputPath + ".concat.txt";
            a.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", r.ConcatListPath });
        }
        a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-c", "copy" });
        if (job.Preset.FastStart) a.AddRange(new[] { "-movflags", "+faststart" });
        a.AddRange(new[] { "-f", "mp4", job.OutputPath });
        var m = job.Clips[0].Media;
        r.OutputWidth = m.Width; r.OutputHeight = m.Height; r.OutputFps = (int)Math.Round(m.Fps);
        return r;
    }

    public static bool AreCopyCompatible(IReadOnlyList<Clip> clips, out string why)
    {
        why = "";
        var f = clips[0].Media;
        foreach (var c in clips.Skip(1))
        {
            var m = c.Media;
            if (m.VideoCodec != f.VideoCodec || m.PixelFormat != f.PixelFormat) { why = $"у роликов разные кодеки ({f.VideoCodec}/{f.PixelFormat} и {m.VideoCodec}/{m.PixelFormat})."; return false; }
            if (m.Width != f.Width || m.Height != f.Height) { why = $"у роликов разный размер кадра ({f.Width}×{f.Height} и {m.Width}×{m.Height})."; return false; }
            if (Math.Abs(m.Fps - f.Fps) > 0.01) { why = $"у роликов разная частота кадров ({f.Fps:0.##} и {m.Fps:0.##})."; return false; }
            if (m.HasAudio != f.HasAudio || m.AudioCodec != f.AudioCodec || m.SampleRate != f.SampleRate || m.Channels != f.Channels) { why = "у роликов разный звук."; return false; }
        }
        return true;
    }

    // ----------------------------------------------------------------- с перекодированием

    private static BuildResult BuildTranscode(ExportJob job)
    {
        var p = job.Preset;
        var r = new BuildResult();
        var a = r.Args;
        a.AddRange(new[] { "-hide_banner", "-y", "-nostats", "-loglevel", "error", "-progress", "pipe:1" });

        // --- размер кадра на выходе
        (r.OutputWidth, r.OutputHeight) = ComputeOutputSize(job);
        int outW = r.OutputWidth, outH = r.OutputHeight;

        // --- частота кадров (0 — исходная); effFps — фактическая, для расчёта уровня и интервала ключевых кадров
        r.OutputFps = ComputeOutputFps(job);
        double effFps = r.OutputFps > 0 ? r.OutputFps : Math.Max(1, job.Clips.Max(c => c.Media.Fps > 0 ? c.Media.Fps : 30));

        // --- входы
        bool anyAudio = job.Clips.Any(c => c.Media.HasAudio);
        var audioInputIndex = new int[job.Clips.Count];
        int inputIndex = 0;
        for (int i = 0; i < job.Clips.Count; i++)
        {
            var c = job.Clips[i];
            if (!string.IsNullOrEmpty(job.HwAccel)) a.AddRange(new[] { "-hwaccel", job.HwAccel });
            AddTrim(a, c);
            a.AddRange(new[] { "-i", c.Media.Path });
            audioInputIndex[i] = c.Media.HasAudio ? inputIndex : -1;
            inputIndex++;
        }
        if (anyAudio)
            for (int i = 0; i < job.Clips.Count; i++)
                if (!job.Clips[i].Media.HasAudio)
                {
                    a.AddRange(new[] { "-f", "lavfi", "-t", Num(job.Clips[i].DurationSec), "-i", $"anullsrc=channel_layout=stereo:sample_rate={p.SampleRate}" });
                    audioInputIndex[i] = inputIndex++;
                }
        var beepInputIndex = new int[job.Clips.Count];
        if (anyAudio)
            for (int i = 0; i < job.Clips.Count; i++)
            {
                beepInputIndex[i] = -1;
                if (job.Clips[i].Censors.Any(x => x.Mode == CensorMode.Beep && x.ToSec > x.FromSec))
                {
                    a.AddRange(new[] { "-f", "lavfi", "-t", Num(job.Clips[i].DurationSec), "-i", $"sine=frequency=1000:sample_rate={p.SampleRate}" });
                    beepInputIndex[i] = inputIndex++;
                }
            }
        var overlayInputIndex = new int[job.Overlays.Count];
        double total = job.TotalDurationSec;
        for (int j = 0; j < job.Overlays.Count; j++)
        {
            var o = job.Overlays[j];
            bool animated = o.Settings.FadeInSec > 0 || o.Settings.FadeOutSec > 0;
            if (animated) a.AddRange(new[] { "-loop", "1", "-framerate", ((int)Math.Round(effFps)).ToString(), "-t", Num(total) });
            a.AddRange(new[] { "-i", o.Path });
            overlayInputIndex[j] = inputIndex++;
        }

        // --- граф фильтров
        var g = new StringBuilder();
        for (int i = 0; i < job.Clips.Count; i++)
        {
            var c = job.Clips[i];
            string label = BuildClipChain(g, c, i, outW, outH, r.OutputFps, p, isFirst: i == 0);
            g.Append($"[{label}]setsar=1[v{i}];");
            if (anyAudio)
                BuildAudioChain(g, c, i, audioInputIndex[i], beepInputIndex[i], p);
        }

        string v = "v0", au = "a0";
        if (job.Clips.Count > 1)
        {
            for (int i = 0; i < job.Clips.Count; i++) g.Append($"[v{i}]").Append(anyAudio ? $"[a{i}]" : "");
            g.Append($"concat=n={job.Clips.Count}:v=1:a={(anyAudio ? 1 : 0)}[vc]").Append(anyAudio ? "[ac]" : "").Append(';');
            v = "vc"; au = "ac";
        }
        if (anyAudio && p.LoudnessNormalize)
        {
            g.Append($"[{au}]loudnorm=I=-16:TP=-1.5:LRA=11[an];");
            au = "an";
        }

        for (int j = 0; j < job.Overlays.Count; j++)
        {
            var o = job.Overlays[j];
            var s = o.Settings;
            var rect = OverlayGeometry.Compute(outW, outH, s, o.Width, o.Height);
            var ov = new List<string> { "format=rgba", $"scale=w={Math.Max(2, rect.W)}:h=-1" };
            if (s.Opacity < 0.999) ov.Add($"colorchannelmixer=aa={Num(Math.Clamp(s.Opacity, 0, 1))}");
            double from = s.FromSec ?? 0, to = s.ToSec ?? total;
            if (s.FadeInSec > 0) ov.Add($"fade=t=in:st={Num(from)}:d={Num(s.FadeInSec)}:alpha=1");
            if (s.FadeOutSec > 0) ov.Add($"fade=t=out:st={Num(Math.Max(from, to - s.FadeOutSec))}:d={Num(s.FadeOutSec)}:alpha=1");
            g.Append($"[{overlayInputIndex[j]}:v]{string.Join(",", ov)}[ov{j}];");
            string enable = s.FromSec is not null || s.ToSec is not null ? $":enable='between(t,{Num(from)},{Num(to)})'" : "";
            g.Append($"[{v}][ov{j}]overlay=x={rect.X}:y={rect.Y}:format=auto{enable}[vo{j}];");
            v = $"vo{j}";
        }
        g.Append($"[{v}]format=yuv420p[vout]");
        r.FilterGraph = g.ToString();

        a.AddRange(new[] { "-filter_complex", r.FilterGraph, "-map", "[vout]" });
        if (anyAudio) a.AddRange(new[] { "-map", $"[{au}]" });
        else a.Add("-an");

        r.Quality = PlanQuality(job, outW, outH, r.OutputFps);
        a.AddRange(EncoderArgs(job.Encoder, p, r.Quality, H264Level(p, outW, outH, effFps)));
        // совместимость с плеерами Telegram и соцсетей: ключевой кадр не реже чем раз в 2 с, цветовые теги SDR
        a.AddRange(new[] { "-g", ((int)Math.Round(effFps * 2)).ToString(), "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709" });
        if (anyAudio) a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{p.AudioBitrateKbps}k", "-ar", p.SampleRate.ToString(), "-ac", "2" });
        if (p.FastStart) a.AddRange(new[] { "-movflags", "+faststart" });
        a.AddRange(new[] { "-f", "mp4", job.OutputPath });
        return r;
    }

    /// <summary>Звук одного ролика: ресемплинг → «запикивание» (гейт громкости + тон) → [a{i}].</summary>
    private static void BuildAudioChain(StringBuilder g, Clip c, int i, int audioInput, int beepInput, ExportPreset p)
    {
        var af = new List<string> { $"aresample={p.SampleRate}", "aformat=sample_fmts=fltp:channel_layouts=stereo" };
        var segments = c.Censors.Where(x => x.ToSec > x.FromSec)
            .Select(x => (From: Math.Max(0, x.FromSec - c.InSec), To: Math.Max(0, x.ToSec - c.InSec), x.Mode))
            .Where(x => x.To > x.From).ToList();
        if (segments.Count == 0)
        {
            g.Append($"[{audioInput}:a:0]{string.Join(",", af)}[a{i}];");
            return;
        }
        string all = string.Join("+", segments.Select(x => $"between(t,{Num(x.From)},{Num(x.To)})"));
        af.Add($"volume=volume='if({all},0,1)':eval=frame");
        if (beepInput < 0)
        {
            g.Append($"[{audioInput}:a:0]{string.Join(",", af)}[a{i}];");
            return;
        }
        var beeps = segments.Where(x => x.Mode == CensorMode.Beep).ToList();
        string beepExpr = string.Join("+", beeps.Select(x => $"between(t,{Num(x.From)},{Num(x.To)})"));
        g.Append($"[{audioInput}:a:0]{string.Join(",", af)}[a{i}_src];");
        g.Append($"[{beepInput}:a:0]aformat=sample_fmts=fltp:channel_layouts=stereo,volume=volume='if({beepExpr},0.25,0)':eval=frame[a{i}_beep];");
        g.Append($"[a{i}_src][a{i}_beep]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a{i}];");
    }

    /// <summary>Цепочка одного ролика: тонмэппинг → формат → ретушь → кроп → вписывание → fps. Возвращает метку результата.</summary>
    private static string BuildClipChain(StringBuilder g, Clip c, int i, int outW, int outH, int fps, ExportPreset p, bool isFirst)
    {
        var m = c.Media;
        var chain = new List<string>();
        if (m.IsHdr && p.HdrToSdr)
            chain.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        chain.Add("format=yuv420p");
        string cur = $"c{i}_0";
        g.Append($"[{i}:v:0]{string.Join(",", chain)}[{cur}];");

        // ретушь — в координатах исходника, до кропа и масштабирования
        int k = 0;
        foreach (var rd in c.Redactions)
        {
            var (x, y, w, h) = PixelRect(rd.X, rd.Y, rd.W, rd.H, m.Width, m.Height);
            if (w < 4 || h < 4) continue;
            k++;
            string effect = rd.Mode == RedactMode.Pixelate ? Pixelate(w, h, rd.Strength) : Blur(w, h, rd.Strength);
            string enable = "";
            if (rd.FromSec is not null || rd.ToSec is not null)
            {
                double from = Math.Max(0, (rd.FromSec ?? c.InSec) - c.InSec);
                double to = Math.Max(from, (rd.ToSec ?? c.OutSec) - c.InSec);
                enable = $":enable='between(t,{Num(from)},{Num(to)})'";
            }
            g.Append($"[{cur}]split[c{i}_m{k}][c{i}_r{k}];");
            g.Append($"[c{i}_r{k}]crop={w}:{h}:{x}:{y},{effect}[c{i}_b{k}];");
            g.Append($"[c{i}_m{k}][c{i}_b{k}]overlay=x={x}:y={y}{enable}[c{i}_{k}];");
            cur = $"c{i}_{k}";
        }

        var tail = new List<string>();
        if (c.Crop is { IsFull: false })
        {
            var (cx, cy, cw, ch) = c.Crop.ToPixels(m.Width, m.Height);
            cw = Clip.Even(cw); ch = Clip.Even(ch); cx -= cx % 2; cy -= cy % 2;
            tail.Add($"crop={cw}:{ch}:{cx}:{cy}");
        }
        var (srcW, srcH) = c.CroppedSize();

        FitMode fit = p.HasFixedSize ? p.Fit : (isFirst ? FitMode.Keep : FitMode.PadBlack);
        string fitLabel = cur;
        if (fit == FitMode.PadBlur && (srcW != outW || srcH != outH))
        {
            if (tail.Count > 0) { g.Append($"[{cur}]{string.Join(",", tail)}[c{i}_t];"); cur = $"c{i}_t"; tail.Clear(); }
            g.Append($"[{cur}]split[c{i}_fg][c{i}_bg];");
            g.Append($"[c{i}_bg]scale={outW}:{outH}:force_original_aspect_ratio=increase:force_divisible_by=2,crop={outW}:{outH},boxblur=lr=24:lp=2:cr=12:cp=2[c{i}_bgb];");
            g.Append($"[c{i}_fg]scale={outW}:{outH}:force_original_aspect_ratio=decrease:force_divisible_by=2[c{i}_fgs];");
            g.Append($"[c{i}_bgb][c{i}_fgs]overlay=x=(W-w)/2:y=(H-h)/2[c{i}_fit];");
            fitLabel = $"c{i}_fit";
            cur = fitLabel;
        }
        else
        {
            switch (fit)
            {
                case FitMode.Keep:
                    if (srcW != outW || srcH != outH) tail.Add($"scale={outW}:{outH}");
                    break;
                case FitMode.Crop:
                    if (srcW != outW || srcH != outH) tail.Add($"scale={outW}:{outH}:force_original_aspect_ratio=increase:force_divisible_by=2,crop={outW}:{outH}");
                    break;
                default: // PadBlack
                    if (srcW != outW || srcH != outH) tail.Add($"scale={outW}:{outH}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2");
                    break;
            }
        }
        if (fps > 0) tail.Add($"fps={fps}");
        if (tail.Count > 0)
        {
            g.Append($"[{cur}]{string.Join(",", tail)}[c{i}_end];");
            cur = $"c{i}_end";
        }
        return cur;
    }

    private static string Blur(int w, int h, int strength)
    {
        int r = (int)Math.Round(Math.Min(w, h) * Math.Clamp(strength, 1, 10) / 25.0);
        r = Math.Clamp(r, 1, Math.Max(1, Math.Min(w, h) / 2 - 1));
        int cr = Math.Max(1, r / 2);
        return $"boxblur=lr={r}:lp=2:cr={cr}:cp=2";
    }

    private static string Pixelate(int w, int h, int strength)
    {
        int blocks = 24 - 2 * Math.Clamp(strength, 1, 10); // 22 … 4 блока по меньшей стороне
        int block = Math.Max(2, Math.Min(w, h) / blocks);
        int sw = Math.Max(1, w / block), sh = Math.Max(1, h / block);
        return $"scale={sw}:{sh}:flags=area,scale={w}:{h}:flags=neighbor";
    }

    private static (int X, int Y, int W, int H) PixelRect(double fx, double fy, double fw, double fh, int frameW, int frameH)
    {
        int x = (int)Math.Round(Math.Clamp(fx, 0, 1) * frameW);
        int y = (int)Math.Round(Math.Clamp(fy, 0, 1) * frameH);
        int w = (int)Math.Round(Math.Clamp(fw, 0, 1) * frameW);
        int h = (int)Math.Round(Math.Clamp(fh, 0, 1) * frameH);
        x -= x % 2; y -= y % 2;
        w = Math.Clamp(w - w % 2, 2, Math.Max(2, frameW - x));
        h = Math.Clamp(h - h % 2, 2, Math.Max(2, frameH - y));
        return (x, y, w, h);
    }

    public static (int W, int H) ComputeOutputSize(ExportJob job)
    {
        var p = job.Preset;
        if (p.HasFixedSize) return (Clip.Even(p.Width!.Value), Clip.Even(p.Height!.Value));
        var (w, h) = job.Clips[0].CroppedSize();
        if (p.MaxLongSide is > 0)
        {
            double k = Math.Min(1.0, p.MaxLongSide.Value / (double)Math.Max(w, h));
            if (k < 1) { w = (int)Math.Round(w * k); h = (int)Math.Round(h * k); }
        }
        return (Clip.Even(w), Clip.Even(h));
    }

    /// <summary>0 — не трогать частоту кадров. Иначе общая частота для всех роликов.</summary>
    public static int ComputeOutputFps(ExportJob job)
    {
        var p = job.Preset;
        int max = p.MaxFps > 0 ? p.MaxFps : 60;
        if (job.Clips.Count == 1)
        {
            var m = job.Clips[0].Media;
            if (m.Fps > max + 0.01) return max;
            if (m.IsVfr) return Math.Min(max, Math.Max(1, (int)Math.Round(m.AvgFps > 0 ? m.AvgFps : m.Fps)));
            return 0;
        }
        int best = job.Clips.Max(c => (int)Math.Round(c.Media.Fps > 0 ? c.Media.Fps : 30));
        return Math.Min(max, Math.Max(1, best));
    }

    private static void AddTrim(List<string> a, Clip c)
    {
        if (c.InSec > Eps) a.AddRange(new[] { "-ss", Num(c.InSec) });
        if (c.OutSec < c.Media.DurationSec - Eps) a.AddRange(new[] { "-to", Num(c.OutSec) });
    }

    /// <summary>
    /// Авто-качество: CRF 18 (визуально без потерь) и потолок битрейта, выведенный из битрейта исходника
    /// с поправкой на изменение числа пикселей и частоты кадров. Так уже сжатый ролик не раздувается, а качественный не портится.
    /// </summary>
    public static QualityPlan PlanQuality(ExportJob job, int outW, int outH, int outFps)
    {
        var p = job.Preset;
        if (!p.AutoQuality) return new QualityPlan(Math.Clamp(p.Crf, 10, 40), null);

        double cap = 0;
        foreach (var c in job.Clips)
        {
            var m = c.Media;
            double src = m.VideoBitrate;
            if (src <= 0 && m.DurationSec > 0 && m.SizeBytes > 0) src = m.SizeBytes * 8.0 / m.DurationSec * 0.9; // без точных данных: по размеру файла минус звук
            if (src <= 0) continue;
            double srcFps = m.Fps > 0 ? m.Fps : 30;
            double fps = outFps > 0 ? Math.Min(outFps, srcFps) : srcFps;
            var (cw, ch) = c.CroppedSize();
            double pixelScale = (double)outW * outH / Math.Max(1.0, (double)m.Width * m.Height);
            // кроп убирает часть кадра: вычитаем её долю из бюджета, но не ниже 60 % (мелкие детали всё равно требуют бит)
            double cropShare = Math.Max(0.6, (double)cw * ch / Math.Max(1.0, (double)m.Width * m.Height));
            double scaled = src * Math.Min(1.0, pixelScale / cropShare) * Math.Min(1.0, fps / srcFps);
            cap = Math.Max(cap, scaled);
        }
        if (cap <= 0) return new QualityPlan(18, null);
        long maxRate = (long)Math.Max(1_500_000, cap * 1.15);
        return new QualityPlan(18, maxRate);
    }

    /// <summary>Минимальный уровень H.264, в который укладываются размер кадра и частота (по таблице A-1 стандарта).</summary>
    public static string H264Level(ExportPreset p, int w, int h, double fps)
    {
        if (!string.IsNullOrEmpty(p.Level) && !p.Level.Equals("auto", StringComparison.OrdinalIgnoreCase)) return p.Level;
        double mbs = Math.Ceiling(w / 16.0) * Math.Ceiling(h / 16.0);
        double mbps = mbs * fps;
        (string Level, double MaxMbps, double MaxFs)[] table =
        {
            ("3.1", 108000, 3600), ("3.2", 216000, 5120), ("4.1", 245760, 8192),
            ("4.2", 522240, 8704), ("5.0", 589824, 22080), ("5.1", 983040, 36864), ("5.2", 2073600, 36864), ("6.0", 4177920, 139264),
        };
        foreach (var (level, maxMbps, maxFs) in table)
            if (mbps <= maxMbps && mbs <= maxFs) return level;
        return "6.2";
    }

    public static IEnumerable<string> EncoderArgs(HardwareEncoder e, ExportPreset p, QualityPlan? q = null, string? levelOverride = null)
    {
        q ??= new QualityPlan(Math.Clamp(p.Crf, 10, 40), null);
        int crf = q.Crf;
        string profile = string.IsNullOrEmpty(p.Profile) ? "high" : p.Profile;
        string level = levelOverride ?? (string.IsNullOrEmpty(p.Level) || p.Level == "auto" ? "4.1" : p.Level);
        var args = new List<string>();
        string Kbps(long bps) => (bps / 1000) + "k";
        switch (e)
        {
            case HardwareEncoder.Nvenc:
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", crf.ToString(), "-b:v", "0", "-profile:v", profile, "-level", level });
                if (q.MaxRateBps is { } nm) args.AddRange(new[] { "-maxrate", Kbps(nm), "-bufsize", Kbps(nm * 2) });
                break;
            case HardwareEncoder.Qsv:
                if (q.MaxRateBps is { } qm)
                    args.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "medium", "-b:v", Kbps((long)(qm * 0.8)), "-maxrate", Kbps(qm), "-bufsize", Kbps(qm * 2), "-profile:v", profile, "-level", level });
                else
                    args.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", (crf + 2).ToString(), "-profile:v", profile, "-level", level });
                break;
            case HardwareEncoder.Amf:
                if (q.MaxRateBps is { } am)
                    args.AddRange(new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "vbr_peak", "-b:v", Kbps((long)(am * 0.8)), "-maxrate", Kbps(am), "-bufsize", Kbps(am * 2), "-profile:v", profile, "-level", level });
                else
                    args.AddRange(new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", crf.ToString(), "-qp_p", (crf + 2).ToString(), "-profile:v", profile, "-level", level });
                break;
            case HardwareEncoder.MediaFoundation:
                args.AddRange(new[] { "-c:v", "h264_mf", "-rate_control", "quality", "-quality", Math.Clamp(100 - crf * 2, 30, 95).ToString() });
                break;
            default:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", string.IsNullOrEmpty(p.X264Preset) ? "veryfast" : p.X264Preset, "-crf", crf.ToString(), "-profile:v", profile, "-level", level });
                if (q.MaxRateBps is { } xm) args.AddRange(new[] { "-maxrate", Kbps(xm), "-bufsize", Kbps(xm * 2) });
                break;
        }
        return args;
    }
}
