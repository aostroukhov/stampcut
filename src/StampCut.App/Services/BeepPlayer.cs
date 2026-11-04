using System.Media;

namespace StampCut.App.Services;

/// <summary>Тон 1 кГц для предпросмотра «запикивания»: WAV генерируется в памяти, играет по кругу, пока позиция внутри интервала.</summary>
public sealed class BeepPlayer : IDisposable
{
    private readonly SoundPlayer _player;
    private bool _playing;

    public BeepPlayer()
    {
        _player = new SoundPlayer(MakeTone(1000, 1.0, 8000, 0.25));
        try { _player.Load(); } catch { }
    }

    public bool IsPlaying => _playing;

    public void Start()
    {
        if (_playing) return;
        try { _player.PlayLooping(); _playing = true; } catch { }
    }

    public void Stop()
    {
        if (!_playing) return;
        try { _player.Stop(); } catch { }
        _playing = false;
    }

    /// <summary>WAV PCM 16 бит моно: синус заданной частоты и амплитуды (0..1).</summary>
    private static MemoryStream MakeTone(double hz, double seconds, int rate, double amplitude)
    {
        int n = (int)(rate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataBytes = n * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < n; i++)
        {
            // короткие фейды на краях цикла, чтобы не щёлкало при зацикливании
            double env = Math.Min(1, Math.Min(i, n - 1 - i) / (rate * 0.005));
            w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * env * short.MaxValue));
        }
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    public void Dispose() { Stop(); _player.Dispose(); }
}
