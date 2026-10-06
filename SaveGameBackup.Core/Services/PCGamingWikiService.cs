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
            var steamMatch = Regex.Match(wikitext, @"(?:\{\{Steam(?:\s+AppID)?\|(?:app\|)?|steam(?:[\s_]+app)?[\s_]*id\s*=\s*|\|\s*steam\s*=\s*|store\.steampowered\.com/(?:app|news/app)/|steamdb\.info/app/)([0-9]+)", RegexOptions.IgnoreCase);
            if (steamMatch.Success)
            {
                gameInfo.SteamAppId = steamMatch.Groups[1].Value;
            }
            else
            {
                gameInfo.SteamAppId = new LudusaviManifestService().FindGame(gameInfo.GameName)?.SteamId;
            }

            if (string.IsNullOrEmpty(gameInfo.SteamAppId))
            {
                gameInfo.SteamAppId = await GameCoverService.FetchSteamAppIdFromStoreSearchAsync(gameInfo.GameName, cancellationToken);
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

        // 1. Phân tích cú pháp template {{Game data/saves ...}} có tính đến ngoặc lồng nhau {{...}}
        foreach (var tpl in ExtractTemplates(wikitext, "Game data/saves"))
        {
            var parameters = SplitTemplateParameters(tpl);
            if (parameters.Count < 2) continue;

            // Template thường có dạng:
            // Vị trí 0: Game data/saves
            // Vị trí 1: Hệ điều hành / Nền tảng (Windows, Steam, Microsoft Store, GOG.com, Steam Play (Linux)...)
            // Vị trí 2: Đường dẫn lưu game
            // Vị trí 3 (tùy chọn): Hệ điều hành phụ (ví dụ: Store | Path | Windows)
            string? detectedPlatform = null;
            string? detectedPath = null;

            // Kiểm tra positional parameters
            if (parameters.Count >= 3 && !parameters[1].Contains('=') && !parameters[2].Contains('='))
            {
                var p1 = parameters[1].Trim();
                var p2 = parameters[2].Trim();
                var p3 = parameters.Count > 3 ? parameters[3].Trim() : string.Empty;

                bool isNonWindows = p1.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
                                    p1.Contains("Steam Play", StringComparison.OrdinalIgnoreCase) ||
                                    p1.Contains("OS X", StringComparison.OrdinalIgnoreCase) ||
                                    p1.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
                                    p1.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                                    p1.Contains("iOS", StringComparison.OrdinalIgnoreCase);

                bool isWindows = p1.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                                 p1.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                                 p1.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
                                 p1.Contains("GOG", StringComparison.OrdinalIgnoreCase) ||
                                 p1.Contains("Epic", StringComparison.OrdinalIgnoreCase) ||
                                 p1.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                                 p3.Contains("Windows", StringComparison.OrdinalIgnoreCase);

                if (!isNonWindows && isWindows)
                {
                    detectedPlatform = p1;
                    detectedPath = p2;
                }
            }

            // Kiểm tra named parameters hoặc quét các tham số chứa path tag Windows
            if (string.IsNullOrEmpty(detectedPath))
            {
                foreach (var param in parameters.Skip(1))
                {
                    var kv = param.Split(new[] { '=' }, 2);
                    if (kv.Length == 2)
                    {
                        var key = kv[0].Trim();
                        var val = kv[1].Trim();
                        if (key.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                            key.Contains("Store", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedPath = val;
                            break;
                        }
                    }
                    else
                    {
                        var trimmed = param.Trim();
                        if (trimmed.Contains("{{p|localappdata}}", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("{{p|appdata}}", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("{{p|userprofile}}", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("{{p|savedgames}}", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("{{p|documents}}", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!trimmed.Contains("compatdata", StringComparison.OrdinalIgnoreCase) &&
                                !trimmed.Contains("/drive_c/", StringComparison.OrdinalIgnoreCase) &&
                                !trimmed.Contains("/home/", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedPath = trimmed;
                                break;
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(detectedPath))
            {
                var cleaned = CleanPattern(detectedPath);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    patterns.Add(cleaned);

                    // Nếu pattern kết thúc bằng {{p|uid}} hoặc tương đương (ví dụ: Sandfall\Saved\SaveGames\{{p|uid}}),
                    // cũng thêm luôn thư mục cha (Sandfall\Saved\SaveGames) vào RawPatterns
                    var parentPattern = Regex.Replace(cleaned, @"[\\/](?:\{\{p\|(?:uid|steamid|uplayid|originid|gogid|accountid)\}\}|<[^>]+>|%USERID%)[\\/]?$", "", RegexOptions.IgnoreCase);
                    if (!string.Equals(parentPattern, cleaned, StringComparison.OrdinalIgnoreCase))
                    {
                        var parentCleaned = CleanPattern(parentPattern);
                        if (!string.IsNullOrWhiteSpace(parentCleaned) &&
                            !parentCleaned.EndsWith(@"\userdata", StringComparison.OrdinalIgnoreCase) &&
                            !parentCleaned.EndsWith("/userdata", StringComparison.OrdinalIgnoreCase) &&
                            !parentCleaned.Equals("{{p|steam}}", StringComparison.OrdinalIgnoreCase))
                        {
                            patterns.Add(parentCleaned);
                        }
                    }
                }
            }
        }

        // 2. Tìm kiếm phụ trợ trong toàn bộ mục '=== Save game data location ==='
        var saveSectionMatch = Regex.Match(wikitext, @"===\s*Save game data location\s*===([\s\S]*?)(?:===|==|\z)", RegexOptions.IgnoreCase);
        if (saveSectionMatch.Success)
        {
            var sectionText = saveSectionMatch.Groups[1].Value;

            // Regex quét đường dẫn bắt đầu bằng root tag Windows và bao gồm cả các tag lồng {{p|...}} tiếp theo
            var pathRegex = new Regex(@"(?:\{\{p\|(?:localappdata|appdata|locallow|userprofile|documents|savedgames|programdata|steam)\}\}|%[A-Z0-9_]+%|[a-zA-Z]:\\)(?:[\\/][^\\/\r\n\|\}<>\[\]]+|\{\{p\|[^}]+\}\})*", RegexOptions.IgnoreCase);
            var lineMatches = pathRegex.Matches(sectionText);
            foreach (Match lm in lineMatches)
            {
                var p = CleanPattern(lm.Value);
                if (!string.IsNullOrWhiteSpace(p) && !p.Contains("compatdata", StringComparison.OrdinalIgnoreCase))
                {
                    patterns.Add(p);

                    var parentPattern = Regex.Replace(p, @"[\\/](?:\{\{p\|(?:uid|steamid|uplayid|originid|gogid|accountid)\}\}|<[^>]+>|%USERID%)[\\/]?$", "", RegexOptions.IgnoreCase);
                    if (!string.Equals(parentPattern, p, StringComparison.OrdinalIgnoreCase))
                    {
                        var parentCleaned = CleanPattern(parentPattern);
                        if (!string.IsNullOrWhiteSpace(parentCleaned) &&
                            !parentCleaned.EndsWith(@"\userdata", StringComparison.OrdinalIgnoreCase) &&
                            !parentCleaned.EndsWith("/userdata", StringComparison.OrdinalIgnoreCase) &&
                            !parentCleaned.Equals("{{p|steam}}", StringComparison.OrdinalIgnoreCase))
                        {
                            patterns.Add(parentCleaned);
                        }
                    }
                }
            }
        }

        return patterns.ToList();
    }

    private static IEnumerable<string> ExtractTemplates(string wikitext, string templateName)
    {
        var tag = "{{" + templateName;
        int startIndex = 0;
        while ((startIndex = wikitext.IndexOf(tag, startIndex, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            int depth = 0;
            int endIndex = -1;
            for (int i = startIndex; i < wikitext.Length - 1; i++)
            {
                if (wikitext[i] == '{' && wikitext[i + 1] == '{')
                {
                    depth++;
                    i++;
                }
                else if (wikitext[i] == '}' && wikitext[i + 1] == '}')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                    {
                        endIndex = i;
                        break;
                    }
                }
            }

            if (endIndex != -1)
            {
                yield return wikitext.Substring(startIndex, endIndex - startIndex + 1);
                startIndex = endIndex + 1;
            }
            else
            {
                startIndex += tag.Length;
            }
        }
    }

    private static List<string> SplitTemplateParameters(string templateText)
    {
        var parameters = new List<string>();
        if (templateText.StartsWith("{{") && templateText.EndsWith("}}"))
        {
            templateText = templateText.Substring(2, templateText.Length - 4);
        }

        int depthBraces = 0;
        int depthBrackets = 0;
        int lastPos = 0;

        for (int i = 0; i < templateText.Length; i++)
        {
            if (i < templateText.Length - 1 && templateText[i] == '{' && templateText[i + 1] == '{')
            {
                depthBraces++;
                i++;
            }
            else if (i < templateText.Length - 1 && templateText[i] == '}' && templateText[i + 1] == '}')
            {
                depthBraces--;
                i++;
            }
            else if (i < templateText.Length - 1 && templateText[i] == '[' && templateText[i + 1] == '[')
            {
                depthBrackets++;
                i++;
            }
            else if (i < templateText.Length - 1 && templateText[i] == ']' && templateText[i + 1] == ']')
            {
                depthBrackets--;
                i++;
            }
            else if (templateText[i] == '|' && depthBraces == 0 && depthBrackets == 0)
            {
                parameters.Add(templateText.Substring(lastPos, i - lastPos));
                lastPos = i + 1;
            }
        }

        if (lastPos < templateText.Length)
        {
            parameters.Add(templateText.Substring(lastPos));
        }

        return parameters;
    }

    private static string CleanPattern(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        p = p.Trim();

        // Xóa trailing comment hoặc notes
        p = Regex.Replace(p, @"<ref[\s\S]*?(?:/>|</ref>)", "");
        p = Regex.Replace(p, @"\{\{note\|[\s\S]*?\}\}", "");
        p = Regex.Replace(p, @"\[\[.*?\|(.*?)\]\]", "$1"); // [[link|text]] -> text
        p = Regex.Replace(p, @"\[\[(.*?)\]\]", "$1");

        // Bỏ {{file|...}} nếu có
        if (p.StartsWith("{{file|", StringComparison.OrdinalIgnoreCase) && p.EndsWith("}}"))
        {
            p = p.Substring(7, p.Length - 9).Trim();
        }

        // Loại bỏ phần đuôi chỉ file mask như \*.png, \*.sav, /*.dat để lấy đúng thư mục cha chứa save
        p = Regex.Replace(p, @"[\\/]\*(\.[a-zA-Z0-9_-]+)?$", "");

        // Cân bằng ngoặc nhọn nếu có ngoặc đóng dư thừa ở cuối
        int openBraces = p.Count(c => c == '{');
        int closeBraces = p.Count(c => c == '}');
        while (closeBraces > openBraces && p.EndsWith("}"))
        {
            p = p.Substring(0, p.Length - 1);
            closeBraces--;
        }

        // Loại bỏ thẻ dở dang hoặc dấu pipe thừa ở cuối nếu có
        p = Regex.Replace(p, @"[\\/]?\{\{p(?:\|[^}]*)?$", "", RegexOptions.IgnoreCase);
        p = p.TrimEnd('\\', '/', '|', ' ', '\t');

        // Bỏ nếu chỉ là thẻ {{p hoặc quá ngắn không phải đường dẫn hợp lệ
        if (p.Equals("{{p", StringComparison.OrdinalIgnoreCase) || p.Length < 4)
            return string.Empty;

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

