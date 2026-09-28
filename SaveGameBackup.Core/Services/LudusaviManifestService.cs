using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class LudusaviGameEntry
{
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? SteamId { get; set; }
    public List<string> SavePatterns { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
}

public class ManifestSyncProgress
{
    public int Percent { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SubText { get; set; } = string.Empty;

    public ManifestSyncProgress(int percent, string status, string subText = "")
    {
        Percent = percent;
        Status = status;
        SubText = subText;
    }
}

/// <summary>
/// Dịch vụ tra cứu cấu hình Save Game từ cơ sở dữ liệu Ludusavi Manifest (Open-source PC Gaming save database).
/// Hoạt động 100% không phụ thuộc PCGamingWiki, không bị Cloudflare chặn và hỗ trợ đồng bộ từ GitHub Raw.
/// </summary>
public class LudusaviManifestService
{
    public const string ManifestGithubUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, LudusaviGameEntry> _entriesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LudusaviGameEntry> _entriesByNormalized = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LudusaviGameEntry> _entriesBySteamId = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;
    private readonly object _loadLock = new();

    public LudusaviManifestService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        EnsurePreloadedCatalog();
    }

    public int TotalGamesCount => _entriesByName.Count;

    public LudusaviGameEntry? FindGame(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var clean = query.Trim();
        var normalized = DatabaseService.NormalizeGameName(clean);

        // 1. Khớp chính xác tên game
        if (_entriesByName.TryGetValue(clean, out var exactMatch))
        {
            return exactMatch;
        }

        // 2. Khớp theo Normalized name
        if (!string.IsNullOrEmpty(normalized) && _entriesByNormalized.TryGetValue(normalized, out var normMatch))
        {
            return normMatch;
        }

        // 3. Khớp tương đối theo tiền tố hoặc chứa chuỗi
        var partial = _entriesByName.Values.FirstOrDefault(g =>
        {
            if (string.Equals(g.Name, clean, StringComparison.OrdinalIgnoreCase)) return true;
            if (g.NormalizedName.Contains(normalized) || normalized.Contains(g.NormalizedName)) return true;
            return g.Aliases.Any(a =>
            {
                var normA = DatabaseService.NormalizeGameName(a);
                return !string.IsNullOrEmpty(normA) && normA.Length >= 3 &&
                       (normalized.Contains(normA) || normA.Contains(normalized));
            });
        });

        return partial;
    }

    public GameSaveInfo? CreateGameSaveInfo(string query)
    {
        var entry = FindGame(query);
        if (entry == null) return null;

        var info = new GameSaveInfo
        {
            GameName = entry.Name,
            NormalizedName = entry.NormalizedName,
            SteamAppId = entry.SteamId,
            Source = "Ludusavi Manifest",
            RawPatterns = new List<string>(entry.SavePatterns)
        };

        if (!string.IsNullOrEmpty(entry.SteamId))
        {
            info.OnlineCoverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{entry.SteamId}/header.jpg";

            var steamCloudPath = $@"{{{{p|steam}}}}\userdata\{{{{p|uid}}}}\{entry.SteamId}\remote";
            if (!info.RawPatterns.Contains(steamCloudPath, StringComparer.OrdinalIgnoreCase))
            {
                info.RawPatterns.Add(steamCloudPath);
            }
        }

        return info;
    }

    public void AddOrUpdateGame(LudusaviGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return;

        entry.NormalizedName = DatabaseService.NormalizeGameName(entry.Name);
        _entriesByName[entry.Name] = entry;

        if (!string.IsNullOrEmpty(entry.NormalizedName))
        {
            _entriesByNormalized[entry.NormalizedName] = entry;
        }

        if (!string.IsNullOrEmpty(entry.SteamId))
        {
            _entriesBySteamId[entry.SteamId] = entry;
        }
    }

    public static string ConvertLudusaviPathToSaveVaultPattern(string ludusaviPath)
    {
        if (string.IsNullOrWhiteSpace(ludusaviPath)) return string.Empty;

        var converted = ludusaviPath.Trim();

        // Chuyển đổi các placeholder của Ludusavi sang SaveVault
        converted = Regex.Replace(converted, @"<winLocalAppData>", "{{p|localappdata}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<winAppData>", "{{p|appdata}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<winDocuments>", "{{p|documents}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<winSavedGames>", "{{p|savedgames}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<winProgramData>", "{{p|programdata}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<home>", "{{p|userprofile}}", RegexOptions.IgnoreCase);
        converted = Regex.Replace(converted, @"<winPackage>", @"{{p|localappdata}}\Packages", RegexOptions.IgnoreCase);

        // Chuẩn hóa dấu gạch chéo
        converted = converted.Replace('/', '\\');

        // Bỏ các mask chỉ đuôi file thừa
        converted = Regex.Replace(converted, @"\\(\*\.[a-zA-Z0-9_-]+|\*|\*\*\/\*)$", "");

        return converted.Trim();
    }

    public static string GetLocalManifestPath()
    {
        return Path.Combine(DatabaseService.GetDefaultProjectRoot(), "data", "database", "manifest.yaml");
    }

    /// <summary>
    /// Đồng bộ tải manifest mới nhất từ GitHub Raw và nạp vào bộ nhớ kèm tiến trình thời gian thực.
    /// </summary>
    public async Task<int> SyncFromGithubAsync(IProgress<ManifestSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new ManifestSyncProgress(0, "Đang kết nối tới máy chủ GitHub...", "0%"));
        LoggingService.LogAction("Ludusavi_Sync_Start", new { Url = ManifestGithubUrl });

        try
        {
            using var response = await _httpClient.GetAsync(ManifestGithubUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memStream = new MemoryStream();

            var buffer = new byte[64 * 1024]; // 64 KB chunk
            long totalRead = 0;
            int bytesRead;

            progress?.Report(new ManifestSyncProgress(5, "Đang tải dữ liệu Ludusavi Manifest từ GitHub...", "Bắt đầu tải"));

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await memStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    // Tải chiếm 5% -> 60% tiến trình tổng
                    int downloadPercent = Math.Clamp((int)(5 + (totalRead * 55.0 / totalBytes.Value)), 5, 60);
                    string sub = $"{totalRead / (1024.0 * 1024.0):F2} MB / {totalBytes.Value / (1024.0 * 1024.0):F2} MB";
                    progress?.Report(new ManifestSyncProgress(downloadPercent, "Đang tải dữ liệu Ludusavi Manifest từ GitHub...", sub));
                }
                else
                {
                    int downloadPercent = Math.Clamp((int)(5 + (totalRead / (120 * 1024))), 5, 60);
                    string sub = $"{totalRead / (1024.0 * 1024.0):F2} MB";
                    progress?.Report(new ManifestSyncProgress(downloadPercent, "Đang tải dữ liệu Ludusavi Manifest từ GitHub...", sub));
                }
            }

            // Lưu cache cục bộ ra đĩa để khởi động offline lần sau nhanh hơn
            try
            {
                var localFile = GetLocalManifestPath();
                var dir = Path.GetDirectoryName(localFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllBytesAsync(localFile, memStream.ToArray(), cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Không thể lưu cache manifest.yaml ra đĩa: {Message}", ex.Message);
            }

            memStream.Position = 0;
            progress?.Report(new ManifestSyncProgress(65, "Đang phân tích và nạp cấu hình tựa game...", "Chuẩn bị nạp..."));

            using var reader = new StreamReader(memStream);
            long totalLength = memStream.Length;
            int parsedCount = ParseYamlStream(reader, totalLength, progress, cancellationToken);

            LoggingService.LogAction("Ludusavi_Sync_Success", new { TotalImported = parsedCount });
            try
            {
                var cfg = AppConfigService.GetConfig();
                cfg.LastLudusaviManifestSync = DateTime.Now;
                AppConfigService.SaveConfig(cfg);
            }
            catch { }

            progress?.Report(new ManifestSyncProgress(100, $"Đồng bộ thành công {parsedCount:N0} tựa game!", $"Tổng cộng {parsedCount:N0} game"));
            return parsedCount;
        }
        catch (OperationCanceledException)
        {
            LoggingService.LogAction("Ludusavi_Sync_Canceled", new { Url = ManifestGithubUrl });
            progress?.Report(new ManifestSyncProgress(0, "Đã hủy thao tác đồng bộ từ GitHub.", "Đã hủy"));
            return 0;
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("Ludusavi_Sync_Failed", new { Message = ex.Message }, level: "Error", ex: ex);
            progress?.Report(new ManifestSyncProgress(0, $"Đồng bộ thất bại: {ex.Message}", "Lỗi mạng"));
            return 0;
        }
    }

    public Task<int> SyncFromGithubAsync(IProgress<string>? textProgress, CancellationToken cancellationToken = default)
    {
        var progress = textProgress != null ? new Progress<ManifestSyncProgress>(p => textProgress.Report(p.Status)) : null;
        return SyncFromGithubAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Kiểm tra xem đã đến thời hạn tự động cập nhật Ludusavi Manifest hay chưa.
    /// </summary>
    public static bool IsUpdateDue()
    {
        var config = AppConfigService.GetConfig();
        if (!config.AutoUpdateLudusaviManifest)
        {
            return false;
        }

        int intervalDays = config.LudusaviAutoUpdateDays > 0 ? config.LudusaviAutoUpdateDays : 15;

        DateTime lastSync;
        if (config.LastLudusaviManifestSync.HasValue)
        {
            lastSync = config.LastLudusaviManifestSync.Value;
        }
        else
        {
            var localFile = GetLocalManifestPath();
            if (File.Exists(localFile))
            {
                lastSync = File.GetLastWriteTime(localFile);
            }
            else
            {
                return true;
            }
        }

        return (DateTime.Now - lastSync).TotalDays >= intervalDays;
    }

    /// <summary>
    /// Tự động kiểm tra và đồng bộ ngầm nếu đã quá chu kỳ ngày kể từ lần cập nhật gần nhất.
    /// Chạy an toàn không làm ảnh hưởng luồng chính của người dùng.
    /// </summary>
    public async Task<int> CheckAndAutoSyncIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (!IsUpdateDue())
        {
            return 0;
        }

        var config = AppConfigService.GetConfig();
        int intervalDays = config.LudusaviAutoUpdateDays > 0 ? config.LudusaviAutoUpdateDays : 15;

        LoggingService.LogAction("Ludusavi_AutoSync_Triggered", new { IntervalDays = intervalDays });
        try
        {
            int count = await SyncFromGithubAsync(progress: null, cancellationToken);
            if (count > 0)
            {
                LoggingService.LogAction("Ludusavi_AutoSync_Completed", new { TotalGames = count });
            }
            return count;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Tự động cập nhật Ludusavi Manifest thất bại: {Message}", ex.Message);
            return 0;
        }
    }

    private int ParseYamlStream(
        TextReader reader,
        long totalLength = 0,
        IProgress<ManifestSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? line;
        string? currentGame = null;
        string? currentSteamId = null;
        var currentPatterns = new List<string>();
        int count = 0;
        int linesRead = 0;

        while ((line = reader.ReadLine()) != null)
        {
            linesRead++;
            if (linesRead % 400 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (progress != null && totalLength > 0 && reader is StreamReader sr && sr.BaseStream.CanSeek)
                {
                    long currentPos = sr.BaseStream.Position;
                    int percent = Math.Clamp(65 + (int)(currentPos * 34.0 / totalLength), 65, 99);
                    progress.Report(new ManifestSyncProgress(percent, "Đang phân tích và nạp cấu hình tựa game...", $"Đã nạp {count:N0} game"));
                }
            }

            // Các dòng cấp cao nhất là tên game (không thụt lề đầu dòng)
            if (!line.StartsWith(" ") && !line.StartsWith("\t") && line.EndsWith(":") && !line.StartsWith("---"))
            {
                // Lưu game trước đó nếu có
                if (!string.IsNullOrEmpty(currentGame) && currentPatterns.Count > 0)
                {
                    AddOrUpdateGame(new LudusaviGameEntry
                    {
                        Name = currentGame,
                        SteamId = currentSteamId,
                        SavePatterns = new List<string>(currentPatterns)
                    });
                    count++;
                }

                currentGame = line.TrimEnd(':').Trim('\"', '\'');
                currentSteamId = null;
                currentPatterns.Clear();
                continue;
            }

            // Kiểm tra Steam ID
            var steamMatch = Regex.Match(line, @"id:\s*([0-9]+)");
            if (steamMatch.Success && currentSteamId == null)
            {
                currentSteamId = steamMatch.Groups[1].Value;
            }

            // Kiểm tra đường dẫn file save
            if (line.Contains("<win") || line.Contains("<home>"))
            {
                var pathMatch = Regex.Match(line, @"(<win[a-zA-Z]+>[^:\""'\r\n]+|<home>[^:\""'\r\n]+)");
                if (pathMatch.Success)
                {
                    var raw = pathMatch.Groups[1].Value;
                    var converted = ConvertLudusaviPathToSaveVaultPattern(raw);
                    if (!string.IsNullOrWhiteSpace(converted) && !currentPatterns.Contains(converted, StringComparer.OrdinalIgnoreCase))
                    {
                        currentPatterns.Add(converted);
                    }
                }
            }
        }

        // Lưu game cuối cùng
        if (!string.IsNullOrEmpty(currentGame) && currentPatterns.Count > 0)
        {
            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = currentGame,
                SteamId = currentSteamId,
                SavePatterns = new List<string>(currentPatterns)
            });
            count++;
        }

        return count;
    }

    private void EnsurePreloadedCatalog()
    {
        lock (_loadLock)
        {
            if (_isLoaded) return;

            var localFile = GetLocalManifestPath();
            if (!File.Exists(localFile))
            {
                var legacyPath = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "manifest.yaml");
                if (File.Exists(legacyPath))
                {
                    localFile = legacyPath;
                }
            }

            if (File.Exists(localFile))
            {
                try
                {
                    using var reader = File.OpenText(localFile);
                    ParseYamlStream(reader);
                    _isLoaded = true;
                    return;
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Không thể nạp manifest.yaml cục bộ: {Message}", ex.Message);
                }
            }

            // Nạp trước các tựa game bom tấn phổ biến nhất để sẵn sàng 100% offline ngay lập tức
            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Palworld",
                SteamId = "1623730",
                Aliases = new List<string> { "pal", "pocketpair" },
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\Pal\Saved\SaveGames",
                    @"{{p|localappdata}}\Packages\PocketpairInc.Palworld_*\SystemAppData\wgs"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Hogwarts Legacy",
                SteamId = "990080",
                Aliases = new List<string> { "phoenix", "hogwarts" },
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\Phoenix\Saved\SaveGames",
                    @"{{p|localappdata}}\HogwartsLegacy\Saved\SaveGames"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Black Myth: Wukong",
                SteamId = "2358720",
                Aliases = new List<string> { "b1", "wukong", "black myth wukong" },
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\b1\Saved\SaveGames"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Cyberpunk 2077",
                SteamId = "1091500",
                Aliases = new List<string> { "cp2077" },
                SavePatterns = new List<string>
                {
                    @"{{p|savedgames}}\CD Projekt Red\Cyberpunk 2077"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Elden Ring",
                SteamId = "1245620",
                Aliases = new List<string> { "er" },
                SavePatterns = new List<string>
                {
                    @"{{p|appdata}}\EldenRing\{{p|uid}}",
                    @"{{p|appdata}}\EldenRing"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Baldur's Gate 3",
                SteamId = "1086940",
                Aliases = new List<string> { "bg3" },
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames\Story",
                    @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "The Witcher 3: Wild Hunt",
                SteamId = "292030",
                Aliases = new List<string> { "witcher 3" },
                SavePatterns = new List<string>
                {
                    @"{{p|documents}}\The Witcher 3\gamesaves"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Hades",
                SteamId = "1145360",
                SavePatterns = new List<string>
                {
                    @"{{p|savedgames}}\Hades",
                    @"{{p|documents}}\Saved Games\Hades"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "God of War",
                SteamId = "1593500",
                SavePatterns = new List<string>
                {
                    @"{{p|savedgames}}\God of War\{{p|uid}}"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Monster Hunter: World",
                SteamId = "582010",
                SavePatterns = new List<string>
                {
                    @"{{p|steam}}\userdata\{{p|uid}}\582010\remote"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Grand Theft Auto V",
                SteamId = "271590",
                Aliases = new List<string> { "gta v", "gta 5" },
                SavePatterns = new List<string>
                {
                    @"{{p|documents}}\Rockstar Games\GTA V\Profiles\{{p|uid}}"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Red Dead Redemption 2",
                SteamId = "1174180",
                Aliases = new List<string> { "rdr2" },
                SavePatterns = new List<string>
                {
                    @"{{p|documents}}\Rockstar Games\Red Dead Redemption 2\Profiles\{{p|uid}}"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Manor Lords",
                SteamId = "1363080",
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\ManorLords\Saved\SaveGames"
                }
            });

            AddOrUpdateGame(new LudusaviGameEntry
            {
                Name = "Stray",
                SteamId = "1332010",
                Aliases = new List<string> { "hk_project" },
                SavePatterns = new List<string>
                {
                    @"{{p|localappdata}}\Hk_project\Saved\SaveGames"
                }
            });

            _isLoaded = true;
        }
    }
}
