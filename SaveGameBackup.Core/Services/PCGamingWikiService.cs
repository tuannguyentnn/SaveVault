using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class PCGamingWikiService
{
    private readonly HttpClient _httpClient;
    private readonly IHeadlessBrowserService? _headlessBrowser;

    public PCGamingWikiService(HttpClient? httpClient = null, IHeadlessBrowserService? headlessBrowser = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _headlessBrowser = headlessBrowser;
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

    public async Task<List<string>> FindPageTitlesAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        try
        {
            var url = $"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(query.Trim())}&limit={limit}&format=json";
            var response = await GetResponseStringWithFallbackAsync(url, cancellationToken);
            if (string.IsNullOrEmpty(response)) return results;

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
            {
                var titles = root[1];
                if (titles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in titles.EnumerateArray())
                    {
                        var title = item.GetString();
                        if (!string.IsNullOrWhiteSpace(title) && !results.Contains(title, StringComparer.OrdinalIgnoreCase))
                        {
                            results.Add(title);
                            if (results.Count >= limit) break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Network or parse error
        }

        return results;
    }

    public async Task<string?> FindPageTitleAsync(string query, CancellationToken cancellationToken = default)
    {
        var titles = await FindPageTitlesAsync(query, 1, cancellationToken);
        return titles.FirstOrDefault();
    }

    public async Task<GameSaveInfo?> FetchPageSaveDataAsync(string pageTitle, string originalQuery, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&format=json&redirects=1";
            var response = await GetResponseStringWithFallbackAsync(url, cancellationToken);
            if (string.IsNullOrEmpty(response)) return null;

            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("parse", out var parseElement))
                return null;

            var actualTitle = pageTitle;
            if (parseElement.TryGetProperty("title", out var titleEl) && !string.IsNullOrEmpty(titleEl.GetString()))
            {
                actualTitle = titleEl.GetString()!;
            }

            if (!parseElement.TryGetProperty("wikitext", out var wikitextElement))
                return null;

            var wikitext = wikitextElement.GetProperty("*").GetString();
            if (string.IsNullOrEmpty(wikitext)) return null;

            // Xử lý trường hợp wikitext chứa #REDIRECT [[Tên trang khác]]
            var redirectMatch = Regex.Match(wikitext, @"#REDIRECT\s*\[\[(.*?)\]\]", RegexOptions.IgnoreCase);
            if (redirectMatch.Success)
            {
                var targetTitle = redirectMatch.Groups[1].Value.Trim();
                return await FetchPageSaveDataAsync(targetTitle, originalQuery, cancellationToken);
            }

            var officialName = actualTitle.Replace('_', ' ').Trim();

            var gameInfo = new GameSaveInfo
            {
                GameName = officialName,
                WikiPageTitle = actualTitle,
                Source = "PCGamingWiki"
            };

            // Extract Steam AppID
            // Extract Steam AppID (hỗ trợ nhiều format template wiki phổ biến)
            var steamMatch = Regex.Match(wikitext, @"(?:\{\{Steam\||steam(?:\s+app)?\s*id\s*=\s*|\|\s*steam\s*=\s*|store\.steampowered\.com/(?:app|news/app)/)([0-9]+)", RegexOptions.IgnoreCase);
            if (steamMatch.Success)
            {
                gameInfo.SteamAppId = steamMatch.Groups[1].Value;
            }
            else
            {
                gameInfo.SteamAppId = new LudusaviManifestService().FindGame(gameInfo.GameName)?.SteamId;
            }

            // Extract Cover image URL (ƯU TIÊN PCGAMINGWIKI TRƯỚC -> STEAM)
            gameInfo.OnlineCoverUrl = await FetchDirectCoverUrlAsync(wikitext, gameInfo.SteamAppId, cancellationToken);

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

    private async Task<string?> FetchDirectCoverUrlAsync(string wikitext, string? steamAppId, CancellationToken cancellationToken)
    {
        try
        {
            // 1. ƯU TIÊN PCGAMINGWIKI TRƯỚC: MediaWiki imageinfo API
            var match = Regex.Match(wikitext, @"cover\s*=\s*([^\r\n\|\}]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var coverFileName = match.Groups[1].Value.Trim().Trim('[', ']');
                if (coverFileName.Contains('|')) coverFileName = coverFileName.Split('|')[0].Trim();
                if (!string.IsNullOrEmpty(coverFileName))
                {
                    var fileTitle = coverFileName.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
                        ? coverFileName
                        : $"File:{coverFileName}";

                    var imageInfoUrl = $"https://www.pcgamingwiki.com/w/api.php?action=query&titles={Uri.EscapeDataString(fileTitle)}&prop=imageinfo&iiprop=url&format=json&redirects=1";
                    var imageInfoResp = await GetResponseStringWithFallbackAsync(imageInfoUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(imageInfoResp))
                    {
                        using var imageDoc = JsonDocument.Parse(imageInfoResp);
                        if (imageDoc.RootElement.TryGetProperty("query", out var queryEl) &&
                            queryEl.TryGetProperty("pages", out var pagesEl))
                        {
                            foreach (var page in pagesEl.EnumerateObject())
                            {
                                if (page.Value.TryGetProperty("imageinfo", out var iiArray) && iiArray.GetArrayLength() > 0)
                                {
                                    var firstInfo = iiArray[0];
                                    if (firstInfo.TryGetProperty("url", out var urlProp) && !string.IsNullOrEmpty(urlProp.GetString()))
                                    {
                                        return urlProp.GetString();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 2. Không có ảnh từ PCGamingWiki -> Mới lấy từ Steam CDN (tạm thời ẩn nếu EnableSteamCovers = false)
            if (GameCoverService.EnableSteamCovers && !string.IsNullOrEmpty(steamAppId))
            {
                return $"https://shared.steamstatic.com/store_item_assets/steam/apps/{steamAppId}/library_600x900.jpg";
            }
        }
        catch
        {
            // Bỏ qua lỗi mạng khi lấy ảnh preview
        }

        return null;
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

        // Loại bỏ phần đuôi chỉ file mask như \*.png, \*.sav, /*.dat để lấy đúng thư mục cha chứa save
        p = Regex.Replace(p, @"[\\/]\*(\.[a-zA-Z0-9_-]+)?$", "");

        return p.Trim();
    }

    private async Task<string?> GetResponseStringWithFallbackAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(url, cancellationToken);
            if (!string.IsNullOrEmpty(response) &&
                !response.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) &&
                !response.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase))
            {
                return response;
            }
        }
        catch
        {
            // Có thể bị chặn HTTP 403 Forbidden do Cloudflare
        }

        // Tầng 4: Vượt Cloudflare qua Edge WebView2 Headless nếu được cấp
        if (_headlessBrowser != null && _headlessBrowser.IsAvailable)
        {
            LoggingService.LogAction("PCGamingWiki_Cloudflare_Bypass_Triggered", new { Url = url });
            return await _headlessBrowser.FetchPageContentAsync(url, 12, cancellationToken);
        }

        return null;
    }
}

