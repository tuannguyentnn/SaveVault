using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class GameSearchCoordinator
{
    private readonly DatabaseService _databaseService;
    private readonly PCGamingWikiService _wikiService;
    private readonly PathResolverService _pathResolver;
    private readonly LudusaviManifestService _ludusaviService;
    private readonly GameVersionDetectorService _versionDetector;
    private readonly GeminiUnifiedGameService _geminiUnifiedService;

    public GameSearchCoordinator(
        DatabaseService databaseService,
        PCGamingWikiService? wikiService = null,
        PathResolverService? pathResolver = null,
        LudusaviManifestService? ludusaviService = null,
        GameVersionDetectorService? versionDetector = null,
        GeminiUnifiedGameService? geminiUnifiedService = null)
    {
        _databaseService = databaseService;
        _wikiService = wikiService ?? new PCGamingWikiService();
        _pathResolver = pathResolver ?? new PathResolverService();
        _ludusaviService = ludusaviService ?? new LudusaviManifestService();
        _versionDetector = versionDetector ?? new GameVersionDetectorService(_pathResolver);
        _geminiUnifiedService = geminiUnifiedService ?? new GeminiUnifiedGameService();
    }

    public PathResolverService PathResolver => _pathResolver;
    public GameVersionDetectorService VersionDetector => _versionDetector;
    public GeminiUnifiedGameService GeminiUnifiedService => _geminiUnifiedService;

    public async Task<GameSaveInfo?> SearchAndDetectGameAsync(
        string gameName,
        IProgress<string>? statusProgress = null,
        Func<List<string>, Task<string?>>? candidateChooser = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new ArgumentException("Tên game không được để trống.", nameof(gameName));
        }

        var cleanQuery = gameName.Trim();
        GameSaveInfo? gameInfo = null;

        LoggingService.LogAction("Search_Game_Start", new { Query = cleanQuery });

        // Step 1: Check Local Cache in SQLite
        statusProgress?.Report($"Đang kiểm tra bộ nhớ đệm SQLite cho '{cleanQuery}'...");
        LoggingService.LogAction("Search_Check_Source", new { Query = cleanQuery, Source = "SQLite Cache" });
        var cachedGame = await _databaseService.GetCachedGameAsync(cleanQuery);
        if (cachedGame != null && cachedGame.RawPatterns.Count > 0)
        {
            LoggingService.LogAction("Search_Source_Found", new
            {
                Query = cleanQuery,
                Source = "SQLite Cache",
                OriginalSource = cachedGame.Source,
                GameName = cachedGame.GameName,
                PatternsCount = cachedGame.RawPatterns.Count
            });

            gameInfo = cachedGame;
        }

        // Step 2: Pha chọn game từ PCGamingWiki (Candidate Selection)
        string targetGameName = cleanQuery;
        if (gameInfo == null)
        {
            if (GameCoverService.IsNetworkAvailable())
            {
                statusProgress?.Report($"Đang tìm kiếm gợi ý các tựa game phù hợp cho '{cleanQuery}' từ PCGamingWiki...");
                LoggingService.LogAction("Search_Check_Source", new { Query = cleanQuery, Source = "PCGamingWiki Titles" });

                var candidates = await _wikiService.FindPageTitlesAsync(cleanQuery, limit: 5, cancellationToken);
                string? chosenTitle = null;

                if (candidates.Count == 0)
                {
                    LoggingService.LogAction("Search_Source_Missed", new { Query = cleanQuery, Source = "PCGamingWiki Titles", Reason = "No candidates found" });
                    statusProgress?.Report($"Không tìm thấy tựa game nào phù hợp với '{cleanQuery}' trên PCGamingWiki.");
                    if (candidateChooser != null)
                    {
                        _ = candidateChooser(candidates);
                    }
                    return null;
                }
                else
                {
                    // Luôn hiển thị dropdownlist cho người dùng chọn game
                    if (candidateChooser != null)
                    {
                        statusProgress?.Report($"Tìm thấy {candidates.Count} tựa game phù hợp trên PCGamingWiki. Đang chờ bạn chọn...");
                        chosenTitle = await candidateChooser(candidates);
                        if (string.IsNullOrWhiteSpace(chosenTitle))
                        {
                            // Người dùng đã hủy chọn
                            LoggingService.LogAction("Search_Cancelled_By_User", new { Query = cleanQuery });
                            statusProgress?.Report("Đã hủy tìm kiếm.");
                            return null;
                        }
                    }
                    else
                    {
                        // Mặc định chọn kết quả đầu tiên nếu không có UI chooser
                        chosenTitle = candidates[0];
                    }
                }

                if (!string.IsNullOrWhiteSpace(chosenTitle))
                {
                    targetGameName = chosenTitle.Trim();

                    // Kiểm tra nếu tên game được chọn đã có sẵn trong SQLite Cache
                    if (!string.Equals(targetGameName, cleanQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        var cachedChosen = await _databaseService.GetCachedGameAsync(targetGameName);
                        if (cachedChosen != null && cachedChosen.RawPatterns.Count > 0)
                        {
                            LoggingService.LogAction("Search_Source_Found", new
                            {
                                Query = cleanQuery,
                                TargetGame = targetGameName,
                                Source = "SQLite Cache (Chosen Candidate)",
                                GameName = cachedChosen.GameName,
                                PatternsCount = cachedChosen.RawPatterns.Count
                            });
                            gameInfo = cachedChosen;
                        }
                    }
                }
            }
            else
            {
                LoggingService.LogAction("Search_Offline_Mode", new { Query = cleanQuery, Reason = "No network available" });
                statusProgress?.Report($"Không có kết nối mạng. Đang tìm kiếm trong cơ sở dữ liệu offline (Ludusavi Manifest)...");
            }
        }

        // Step 3: Tra cứu Data Save & Exe cho game đã chọn (targetGameName)
        // 3.1. LUỒNG MỚI (Gemini Unified 1-Shot)
        var config = AppConfigService.GetConfig();
        if (gameInfo == null && config.UseGeminiUnifiedWorkflow && config.EnableGeminiExeSearch && !string.IsNullOrWhiteSpace(config.GeminiApiKey) && GameCoverService.IsNetworkAvailable())
        {
            statusProgress?.Report($"Đang tra cứu cấu hình Save & File Exe cho '{targetGameName}' qua Gemini AI Unified ({config.GeminiModel})...");
            LoggingService.LogAction("Search_Check_Source", new { Query = targetGameName, Source = "Gemini AI Unified" });

            try
            {
                var (unifiedInfo, exeDef) = await _geminiUnifiedService.ResolveUnifiedGameAsync(targetGameName, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (unifiedInfo != null && unifiedInfo.RawPatterns.Count > 0)
                {
                    LoggingService.LogAction("Search_Source_Found", new
                    {
                        Query = cleanQuery,
                        TargetGame = targetGameName,
                        Source = "Gemini AI Unified",
                        GameName = unifiedInfo.GameName,
                        PatternsCount = unifiedInfo.RawPatterns.Count,
                        ExeName = exeDef?.PrimaryExeName
                    });

                    gameInfo = unifiedInfo;

                    if (exeDef != null)
                    {
                        OnlineExeResolverService.RegisterDefinition(exeDef);
                        gameInfo.PreResolvedExecutable = exeDef;
                    }
                }
                else
                {
                    LoggingService.LogAction("Search_Source_Missed", new { Query = targetGameName, Source = "Gemini AI Unified", Reason = "Empty patterns or null response" });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi trong quá trình tra cứu Gemini Unified: {Message}", ex.Message);
            }
        }

        // 3.2. LUỒNG CŨ (hoặc Fallback PCGamingWiki)
        if (gameInfo == null && GameCoverService.IsNetworkAvailable())
        {
            statusProgress?.Report($"Đang tải dữ liệu cấu hình save & ảnh cho '{targetGameName}' từ PCGamingWiki...");
            var onlineInfo = await _wikiService.FetchPageSaveDataAsync(targetGameName, cleanQuery, cancellationToken);
            if (onlineInfo != null && onlineInfo.RawPatterns.Count > 0)
            {
                LoggingService.LogAction("Search_Source_Found", new
                {
                    Query = cleanQuery,
                    TargetGame = targetGameName,
                    Source = "PCGamingWiki",
                    GameName = onlineInfo.GameName,
                    WikiTitle = onlineInfo.WikiPageTitle,
                    PatternsCount = onlineInfo.RawPatterns.Count
                });

                gameInfo = onlineInfo;
            }
        }

        // 3.3. Fallback offline khi PCGamingWiki/Gemini không có hoặc không có mạng (Dùng Ludusavi)
        if (gameInfo == null)
        {
            var ludusaviGame = _ludusaviService.CreateGameSaveInfo(targetGameName);
            if (ludusaviGame != null && ludusaviGame.RawPatterns.Count > 0)
            {
                if (!GameCoverService.IsNetworkAvailable())
                {
                    ludusaviGame.OnlineCoverUrl = null;
                }
                gameInfo = ludusaviGame;
            }
        }

        // 3.4. Fallback Heuristic nếu vẫn không tìm thấy
        if (gameInfo == null)
        {
            gameInfo = new GameSaveInfo
            {
                GameName = targetGameName,
                Source = "Heuristic Detection"
            };
            LoggingService.LogAction("Search_Fallback_Source", new { Query = targetGameName, Source = "Heuristic Detection" });
        }

        // Step 4: Add smart heuristics, Unreal Engine auto-detection & Xbox packages
        statusProgress?.Report($"Đang quét tìm kiếm theo cơ chế Heuristic & Smart Engine Scanner...");
        var beforePatternCount = gameInfo.RawPatterns.Count;
        AddSmartHeuristicCandidates(gameInfo, cleanQuery);
        var addedByScanner = gameInfo.RawPatterns.Count - beforePatternCount;
        if (addedByScanner > 0)
        {
            LoggingService.LogAction("Search_Smart_Scanner_Added", new
            {
                Query = cleanQuery,
                AddedPatternsCount = addedByScanner,
                TotalPatterns = gameInfo.RawPatterns.Count
            });
        }

        // Step 5: Resolve concrete paths
        statusProgress?.Report($"Đang phân giải {gameInfo.RawPatterns.Count} mẫu đường dẫn (Nguồn: {gameInfo.Source})...");
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pat in gameInfo.RawPatterns)
        {
            var res = _pathResolver.ResolveRawPattern(pat);
            foreach (var r in res) resolved.Add(r);
        }
        gameInfo.ResolvedPaths = resolved.ToList();

        // Step 6: Check which ones actually exist on disk right now
        var detectedItems = _pathResolver.InspectDetectedPathItems(gameInfo.ResolvedPaths);
        gameInfo.DetectedPathItems = detectedItems;
        gameInfo.DetectedPathsOnDisk = detectedItems.Select(i => i.Path).ToList();
        gameInfo.TotalSizeBytes = detectedItems.Sum(i => i.TotalSizeBytes);
        gameInfo.FileCount = detectedItems.Sum(i => i.FileCount);
        gameInfo.LastScanned = DateTime.Now;

        LoggingService.LogAction("Search_Game_Completed", new
        {
            Query = cleanQuery,
            GameName = gameInfo.GameName,
            FinalSource = gameInfo.Source,
            IsFoundOnDisk = gameInfo.IsFoundOnDisk,
            DetectedPathsCount = gameInfo.DetectedPathsOnDisk.Count,
            DetectedPaths = gameInfo.DetectedPathsOnDisk,
            FileCount = gameInfo.FileCount,
            TotalSizeBytes = gameInfo.TotalSizeBytes
        });

        statusProgress?.Report(gameInfo.IsFoundOnDisk
            ? $"Đã tìm thấy {gameInfo.FileCount} file save ({gameInfo.DetectedPathsOnDisk.Count} thư mục) từ nguồn: {gameInfo.Source}!"
            : $"Đã hoàn tất tìm kiếm từ nguồn: {gameInfo.Source}, nhưng chưa phát hiện file save thực tế trên máy tính.");

        // Step 7: Auto-detect installed game version
        try
        {
            var detectedVersion = await _versionDetector.DetectGameVersionAsync(gameInfo.GameName, gameInfo.SteamAppId, cancellationToken: cancellationToken).ConfigureAwait(false);
            gameInfo.DetectedVersion = detectedVersion;
            if (detectedVersion.IsDetected)
            {
                LoggingService.LogAction("Search_Game_Version_Detected", new
                {
                    GameName = gameInfo.GameName,
                    Version = detectedVersion.DisplayVersion,
                    Source = detectedVersion.DetectionSource
                });
            }
        }
        catch { }

        return gameInfo;
    }

    private static void AddSmartHeuristicCandidates(GameSaveInfo gameInfo, string? originalQuery)
    {
        // 1. Candidate tiêu chuẩn từ thư mục AppData, Documents, Saved Games...
        AddHeuristicCandidates(gameInfo, originalQuery);

        // 2. Smart Unreal Engine Detection trên %LOCALAPPDATA%
        DetectUnrealEngineSaves(gameInfo, originalQuery);

        // 3. Smart Xbox / Windows Store Packages Detection trên %LOCALAPPDATA%\Packages
        DetectXboxPackageSaves(gameInfo, originalQuery);
    }

    private static void AddHeuristicCandidates(GameSaveInfo gameInfo, string? originalQuery = null)
    {
        var namesToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(gameInfo.GameName))
        {
            namesToCheck.Add(gameInfo.GameName);
            if (gameInfo.GameName.Contains(':'))
            {
                namesToCheck.Add(gameInfo.GameName.Split(':')[0].Trim());
            }
        }
        if (!string.IsNullOrWhiteSpace(originalQuery))
        {
            namesToCheck.Add(originalQuery.Trim());
            if (originalQuery.Contains(':'))
            {
                namesToCheck.Add(originalQuery.Split(':')[0].Trim());
            }
        }

        foreach (var name in namesToCheck)
        {
            var sanitized = BackupService.SanitizeFolderName(name);
            var candidates = new[]
            {
                $@"{{{{p|localappdata}}}}\{sanitized}\Saved\SaveGames",
                $@"{{{{p|localappdata}}}}\{sanitized}",
                $@"{{{{p|appdata}}}}\{sanitized}",
                $@"{{{{p|savedgames}}}}\{sanitized}",
                $@"{{{{p|documents}}}}\My Games\{sanitized}",
                $@"{{{{p|documents}}}}\Saved Games\{sanitized}",
                $@"{{{{p|documents}}}}\{sanitized}\gamesaves",
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

    private static void DetectUnrealEngineSaves(GameSaveInfo gameInfo, string? originalQuery)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Directory.Exists(localAppData)) return;

            var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(gameInfo.GameName)) queries.Add(gameInfo.GameName);
            if (!string.IsNullOrWhiteSpace(originalQuery)) queries.Add(originalQuery);

            var queryTokens = queries
                .SelectMany(q => q.Split(new[] { ' ', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(t => t.Length >= 3)
                .ToList();

            var dirs = Directory.GetDirectories(localAppData);
            foreach (var dir in dirs)
            {
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                // Kiểm tra xem folder này có thư mục con Saved\SaveGames không
                var saveGamesDir = Path.Combine(dir, "Saved", "SaveGames");
                if (Directory.Exists(saveGamesDir))
                {
                    bool isMatch = false;

                    foreach (var q in queries)
                    {
                        var normQ = DatabaseService.NormalizeGameName(q);
                        var normFolder = DatabaseService.NormalizeGameName(folderName);

                        if (normQ.Contains(normFolder) || normFolder.Contains(normQ) ||
                            string.Equals(normQ, normFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                            break;
                        }
                    }

                    if (!isMatch)
                    {
                        foreach (var token in queryTokens)
                        {
                            var normToken = DatabaseService.NormalizeGameName(token);
                            var normFolder = DatabaseService.NormalizeGameName(folderName);
                            if (normToken.Length >= 3 && (normFolder.StartsWith(normToken) || normToken.StartsWith(normFolder)))
                            {
                                isMatch = true;
                                break;
                            }
                        }
                    }

                    if (isMatch)
                    {
                        var pattern = $@"{{{{p|localappdata}}}}\{folderName}\Saved\SaveGames";
                        if (!gameInfo.RawPatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                        {
                            gameInfo.RawPatterns.Add(pattern);
                        }
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua nếu có lỗi quyền truy cập file hệ thống
        }
    }

    private static void DetectXboxPackageSaves(GameSaveInfo gameInfo, string? originalQuery)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packagesDir = Path.Combine(localAppData, "Packages");
            if (!Directory.Exists(packagesDir)) return;

            var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(gameInfo.GameName)) queries.Add(gameInfo.GameName);
            if (!string.IsNullOrWhiteSpace(originalQuery)) queries.Add(originalQuery);

            var queryTokens = queries
                .SelectMany(q => q.Split(new[] { ' ', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(t => t.Length >= 3)
                .Select(DatabaseService.NormalizeGameName)
                .ToList();

            var pkgDirs = Directory.GetDirectories(packagesDir);
            foreach (var pDir in pkgDirs)
            {
                var pkgName = Path.GetFileName(pDir);
                var normPkgName = DatabaseService.NormalizeGameName(pkgName);

                bool isMatch = queryTokens.Any(t => normPkgName.Contains(t));
                if (isMatch)
                {
                    var wgsDir = Path.Combine(pDir, "SystemAppData", "wgs");
                    if (Directory.Exists(wgsDir))
                    {
                        var pattern = $@"{{{{p|localappdata}}}}\Packages\{pkgName}\SystemAppData\wgs";
                        if (!gameInfo.RawPatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                        {
                            gameInfo.RawPatterns.Add(pattern);
                        }
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi truy cập thư mục Packages
        }
    }
}
