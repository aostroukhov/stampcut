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
            e.Handled = true;
            try { CrashDialog.Show(Lang.T("Произошла ошибка"), e.Exception, app.MainWindow as MainWindow); }
            catch (Exception ex2) { Logger.Error("Окно отчёта", ex2); }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Error("Фатальная ошибка", ex);
            // процесс сейчас умрёт: сохраняем отчёт, предложим отправить при следующем запуске
            try { CrashReporter.Save(CrashReporter.Build("Фатальная ошибка (приложение закрылось)", ex, (app.MainWindow as MainWindow)?.Services)); } catch { }
        };
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
