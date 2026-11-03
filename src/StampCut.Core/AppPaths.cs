namespace StampCut.Core;

/// <summary>Папки приложения: настройки и пресеты в %APPDATA%, прокси и журналы в %LOCALAPPDATA%.</summary>
public static class AppPaths
{
    public const string AppName = "StampCut";

    public static string Roaming => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
    public static string Local => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string PresetsDir => Ensure(Path.Combine(Roaming, "presets"));
    public static string OverlaysDir => Ensure(Path.Combine(Roaming, "overlays"));
    public static string ProxyDir => Ensure(Path.Combine(Local, "proxies"));
    public static string LogDir => Ensure(Path.Combine(Local, "logs"));
    public static string TempDir => Ensure(Path.Combine(Local, "temp"));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Удаляет журналы и прокси старше заданного срока.</summary>
    public static void Cleanup(TimeSpan logAge, TimeSpan proxyAge)
    {
        DeleteOlder(LogDir, logAge);
        DeleteOlder(ProxyDir, proxyAge);
        DeleteOlder(TempDir, TimeSpan.FromDays(1));
    }

    private static void DeleteOlder(string dir, TimeSpan age)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > age)
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
