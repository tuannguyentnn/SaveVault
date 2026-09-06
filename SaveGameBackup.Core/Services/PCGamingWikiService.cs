using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class PCGamingWikiService
{
    private readonly HttpClient _httpClient;

    public PCGamingWikiService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SaveGameBackupApp", "1.0"));
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }
    }

    public async Task<GameSaveInfo?> SearchAndFetchSaveInfoAsync(string gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var pageTitle = await FindPageTitleAsync(gameName, cancellationToken);
        if (string.IsNullOrEmpty(pageTitle)) return null;

        return await FetchPageSaveDataAsync(pageTitle, gameName, cancellationToken);
    }

    public async Task<string?> FindPageTitleAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(query)}&limit=5&format=json";
            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
            {
                var titles = root[1];
                if (titles.GetArrayLength() > 0)
                {
                    // Return the first match
                    return titles[0].GetString();
                }
            }
        }
        catch
        {
            // Network or parse error
        }

        return null;
    }

    public async Task<GameSaveInfo?> FetchPageSaveDataAsync(string pageTitle, string originalQuery, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&format=json";
            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("parse", out var parseElement))
                return null;

            if (!parseElement.TryGetProperty("wikitext", out var wikitextElement))
                return null;

            var wikitext = wikitextElement.GetProperty("*").GetString();
            if (string.IsNullOrEmpty(wikitext)) return null;

            var gameInfo = new GameSaveInfo
            {
                GameName = pageTitle.Replace('_', ' '),
                WikiPageTitle = pageTitle,
                Source = "PCGamingWiki"
            };

            // Extract Steam AppID
            var steamMatch = Regex.Match(wikitext, @"\{\{Steam\|([0-9]+)\}\}", RegexOptions.IgnoreCase);
            if (steamMatch.Success)
            {
                gameInfo.SteamAppId = steamMatch.Groups[1].Value;
            }
            else
            {
                var steamAppMatch = Regex.Match(wikitext, @"steam\s+app\s*id\s*=\s*([0-9]+)", RegexOptions.IgnoreCase);
                if (steamAppMatch.Success)
                {
                    gameInfo.SteamAppId = steamAppMatch.Groups[1].Value;
                }
            }

            // Extract Windows save patterns
            var patterns = ExtractWindowsSavePatterns(wikitext);
            gameInfo.RawPatterns = patterns;

            // If we have Steam AppID, add Steam cloud save candidate pattern
            if (!string.IsNullOrEmpty(gameInfo.SteamAppId))
            {
                var steamCloudPath = $@"{{{{p|steam}}}}\userdata\{{{{p|uid}}}}\{gameInfo.SteamAppId}\remote";
                if (!gameInfo.RawPatterns.Any(p => p.Contains(gameInfo.SteamAppId)))
                {
                    gameInfo.RawPatterns.Add(steamCloudPath);
                }
            }

            return gameInfo;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractWindowsSavePatterns(string wikitext)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern 1: {{Game data/saves|Windows|...}}
        var matches = Regex.Matches(wikitext, @"\{\{Game data/saves\|Windows\|([^\|\}]+)", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var p = CleanPattern(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(p)) patterns.Add(p);
        }

        // Pattern 2: {{Game data/saves|...}} where Windows might be first or second parameter
        var generalMatches = Regex.Matches(wikitext, @"\{\{Game data/saves\|([^\{\}]+)\}\}", RegexOptions.IgnoreCase);
        foreach (Match gm in generalMatches)
        {
            var content = gm.Groups[1].Value;
            var parts = content.Split('|');
            foreach (var part in parts)
            {
                if (part.Contains("{{p|", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase) ||
                    part.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
                {
                    var p = CleanPattern(part);
                    if (!string.IsNullOrWhiteSpace(p)) patterns.Add(p);
                }
            }
        }

        // Pattern 3: Search within Save game data location section
        var saveSectionMatch = Regex.Match(wikitext, @"===\s*Save game data location\s*===([\s\S]*?)(?:===|==|\z)", RegexOptions.IgnoreCase);
        if (saveSectionMatch.Success)
        {
            var sectionText = saveSectionMatch.Groups[1].Value;
            var lineMatches = Regex.Matches(sectionText, @"(\{\{p\|[^\r\n\}]+\}\}[^\r\n\|\}]*)", RegexOptions.IgnoreCase);
            foreach (Match lm in lineMatches)
            {
                var p = CleanPattern(lm.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(p)) patterns.Add(p);
            }
        }

        return patterns.ToList();
    }

    private static string CleanPattern(string p)
    {
        p = p.Trim();
        // Remove trailing comment or notes like <ref ...> or {{note|...}}
        p = Regex.Replace(p, @"<ref[\s\S]*?(?:/>|</ref>)", "");
        p = Regex.Replace(p, @"\{\{note\|[\s\S]*?\}\}", "");
        p = Regex.Replace(p, @"\[\[.*?\|(.*?)\]\]", "$1"); // [[link|text]] -> text
        p = Regex.Replace(p, @"\[\[(.*?)\]\]", "$1");
        return p.Trim();
    }
}
