using System.Diagnostics;
using System.Net;
using System.Text;
using StampCut.Core;

namespace StampCut.App.Services;

/// <summary>Отчёты о сбоях: файл в %LOCALAPPDATA%\StampCut\crashes и готовая ссылка на новый issue в GitHub с заполненным текстом.</summary>
public static class CrashReporter
{
    public const string RepoUrl = "https://github.com/aostroukhov/stampcut";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues/new";

    public static string CrashDir => AppPaths.Ensure(Path.Combine(AppPaths.Local, "crashes"));

    /// <summary>Собрать отчёт: окружение + ошибка + хвост журнала.</summary>
    public static string Build(string title, Exception? ex, AppServices? services, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"StampCut {MainWindow.AppVersion}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}), .NET {Environment.Version}");
        try
        {
            sb.AppendLine($"Язык/тема: {services?.Settings.Language}/{services?.Settings.Theme}; кодер: {services?.Settings.Encoder}; hwdecode: {services?.Settings.HardwareDecode}");
            if (services?.Hardware is { } hw)
            {
                sb.AppendLine($"ffmpeg: {hw.FfmpegVersion}");
                sb.AppendLine($"GPU: {hw.GpuName} {hw.NvidiaDriver}; кодеры: {string.Join(", ", hw.Encoders)}; hwaccel: {hw.PreferredHwAccel}");
            }
            sb.AppendLine($"Шейдер фильтров: {(Controls.ColorFilterEffect.Available ? "да" : "нет")}, tier {System.Windows.Media.RenderCapability.Tier >> 16}");
        }
        catch { }
        sb.AppendLine();
        sb.AppendLine("### " + title);
        if (ex is not null) { sb.AppendLine("```"); sb.AppendLine(ex.ToString()); sb.AppendLine("```"); }
        if (!string.IsNullOrEmpty(extra)) { sb.AppendLine(); sb.AppendLine(extra); }
        sb.AppendLine();
        sb.AppendLine("### Журнал (последние строки)");
        sb.AppendLine("```");
        sb.AppendLine(LogTail(60));
        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string LogTail(int lines)
    {
        try
        {
            if (!File.Exists(Logger.CurrentFile)) return "";
            using var fs = new FileStream(Logger.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var all = sr.ReadToEnd().Split('\n');
            return string.Join("\n", all.Where(l => l.Length > 0).TakeLast(lines)).Replace("\r", "");
        }
        catch { return ""; }
    }

    /// <summary>Сохранить отчёт на диск (для отправки сейчас или при следующем запуске).</summary>
    public static string Save(string report)
    {
        var path = Path.Combine(CrashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        try { File.WriteAllText(path, report, Encoding.UTF8); } catch { }
        return path;
    }

    /// <summary>Неотправленный отчёт с прошлого запуска.</summary>
    public static string? PendingReport()
    {
        try
        {
            return Directory.EnumerateFiles(CrashDir, "crash-*.md").OrderByDescending(f => f).FirstOrDefault();
        }
        catch { return null; }
    }

    public static void MarkReported(string path)
    {
        try { File.Move(path, Path.ChangeExtension(path, ".sent.md"), overwrite: true); } catch { }
    }

    /// <summary>Открыть в браузере форму нового issue с заполненным заголовком и текстом (пользователь видит, что отправляет).</summary>
    public static void OpenIssue(string title, string body)
    {
        // у GitHub ограничение на длину URL ~8 КБ: длинный отчёт обрезаем, полный остаётся в буфере обмена
        const int max = 6000;
        var text = body.Length > max ? body[..max] + "\n…(обрезано; полный отчёт — в буфере обмена, вставьте его в описание)" : body;
        try { System.Windows.Clipboard.SetText(body); } catch { }
        var url = $"{IssuesUrl}?title={WebUtility.UrlEncode(title)}&body={WebUtility.UrlEncode(text)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("Не удалось открыть браузер: " + ex.Message); }
    }
}
