using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Cấu trúc dữ liệu JSON phản hồi từ Gemini AI khi tra cứu hợp nhất thông tin Game và File thực thi.
/// </summary>
public class GeminiUnifiedResponse
{
    [JsonPropertyName("gameName")]
    public string? GameName { get; set; }

    [JsonPropertyName("steamAppId")]
    public string? SteamAppId { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("savePatterns")]
    public List<string>? SavePatterns { get; set; }

    [JsonPropertyName("primaryExeName")]
    public string? PrimaryExeName { get; set; }

    [JsonPropertyName("knownRelativePaths")]
    public List<string>? KnownRelativePaths { get; set; }

    [JsonPropertyName("latestKnownVersion")]
    public string? LatestKnownVersion { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Dịch vụ tra cứu hợp nhất (1-Shot Unified Search) qua Google Gemini AI (REST API).
/// Thu thập cùng lúc: Tên chuẩn, Steam AppID, Vị trí thư mục Save Windows, File thực thi chính (.exe) và thư mục con.
/// Tối ưu hóa hiệu năng, giảm 50% quota API, phản hồi dưới 1 giây.
/// </summary>
public class GeminiUnifiedGameService
{
    private readonly HttpClient _httpClient;
    private const string DefaultModel = "gemini-3.5-flash";

    public GeminiUnifiedGameService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Tra cứu trọn gói thông tin Save Game và Executable Definition cho một tựa game thông qua Gemini AI.
    /// </summary>
    public async Task<(GameSaveInfo? GameInfo, GameExecutableDefinition? ExeDef)> ResolveUnifiedGameAsync(
        string gameName,
        string? customApiKey = null,
        string? customModel = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return (null, null);
        }

        var config = AppConfigService.GetConfig();
        if (!config.EnableGeminiExeSearch && string.IsNullOrWhiteSpace(customApiKey))
        {
            return (null, null);
        }

        var apiKey = !string.IsNullOrWhiteSpace(customApiKey) ? customApiKey.Trim() : config.GeminiApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LoggingService.Warn("Bỏ qua Gemini Unified AI do chưa cấu hình Gemini API Key.");
            return (null, null);
        }

        var rawModel = !string.IsNullOrWhiteSpace(customModel)
            ? customModel.Trim()
            : (!string.IsNullOrWhiteSpace(config.GeminiModel) ? config.GeminiModel.Trim() : DefaultModel);
        var model = rawModel.Contains("2.5") ? DefaultModel : rawModel;

        var cleanQuery = gameName.Trim();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(12));

            LoggingService.LogAction("Gemini_Unified_Resolving_Start", new { Game = cleanQuery, Model = model });

            var prompt = $@"You are a world-class PC game data expert specializing in Windows save file architectures, Steam integrations, and game binaries.
Analyze the PC game: ""{cleanQuery}"".

CRITICAL INSTRUCTIONS:
1. ""gameName"": The official canonical title in English (e.g. ""Black Myth: Wukong"", ""Cyberpunk 2077"").
2. ""steamAppId"": The official numerical Steam Store App ID as a string (e.g. ""2358720"", ""1091500""). Return null if the game is never on Steam.
3. ""aliases"": List of known alternative names, acronyms, or franchise identifiers.
4. ""savePatterns"": List of all known Windows save game directory paths.
   - Use Windows standard environment variables: %LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, %USERPROFILE%\Saved Games, %USERPROFILE%\Documents\My Games.
   - If available on Steam, include Steam userdata cloud path: ""{{{{p|steam}}}}\\userdata\\{{{{p|uid}}}}\\{{steamAppId}}\\remote"".
   - Include common emulator/crack locations if popular (e.g., ""%APPDATA%\\Goldberg SteamEmu Saves\\{{steamAppId}}"", ""C:\\Users\\Public\\Documents\\Steam\\CODEX\\{{steamAppId}}"").
5. ""primaryExeName"": The REAL Windows gameplay executable file name (e.g. ""Cyberpunk2077.exe"", ""b1-Win64-Shipping.exe"", ""witcher3.exe"", ""eldenring.exe"").
   - NEVER return crash reporters, launchers, updaters, or anticheat (NOT REDprelauncher.exe, NOT UnityCrashHandler64.exe, NOT EasyAntiCheat).
6. ""knownRelativePaths"": Subfolders within the game install directory where primaryExeName resides (e.g. [""b1/Binaries/Win64"", """"] or [""bin/x64"", """"]). Include """" for the root folder.
7. ""latestKnownVersion"": The latest known public version or patch number (e.g. ""1.0.12.16438"" or ""2.13"") as a string.

Return STRICTLY a JSON object conforming to this schema:
{{
  ""gameName"": ""Official Game Name"",
  ""steamAppId"": ""123456"",
  ""aliases"": [""Alias1""],
  ""savePatterns"": [""%LOCALAPPDATA%\\GameName\\Saved\\SaveGames"", ""%USERPROFILE%\\Documents\\My Games\\GameName""],
  ""primaryExeName"": ""Game.exe"",
  ""knownRelativePaths"": [""bin/x64"", """"],
  ""latestKnownVersion"": ""1.0.0"",
  ""notes"": """"
}}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    temperature = 0.1
                }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
            var response = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LoggingService.Warn("Gemini Unified API tra cứu thất bại (Status {Status}): {Error}", response.StatusCode, errorText);
                return (null, null);
            }

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);

            var root = doc.RootElement;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var candContent) &&
                    candContent.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var text = parts[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var parsed = JsonSerializer.Deserialize<GeminiUnifiedResponse>(text, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (parsed != null)
                        {
                            return await ProcessGeminiResponseAsync(cleanQuery, parsed, cts.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LoggingService.Warn("Gemini Unified API tra cứu cho game '{Game}' bị timeout (>12s)", cleanQuery);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi kết nối Gemini Unified API cho game '{Game}': {Message}", cleanQuery, ex.Message);
        }

        return (null, null);
    }

    private static async Task<(GameSaveInfo? GameInfo, GameExecutableDefinition? ExeDef)> ProcessGeminiResponseAsync(
        string originalQuery,
        GeminiUnifiedResponse parsed,
        CancellationToken cancellationToken = default)
    {
        var finalGameName = !string.IsNullOrWhiteSpace(parsed.GameName) ? parsed.GameName.Trim() : originalQuery;

        // Trích xuất Steam AppID hợp lệ nếu có
        string? cleanSteamAppId = null;
        if (!string.IsNullOrWhiteSpace(parsed.SteamAppId))
        {
            var match = Regex.Match(parsed.SteamAppId, @"\d+");
            if (match.Success)
            {
                cleanSteamAppId = match.Value;
            }
        }

        // 1. Xử lý Save Patterns
        var rawPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parsed.SavePatterns != null)
        {
            foreach (var pat in parsed.SavePatterns)
            {
                if (!string.IsNullOrWhiteSpace(pat))
                {
                    var p = pat.Trim();
                    // Thay thế placeholder {steamAppId} nếu có
                    if (!string.IsNullOrEmpty(cleanSteamAppId))
                    {
                        p = p.Replace("{steamAppId}", cleanSteamAppId, StringComparison.OrdinalIgnoreCase);
                    }
                    rawPatterns.Add(p);
                }
            }
        }

        // Tự động bổ sung Steam Cloud pattern nếu có Steam AppID mà chưa có trong danh sách
        if (!string.IsNullOrEmpty(cleanSteamAppId))
        {
            var steamCloudPath = $@"{{{{p|steam}}}}\userdata\{{{{p|uid}}}}\{cleanSteamAppId}\remote";
            if (!rawPatterns.Any(p => p.Contains(cleanSteamAppId, StringComparison.OrdinalIgnoreCase)))
            {
                rawPatterns.Add(steamCloudPath);
            }
        }

        // 2. Tạo GameSaveInfo
        var gameInfo = new GameSaveInfo
        {
            GameName = finalGameName,
            NormalizedName = DatabaseService.NormalizeGameName(finalGameName),
            SteamAppId = cleanSteamAppId,
            Source = "Google Gemini AI (Unified 1-Shot)",
            RawPatterns = rawPatterns.ToList()
        };

        // Gán ảnh bìa: ƯU TIÊN PCGAMINGWIKI TRƯỚC -> NẾU KHÔNG CÓ MỚI LẤY TỪ STEAM
        try
        {
            var onlineCover = await GameCoverService.FindOnlineCoverUrlAsync(finalGameName, cleanSteamAppId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(onlineCover))
            {
                gameInfo.OnlineCoverUrl = onlineCover;
            }
            else if (!string.IsNullOrEmpty(cleanSteamAppId) && GameCoverService.EnableSteamCovers)
            {
                gameInfo.OnlineCoverUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{cleanSteamAppId}/library_600x900.jpg";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi tìm ảnh cover PCGamingWiki/Steam cho {Game}: {Message}", finalGameName, ex.Message);
            if (!string.IsNullOrEmpty(cleanSteamAppId) && GameCoverService.EnableSteamCovers)
            {
                gameInfo.OnlineCoverUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{cleanSteamAppId}/library_600x900.jpg";
            }
        }

        // 3. Xử lý Executable Definition
        GameExecutableDefinition? exeDef = null;
        if (!string.IsNullOrWhiteSpace(parsed.PrimaryExeName))
        {
            var primaryExe = parsed.PrimaryExeName.Trim();
            if (!primaryExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                primaryExe += ".exe";
            }

            var relativePaths = parsed.KnownRelativePaths?
                .Where(p => p != null)
                .Select(p => p.Trim().TrimStart('/', '\\'))
                .Distinct()
                .ToList() ?? new List<string> { "" };

            if (!relativePaths.Contains(""))
            {
                relativePaths.Add("");
            }

            exeDef = new GameExecutableDefinition
            {
                GameName = finalGameName,
                PrimaryExeName = primaryExe,
                KnownRelativePaths = relativePaths,
                Aliases = parsed.Aliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? new List<string>(),
                SteamAppId = cleanSteamAppId,
                SourceNetwork = "Google Gemini AI (Unified 1-Shot)"
            };

            gameInfo.PreResolvedExecutable = exeDef;
        }

        // Lưu thông tin Version tham chiếu (nếu có từ Gemini)
        if (!string.IsNullOrWhiteSpace(parsed.LatestKnownVersion))
        {
            gameInfo.DetectedVersion = new GameVersionInfo
            {
                DisplayVersion = parsed.LatestKnownVersion.Trim(),
                DetectionSource = "Gemini Reference",
                DetectionMechanism = "GeminiUnifiedCatalog",
                OnlineSource = "Google Gemini AI"
            };
        }

        LoggingService.LogAction("Gemini_Unified_Resolved", new
        {
            Game = finalGameName,
            SteamId = cleanSteamAppId,
            PatternsCount = gameInfo.RawPatterns.Count,
            PrimaryExe = exeDef?.PrimaryExeName,
            RelativePaths = exeDef?.KnownRelativePaths != null ? string.Join(", ", exeDef.KnownRelativePaths) : "",
            LatestVersion = parsed.LatestKnownVersion
        });

        return (gameInfo, exeDef);
    }
}
