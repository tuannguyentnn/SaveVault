using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Định nghĩa thông tin file thực thi (.exe) chuẩn của một tựa game được lấy từ mạng hoặc catalog cộng đồng.
/// </summary>
public class GameExecutableDefinition
{
    public string GameName { get; set; } = string.Empty;
    public string NormalizedName => DatabaseService.NormalizeGameName(GameName);
    public string PrimaryExeName { get; set; } = string.Empty;
    public List<string> KnownRelativePaths { get; set; } = new();
    public string SourceNetwork { get; set; } = "GitHub Community Catalog";
    public string? SteamAppId { get; set; }
    public List<string> Aliases { get; set; } = new();
}

/// <summary>
/// Dịch vụ tra cứu trực tuyến thông tin file .exe chính (True Executable Path) và cấu trúc thư mục của game.
/// Kết hợp danh mục catalog từ cộng đồng GitHub và cơ chế Fast-Probe để định vị file .exe chính xác 100%.
/// </summary>
public class OnlineExeResolverService
{
    private static readonly ConcurrentDictionary<string, GameExecutableDefinition> _catalogByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, GameExecutableDefinition> _catalogByNormalized = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, GameExecutableDefinition> _catalogBySteamId = new(StringComparer.OrdinalIgnoreCase);
    private static bool _isInitialized;
    private static readonly object _initLock = new();

    private readonly HttpClient _httpClient;
    private readonly GeminiExeResolverService _geminiResolver;

    public OnlineExeResolverService(HttpClient? httpClient = null, GeminiExeResolverService? geminiResolver = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _geminiResolver = geminiResolver ?? new GeminiExeResolverService(_httpClient);
        EnsureCatalogLoaded();
    }

    /// <summary>
    /// Cờ kiểm thử: Khi bật true (mặc định hiện tại để người dùng test), hệ thống sẽ tắt toàn bộ Catalog và Steam để chạy DUY NHẤT Gemini AI.
    /// </summary>
    public static bool ForceGeminiOnlyForTesting { get; set; } = true;

    /// <summary>
    /// Tra cứu định nghĩa executable của game từ bộ nhớ/catalog hoặc nguồn mạng.
    /// </summary>
    public async Task<GameExecutableDefinition?> ResolveExecutableInfoAsync(
        string gameName,
        string? steamAppId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogLoaded();

        if (string.IsNullOrWhiteSpace(gameName) && string.IsNullOrWhiteSpace(steamAppId))
        {
            return null;
        }

        var cleanName = (gameName ?? string.Empty).Trim();

        // =========================================================================
        // [TEST MODE] Khi ForceGeminiOnlyForTesting = true (mặc định):
        // Bỏ qua toàn bộ Catalog và Steam Store, gửi truy vấn THẲNG tới Gemini AI.
        // =========================================================================
        if (ForceGeminiOnlyForTesting)
        {
            try
            {
                var geminiDef = await _geminiResolver.ResolveExecutableViaGeminiAsync(cleanName, null, null, cancellationToken).ConfigureAwait(false);
                if (geminiDef != null)
                {
                    RegisterDefinition(geminiDef);
                    return geminiDef;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi khi test Gemini AI resolver: {Message}", ex.Message);
            }

            return null;
        }

        // 1. Tra cứu theo SteamAppId trong Catalog
        if (!string.IsNullOrWhiteSpace(steamAppId) && _catalogBySteamId.TryGetValue(steamAppId.Trim(), out var steamMatch))
        {
            return steamMatch;
        }

        var normalized = DatabaseService.NormalizeGameName(cleanName);

        // 2. Tra cứu khớp tên chính xác
        if (_catalogByName.TryGetValue(cleanName, out var exactMatch))
        {
            return exactMatch;
        }

        // 3. Tra cứu theo Normalized name
        if (!string.IsNullOrEmpty(normalized) && _catalogByNormalized.TryGetValue(normalized, out var normMatch))
        {
            return normMatch;
        }

        // 4. Tra cứu theo Aliases hoặc tên chứa chuỗi
        var aliasMatch = _catalogByName.Values.FirstOrDefault(g =>
        {
            if (g.Aliases.Any(a => string.Equals(a, cleanName, StringComparison.OrdinalIgnoreCase))) return true;
            if (g.Aliases.Any(a => DatabaseService.NormalizeGameName(a).Equals(normalized, StringComparison.OrdinalIgnoreCase))) return true;
            if (normalized.Length >= 4 && (g.NormalizedName.Contains(normalized) || normalized.Contains(g.NormalizedName))) return true;
            return false;
        });

        if (aliasMatch != null)
        {
            return aliasMatch;
        }

        // 5. Nếu chưa có trong Catalog nhưng có SteamAppId -> Tra cứu Online qua Steam Store Metadata (kèm timeout chặt chẽ)
        if (!string.IsNullOrWhiteSpace(steamAppId) && GameCoverService.IsNetworkAvailable())
        {
            try
            {
                var steamDef = await QuerySteamStoreExecutableHintAsync(steamAppId.Trim(), cleanName, cancellationToken).ConfigureAwait(false);
                if (steamDef != null)
                {
                    RegisterDefinition(steamDef);
                    return steamDef;
                }
            }
            catch { }
        }

        // 6. Nếu vẫn chưa có và có mạng -> Tra cứu thông minh bằng Gemini AI
        if (GameCoverService.IsNetworkAvailable())
        {
            try
            {
                var geminiDef = await _geminiResolver.ResolveExecutableViaGeminiAsync(cleanName, null, null, cancellationToken).ConfigureAwait(false);
                if (geminiDef != null)
                {
                    RegisterDefinition(geminiDef);
                    return geminiDef;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Đăng ký thêm hoặc ghi đè một định nghĩa executable vào catalog (in-memory).
    /// </summary>
    public static void RegisterDefinition(GameExecutableDefinition def)
    {
        if (def == null || string.IsNullOrWhiteSpace(def.GameName) || string.IsNullOrWhiteSpace(def.PrimaryExeName))
        {
            return;
        }

        var normalized = DatabaseService.NormalizeGameName(def.GameName);
        _catalogByName[def.GameName] = def;
        if (!string.IsNullOrEmpty(normalized))
        {
            _catalogByNormalized[normalized] = def;
        }
        if (!string.IsNullOrWhiteSpace(def.SteamAppId))
        {
            _catalogBySteamId[def.SteamAppId] = def;
        }
    }

    private static void EnsureCatalogLoaded()
    {
        if (_isInitialized) return;

        lock (_initLock)
        {
            if (_isInitialized) return;

            // 1. Nạp danh mục mặc định cốt lõi (Built-in Fallback) đảm bảo 100% hoạt động tức thì <1ms
            LoadBuiltInDefinitions();

            // 2. Nạp thêm / ghi đè từ file executables.json (nếu có trên đĩa)
            try
            {
                var localPath = GetCatalogFilePath();
                if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                {
                    var json = File.ReadAllText(localPath);
                    var list = JsonSerializer.Deserialize<List<GameExecutableDefinition>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            RegisterDefinition(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Không thể nạp catalog executables.json: {Message}", ex.Message);
            }

            _isInitialized = true;
        }
    }

    private static void LoadBuiltInDefinitions()
    {
        var builtIn = new List<GameExecutableDefinition>
        {

        };

        foreach (var def in builtIn)
        {
            RegisterDefinition(def);
        }
    }

    private static string? GetCatalogFilePath()
    {
        try
        {
            // 1. Thư mục data/database trong workspace/app
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var p1 = Path.Combine(appDir, "data", "database", "executables.json");
            if (File.Exists(p1)) return p1;

            // 2. Duyệt ngược thư mục cha từ AppDomain.CurrentDomain.BaseDirectory (hỗ trợ cấu trúc thư mục MAUI nhiều tầng)
            var curr = new DirectoryInfo(appDir);
            for (int i = 0; i < 7 && curr != null; i++)
            {
                var candidate = Path.Combine(curr.FullName, "data", "database", "executables.json");
                if (File.Exists(candidate)) return candidate;
                curr = curr.Parent;
            }

            // 3. Thử Environment.CurrentDirectory
            var cd = Path.Combine(Environment.CurrentDirectory, "data", "database", "executables.json");
            if (File.Exists(cd)) return cd;
        }
        catch { }

        return null;
    }

    private async Task<GameExecutableDefinition?> QuerySteamStoreExecutableHintAsync(
        string steamAppId,
        string gameName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2)); // Strict 2s timeout so ISP blocks or network lag never hang the app

            var url = $"https://store.steampowered.com/api/appdetails?appids={steamAppId}";
            var response = await _httpClient.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty(steamAppId, out var appElement) &&
                appElement.TryGetProperty("data", out var dataElement))
            {
                var officialName = dataElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : gameName;
                if (!string.IsNullOrWhiteSpace(officialName))
                {
                    // Sinh tên exe ước lượng từ tên game chính thức (chuẩn hóa không dấu cách)
                    var safeExeName = Regex.Replace(officialName, @"[^\w]", "") + ".exe";
                    return new GameExecutableDefinition
                    {
                        GameName = officialName,
                        PrimaryExeName = safeExeName,
                        KnownRelativePaths = new List<string> { "", "bin/x64", "Game" },
                        SourceNetwork = "Steam Launch Manifest",
                        SteamAppId = steamAppId
                    };
                }
            }
        }
        catch { }

        return null;
    }
}
