using System.Runtime.InteropServices;
using System.Text;

namespace StampCut.App.Services;

/// <summary>
/// Консольный режим оконного приложения: подключаемся к консоли родителя (cmd, PowerShell), чтобы печатать туда,
/// но сохраняем перенаправленные потоки («> файл», конвейер), если они уже заданы.
/// </summary>
public static class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutput = -11, StdError = -12;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int which, IntPtr handle);

    public static void Attach()
    {
        try
        {
            var outBefore = GetStdHandle(StdOutput);
            var errBefore = GetStdHandle(StdError);
            bool outRedirected = IsValid(outBefore), errRedirected = IsValid(errBefore);
            if (AttachConsole(AttachParentProcess))
            {
                // AttachConsole переназначает стандартные потоки на консоль; перенаправления возвращаем на место
                if (outRedirected) SetStdHandle(StdOutput, outBefore);
                if (errRedirected) SetStdHandle(StdError, errBefore);
            }
            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch { /* без консоли — вывод уйдёт в никуда, но команда выполнится */ }
    }

    private static bool IsValid(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);
}
