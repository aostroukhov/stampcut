using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using StampCut.App.Services;

namespace StampCut.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Logger.Error("Необработанная ошибка", e.Exception);
            MessageBox.Show("Произошла ошибка:\n" + e.Exception.Message + "\n\nПодробности в журнале:\n" + Logger.CurrentFile,
                "StampCut", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("Фатальная ошибка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Logger.Error("Необработанная ошибка задачи", e.Exception); e.SetObserved(); };

        Logger.Info("StampCut " + MainWindow.AppVersion + " запуск: " + string.Join(" ", args));
        var files = args.Where(File.Exists).Select(Path.GetFullPath).ToList();
        var window = new MainWindow(files);
        app.MainWindow = window;
        window.Show();
        return app.Run();
    }
}
