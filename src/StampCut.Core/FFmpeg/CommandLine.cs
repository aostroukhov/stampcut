using System.Text;

namespace StampCut.Core.FFmpeg;

public static class CommandLine
{
    /// <summary>Командная строка для показа пользователю и запуска вручную (кавычки в стиле Windows/bash).</summary>
    public static string Render(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or '+' or ','))
            return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
