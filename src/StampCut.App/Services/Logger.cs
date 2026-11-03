using System.Text;
using StampCut.Core;

namespace StampCut.App.Services;

/// <summary>Простой журнал в %LOCALAPPDATA%\StampCut\logs\ГГГГ-ММ-ДД.log.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    public static string CurrentFile => Path.Combine(AppPaths.LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(CurrentFile, line, Encoding.UTF8);
        }
        catch { /* журнал не должен ронять приложение */ }
    }
}
