using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpeg.AutoGen;
using NAudio.Wave;
using StampCut.App.Services;
using StampCut.Core.Models;

namespace StampCut.App.Playback;

/// <summary>
/// Встроенный плеер на библиотеках FFmpeg (те же DLL, что у ffmpeg.exe из комплекта): играет любой файл без прокси —
/// HEVC, HDR, повёрнутые, не-MP4. Кадры декодируются в фоне (при возможности на видеокарте через D3D11VA), переводятся
/// в BGRA и кладутся в WriteableBitmap обычного Image, поэтому все слои превью и шейдер фильтров работают как прежде.
/// Звук — через NAudio (WaveOut), он же задаёт часы; без звука часы идут от системного времени.
/// Точная перемотка: до ключевого кадра и декодирование вперёд до нужного времени.
/// </summary>
public sealed unsafe class FfmpegPlayer : IVideoPlayer
{
    // ------------------------------------------------------------------ библиотеки
    private static bool _initialized;
    private static string? _initError;

    /// <summary>Подключить DLL FFmpeg из папки (та же, где ffmpeg.exe). Вызывать один раз; false — библиотек нет или версия не та.</summary>
    public static bool Initialize(string? ffmpegDir, out string? error)
    {
        error = _initError;
        if (_initialized) return true;
        if (_initError is not null) return false;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("только Windows");
            if (string.IsNullOrEmpty(ffmpegDir) || !Directory.Exists(ffmpegDir)) throw new DirectoryNotFoundException("папка FFmpeg не найдена: " + ffmpegDir);
            ffmpeg.RootPath = ffmpegDir;
            DynamicallyLoadedBindings.Initialize();
            uint v = ffmpeg.avcodec_version();
            uint major = v >> 16;
            if (major != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
                throw new InvalidOperationException($"версия libavcodec {major} не совпадает с ожидаемой {ffmpeg.LIBAVCODEC_VERSION_MAJOR}");
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            Logger.Info("FFmpeg-плеер: " + ffmpeg.av_version_info() + " из " + ffmpegDir);
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _initError = error = ex.Message;
            Logger.Warn("FFmpeg-плеер недоступен: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ состояние
    private const int MaxPresentWidth = 1920, MaxPresentHeight = 1080;
    private const int QueueCapacity = 4, PoolSize = 7;
    private const int AudioRate = 48000, AudioChannels = 2;
    private const double AudioLatencySec = 0.12;

    private sealed class VideoFrame
    {
        public IntPtr Buffer;
        public double Pts;
        public bool Eof;
        public int Gen;
    }

    private abstract record Command;
    private sealed record SeekCommand(double Target) : Command;
    private sealed record StepCommand : Command;

    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsHitTestVisible = true, SnapsToDevicePixels = true };
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    private int _dstW, _dstH, _dstStride;
    private readonly ConcurrentBag<IntPtr> _pool = new();
    private readonly List<IntPtr> _allBuffers = new();
    private BlockingCollection<VideoFrame> _queue = new(QueueCapacity);
    private readonly ConcurrentQueue<Command> _cmds = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _decodeThread, _presentThread;
    private volatile bool _stop;
    private volatile bool _playing;
    private volatile bool _stepPending;
    private volatile bool _presentImmediately;
    private volatile bool _playRequested;
    private int _gen;
    private bool _eofQueued, _endedRaised;
    private readonly object _lifeLock = new();
    private int _openSerial;
    private int _bufferEpoch;

    // ffmpeg
    private AVFormatContext* _fmt;
    private AVCodecContext* _vctx;
    private AVCodecContext* _actx;
    private AVBufferRef* _hwDevice;
    private SwsContext* _sws;
    private int _swsFmt = -1, _swsW, _swsH;
    private SwrContext* _swr;
    private int _vIdx = -1, _aIdx = -1;
    private double _vTb, _aTb, _startOffset, _duration, _fps = 30;
    // делегаты держим в статических полях, чтобы сборщик мусора не освободил переходник, пока ffmpeg им пользуется
    private static readonly AVCodecContext_get_format GetFormatDelegate = GetFormat;
    private static readonly AVIOInterruptCB_callback InterruptDelegate = Interrupt;
    private GCHandle _self;

    // часы
    private readonly Stopwatch _wall = new();
    private double _wallBase;
    private double _audioClockLast = -1;
    private readonly Stopwatch _audioStall = new();
    private double _lastPresentedPts;
    private double _seekTarget = -1;
    private bool _seekingVideo, _seekingAudio;

    // звук
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _audioBuffer;
    private long _audioPushedSamples;
    private double _audioBasePts = -1;
    private double _volume = 1;
    private byte[] _audioScratch = new byte[AudioRate * AudioChannels * 2];

    public FfmpegPlayer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
    }

    public UIElement View => _image;
    public string Name => "FFmpeg";
    public bool IsOpen { get; private set; }
    public int RotationDegrees { get; private set; }
    public bool IsHdrPq { get; private set; }
    public bool IsHdrHlg { get; private set; }
    public bool HasAudio => _aIdx >= 0;
    public bool NeedsProxy(MediaInfo media) => false;

    public event Action? Opened;
    public event Action<Exception?>? Failed;
    public event Action? Ended;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(ClampTime(_playing ? Clock() : _lastPresentedPts));
        set => Seek(value.TotalSeconds);
    }

    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 1); try { if (_waveOut is not null) _waveOut.Volume = (float)_volume; } catch { } }
    }

    // ------------------------------------------------------------------ открытие / закрытие

    public void Open(string path, double startAt)
    {
        Close();
        int serial = ++_openSerial;
        _lastPresentedPts = Math.Max(0, startAt);
        Task.Run(() =>
        {
            // открытие в фоне под замком: Close() во время открытия дождётся и освободит всё
            lock (_lifeLock)
            {
                if (serial != _openSerial) return;
                try
                {
                    _stop = false; _eofQueued = false; _endedRaised = false; _playing = false;
                    OpenCore(path);
                }
                catch (Exception ex)
                {
                    Logger.Warn("FFmpeg-плеер не открыл файл: " + ex.Message);
                    try { FreeNative(); } catch { }
                    _dispatcher.BeginInvoke(() => { if (serial == _openSerial) Failed?.Invoke(ex); });
                    return;
                }
            }
            _dispatcher.BeginInvoke(() =>
            {
                lock (_lifeLock)
                {
                    if (serial != _openSerial || _fmt is null) return; // пока открывали — закрыли
                    _bitmap = new WriteableBitmap(_dstW, _dstH, 96, 96, PixelFormats.Bgra32, null);
                    _image.Source = _bitmap;
                    _image.LayoutTransform = RotationDegrees != 0 ? new RotateTransform(RotationDegrees) : null;
                    StartThreads(startAt);
                    IsOpen = true;
                    if (_playRequested) { _playRequested = false; Play(); } // Play() вызвали, пока файл открывался
                }
                Opened?.Invoke();
            });
        });
    }

    private void OpenCore(string path)
    {
        if (!_self.IsAllocated) _self = GCHandle.Alloc(this, GCHandleType.Normal);
        AVFormatContext* fmt = ffmpeg.avformat_alloc_context();
        fmt->interrupt_callback.callback = InterruptDelegate;
        fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_self);
        int openResult = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (openResult < 0) throw new InvalidOperationException("avformat_open_input: " + ErrorText(openResult)); // контекст ffmpeg уже освободил
        _fmt = fmt;
        Check(ffmpeg.avformat_find_stream_info(_fmt, null), "avformat_find_stream_info");
        _vIdx = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_vIdx < 0) throw new InvalidOperationException("нет видеодорожки");
        _aIdx = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _vIdx, null, 0);
        if (_aIdx < 0) _aIdx = -1;
        _startOffset = _fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? _fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
        _duration = _fmt->duration != ffmpeg.AV_NOPTS_VALUE ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;

        var vs = _fmt->streams[_vIdx];
        _vTb = ffmpeg.av_q2d(vs->time_base);
        var fr = vs->avg_frame_rate.num > 0 ? vs->avg_frame_rate : vs->r_frame_rate;
        if (fr.num > 0 && fr.den > 0) _fps = fr.num / (double)fr.den;
        RotationDegrees = ReadRotation(vs);
        OpenVideoDecoder(vs);
        if (_aIdx >= 0)
        {
            try { OpenAudioDecoder(_fmt->streams[_aIdx]); }
            catch (Exception ex) { Logger.Warn("FFmpeg-плеер: звук отключён: " + ex.Message); CloseAudio(); _aIdx = -1; }
        }

        // размер картинки: не больше 1920×1080, чётный
        int sw = _vctx->width, sh = _vctx->height;
        double k = Math.Min(1.0, Math.Min(MaxPresentWidth / (double)sw, MaxPresentHeight / (double)sh));
        _dstW = Math.Max(2, (int)Math.Round(sw * k) & ~1); _dstH = Math.Max(2, (int)Math.Round(sh * k) & ~1);
        _dstStride = _dstW * 4;
        for (int i = 0; i < PoolSize; i++) { var b = Marshal.AllocHGlobal(_dstStride * _dstH); _allBuffers.Add(b); _pool.Add(b); }
        IsHdrPq = _vctx->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
        IsHdrHlg = _vctx->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
    }

    private void OpenVideoDecoder(AVStream* vs)
    {
        var codec = ffmpeg.avcodec_find_decoder(vs->codecpar->codec_id);
        if (codec is null) throw new InvalidOperationException("нет декодера для " + vs->codecpar->codec_id);
        _vctx = ffmpeg.avcodec_alloc_context3(codec);
        Check(ffmpeg.avcodec_parameters_to_context(_vctx, vs->codecpar), "avcodec_parameters_to_context");
        _vctx->pkt_timebase = vs->time_base;
        _vctx->thread_count = Math.Clamp(Environment.ProcessorCount, 2, 8);
        _vctx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        if (HardwareDecode)
        {
            AVBufferRef* dev = null;
            if (ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
            {
                _hwDevice = dev;
                _vctx->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
                _vctx->get_format = GetFormatDelegate;
            }
            else Logger.Info("FFmpeg-плеер: D3D11VA недоступен, декодирую на CPU");
        }
        Check(ffmpeg.avcodec_open2(_vctx, codec, null), "avcodec_open2 (video)");
    }

    /// <summary>Прерывание чтения (сетевой диск, медленный носитель): при закрытии av_read_frame возвращается сразу.</summary>
    private static int Interrupt(void* opaque)
    {
        try { return GCHandle.FromIntPtr((IntPtr)opaque).Target is FfmpegPlayer p && p._stop ? 1 : 0; }
        catch { return 1; }
    }

    private double ClampTime(double t) => _duration > 0 ? Math.Clamp(t, 0, _duration) : Math.Max(0, t);

    /// <summary>Аппаратное декодирование: выбор поверхности D3D11, если декодер её предлагает, иначе первый программный формат.</summary>
    private static AVPixelFormat GetFormat(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
        return *fmts;
    }

    private void OpenAudioDecoder(AVStream* st)
    {
        var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
        if (codec is null) throw new InvalidOperationException("нет аудиодекодера");
        _actx = ffmpeg.avcodec_alloc_context3(codec);
        Check(ffmpeg.avcodec_parameters_to_context(_actx, st->codecpar), "avcodec_parameters_to_context (audio)");
        _actx->pkt_timebase = st->time_base;
        Check(ffmpeg.avcodec_open2(_actx, codec, null), "avcodec_open2 (audio)");
        _aTb = ffmpeg.av_q2d(st->time_base);

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, AudioChannels);
        SwrContext* swr = null;
        AVChannelLayout inLayout = _actx->ch_layout;
        if (inLayout.nb_channels <= 0) ffmpeg.av_channel_layout_default(&inLayout, 2);
        Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, AudioRate, &inLayout, _actx->sample_fmt, _actx->sample_rate, 0, null), "swr_alloc_set_opts2");
        Check(ffmpeg.swr_init(swr), "swr_init");
        _swr = swr;

        _audioBuffer = new BufferedWaveProvider(new WaveFormat(AudioRate, 16, AudioChannels)) { BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = true, ReadFully = true };
        _waveOut = new WaveOutEvent { DesiredLatency = (int)(AudioLatencySec * 1000), NumberOfBuffers = 3 };
        _waveOut.Init(_audioBuffer);
        _waveOut.Volume = (float)_volume;
    }

    private static int ReadRotation(AVStream* vs)
    {
        var sd = ffmpeg.av_packet_side_data_get(vs->codecpar->coded_side_data, vs->codecpar->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sd is null || sd->data is null) return 0;
        var matrix = *(int_array9*)sd->data;
        double theta = ffmpeg.av_display_rotation_get(in matrix); // против часовой
        if (double.IsNaN(theta)) return 0;
        int cw = (int)Math.Round(-theta / 90.0) * 90;
        cw = ((cw % 360) + 360) % 360;
        return cw;
    }

    public static bool HardwareDecode { get; set; } = true;

    public void Close()
    {
        _openSerial++; // незавершённое открытие станет недействительным
        _stop = true;
        _wake.Set();
        lock (_lifeLock)
        {
            if (_decodeThread is null && _fmt is null) { IsOpen = false; return; }
            try { _queue.CompleteAdding(); } catch { }
            bool stopped = true;
            try { stopped &= _decodeThread?.Join(5000) ?? true; } catch { }
            try { stopped &= _presentThread?.Join(2000) ?? true; } catch { }
            _decodeThread = _presentThread = null;
            _playRequested = false;
            if (stopped) FreeNative();
            else
            {
                // поток не остановился (завис в чтении): контексты не трогаем, лучше утечка, чем падение
                Logger.Warn("FFmpeg-плеер: поток декодирования не остановился, ресурсы не освобождены");
                _fmt = null; _vctx = null; _actx = null; _sws = null; _swr = null; _hwDevice = null; _waveOut = null; _audioBuffer = null;
                _queue = new BlockingCollection<VideoFrame>(QueueCapacity);
                _bufferEpoch++; _allBuffers.Clear(); while (_pool.TryTake(out _)) { }
            }
            IsOpen = false;
            _playing = false;
            _image.Source = null;
            _bitmap = null;
        }
    }

    private void FreeNative()
    {
        CloseAudio();
        if (_sws is not null) { ffmpeg.sws_freeContext(_sws); _sws = null; _swsFmt = -1; }
        if (_vctx is not null) { var c = _vctx; ffmpeg.avcodec_free_context(&c); _vctx = null; }
        if (_hwDevice is not null) { var h = _hwDevice; ffmpeg.av_buffer_unref(&h); _hwDevice = null; }
        if (_fmt is not null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        _queue = new BlockingCollection<VideoFrame>(QueueCapacity);
        while (_cmds.TryDequeue(out _)) { }
        _bufferEpoch++; // буферы, ещё не вернувшиеся из интерфейса, в новый пул не попадут
        foreach (var b in _allBuffers) Marshal.FreeHGlobal(b);
        _allBuffers.Clear();
        while (_pool.TryTake(out _)) { }
        _vIdx = _aIdx = -1;
    }

    private void CloseAudio()
    {
        try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
        _waveOut = null; _audioBuffer = null;
        if (_swr is not null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_actx is not null) { var c = _actx; ffmpeg.avcodec_free_context(&c); _actx = null; }
        _audioPushedSamples = 0; _audioBasePts = -1;
    }

    public void Dispose() { Close(); if (_self.IsAllocated) _self.Free(); }

    // ------------------------------------------------------------------ управление

    public void Play()
    {
        if (!IsOpen) { _playRequested = true; return; }
        if (_playing) return;
        // после шагов по кадрам звук остался на месте паузы: перемотать его к показанному кадру
        if (_audioBasePts >= 0 && Math.Abs(Clock() - _lastPresentedPts) > 0.05) Seek(_lastPresentedPts);
        _wallBase = _lastPresentedPts;
        _wall.Restart();
        _playing = true;
        try { _waveOut?.Play(); } catch (Exception ex) { Logger.Warn("WaveOut.Play: " + ex.Message); }
        _wake.Set();
    }

    public void Pause()
    {
        _playRequested = false;
        if (!_playing) { return; }
        _lastPresentedPts = Clock();
        _playing = false;
        try { _waveOut?.Pause(); } catch { }
    }

    public bool StepFrame(int direction)
    {
        if (!IsOpen) return false;
        if (direction > 0) { _stepPending = true; _wake.Set(); return true; }
        Seek(Math.Max(0, _lastPresentedPts - 1.0 / Math.Max(1, _fps)));
        return true;
    }

    private void Seek(double t)
    {
        if (!IsOpen && _fmt is null) { _lastPresentedPts = t; return; }
        t = ClampTime(t);
        _lastPresentedPts = t;
        _wallBase = t; _wall.Restart();
        Interlocked.Increment(ref _gen);
        _cmds.Enqueue(new SeekCommand(t));
        _wake.Set();
    }

    /// <summary>Текущее время воспроизведения: часы звука (если он есть и уже пошёл), иначе системное время от последней точки.</summary>
    private double Clock()
    {
        if (_audioBuffer is not null && _audioBasePts >= 0 && _audioPushedSamples > 0)
        {
            int buffered = _audioBuffer.BufferedBytes;
            double played = (_audioPushedSamples - buffered / (AudioChannels * 2)) / (double)AudioRate - AudioLatencySec;
            double t = _audioBasePts + Math.Max(0, played);
            // звук кончился (конец дорожки или пауза в ней): часы идут дальше сами, иначе видео замрёт у последних кадров
            if (buffered == 0 && _playing)
            {
                if (!_audioStall.IsRunning) { _audioClockLast = t; _audioStall.Restart(); }
                return _audioClockLast + _audioStall.Elapsed.TotalSeconds;
            }
            if (_audioStall.IsRunning) _audioStall.Reset();
            return t;
        }
        return _wallBase + (_playing ? _wall.Elapsed.TotalSeconds : 0);
    }

    // ------------------------------------------------------------------ потоки

    private void StartThreads(double startAt)
    {
        _seekTarget = startAt; _seekingVideo = _seekingAudio = startAt > 0.01;
        _presentImmediately = true;
        if (startAt > 0.01) SeekNative(startAt);
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "stampcut-decode", Priority = ThreadPriority.AboveNormal };
        _presentThread = new Thread(PresentLoop) { IsBackground = true, Name = "stampcut-present", Priority = ThreadPriority.AboveNormal };
        _decodeThread.Start();
        _presentThread.Start();
    }

    private void SeekNative(double t)
    {
        long ts = (long)((t + _startOffset) * ffmpeg.AV_TIME_BASE);
        int r = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (r < 0) Logger.Warn("FFmpeg-плеер: av_seek_frame " + r);
        if (_vctx is not null) ffmpeg.avcodec_flush_buffers(_vctx);
        if (_actx is not null) ffmpeg.avcodec_flush_buffers(_actx);
        _eofQueued = false; _endedRaised = false;
    }

    private void HandleSeek(SeekCommand cmd)
    {
        SeekNative(cmd.Target);
        _seekTarget = cmd.Target; _seekingVideo = true; _seekingAudio = _actx is not null;
        _presentImmediately = true;
        while (_queue.TryTake(out var f)) ReleaseFrame(f);
        if (_audioBuffer is not null)
        {
            try { _audioBuffer.ClearBuffer(); } catch { }
            _audioPushedSamples = 0; _audioBasePts = -1; _audioStall.Reset();
        }
    }

    private void DecodeLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        var sw = ffmpeg.av_frame_alloc();
        int serial = _openSerial;
        int videoErrors = 0;
        try
        {
            while (!_stop)
            {
                bool hadCmd = false;
                while (_cmds.TryDequeue(out var cmd)) { hadCmd = true; if (cmd is SeekCommand s) HandleSeek(s); }
                if (_eofQueued && !hadCmd) { _wake.WaitOne(50); continue; }
                if (_eofQueued) continue;
                int r = ffmpeg.av_read_frame(_fmt, pkt);
                if (r < 0)
                {
                    if (r == ffmpeg.AVERROR_EOF || r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ffmpeg.avcodec_send_packet(_vctx, null);
                        DrainVideo(frame, sw);
                        if (_actx is not null) { ffmpeg.avcodec_send_packet(_actx, null); DrainAudio(frame); }
                        if (EnqueueFrame(new VideoFrame { Eof = true, Gen = _gen })) _eofQueued = true;
                        continue;
                    }
                    Thread.Sleep(10);
                    continue;
                }
                try
                {
                    if (pkt->stream_index == _vIdx)
                    {
                        int sr = ffmpeg.avcodec_send_packet(_vctx, pkt);
                        if (sr >= 0) { videoErrors = 0; DrainVideo(frame, sw); }
                        else if (++videoErrors >= 30) throw new InvalidOperationException("видео не декодируется: " + ErrorText(sr));
                    }
                    else if (pkt->stream_index == _aIdx && _actx is not null)
                    {
                        if (ffmpeg.avcodec_send_packet(_actx, pkt) >= 0) DrainAudio(frame);
                    }
                }
                finally { ffmpeg.av_packet_unref(pkt); }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("FFmpeg-плеер: декодирование", ex);
            _dispatcher.BeginInvoke(() => { if (serial == _openSerial) Failed?.Invoke(ex); });
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&sw);
        }
    }

    private void DrainVideo(AVFrame* frame, AVFrame* sw)
    {
        while (!_stop && ffmpeg.avcodec_receive_frame(_vctx, frame) >= 0)
        {
            try
            {
                int gen = _gen; // поколение до проверок: кадр, сконвертированный во время перемотки, не выдаст себя за новый
                long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                double pts = ts == ffmpeg.AV_NOPTS_VALUE ? _lastPresentedPts : ts * _vTb - _startOffset;
                if (_seekingVideo)
                {
                    if (!_cmds.IsEmpty) return; // новая перемотка — не тратить время на старую цель
                    if (pts < _seekTarget - 0.6 / Math.Max(1, _fps)) continue; // до цели: декодируем, но не показываем
                    _seekingVideo = false;
                }
                if (!_cmds.IsEmpty) return; // пришла новая команда — этот кадр уже не нужен
                AVFrame* src = frame;
                if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    ffmpeg.av_frame_unref(sw);
                    if (ffmpeg.av_hwframe_transfer_data(sw, frame, 0) < 0) continue;
                    src = sw;
                }
                var buf = TakeBuffer();
                if (buf == IntPtr.Zero) continue; // буферов нет — кадр пропускаем
                Convert(src, buf);
                if (!EnqueueFrame(new VideoFrame { Buffer = buf, Pts = pts, Gen = gen })) _pool.Add(buf);
            }
            finally { ffmpeg.av_frame_unref(frame); }
        }
    }

    private void Convert(AVFrame* src, IntPtr dst)
    {
        if (_sws is null || _swsFmt != src->format || _swsW != src->width || _swsH != src->height)
        {
            if (_sws is not null) ffmpeg.sws_freeContext(_sws);
            _sws = ffmpeg.sws_getContext(src->width, src->height, (AVPixelFormat)src->format, _dstW, _dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)(src->width > _dstW ? SwsFlags.SWS_AREA : SwsFlags.SWS_BILINEAR), null, null, null);
            if (_sws is null) throw new InvalidOperationException("sws_getContext");
            _swsFmt = src->format; _swsW = src->width; _swsH = src->height;
            // матрица YUV→RGB по цветовому пространству файла (BT.2020 для HDR, BT.709 для остальных)
            int cs = src->colorspace == AVColorSpace.AVCOL_SPC_BT2020_NCL || src->colorspace == AVColorSpace.AVCOL_SPC_BT2020_CL ? ffmpeg.SWS_CS_BT2020
                : src->colorspace == AVColorSpace.AVCOL_SPC_BT470BG || src->colorspace == AVColorSpace.AVCOL_SPC_SMPTE170M ? ffmpeg.SWS_CS_ITU601 : ffmpeg.SWS_CS_ITU709;
            int fullRange = src->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            var inv = *(int_array4*)ffmpeg.sws_getCoefficients(cs);
            var tbl = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            ffmpeg.sws_setColorspaceDetails(_sws, in inv, fullRange, in tbl, 1, 0, 1 << 16, 1 << 16);
        }
        var srcData = new byte*[8]; var srcStride = new int[8];
        for (uint i = 0; i < 8; i++) { srcData[i] = src->data[i]; srcStride[i] = src->linesize[i]; }
        var dstData = new byte*[] { (byte*)dst, null, null, null };
        var dstStride = new[] { _dstStride, 0, 0, 0 };
        ffmpeg.sws_scale(_sws, srcData, srcStride, 0, src->height, dstData, dstStride);
    }

    private void DrainAudio(AVFrame* frame)
    {
        while (!_stop && ffmpeg.avcodec_receive_frame(_actx, frame) >= 0)
        {
            try
            {
                if (_audioBuffer is null || _swr is null) continue;
                long ts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                double pts = ts == ffmpeg.AV_NOPTS_VALUE ? -1 : ts * _aTb - _startOffset;
                double dur = frame->nb_samples / (double)Math.Max(1, frame->sample_rate);
                if (_seekingAudio)
                {
                    if (pts >= 0 && pts + dur < _seekTarget) continue;
                    _seekingAudio = false;
                }
                int outSamples = ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
                int need = outSamples * AudioChannels * 2;
                if (_audioScratch.Length < need) _audioScratch = new byte[need * 2];
                int got;
                fixed (byte* p = _audioScratch)
                {
                    byte* outPtr = p;
                    got = ffmpeg.swr_convert(_swr, &outPtr, outSamples, frame->extended_data, frame->nb_samples);
                }
                if (got <= 0) continue;
                int bytes = got * AudioChannels * 2;
                // не переполнять буфер: ждём, пока плеер проиграет (на паузе — просто ждём команду)
                while (!_stop && _cmds.IsEmpty && _audioBuffer.BufferedBytes + bytes > _audioBuffer.BufferLength - bytes) Thread.Sleep(8);
                if (_stop || !_cmds.IsEmpty) return;
                if (_audioBasePts < 0 && pts >= 0) _audioBasePts = pts;
                _audioBuffer.AddSamples(_audioScratch, 0, bytes);
                _audioPushedSamples += got;
            }
            finally { ffmpeg.av_frame_unref(frame); }
        }
    }

    private bool EnqueueFrame(VideoFrame f)
    {
        while (!_stop)
        {
            try { if (_queue.TryAdd(f, 40)) return true; }
            catch (InvalidOperationException) { return false; }
            if (!_cmds.IsEmpty) return false; // команда ждёт — кадр устарел
        }
        return false;
    }

    private IntPtr TakeBuffer() => _pool.TryTake(out var b) ? b : IntPtr.Zero;
    private void ReleaseFrame(VideoFrame f) { if (f.Buffer != IntPtr.Zero) { _pool.Add(f.Buffer); f.Buffer = IntPtr.Zero; } }

    private void PresentLoop()
    {
        try
        {
            while (!_stop)
            {
                VideoFrame f;
                try { if (!_queue.TryTake(out f!, 50)) continue; }
                catch (InvalidOperationException) { break; }
                if (f.Gen != _gen) { ReleaseFrame(f); continue; }
                if (f.Eof)
                {
                    // конец: дождаться, пока доиграет звук, и сообщить один раз
                    while (!_stop && _playing && _audioBuffer is not null && _audioBuffer.BufferedBytes > 0 && _cmds.IsEmpty) Thread.Sleep(20);
                    if (!_stop && _cmds.IsEmpty && !_endedRaised && f.Gen == _gen)
                    {
                        _endedRaised = true;
                        _lastPresentedPts = _duration > 0 ? _duration : _lastPresentedPts;
                        _dispatcher.BeginInvoke(() => Ended?.Invoke());
                    }
                    continue;
                }
                if (_presentImmediately) { _presentImmediately = false; Present(f); continue; }

                bool drop = false;
                while (!_stop)
                {
                    if (f.Gen != _gen) { drop = true; break; }
                    if (!_playing)
                    {
                        if (_stepPending) { _stepPending = false; break; }
                        _wake.WaitOne(30);
                        continue;
                    }
                    double wait = f.Pts - Clock();
                    if (wait <= 0.003) break;
                    if (wait > 1.5) { _wallBase = f.Pts; _wall.Restart(); break; } // разрыв времени (переменная частота, обрезка): подхватить часы
                    Thread.Sleep((int)Math.Clamp(wait * 1000 - 1, 1, 12));
                }
                if (drop) { ReleaseFrame(f); continue; }
                if (_playing && f.Pts < Clock() - 0.12 && _queue.Count > 0) { ReleaseFrame(f); continue; } // опоздавший кадр, есть свежее
                Present(f);
            }
        }
        catch (Exception ex) { Logger.Error("FFmpeg-плеер: показ", ex); }
    }

    private void Present(VideoFrame f)
    {
        _lastPresentedPts = f.Pts;
        var buffer = f.Buffer; f.Buffer = IntPtr.Zero;
        int epoch = _bufferEpoch;
        var op = _dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (epoch == _bufferEpoch && _bitmap is not null && buffer != IntPtr.Zero)
                    _bitmap.WritePixels(new Int32Rect(0, 0, _dstW, _dstH), buffer, _dstStride * _dstH, _dstStride);
            }
            catch (Exception ex) { Logger.Warn("WritePixels: " + ex.Message); }
            finally { if (epoch == _bufferEpoch) _pool.Add(buffer); }
        }, DispatcherPriority.Render);
        op.Wait(TimeSpan.FromMilliseconds(80)); // обычно мгновенно; если интерфейс занят — не ждём, буфер вернётся сам
    }

    private static void Check(int r, string what)
    {
        if (r < 0) throw new InvalidOperationException($"{what}: {ErrorText(r)}");
    }

    private static string ErrorText(int err)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(err, buf, 256UL);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? err.ToString();
    }
}
