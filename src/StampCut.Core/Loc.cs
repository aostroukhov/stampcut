namespace StampCut.Core;

/// <summary>Перевод строк ядра: по умолчанию — как есть (русский); приложение подставляет словарь для английского.</summary>
public static class Loc
{
    public static Func<string, string> Translate { get; set; } = s => s;
    public static string T(string ru) => Translate(ru);
}
