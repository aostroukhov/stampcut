using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using StampCut.App.Services;

namespace StampCut.App;

public static class Program
{
    /// <summary>Тема Fluent (Windows 11): по системе, светлая или тёмная.</summary>
    public static ThemeMode ThemeFor(string? name) => name?.ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    [STAThread]
    public static int Main(string[] args)
    {
        var settings = StampCut.Core.AppSettings.Load();
        Lang.Set(settings.Language);
        var culture = CultureInfo.GetCultureInfo(Lang.Current == "en" ? "en-US" : "ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose, ThemeMode = ThemeFor(settings.Theme) };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Logger.Error("Необработанная ошибка", e.Exception);
            MessageBox.Show(Lang.T("Произошла ошибка:") + "\n" + e.Exception.Message + "\n\n" + Lang.T("Подробности в журнале:") + "\n" + Logger.CurrentFile,
                "StampCut", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("Фатальная ошибка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Logger.Error("Необработанная ошибка задачи", e.Exception); e.SetObserved(); };

        Logger.Info("StampCut " + MainWindow.AppVersion + " запуск: " + string.Join(" ", args));
        var files = args.Where(File.Exists).Select(Path.GetFullPath).ToList();
        // окно создаём после старта приложения: только тогда ресурсы темы Fluent уже загружены
        app.Startup += (_, _) =>
        {
            var window = new MainWindow(files, settings);
            app.MainWindow = window;
            window.Show();
        };
        return app.Run();
    }
}
