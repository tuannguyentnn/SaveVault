using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class GameSearchCoordinator
{
    private readonly DatabaseService _databaseService;
    private readonly PCGamingWikiService _wikiService;
    private readonly LudusaviDatabaseService _ludusaviService;
    private readonly PathResolverService _pathResolver;

    public GameSearchCoordinator(
        DatabaseService databaseService,
        PCGamingWikiService? wikiService = null,
        LudusaviDatabaseService? ludusaviService = null,
        PathResolverService? pathResolver = null)
    {
        _databaseService = databaseService;
        _wikiService = wikiService ?? new PCGamingWikiService();
        _ludusaviService = ludusaviService ?? new LudusaviDatabaseService();
        _pathResolver = pathResolver ?? new PathResolverService();
    }

    public async Task<GameSaveInfo> SearchAndDetectGameAsync(
        string gameName,
        bool forceOnline = false,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new ArgumentException("Tên game không được để trống.", nameof(gameName));
        }

        GameSaveInfo? gameInfo = null;

        // Step 1: Check Local Cache if not forcing online
        if (!forceOnline)
        {
            statusProgress?.Report("Đang kiểm tra dữ liệu trong bộ nhớ đệm SQLite...");
            gameInfo = await _databaseService.GetCachedGameAsync(gameName);
        }

        // Step 2: Check Ludusavi Catalog
        if (gameInfo == null || gameInfo.RawPatterns.Count == 0)
        {
            statusProgress?.Report("Đang đối chiếu cơ sở dữ liệu game phổ biến...");
            var catalogMatch = _ludusaviService.FindMatchingGame(gameName);
            if (catalogMatch != null)
            {
                gameInfo = catalogMatch;
            }
        }

        // Step 3: Fetch online from PCGamingWiki
        if (gameInfo == null || gameInfo.RawPatterns.Count == 0 || forceOnline)
        {
            statusProgress?.Report($"Đang tìm kiếm thông tin game '{gameName}' trên mạng...");
            var onlineInfo = await _wikiService.SearchAndFetchSaveInfoAsync(gameName, cancellationToken);
            if (onlineInfo != null && onlineInfo.RawPatterns.Count > 0)
            {
                if (gameInfo != null)
                {
                    // Merge patterns
                    foreach (var p in onlineInfo.RawPatterns)
                    {
                        if (!gameInfo.RawPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            gameInfo.RawPatterns.Add(p);
                        }
                    }
                    gameInfo.WikiPageTitle = onlineInfo.WikiPageTitle;
                    gameInfo.SteamAppId ??= onlineInfo.SteamAppId;
                    gameInfo.Source = "PCGamingWiki (Online)";
                }
                else
                {
                    gameInfo = onlineInfo;
                }
            }
        }

        // Fallback: If still nothing, create template with heuristic fallback locations
        if (gameInfo == null)
        {
            gameInfo = new GameSaveInfo
            {
                GameName = gameName.Trim(),
                Source = "Heuristic Detection"
            };
        }

        // Add standard heuristic candidate locations if not already present
        AddHeuristicCandidates(gameInfo);

        // Step 4: Resolve concrete paths
        statusProgress?.Report("Đang phân giải đường dẫn và quét file save trên máy tính...");
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pat in gameInfo.RawPatterns)
        {
            var res = _pathResolver.ResolveRawPattern(pat);
            foreach (var r in res) resolved.Add(r);
        }
        gameInfo.ResolvedPaths = resolved.ToList();

        // Step 5: Check which ones actually exist on disk right now
        var detectedItems = _pathResolver.InspectDetectedPathItems(gameInfo.ResolvedPaths);
        gameInfo.DetectedPathItems = detectedItems;
        gameInfo.DetectedPathsOnDisk = detectedItems.Select(i => i.Path).ToList();
        gameInfo.TotalSizeBytes = detectedItems.Sum(i => i.TotalSizeBytes);
        gameInfo.FileCount = detectedItems.Sum(i => i.FileCount);
        gameInfo.LastScanned = DateTime.Now;

        // Step 6: Cache into SQLite
        await _databaseService.SaveGameCacheAsync(gameInfo);

        statusProgress?.Report(gameInfo.IsFoundOnDisk
            ? $"Đã tìm thấy {gameInfo.FileCount} file save ({gameInfo.DetectedPathsOnDisk.Count} thư mục) trên máy!"
            : "Chưa phát hiện file save của game này trên máy tính.");

        return gameInfo;
    }

    private static void AddHeuristicCandidates(GameSaveInfo gameInfo)
    {
        var sanitized = BackupService.SanitizeFolderName(gameInfo.GameName);
        var candidates = new[]
        {
            $@"{{{{p|localappdata}}}}\{sanitized}\Saved\SaveGames",
            $@"{{{{p|localappdata}}}}\{sanitized}",
            $@"{{{{p|appdata}}}}\{sanitized}",
            $@"{{{{p|savedgames}}}}\{sanitized}",
            $@"{{{{p|documents}}}}\My Games\{sanitized}",
            $@"{{{{p|documents}}}}\Saved Games\{sanitized}",
            $@"{{{{p|documents}}}}\{sanitized}"
        };

        foreach (var c in candidates)
        {
            if (!gameInfo.RawPatterns.Contains(c, StringComparer.OrdinalIgnoreCase))
            {
                gameInfo.RawPatterns.Add(c);
            }
        }
    }
}
