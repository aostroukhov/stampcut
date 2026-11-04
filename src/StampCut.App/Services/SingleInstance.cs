using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StampCut.App.Services;

/// <summary>Один экземпляр приложения: второй запуск передаёт свои файлы первому через именованный канал и завершается.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\StampCut.SingleInstance";
    private const string PipeName = "StampCut.OpenFiles";
    private readonly Mutex _mutex;
    private readonly bool _owner;
    private CancellationTokenSource? _cts;

    public bool IsFirst => _owner;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _owner);
    }

    /// <summary>Передать файлы работающему экземпляру. true — доставлено.</summary>
    public static bool SendToRunning(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files));
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) { Logger.Warn("Не удалось передать файлы работающему экземпляру: " + ex.Message); return false; }
    }

    /// <summary>Слушать канал; onFiles вызывается в фоне — вызывающий сам маршалит в UI-поток.</summary>
    public void Listen(Action<IReadOnlyList<string>> onFiles)
    {
        if (!_owner) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct);
                    var files = JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(ms.ToArray())) ?? new List<string>();
                    if (files.Count > 0) onFiles(files);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.Warn("Канал единого экземпляра: " + ex.Message); await Task.Delay(500, CancellationToken.None); }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_owner) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
