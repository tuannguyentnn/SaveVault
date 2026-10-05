using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Dịch vụ tự động dò tìm và phân tích phiên bản (Game Version / Build ID) của game đang cài đặt trên máy.
/// Hỗ trợ chiến lược đa tầng: Steam Manifest -> GOG -> Epic Games -> Windows Registry -> Executable Metadata (.exe).
/// </summary>
public class GameVersionDetectorService
{
    private readonly PathResolverService _pathResolver;
    private readonly OnlineExeResolverService _onlineExeResolver;

    public GameVersionDetectorService(
        PathResolverService? pathResolver = null,
        OnlineExeResolverService? onlineExeResolver = null)
    {
        _pathResolver = pathResolver ?? new PathResolverService();
        _onlineExeResolver = onlineExeResolver ?? new OnlineExeResolverService();
    }

    public OnlineExeResolverService OnlineExeResolver => _onlineExeResolver;

    /// <summary>
    /// Dò tìm phiên bản của game theo tên và các tham số nhận dạng tùy chọn.
    /// </summary>
    /// <summary>
    /// Dò tìm phiên bản của game bất đồng bộ theo tên và các tham số nhận dạng tùy chọn (tránh nghẽn UI).
    /// </summary>
    public async Task<GameVersionInfo> DetectGameVersionAsync(
        string gameName,
        string? steamAppId = null,
        string? knownGameDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return new GameVersionInfo { DisplayVersion = "Không tìm ra phiên bản", DetectionSource = "None", DetectionMechanism = "None" };
        }

        var normalizedTarget = DatabaseService.NormalizeGameName(gameName);

        // 1. Dò từ Steam Manifests (Chính xác cao nhất cho game Steam)
        try
        {
            var steamResult = await Task.Run(() => DetectFromSteam(gameName, normalizedTarget, steamAppId, knownGameDirectory), cancellationToken).ConfigureAwait(false);
            if (steamResult != null && steamResult.IsDetected)
            {
                steamResult.DetectionMechanism = "SteamManifest";
                steamResult.OnlineSource = "Steam Local ACF / Cloud";
                LogDetectionResult(gameName, steamResult);
                return steamResult;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("GameVersion_Steam_Error", new { GameName = gameName, Error = ex.Message });
        }

        // 2. Dò từ GOG Galaxy / Registry GOG
        try
        {
            var gogResult = await Task.Run(() => DetectFromGog(gameName, normalizedTarget, knownGameDirectory), cancellationToken).ConfigureAwait(false);
            if (gogResult != null && gogResult.IsDetected)
            {
                gogResult.DetectionMechanism = "GogRegistry";
                gogResult.OnlineSource = "GOG Galaxy / Registry";
                LogDetectionResult(gameName, gogResult);
                return gogResult;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("GameVersion_Gog_Error", new { GameName = gameName, Error = ex.Message });
        }

        // 3. Dò từ Epic Games Manifests
        try
        {
            var epicResult = await Task.Run(() => DetectFromEpic(gameName, normalizedTarget, knownGameDirectory), cancellationToken).ConfigureAwait(false);
            if (epicResult != null && epicResult.IsDetected)
            {
                epicResult.DetectionMechanism = "EpicManifest";
                epicResult.OnlineSource = "Epic Games Launcher";
                LogDetectionResult(gameName, epicResult);
                return epicResult;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("GameVersion_Epic_Error", new { GameName = gameName, Error = ex.Message });
        }

        // 4. Dò từ Thư mục đã biết hoặc quét thư mục cài đặt trên các ổ đĩa (Kết hợp Online Fast-Probe & Local BFS)
        try
        {
            var exeResult = await DetectFromExecutablesAsync(gameName, normalizedTarget, knownGameDirectory, steamAppId, cancellationToken).ConfigureAwait(false);
            if (exeResult != null && exeResult.IsDetected)
            {
                LogDetectionResult(gameName, exeResult);
                return exeResult;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("GameVersion_Exe_Error", new { GameName = gameName, Error = ex.Message });
        }

        // 5. Dò từ Windows Registry Uninstall
        try
        {
            var regResult = await Task.Run(() => DetectFromWindowsRegistry(gameName, normalizedTarget), cancellationToken).ConfigureAwait(false);
            if (regResult != null && regResult.IsDetected)
            {
                regResult.DetectionMechanism = "WindowsRegistry";
                regResult.OnlineSource = "Windows Registry Uninstall";
                LogDetectionResult(gameName, regResult);
                return regResult;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("GameVersion_Registry_Error", new { GameName = gameName, Error = ex.Message });
        }

        return new GameVersionInfo
        {
            DisplayVersion = "Không tìm ra phiên bản",
            DetectionSource = "NotDetected",
            DetectionMechanism = "None",
            OnlineSource = "Offline / Local"
        };
    }

    /// <summary>
    /// Dò tìm phiên bản của game theo tên và các tham số nhận dạng tùy chọn (chạy an toàn trên thread pool).
    /// </summary>
    public GameVersionInfo DetectGameVersion(
        string gameName,
        string? steamAppId = null,
        string? knownGameDirectory = null)
    {
        return Task.Run(() => DetectGameVersionAsync(gameName, steamAppId, knownGameDirectory, CancellationToken.None)).GetAwaiter().GetResult();
    }

    private static void LogDetectionResult(string gameName, GameVersionInfo result)
    {
        LoggingService.LogAction("GameVersion_Detected", new
        {
            GameName = gameName,
            Source = result.DetectionSource,
            Mechanism = result.DetectionMechanism,
            OnlineSource = result.OnlineSource ?? "Offline / Local",
            Version = result.DisplayVersion,
            ExecutablePath = result.ExecutablePath,
            DurationMs = result.ResolutionDurationMs
        });
    }

    #region Steam Detection

    private GameVersionInfo? DetectFromSteam(
        string gameName,
        string normalizedTarget,
        string? steamAppId,
        string? knownGameDirectory)
    {
        var steamLibraries = GetSteamLibraryFolders();
        if (steamLibraries.Count == 0) return null;

        // Tìm kiếm manifest theo AppId hoặc duyệt qua các manifest trong tất cả library
        foreach (var lib in steamLibraries)
        {
            var steamAppsDir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamAppsDir)) continue;

            // Nếu có SteamAppId cụ thể
            if (!string.IsNullOrWhiteSpace(steamAppId))
            {
                var manifestPath = Path.Combine(steamAppsDir, $"appmanifest_{steamAppId}.acf");
                if (File.Exists(manifestPath))
                {
                    var info = ParseSteamManifest(manifestPath, lib);
                    if (info != null) return info;
                }
            }

            // Quét tất cả appmanifest_*.acf nếu chưa khớp theo AppId
            var acfFiles = Directory.GetFiles(steamAppsDir, "appmanifest_*.acf");
            foreach (var acf in acfFiles)
            {
                // Đọc siêu nhanh header của acf (<0.1ms) trước khi quét ổ đĩa
                var (appName, installDir, buildId) = ReadAcfHeader(acf);
                if (string.IsNullOrWhiteSpace(buildId)) continue;

                if (MatchesGameName(gameName, normalizedTarget, appName, installDir))
                {
                    var info = ParseSteamManifest(acf, lib);
                    if (info != null) return info;
                }
            }
        }

        return null;
    }

    private static (string? AppName, string? InstallDir, string? BuildId) ReadAcfHeader(string acfPath)
    {
        try
        {
            var content = File.ReadAllText(acfPath);
            var buildIdMatch = Regex.Match(content, @"""buildid""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);

            var buildId = buildIdMatch.Success ? buildIdMatch.Groups[1].Value.Trim() : null;
            var appName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null;
            var installDir = installDirMatch.Success ? installDirMatch.Groups[1].Value.Trim() : null;

            return (appName, installDir, buildId);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private GameVersionInfo? ParseSteamManifest(string acfPath, string steamLibraryRoot)
    {
        try
        {
            var content = File.ReadAllText(acfPath);
            var buildIdMatch = Regex.Match(content, @"""buildid""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var appIdMatch = Regex.Match(content, @"""appid""\s+""([^""]+)""", RegexOptions.IgnoreCase);

            var buildId = buildIdMatch.Success ? buildIdMatch.Groups[1].Value.Trim() : null;
            var appName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null;
            var installDir = installDirMatch.Success ? installDirMatch.Groups[1].Value.Trim() : null;
            var appId = appIdMatch.Success ? appIdMatch.Groups[1].Value.Trim() : null;

            if (string.IsNullOrWhiteSpace(buildId)) return null;

            string? gameDir = null;
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                gameDir = Path.Combine(steamLibraryRoot, "steamapps", "common", installDir);
            }

            // Thử tìm file .exe trong gameDir để lấy thêm version chính xác từ binary
            GameVersionInfo? exeInfo = null;
            if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
            {
                exeInfo = ScanDirectoryForBestExecutable(gameDir, appName ?? "");
            }

            string displayVer;
            if (exeInfo != null && !string.IsNullOrWhiteSpace(exeInfo.DisplayVersion) &&
                !exeInfo.DisplayVersion.Equals("1.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                displayVer = $"{exeInfo.DisplayVersion} (Build {buildId})";
            }
            else
            {
                displayVer = $"Build {buildId}";
            }

            return new GameVersionInfo
            {
                DisplayVersion = displayVer,
                SteamBuildId = buildId,
                RawFileVersion = exeInfo?.RawFileVersion,
                RawProductVersion = exeInfo?.RawProductVersion,
                InstallDirectory = gameDir,
                ExecutablePath = exeInfo?.ExecutablePath,
                ExecutableModifiedDate = exeInfo?.ExecutableModifiedDate,
                DetectionSource = "SteamManifest"
            };
        }
        catch
        {
            return null;
        }
    }

    private List<string> GetSteamLibraryFolders()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steamPath = _pathResolver.GetSteamPath();
        if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
        {
            libraries.Add(steamPath);

            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    var text = File.ReadAllText(vdfPath);
                    var matches = Regex.Matches(text, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        var raw = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(raw))
                        {
                            libraries.Add(raw);
                        }
                    }
                }
                catch { }
            }
        }

        // Quét thêm các ổ đĩa cố định phổ biến (chỉ lọc Fixed drives để tránh treo I/O trên ổ CD/đĩa ảo)
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var candidates = new[]
            {
                Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"),
                Path.Combine(drive.RootDirectory.FullName, "Steam Game"),
                Path.Combine(drive.RootDirectory.FullName, "Steam"),
                Path.Combine(drive.RootDirectory.FullName, "Games", "SteamLibrary")
            };

            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) libraries.Add(c);
            }
        }

        return libraries.ToList();
    }

    #endregion

    #region GOG Detection

    private GameVersionInfo? DetectFromGog(
        string gameName,
        string normalizedTarget,
        string? knownGameDirectory)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var regPaths = new[]
        {
            @"SOFTWARE\WOW6432Node\GOG.com\Games",
            @"SOFTWARE\GOG.com\Games"
        };

        foreach (var regPath in regPaths)
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(regPath);
            if (baseKey == null) continue;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                using var subKey = baseKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var gogGameName = subKey.GetValue("gameName")?.ToString() ?? "";
                var path = subKey.GetValue("path")?.ToString() ?? "";
                var ver = subKey.GetValue("ver")?.ToString() ?? "";
                var buildId = subKey.GetValue("buildId")?.ToString() ?? "";
                var exe = subKey.GetValue("exe")?.ToString() ?? "";

                if (MatchesGameName(gameName, normalizedTarget, gogGameName, path))
                {
                    string displayVer = !string.IsNullOrWhiteSpace(ver)
                        ? (ver.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? ver : $"v{ver}")
                        : (!string.IsNullOrWhiteSpace(buildId) ? $"Build {buildId}" : "Unknown");

                    DateTime? exeDate = null;
                    if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    {
                        exeDate = File.GetLastWriteTime(exe);
                    }

                    return new GameVersionInfo
                    {
                        DisplayVersion = displayVer,
                        GogBuildId = buildId,
                        InstallDirectory = path,
                        ExecutablePath = File.Exists(exe) ? exe : null,
                        ExecutableModifiedDate = exeDate,
                        DetectionSource = "GogRegistry"
                    };
                }
            }
        }

        // Kiểm tra file goggame-*.info nếu có knownGameDirectory
        if (!string.IsNullOrWhiteSpace(knownGameDirectory) && Directory.Exists(knownGameDirectory))
        {
            var infoFiles = Directory.GetFiles(knownGameDirectory, "goggame-*.info");
            foreach (var file in infoFiles)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var ver = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                    var buildId = root.TryGetProperty("buildId", out var b) ? b.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(ver) || !string.IsNullOrWhiteSpace(buildId))
                    {
                        var displayVer = !string.IsNullOrWhiteSpace(ver) ? $"v{ver}" : $"Build {buildId}";
                        return new GameVersionInfo
                        {
                            DisplayVersion = displayVer,
                            GogBuildId = buildId,
                            InstallDirectory = knownGameDirectory,
                            DetectionSource = "GogInfoFile"
                        };
                    }
                }
                catch { }
            }
        }

        return null;
    }

    #endregion

    #region Epic Games Detection

    private GameVersionInfo? DetectFromEpic(
        string gameName,
        string normalizedTarget,
        string? knownGameDirectory)
    {
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestsDir)) return null;

        var items = Directory.GetFiles(manifestsDir, "*.item");
        foreach (var itemFile in items)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                var root = doc.RootElement;

                var displayName = root.TryGetProperty("DisplayName", out var d) ? d.GetString() : "";
                var installLoc = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : "";
                var appVer = root.TryGetProperty("AppVersionString", out var v) ? v.GetString() : "";

                if (MatchesGameName(gameName, normalizedTarget, displayName, installLoc))
                {
                    if (!string.IsNullOrWhiteSpace(appVer))
                    {
                        var cleanVer = appVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? appVer : $"v{appVer}";
                        return new GameVersionInfo
                        {
                            DisplayVersion = cleanVer,
                            RawFileVersion = appVer,
                            InstallDirectory = installLoc,
                            DetectionSource = "EpicManifest"
                        };
                    }
                }
            }
            catch { }
        }

        return null;
    }

    #endregion

    #region Executable (.exe) Metadata Detection

    private async Task<GameVersionInfo?> DetectFromExecutablesAsync(
        string gameName,
        string normalizedTarget,
        string? knownGameDirectory,
        string? steamAppId = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(knownGameDirectory) && Directory.Exists(knownGameDirectory))
        {
            candidateDirs.Add(knownGameDirectory);
        }

        // Tự động tìm thư mục game trên các ổ đĩa nếu chưa có knownGameDirectory
        if (candidateDirs.Count == 0)
        {
            var foundDirs = FindGameDirectoriesOnDrives(gameName, normalizedTarget);
            foreach (var d in foundDirs) candidateDirs.Add(d);
        }

        // Giai đoạn 1: Tra cứu Online Definition & Fast-Probe trực tiếp (<5ms)
        GameExecutableDefinition? onlineDef = null;
        try
        {
            onlineDef = await _onlineExeResolver.ResolveExecutableInfoAsync(gameName, steamAppId, cancellationToken).ConfigureAwait(false);
            if (onlineDef != null && !string.IsNullOrWhiteSpace(onlineDef.PrimaryExeName))
            {
                foreach (var dir in candidateDirs)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    // Thử các relative paths đã biết từ mạng (vd: "bin/x64", "game/bin/x64", "")
                    foreach (var relPath in onlineDef.KnownRelativePaths)
                    {
                        var targetFile = string.IsNullOrWhiteSpace(relPath)
                            ? Path.Combine(dir, onlineDef.PrimaryExeName)
                            : Path.Combine(dir, relPath.Replace('/', Path.DirectorySeparatorChar), onlineDef.PrimaryExeName);

                        if (File.Exists(targetFile))
                        {
                            var fastInfo = ExtractVersionFromExe(targetFile, dir);
                            if (fastInfo != null && fastInfo.IsDetected)
                            {
                                sw.Stop();
                                fastInfo.DetectionMechanism = "OnlineFastProbe";
                                fastInfo.OnlineSource = onlineDef.SourceNetwork;
                                fastInfo.ResolutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                                return fastInfo;
                            }
                        }
                    }

                    // Nếu fast-probe theo relative path chưa trúng, quét tìm chính xác file có tên onlineDef.PrimaryExeName
                    var exactExeInfo = ScanDirectoryForSpecificExecutable(dir, onlineDef.PrimaryExeName);
                    if (exactExeInfo != null && exactExeInfo.IsDetected)
                    {
                        sw.Stop();
                        exactExeInfo.DetectionMechanism = "OnlineExeSearch";
                        exactExeInfo.OnlineSource = onlineDef.SourceNetwork;
                        exactExeInfo.ResolutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                        return exactExeInfo;
                    }
                }
            }
        }
        catch { }

        if (cancellationToken.IsCancellationRequested) return null;

        // Giai đoạn 2: Quét đệ quy có kiểm soát độ sâu và chấm điểm (LocalRecursiveBFS)
        foreach (var dir in candidateDirs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var info = ScanDirectoryForBestExecutable(dir, gameName);
            if (info != null && info.IsDetected)
            {
                sw.Stop();
                info.DetectionMechanism = "LocalRecursiveBFS";
                info.OnlineSource = "Offline / Local";
                info.ResolutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                return info;
            }

            // Kiểm tra các file marker đặc biệt (vd: v1.004.blake3, version.txt)
            var markerVer = ScanForVersionMarkerFiles(dir);
            if (!string.IsNullOrWhiteSpace(markerVer))
            {
                sw.Stop();
                return new GameVersionInfo
                {
                    DisplayVersion = markerVer,
                    InstallDirectory = dir,
                    DetectionSource = "DirectoryMarker",
                    DetectionMechanism = "DirectoryMarker",
                    OnlineSource = "Offline / Local",
                    ResolutionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
                };
            }
        }

        return null;
    }

    private GameVersionInfo? DetectFromExecutables(
        string gameName,
        string normalizedTarget,
        string? knownGameDirectory,
        string? steamAppId = null)
    {
        return Task.Run(() => DetectFromExecutablesAsync(gameName, normalizedTarget, knownGameDirectory, steamAppId, CancellationToken.None)).GetAwaiter().GetResult();
    }

    public GameVersionInfo? ScanDirectoryForSpecificExecutable(string rootDir, string targetExeName)
    {
        if (!Directory.Exists(rootDir) || string.IsNullOrWhiteSpace(targetExeName)) return null;

        try
        {
            var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
            queue.Enqueue((new DirectoryInfo(rootDir), 0));

            while (queue.Count > 0)
            {
                var (currentDir, depth) = queue.Dequeue();

                try
                {
                    var file = currentDir.GetFiles(targetExeName, SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (file != null)
                    {
                        return ExtractVersionFromExe(file.FullName, rootDir);
                    }
                }
                catch { }

                if (depth < 4)
                {
                    try
                    {
                        var subDirs = currentDir.GetDirectories();
                        foreach (var sub in subDirs)
                        {
                            if (!IsIgnoredFolderName(sub.Name))
                            {
                                queue.Enqueue((sub, depth + 1));
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return null;
    }

    public static GameVersionInfo? ExtractVersionFromExe(string exePath, string rootDir)
    {
        try
        {
            var file = new FileInfo(exePath);
            if (!file.Exists) return null;

            var vi = FileVersionInfo.GetVersionInfo(file.FullName);
            var rawProduct = CleanVersionString(vi.ProductVersion);
            var rawFile = CleanVersionString(vi.FileVersion);

            // 1. Ưu tiên cao nhất: Chuỗi ProductVersion & FileVersion chuẩn nếu có
            var chosenVersion = !string.IsNullOrWhiteSpace(rawProduct) && !IsTrivialVersion(rawProduct)
                ? rawProduct
                : (!string.IsNullOrWhiteSpace(rawFile) && !IsTrivialVersion(rawFile) ? rawFile : null);

            // 2. Nếu chuỗi rỗng (như game Square Enix, Unreal Engine, Unity một số bản), đọc từ numeric parts:
            if (string.IsNullOrWhiteSpace(chosenVersion))
            {
                if (vi.ProductMajorPart > 0 || vi.ProductMinorPart > 0 || vi.ProductBuildPart > 0 || vi.ProductPrivatePart > 0)
                {
                    var rawParts = $"{vi.ProductMajorPart}.{vi.ProductMinorPart}.{vi.ProductBuildPart}.{vi.ProductPrivatePart}";
                    var cleanParts = CleanVersionString(rawParts);
                    if (!string.IsNullOrWhiteSpace(cleanParts) && !IsTrivialVersion(cleanParts))
                    {
                        chosenVersion = cleanParts;
                    }
                    else if (!string.IsNullOrWhiteSpace(cleanParts))
                    {
                        chosenVersion = cleanParts;
                    }
                }
                else if (vi.FileMajorPart > 0 || vi.FileMinorPart > 0 || vi.FileBuildPart > 0 || vi.FilePrivatePart > 0)
                {
                    var rawParts = $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
                    var cleanParts = CleanVersionString(rawParts);
                    if (!string.IsNullOrWhiteSpace(cleanParts) && !IsTrivialVersion(cleanParts))
                    {
                        chosenVersion = cleanParts;
                    }
                    else if (!string.IsNullOrWhiteSpace(cleanParts))
                    {
                        chosenVersion = cleanParts;
                    }
                }
            }

            // 3. Nếu chuỗi là trivial (vd: 1.0.0.0 hoặc 1.0), nhưng có chuỗi thì vẫn dùng hơn là bỏ qua
            if (string.IsNullOrWhiteSpace(chosenVersion))
            {
                if (!string.IsNullOrWhiteSpace(rawProduct)) chosenVersion = rawProduct;
                else if (!string.IsNullOrWhiteSpace(rawFile)) chosenVersion = rawFile;
            }

            // 5. Fallback cuối cùng cho file exe: Ngày sửa đổi/build của file exe
            if (string.IsNullOrWhiteSpace(chosenVersion))
            {
                chosenVersion = $"Build {file.LastWriteTime:yyyy.MM.dd}";
            }

            if (!string.IsNullOrWhiteSpace(chosenVersion))
            {
                var display = chosenVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
                              chosenVersion.StartsWith("Build", StringComparison.OrdinalIgnoreCase)
                    ? chosenVersion
                    : $"v{chosenVersion}";

                var rawProdStr = !string.IsNullOrWhiteSpace(vi.ProductVersion)
                    ? vi.ProductVersion
                    : (vi.ProductMajorPart > 0 || vi.ProductMinorPart > 0 || vi.ProductBuildPart > 0 || vi.ProductPrivatePart > 0
                        ? $"{vi.ProductMajorPart}.{vi.ProductMinorPart}.{vi.ProductBuildPart}.{vi.ProductPrivatePart}"
                        : null);

                var rawFileStr = !string.IsNullOrWhiteSpace(vi.FileVersion)
                    ? vi.FileVersion
                    : (vi.FileMajorPart > 0 || vi.FileMinorPart > 0 || vi.FileBuildPart > 0 || vi.FilePrivatePart > 0
                        ? $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}"
                        : null);

                return new GameVersionInfo
                {
                    DisplayVersion = display,
                    RawProductVersion = rawProdStr,
                    RawFileVersion = rawFileStr,
                    ExecutablePath = file.FullName,
                    ExecutableModifiedDate = file.LastWriteTime,
                    InstallDirectory = rootDir,
                    DetectionSource = "ExecutableMetadata"
                };
            }
        }
        catch { }

        return null;
    }

    public GameVersionInfo? ScanDirectoryForBestExecutable(string rootDir, string gameName)
    {
        if (!Directory.Exists(rootDir)) return null;

        var exeCandidates = new List<FileInfo>();
        try
        {
            // Sử dụng Queue (BFS) để duyệt tối đa 4 cấp thư mục con
            // Nhằm tìm các exe nằm sâu như: game\bin\x64\Cyberpunk2077.exe hoặc Unreal Engine Binaries\Win64
            var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
            queue.Enqueue((new DirectoryInfo(rootDir), 0));

            while (queue.Count > 0)
            {
                var (currentDir, depth) = queue.Dequeue();

                try
                {
                    var files = currentDir.GetFiles("*.exe", SearchOption.TopDirectoryOnly);
                    exeCandidates.AddRange(files);
                }
                catch { }

                // Tiếp tục duyệt thư mục con nếu chưa vượt quá độ sâu tối đa (4 cấp)
                if (depth < 4)
                {
                    try
                    {
                        var subDirs = currentDir.GetDirectories();
                        foreach (var sub in subDirs)
                        {
                            if (!IsIgnoredFolderName(sub.Name))
                            {
                                queue.Enqueue((sub, depth + 1));
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch
        {
            return null;
        }

        // Lọc bỏ các exe không phải game chính (crash handler, uninstaller, launcher, prelauncher...)
        var filteredExes = exeCandidates.Where(e => !IsHelperExecutable(e.Name, gameName)).ToList();
        if (filteredExes.Count == 0) return null;

        // Xếp hạng exe: ưu tiên khớp metadata (ProductName/Description), tên exe gần với tên game, thư mục binary chuẩn, dung lượng file lớn
        var scored = filteredExes.Select(e => new
        {
            File = e,
            Score = ScoreExecutable(e, gameName)
        }).OrderByDescending(x => x.Score).ToList();

        foreach (var item in scored)
        {
            var info = ExtractVersionFromExe(item.File.FullName, rootDir);
            if (info != null && info.IsDetected)
            {
                return info;
            }
        }

        return null;
    }

    private static string? ScanForVersionMarkerFiles(string dir)
    {
        try
        {
            // Tìm file dạng v1.004.blake3 hoặc version.txt
            var files = Directory.GetFiles(dir, "v*.*", SearchOption.TopDirectoryOnly);
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var match = Regex.Match(name, @"^v\d+(?:\.\d+)+", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            var verFile = Path.Combine(dir, "version.txt");
            if (File.Exists(verFile))
            {
                var line = File.ReadAllLines(verFile).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line) && line.Length < 30)
                {
                    return line.Trim();
                }
            }
        }
        catch { }

        return null;
    }

    private static int ScoreExecutable(FileInfo file, string gameName)
    {
        int score = 0;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(file.Name);
        var normFile = DatabaseService.NormalizeGameName(nameWithoutExt);
        var normGame = DatabaseService.NormalizeGameName(gameName);

        // Khớp hoàn toàn tên file với tên game
        if (normFile.Equals(normGame, StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        else if (normFile.Contains(normGame) || normGame.Contains(normFile))
        {
            score += 500;
        }

        // Đọc metadata file (ProductName, FileDescription) để đối chiếu trực tiếp với tên game
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(file.FullName);
            if (!string.IsNullOrWhiteSpace(vi.ProductName))
            {
                var normProduct = DatabaseService.NormalizeGameName(vi.ProductName);
                if (normProduct.Equals(normGame, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1500; // Khớp chuẩn xác tên game trong Product Details
                }
                else if (normProduct.Contains(normGame) || normGame.Contains(normProduct))
                {
                    score += 800;
                }
            }

            if (!string.IsNullOrWhiteSpace(vi.FileDescription))
            {
                var normDesc = DatabaseService.NormalizeGameName(vi.FileDescription);
                if (normDesc.Equals(normGame, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1000;
                }
                else if (normDesc.Contains(normGame) || normGame.Contains(normDesc))
                {
                    score += 500;
                }
            }
        }
        catch { }

        // File nằm ở thư mục binary chuẩn: bin\x64, Binaries\Win64, Binaries\WinGDK
        var dirPath = file.DirectoryName ?? "";
        if (dirPath.Contains(@"Binaries\Win64", StringComparison.OrdinalIgnoreCase) ||
            dirPath.Contains(@"bin\x64", StringComparison.OrdinalIgnoreCase) ||
            dirPath.Contains(@"Binaries\WinGDK", StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }
        else if (dirPath.Contains(@"bin", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        // Tên file có hậu tố Shipping của Unreal Engine
        if (nameWithoutExt.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }

        // Điểm theo dung lượng file (game executable chính thường nặng > 10MB, > 50MB)
        if (file.Length > 50 * 1024 * 1024) score += 150;
        else if (file.Length > 20 * 1024 * 1024) score += 100;
        else if (file.Length > 5 * 1024 * 1024) score += 50;

        return score;
    }

    private static bool IsHelperExecutable(string fileName, string gameName = "")
    {
        var lower = fileName.ToLowerInvariant();

        // Nếu game bản thân có từ khóa "launcher" thì không lọc bỏ launcher
        var gameHasLauncher = !string.IsNullOrWhiteSpace(gameName) &&
                              gameName.Contains("launcher", StringComparison.OrdinalIgnoreCase);

        if (!gameHasLauncher && (lower.Contains("launcher") || lower.Contains("prelauncher")))
        {
            return true;
        }

        return lower.Contains("crash") ||
               lower.Contains("report") ||
               lower.Contains("unins") ||
               lower.Contains("setup") ||
               lower.Contains("update") ||
               lower.Contains("redist") ||
               lower.Contains("vc_redist") ||
               lower.Contains("dxwebsetup") ||
               lower.Contains("rapidcrc") ||
               lower.Contains("7za") ||
               lower.Contains("errorreporter") ||
               lower.Contains("crashreporter") ||
               lower.Contains("helper") ||
               lower.Contains("benchmark") ||
               lower.Contains("config") ||
               lower.Contains("redmod") ||
               lower.Contains("scc.exe") ||
               lower.Contains("easyanticheat") ||
               lower.Contains("battleye") ||
               lower.Contains("eac") ||
               lower.Contains("unitycrashhandler") ||
               lower.Contains("cef") ||
               lower.Contains("webview") ||
               lower.Equals("cmd.exe");
    }

    private static bool IsIgnoredFolderName(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        return lower.StartsWith(".") ||
               lower.Equals("_commonredist") ||
               lower.Equals("support") ||
               lower.Equals("tools") ||
               lower.Equals("directx") ||
               lower.Equals("vcredist") ||
               lower.Equals("crashreporter") ||
               lower.Equals("errorreporter") ||
               lower.Equals("redmod") ||
               lower.Equals("dotnet") ||
               lower.Equals("easyanticheat") ||
               lower.Equals("battleye") ||
               lower.Equals("node_modules") ||
               lower.Equals("cache") ||
               lower.Equals("temp");
    }

    private static string? CleanVersionString(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var clean = version.Trim();

        // Xóa phần commit hash hoặc thông tin phụ trong ngoặc (vd: 6000.0.62f1 (f99f05b3e950) -> 6000.0.62f1)
        var parenIndex = clean.IndexOf('(');
        if (parenIndex > 0)
        {
            clean = clean.Substring(0, parenIndex).Trim();
        }

        // Xóa phần build metadata sau dấu cộng (vd: 1.0.0+47f892a -> 1.0.0)
        var plusIndex = clean.IndexOf('+');
        if (plusIndex > 0)
        {
            clean = clean.Substring(0, plusIndex).Trim();
        }

        // Bỏ dấu phẩy thừa nếu có (vd: 1, 2, 3, 4 -> 1.2.3.4)
        clean = clean.Replace(',', '.').Replace(" ", "");

        // Rút gọn dạng 2.31.0.0 thành 2.31 nếu hai số cuối là 0
        var matchFourParts = Regex.Match(clean, @"^(\d+\.\d+)\.0\.0$");
        if (matchFourParts.Success)
        {
            clean = matchFourParts.Groups[1].Value;
        }

        return clean;
    }

    private static bool IsTrivialVersion(string version)
    {
        var digits = Regex.Replace(version, @"[^\d]", "");
        return digits == "0" || digits == "0000" || digits == "1000"; // Bỏ qua 0.0.0.0 hoặc 1.0.0.0
    }

    #endregion

    #region Windows Registry Uninstall Detection

    private GameVersionInfo? DetectFromWindowsRegistry(string gameName, string normalizedTarget)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var hives = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var hive in hives)
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(hive);
            if (baseKey == null) continue;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                using var subKey = baseKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName")?.ToString() ?? "";
                var displayVer = subKey.GetValue("DisplayVersion")?.ToString() ?? "";
                var installLoc = subKey.GetValue("InstallLocation")?.ToString() ?? "";

                if (!string.IsNullOrWhiteSpace(displayVer) && MatchesGameName(gameName, normalizedTarget, displayName, installLoc))
                {
                    var cleanVer = displayVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? displayVer : $"v{displayVer}";
                    return new GameVersionInfo
                    {
                        DisplayVersion = cleanVer,
                        InstallDirectory = installLoc,
                        DetectionSource = "WindowsRegistry"
                    };
                }
            }
        }

        return null;
    }

    #endregion

    #region Helper Matching Methods

    private static readonly HashSet<string> _genericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "games", "game", "steam", "steamlibrary", "steamapps", "common", "data", "backup",
        "saved", "saves", "programfiles", "download", "downloads", "users", "system", "temp", "bin", "windows"
    };

    private List<string> FindGameDirectoriesOnDrives(string gameName, string normalizedTarget)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(gameName)) return results;

        var subLibraryRoots = new[]
        {
            "Games",
            "Game",
            Path.Combine("SteamLibrary", "steamapps", "common"),
            Path.Combine("Steam", "steamapps", "common"),
            Path.Combine("Program Files (x86)", "Steam", "steamapps", "common"),
            Path.Combine("Program Files", "Steam", "steamapps", "common"),
            Path.Combine("Program Files", "Epic Games"),
            Path.Combine("Program Files (x86)", "Epic Games"),
            Path.Combine("Program Files", "GOG Galaxy", "Games"),
            "GOG Games",
            "XboxGames"
        };

        // Chỉ quét các ổ đĩa cố định (Fixed) đang sẵn sàng để tránh nghẽn I/O trên ổ CD-ROM / thẻ nhớ rỗng
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;

            // Kiểm tra các thư mục trực tiếp ở ổ đĩa (vd: F:\Cyberpunk.2077..., E:\FINAL FANTASY...)
            try
            {
                var topDirs = Directory.GetDirectories(root);
                foreach (var td in topDirs)
                {
                    var dirName = Path.GetFileName(td);
                    if (MatchesGameName(gameName, normalizedTarget, dirName))
                    {
                        results.Add(td);
                    }
                }
            }
            catch { }

            // Kiểm tra trong các thư mục thư viện phổ biến (Games, SteamLibrary, Epic Games, XboxGames...)
            foreach (var subLib in subLibraryRoots)
            {
                var fullLibPath = Path.Combine(root, subLib);
                if (Directory.Exists(fullLibPath))
                {
                    try
                    {
                        var gameSubDirs = Directory.GetDirectories(fullLibPath);
                        foreach (var gd in gameSubDirs)
                        {
                            var dirName = Path.GetFileName(gd);
                            if (MatchesGameName(gameName, normalizedTarget, dirName))
                            {
                                results.Add(gd);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool MatchesGameName(string targetName, string normalizedTarget, params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;

            var normCandidate = DatabaseService.NormalizeGameName(c);
            if (string.Equals(normCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normCandidate.Length >= 4 && normalizedTarget.Length >= 4)
            {
                if (_genericFolderNames.Contains(normCandidate)) continue;

                // Thư mục chứa trọn vẹn tên game (vd: F:\Cyberpunk.2077.v2.2.ALL.DLC chứa "cyberpunk2077")
                if (normCandidate.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Tên game chứa candidate (vd: Game "The Witcher 3: Wild Hunt" chứa "The Witcher 3")
                if (normalizedTarget.Contains(normCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    if (normCandidate.Length >= 8 || ((double)normCandidate.Length / normalizedTarget.Length >= 0.5))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    #endregion
}
