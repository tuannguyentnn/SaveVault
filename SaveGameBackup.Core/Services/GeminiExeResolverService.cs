using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Cấu trúc dữ liệu JSON phản hồi từ Gemini AI khi phân tích file thực thi game.
/// </summary>
public class GeminiExeResponse
{
    [JsonPropertyName("gameName")]
    public string? GameName { get; set; }

    [JsonPropertyName("primaryExeName")]
    public string? PrimaryExeName { get; set; }

    [JsonPropertyName("knownRelativePaths")]
    public List<string>? KnownRelativePaths { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Dịch vụ kết nối Google Gemini API (REST) để tra cứu thông minh tên file thực thi (.exe) chính,
/// cấu trúc thư mục con và alias cho các tựa game (đặc biệt là game lạ, game indie, repack) mà catalog chưa có.
/// Hoạt động qua API Key mà không cần người dùng cuối đăng nhập tài khoản.
/// </summary>
public class GeminiExeResolverService
{
    private readonly HttpClient _httpClient;
    private const string DefaultModel = "gemini-3.5-flash-lite";

    public GeminiExeResolverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    /// <summary>
    /// Tra cứu định nghĩa executable của game thông qua Gemini AI.
    /// </summary>
    public async Task<GameExecutableDefinition?> ResolveExecutableViaGeminiAsync(
        string gameName,
        string? customApiKey = null,
        string? customModel = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return null;
        }

        var config = AppConfigService.GetConfig();
        if (!config.EnableGeminiExeSearch && string.IsNullOrWhiteSpace(customApiKey))
        {
            return null;
        }

        var apiKey = !string.IsNullOrWhiteSpace(customApiKey) ? customApiKey.Trim() : config.GeminiApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LoggingService.Warn("Bỏ qua Gemini AI do chưa cấu hình Gemini API Key.");
            return null;
        }

        var rawModel = !string.IsNullOrWhiteSpace(customModel) 
            ? customModel.Trim() 
            : (!string.IsNullOrWhiteSpace(config.GeminiModel) ? config.GeminiModel.Trim() : DefaultModel);
        var model = rawModel.Contains("2.5") ? DefaultModel : rawModel;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(12)); // Cho phép tối đa 12s khi test Gemini AI

            LoggingService.LogAction("Gemini_AI_Resolving_Start", new { Game = gameName, Model = model });

            var prompt = $@"You are a PC gaming directory and executable expert.
Identify the primary Windows executable (.exe) and known relative paths within the game install folder for the PC game: ""{gameName}"".

CRITICAL RULES:
1. ""primaryExeName"" must be the REAL game gameplay executable (e.g. ""Cyberpunk2077.exe"", ""witcher3.exe"", ""b1-Win64-Shipping.exe"", ""eldenring.exe"", ""ff7rebirth.exe"").
2. DO NOT return helper launchers, crash reporters, anticheat or setup files (e.g. NOT REDprelauncher.exe, UnityCrashHandler64.exe, EasyAntiCheat, unins000.exe).
3. ""knownRelativePaths"" must list subfolders where this exe resides, such as ""bin/x64"", ""Game"", ""End/Binaries/Win64"", or """" if directly in the root directory.
4. Output strictly a JSON object conforming to this schema:
{{
  ""gameName"": ""{gameName}"",
  ""primaryExeName"": ""filename.exe"",
  ""knownRelativePaths"": [""bin/x64"", """"],
  ""aliases"": [""ShortName""]
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
                LoggingService.Warn("Gemini API tra cứu exe thất bại (Status {Status}): {Error}", response.StatusCode, errorText);
                return null;
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
                        var parsed = JsonSerializer.Deserialize<GeminiExeResponse>(text, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (parsed != null && !string.IsNullOrWhiteSpace(parsed.PrimaryExeName))
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

                            var def = new GameExecutableDefinition
                            {
                                GameName = string.IsNullOrWhiteSpace(parsed.GameName) ? gameName : parsed.GameName.Trim(),
                                PrimaryExeName = primaryExe,
                                KnownRelativePaths = relativePaths,
                                Aliases = parsed.Aliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? new List<string>(),
                                SourceNetwork = "Google Gemini AI"
                            };

                            LoggingService.LogAction("Gemini_Exe_Resolved", new
                            {
                                Game = gameName,
                                PrimaryExe = def.PrimaryExeName,
                                RelativePaths = def.KnownRelativePaths.Count > 0
                                    ? $"[{string.Join(", ", def.KnownRelativePaths.Select(p => string.IsNullOrEmpty(p) ? "\"\"" : $"\"{p}\""))}]"
                                    : "[\"\"]"
                            });

                            return def;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            LoggingService.Warn("Gemini API tra cứu exe cho game '{Game}' bị timeout (>4s)", gameName);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi kết nối Gemini API cho game '{Game}': {Message}", gameName, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Kiểm tra kết nối và tính hợp lệ của API Key.
    /// </summary>
    public async Task<(bool Success, string Message)> TestApiKeyAsync(
        string apiKey,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Vui lòng nhập API Key.");
        }

        var rawModel = !string.IsNullOrWhiteSpace(model) ? model.Trim() : DefaultModel;
        var selectedModel = rawModel.Contains("2.5") ? DefaultModel : rawModel;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = "Ping. Answer with 'OK'." }
                        }
                    }
                }
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(selectedModel)}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";
            var response = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"Kết nối Google Gemini thành công! (Model: {selectedModel})");
            }

            // Nếu model được chọn gặp lỗi 503 Service Unavailable (Google quá tải model này), thử fallback qua DefaultModel
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && selectedModel != DefaultModel)
            {
                try
                {
                    using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    fallbackCts.CancelAfter(TimeSpan.FromSeconds(10));

                    using var fallbackContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    var fallbackUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(DefaultModel)}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";
                    var fallbackResponse = await _httpClient.PostAsync(fallbackUrl, fallbackContent, fallbackCts.Token).ConfigureAwait(false);

                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        return (true, $"API Key hoàn toàn hợp lệ! (Lưu ý: Model '{selectedModel}' đang quá tải trên server Google (503), hệ thống đã xác thực thành công qua '{DefaultModel}')");
                    }
                }
                catch
                {
                    // Bỏ qua lỗi fallback để hiển thị thông báo lỗi của model chính
                }
            }

            var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                return (false, $"Máy chủ Google Gemini đang tạm thời quá tải đối với model '{selectedModel}' (Mã 503 Service Unavailable). Hãy đổi sang model '{DefaultModel}' và thử lại.");
            }

            return (false, $"Kết nối thất bại (Mã {response.StatusCode}): {err}");
        }
        catch (OperationCanceledException)
        {
            return (false, $"Lỗi kết nối: Quá thời gian chờ (Timeout sau 15 giây). Vui lòng thử lại hoặc đổi sang model '{DefaultModel}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi kết nối: {ex.Message}");
        }
    }
}
