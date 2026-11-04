using System.Globalization;
using StampCut.Core;
using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using StampCut.Core.Presets;

namespace StampCut.Cli;

/// <summary>
/// Консольный StampCut: те же возможности, что в приложении, для скриптов и проверки конвейера.
///   stampcut probe FILE
///   stampcut detect
///   stampcut export [--preset ID] [--encoder auto|none|nvenc|qsv|amf] [--no-hwdecode] [--out PATH] [--dry-run]
///                   [--overlay PNG --anchor bottom-right --offset 3,3 --size 25 --opacity 0.8 --show FROM-TO --fade IN,OUT]
///                   --clip FILE [--in SEC] [--to SEC] [--crop X,Y,W,H] [--blur X,Y,W,H[,FROM,TO]] [--pixelate ...] [--beep FROM-TO] [--mute FROM-TO]
///                   [--clip FILE ...]
/// Координаты кропа и ретуши — доли кадра 0..1; время — секунды исходника.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }
        if (args[0] is "-v" or "--version" or "version") { Console.WriteLine(Version); return 0; }
        // голый путь к файлу — анализ, как `probe`
        if (args.Length == 1 && File.Exists(args[0])) args = new[] { "probe", args[0] };
        var settings = AppSettings.Load();
        var paths = FFmpegPaths.Locate(settings.FfmpegDir);
        try
        {
            return args[0] switch
            {
                "probe" => await Probe(paths, args.Skip(1).ToArray()),
                "detect" => await Detect(paths),
                "export" => await Export(paths, settings, args.Skip(1).ToArray()),
                _ => Fail("Неизвестная команда: " + args[0]),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string msg) { Console.Error.WriteLine("Ошибка: " + msg); return 2; }

    public static string Version => typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static void Usage() => Console.WriteLine($"""
        StampCut CLI {Version} — консольная версия; графическое приложение: StampCut.exe
          stampcut-cli probe FILE                  — анализ файла (или просто: stampcut-cli FILE)
          stampcut-cli detect                      — проверить GPU-кодеры и фильтры ffmpeg
          stampcut-cli export [параметры] --clip FILE [параметры ролика] [--clip FILE ...]
            общие:   --preset ID  --encoder auto|none|nvenc|qsv|amf  --no-hwdecode  --out PATH  --dry-run  --list-presets
                     --normalize (выровнять громкость)  --transition SEC[:fade|dissolve|wipeleft|…] (переходы между фрагментами)
            оверлей: --overlay PNG  --anchor top-left|top-center|...|bottom-right  --offset X,Y(%)  --size W(%)  --opacity 0..1
                     --show FROM-TO (сек)  --fade IN,OUT (сек)
            ролик:   --in SEC  --to SEC  (или несколько фрагментов: --segment FROM-TO --segment FROM-TO …)  --crop X,Y,W,H (доли 0..1)
                     --blur X,Y,W,H[,FROM,TO]  --pixelate X,Y,W,H[,FROM,TO]  --strength N (радиус/квадратик = N×4 px)
                     --beep FROM-TO  --mute FROM-TO  --stabilize 1..10 (стабилизация, два прохода)
        """);

    private static async Task<int> Probe(FFmpegPaths paths, string[] files)
    {
        if (files.Length == 0) return Fail("укажите файл");
        var probe = new MediaProbe(paths);
        foreach (var f in files)
        {
            var m = await probe.ProbeAsync(f);
            Console.WriteLine($"{f}\n  {m.Summary()}\n  codec={m.VideoCodec} pix={m.PixelFormat} trc={m.ColorTransfer} rot={m.Rotation} fps={m.Fps:0.###} avg={m.AvgFps:0.###} vfr={m.IsVfr} bitrate={m.VideoBitrate} proxy={m.NeedsProxy}");
        }
        return 0;
    }

    private static async Task<int> Detect(FFmpegPaths paths)
    {
        Console.WriteLine($"ffmpeg: {paths.Ffmpeg}");
        var caps = await new HardwareDetector(paths).DetectAsync(default, Console.WriteLine);
        Console.WriteLine(caps.FfmpegVersion);
        Console.WriteLine("Кодеры: " + (caps.Encoders.Count == 0 ? "только CPU (libx264)" : string.Join(", ", caps.Encoders.Select(HardwareDetector.DisplayName))));
        Console.WriteLine("Аппаратное декодирование: " + (caps.PreferredHwAccel == "" ? "нет" : caps.PreferredHwAccel) + $" (доступно: {string.Join(", ", caps.HwAccels)})");
        Console.WriteLine($"Фильтры HDR→SDR: zscale={caps.HasFilter_Zscale} tonemap={caps.HasFilter_Tonemap}");
        if (!string.IsNullOrEmpty(caps.NvidiaDriver)) Console.WriteLine($"NVIDIA: {caps.GpuName}, драйвер {caps.NvidiaDriver}");
        if (!string.IsNullOrEmpty(caps.Recommendation)) Console.WriteLine("⚠ " + caps.Recommendation + "\n  Скачать: https://www.nvidia.com/Download/index.aspx");
        return 0;
    }

    private static async Task<int> Export(FFmpegPaths paths, AppSettings settings, string[] args)
    {
        var store = new PresetStore(AppPaths.PresetsDir, settings.LibraryDir);
        var presets = store.LoadExportPresets();
        if (args.Contains("--list-presets"))
        {
            foreach (var p in presets) Console.WriteLine($"{p.Id,-20} {p.Name} — {p.Description}");
            return 0;
        }

        string presetId = "telegram-1080p", encoderText = "auto", outPath = "";
        bool hwDecode = true, dryRun = false, normalize = false;
        double transitionSec = 0; string transition = "fade";
        var overlay = new OverlaySettings { Opacity = 0.8, Width = 25 };
        string? overlayFile = null;
        var clipSpecs = new List<ClipSpec>();
        ClipSpec? cur = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"после {a} нужно значение");
            switch (a)
            {
                case "--preset": presetId = Next(); break;
                case "--encoder": encoderText = Next(); break;
                case "--no-hwdecode": hwDecode = false; break;
                case "--out": outPath = Next(); break;
                case "--dry-run": dryRun = true; break;
                case "--normalize": normalize = true; break;
                case "--transition": { var v = Next().Split(':'); transitionSec = Dbl(v[0]); if (v.Length > 1) transition = v[1]; break; }
                case "--stabilize": Clip().Stabilize = (int)Dbl(Next()); break;
                case "--overlay": overlayFile = Next(); break;
                case "--anchor": overlay.Anchor = OverlayPresetFile.ParseAnchor(Next()); break;
                case "--offset": { var v = Nums(Next(), 2); overlay.OffsetX = v[0]; overlay.OffsetY = v[1]; break; }
                case "--size": overlay.Width = Dbl(Next()); break;
                case "--opacity": overlay.Opacity = Dbl(Next()); break;
                case "--show": { var (f, t) = Range(Next()); overlay.FromSec = f; overlay.ToSec = t; break; }
                case "--fade": { var v = Nums(Next(), 2); overlay.FadeInSec = v[0]; overlay.FadeOutSec = v[1]; break; }
                case "--clip": cur = new ClipSpec { Path = Next() }; clipSpecs.Add(cur); break;
                case "--in": Clip().In = Dbl(Next()); break;
                case "--to": Clip().Out = Dbl(Next()); break;
                case "--segment": { var (f, t) = Range(Next()); Clip().Segments.Add(new Segment { InSec = f, OutSec = t }); break; }
                case "--crop": { var v = Nums(Next(), 4); Clip().Crop = new CropRect { X = v[0], Y = v[1], W = v[2], H = v[3] }; break; }
                case "--strength": Clip().Strength = (int)Dbl(Next()); break;
                case "--blur":
                case "--pixelate":
                    {
                        var v = Nums(Next(), 4, 6);
                        var r = new RedactRegion { X = v[0], Y = v[1], W = v[2], H = v[3], Mode = a == "--blur" ? RedactMode.Blur : RedactMode.Pixelate };
                        if (a == "--blur") r.BlurRadius = Clip().Strength * 4; else r.BlockSize = Clip().Strength * 4;
                        if (v.Length == 6) { r.FromSec = v[4]; r.ToSec = v[5]; }
                        Clip().Redactions.Add(r);
                        break;
                    }
                case "--beep":
                case "--mute":
                    {
                        var (f, t) = Range(Next());
                        Clip().Censors.Add(new AudioCensor { FromSec = f, ToSec = t, Mode = a == "--beep" ? CensorMode.Beep : CensorMode.Mute });
                        break;
                    }
                default:
                    if (a.StartsWith("--")) throw new ArgumentException("неизвестный параметр " + a);
                    cur = new ClipSpec { Path = a }; clipSpecs.Add(cur);
                    break;
            }
            ClipSpec Clip() => cur ?? throw new ArgumentException($"{a} указан до --clip");
        }
        if (clipSpecs.Count == 0) return Fail("не указан ни один ролик (--clip FILE)");

        var preset = presets.FirstOrDefault(p => p.Id == presetId) ?? throw new ArgumentException("нет пресета " + presetId);
        var probe = new MediaProbe(paths);
        var clips = new List<Clip>();
        foreach (var spec in clipSpecs)
        {
            var media = await probe.ProbeAsync(spec.Path);
            var clip = Core.Models.Clip.FromMedia(media);
            if (spec.Segments.Count > 0)
            {
                clip.Segments.Clear();
                foreach (var sg in spec.Segments) clip.Segments.Add(new Segment { InSec = sg.InSec, OutSec = Math.Min(sg.OutSec, media.DurationSec) });
            }
            if (spec.In is { } ci) clip.InSec = ci;
            if (spec.Out is { } co) clip.OutSec = Math.Min(co, media.DurationSec);
            clip.Crop = spec.Crop;
            clip.Stabilize = spec.Stabilize;
            clip.Redactions.AddRange(spec.Redactions);
            clip.Censors.AddRange(spec.Censors);
            foreach (var x in clip.Censors.Where(x => x.Mode == CensorMode.Beep && media.HasAudio))
                if (await probe.MeanVolumeAsync(media.Path, x.FromSec, x.ToSec) is { } lv) x.SetLevel(lv); // тон — на уровне речи в этом месте
            clips.Add(clip);
            Console.Error.WriteLine($"{spec.Path}: {media.Summary()}");
        }

        var overlays = new List<ResolvedOverlay>();
        if (overlayFile is not null)
        {
            var resolved = store.ResolveOverlayFile(overlayFile, AppPaths.OverlaysDir) ?? throw new FileNotFoundException("оверлей не найден", overlayFile);
            var (w, h) = await probe.ImageSizeAsync(resolved);
            overlays.Add(new ResolvedOverlay { Settings = overlay, Path = resolved, Width = w, Height = h });
        }

        HardwareEncoder encoder = ExportPresetFile.ParseEncoder(encoderText);
        string hwAccel = "";
        if (encoder == HardwareEncoder.Auto || hwDecode)
        {
            if (preset.StreamCopy) encoder = HardwareEncoder.None;
            else
            {
                var caps = await new HardwareDetector(paths).DetectAsync();
                encoder = caps.Resolve(encoder == HardwareEncoder.Auto ? preset.HardwareEncoder : encoder);
                hwAccel = hwDecode ? caps.HwAccelFor(encoder) : "";
                Console.Error.WriteLine($"Кодер: {HardwareDetector.DisplayName(encoder)}; декодирование: {(hwAccel == "" ? "CPU" : hwAccel)}");
            }
        }

        if (string.IsNullOrEmpty(outPath)) outPath = Exporter.DefaultOutputPath(clips[0], preset, null);
        var job = new ExportJob { Clips = clips, Overlays = overlays, Preset = preset, OutputPath = outPath, Encoder = encoder, HwAccel = hwAccel,
            NormalizeLoudness = normalize, TransitionSec = transitionSec, Transition = transition, TempDir = AppPaths.TempDir };

        var errors = FFmpegCommandBuilder.Validate(job);
        if (errors.Count > 0) return Fail(string.Join("\n", errors));

        if (dryRun)
        {
            var b = FFmpegCommandBuilder.Build(job);
            Console.WriteLine(CommandLine.Render(paths.Ffmpeg, b.Args));
            if (b.ConcatList is not null) Console.WriteLine("--- " + b.ConcatListPath + "\n" + b.ConcatList);
            Console.WriteLine($"--- выход {b.OutputWidth}×{b.OutputHeight}, fps={(b.OutputFps == 0 ? "исходная" : b.OutputFps)}, crf={b.Quality.Crf}, maxrate={(b.Quality.MaxRateBps is { } mr ? mr / 1000 + "k" : "нет")}");
            return 0;
        }

        var exporter = new Exporter(paths);
        var progress = new Progress<ExportProgress>(pr =>
            Console.Error.Write($"\r{pr.Fraction * 100,5:0.0}%  скорость {pr.Speed:0.00}x  осталось {(pr.Remaining is { } rem ? TimeFormat.Format(rem.TotalSeconds, false) : "--:--")}   "));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var outcome = await exporter.ExportAsync(job, progress, cts.Token);
        Console.Error.WriteLine();
        if (!outcome.Success) { Console.Error.WriteLine(outcome.CommandLine); return Fail(outcome.Message); }
        Console.WriteLine($"{outcome.OutputPath}  ({TimeFormat.Format(outcome.Elapsed.TotalSeconds, false)})");
        return 0;
    }

    private sealed class ClipSpec
    {
        public string Path = "";
        public double? In, Out;
        public CropRect? Crop;
        public List<Segment> Segments = new();
        public int Strength = 6;
        public int Stabilize;
        public List<RedactRegion> Redactions = new();
        public List<AudioCensor> Censors = new();
    }

    private static double Dbl(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static double[] Nums(string s, int min, int? max = null)
    {
        var v = s.Split(',', ';').Select(x => double.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        if (v.Length < min || v.Length > (max ?? min)) throw new ArgumentException($"ожидалось {min}{(max is null ? "" : "–" + max)} чисел через запятую: {s}");
        return v;
    }

    private static (double, double) Range(string s)
    {
        var parts = s.Split('-', ':');
        if (parts.Length != 2 || !TimeFormat.TryParse(parts[0], out var f) || !TimeFormat.TryParse(parts[1], out var t)) throw new ArgumentException("ожидался интервал FROM-TO: " + s);
        return (f, t);
    }
}
