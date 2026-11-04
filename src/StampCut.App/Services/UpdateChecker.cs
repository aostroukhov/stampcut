using System.Net.Http;
using System.Text.Json;
using StampCut.Core;

namespace StampCut.App.Services;

public sealed record UpdateInfo(string Version, string Url, string Name);

/// <summary>Проверка новых релизов через публичное API GitHub (без ключей; лимит 60 запросов в час на адрес).</summary>
public static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/aostroukhov/stampcut/releases/latest";

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StampCut/" + MainWindow.AppVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi, ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? CrashReporter.ReleasesUrl : CrashReporter.ReleasesUrl;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        var version = tag.TrimStart('v', 'V');
        return IsNewer(version, MainWindow.AppVersion) ? new UpdateInfo(version, url, name) : null;
    }

    public static bool IsNewer(string candidate, string current)
    {
        return Version.TryParse(Normalize(candidate), out var c) && Version.TryParse(Normalize(current), out var cur) && c > cur;
        static string Normalize(string v)
        {
            var core = v.Split('-', '+')[0];
            var parts = core.Split('.');
            return parts.Length >= 2 ? core : core + ".0";
        }
    }
}
