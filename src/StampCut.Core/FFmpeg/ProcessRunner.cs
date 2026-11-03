using System.Diagnostics;
using System.Text;

namespace StampCut.Core.FFmpeg;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Запуск внешнего процесса без окна, с чтением обоих потоков и отменой.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var p = new Process();
        p.StartInfo = CreateStartInfo(exe, args);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr + "\n[timeout]");
        }
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
    }
}
