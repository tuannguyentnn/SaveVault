using System.Text.RegularExpressions;
using Microsoft.Win32;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class PathResolverService
{
    private string? _detectedSteamPath;

    public string GetSteamPath()
    {
        if (!string.IsNullOrEmpty(_detectedSteamPath) && Directory.Exists(_detectedSteamPath))
        {
            return _detectedSteamPath;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath")?.ToString();
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    _detectedSteamPath = path.Replace('/', '\\');
                    return _detectedSteamPath;
                }

                using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var path64 = key64?.GetValue("InstallPath")?.ToString();
                if (!string.IsNullOrEmpty(path64) && Directory.Exists(path64))
                {
                    _detectedSteamPath = path64.Replace('/', '\\');
                    return _detectedSteamPath;
                }
            }
        }
        catch
        {
            // Fallback if registry access fails
        }

        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"E:\Steam",
            @"C:\Steam"
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                _detectedSteamPath = c;
                return c;
            }
        }

        return @"C:\Program Files (x86)\Steam";
    }

    public List<string> GetSteamUserIds()
    {
        var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 1. Lấy từ Registry ActiveUser (User đang chạy hoặc đăng nhập gần nhất)
                using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                var activeUser = activeKey?.GetValue("ActiveUser");
                if (activeUser is int activeInt && activeInt > 0)
                {
                    userIds.Add(activeInt.ToString());
                }
                else if (activeUser != null && long.TryParse(activeUser.ToString(), out var activeLong) && activeLong > 0)
                {
                    userIds.Add(activeLong.ToString());
                }

                // 2. Lấy từ danh sách Users trong Registry
                using var usersKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Users");
                if (usersKey != null)
                {
                    foreach (var subKey in usersKey.GetSubKeyNames())
                    {
                        if (long.TryParse(subKey, out var uid) && uid > 0)
                        {
                            userIds.Add(subKey);
                        }
                    }
                }
            }
        }
        catch { }

        // 3. Quét các thư mục con trong <SteamPath>\userdata
        try
        {
            var steamPath = GetSteamPath();
            var userDataPath = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(userDataPath))
            {
                foreach (var dir in Directory.GetDirectories(userDataPath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (long.TryParse(dirName, out var uid) && uid > 0)
                    {
                        userIds.Add(dirName);
                    }
                }
            }
        }
        catch { }

        return userIds.ToList();
    }

    public string? GetActiveSteamUserId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var activeKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                var activeUser = activeKey?.GetValue("ActiveUser");
                if (activeUser is int activeInt && activeInt > 0)
                {
                    return activeInt.ToString();
                }
                if (activeUser != null && long.TryParse(activeUser.ToString(), out var activeLong) && activeLong > 0)
                {
                    return activeLong.ToString();
                }
            }
        }
        catch { }

        return GetSteamUserIds().FirstOrDefault();
    }

    public bool IsSystemOrContainerDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var clean = path.Trim().TrimEnd('\\', '/');

        // Ổ đĩa gốc (C:, D:, C:\, D:\)
        if (clean.Length <= 3 && clean.Contains(':')) return true;

        var steamPath = GetSteamPath().TrimEnd('\\', '/');
        var steamUserData = Path.Combine(steamPath, "userdata").TrimEnd('\\', '/');

        if (string.Equals(clean, steamPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(clean, steamUserData, StringComparison.OrdinalIgnoreCase)) return true;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\', '/');
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\', '/');
        var localLow = Path.Combine(userProfile, "AppData", "LocalLow").TrimEnd('\\', '/');
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\', '/');
        var savedGames = Path.Combine(userProfile, "Saved Games").TrimEnd('\\', '/');
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\', '/');
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\', '/');
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\', '/');
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/');

        var systemDirs = new[]
        {
            userProfile, appData, localAppData, localLow, myDocuments, savedGames,
            programData, programFiles, programFilesX86, windowsDir
        };

        return systemDirs.Any(s => !string.IsNullOrEmpty(s) && string.Equals(clean, s, StringComparison.OrdinalIgnoreCase));
    }

    public List<string> ResolveRawPattern(string pattern)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pattern)) return results.ToList();

        var cleaned = pattern.Trim();
        // Bỏ qua nếu chuỗi rác như "{{p" hoặc quá ngắn
        if (cleaned.Equals("{{p", StringComparison.OrdinalIgnoreCase) || cleaned.Length < 3)
            return results.ToList();

        // Xóa sạch phần thẻ dở dang ở đuôi như \{{p hoặc \{{p|...
        cleaned = Regex.Replace(cleaned, @"[\\/]?\{\{p(?:\|[^}]*)?$", "", RegexOptions.IgnoreCase);
        cleaned = cleaned.TrimEnd('\\', '/', ' ');
        if (string.IsNullOrWhiteSpace(cleaned)) return results.ToList();

        // Remove {{file|...}} markup if wrapped
        if (cleaned.StartsWith("{{file|", StringComparison.OrdinalIgnoreCase) && cleaned.EndsWith("}}"))
        {
            cleaned = cleaned.Substring(7, cleaned.Length - 9).Trim();
        }

        // Cân bằng ngoặc nếu có ngoặc đóng dư thừa
        int openBraces = cleaned.Count(c => c == '{');
        int closeBraces = cleaned.Count(c => c == '}');
        while (closeBraces > openBraces && cleaned.EndsWith("}"))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 1);
            closeBraces--;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.Combine(userProfile, "AppData", "LocalLow");
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var savedGames = Path.Combine(userProfile, "Saved Games");
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var steamPath = GetSteamPath();

        // Replace common tags
        cleaned = ReplaceTag(cleaned, "appdata", appData);
        cleaned = ReplaceTag(cleaned, "localappdata", localAppData);
        cleaned = ReplaceTag(cleaned, "locallow", localLow);
        cleaned = ReplaceTag(cleaned, "userprofile", userProfile);
        cleaned = ReplaceTag(cleaned, "user", userProfile);
        cleaned = ReplaceTag(cleaned, "username", Environment.UserName);
        cleaned = ReplaceTag(cleaned, "documents", myDocuments);
        cleaned = ReplaceTag(cleaned, "savedgames", savedGames);
        cleaned = ReplaceTag(cleaned, "public", publicFolder);
        cleaned = ReplaceTag(cleaned, "programdata", programData);
        cleaned = ReplaceTag(cleaned, "programfiles", programFiles);
        cleaned = ReplaceTag(cleaned, "programfiles(x86)", programFilesX86);
        cleaned = ReplaceTag(cleaned, "windir", windowsDir);
        cleaned = ReplaceTag(cleaned, "systemroot", windowsDir);
        cleaned = ReplaceTag(cleaned, "steam", steamPath);
        cleaned = ReplaceTag(cleaned, "root", steamPath);

        // Windows Environment Variables (%APPDATA%, %LOCALAPPDATA%, etc.)
        cleaned = Environment.ExpandEnvironmentVariables(cleaned);

        // Standardize slashes
        cleaned = cleaned.Replace('/', '\\');

        // Check if there are user ID placeholders or wildcards
        var uidPattern = @"\{\{p\|(?:uid|steamid|storeuserid|uplayid|originid|gogid|accountid)\}\}|<(?:user-id|steam-id|storeUserId|account-id)>|%USERID%";
        var hasUidPlaceholder = Regex.IsMatch(cleaned, uidPattern, RegexOptions.IgnoreCase);
        if (hasUidPlaceholder)
        {
            // 1. Quét tìm tất cả Steam User ID trên máy tính để thế trực tiếp
            var knownUserIds = GetSteamUserIds();
            foreach (var uid in knownUserIds)
            {
                var specificReplaced = Regex.Replace(cleaned, uidPattern, uid, RegexOptions.IgnoreCase);
                ExpandWildcards(specificReplaced, results);
            }

            // 2. Luôn mở rộng bằng wildcard '*' để tìm bất kỳ thư mục account/user ID thực tế nào khác trên ổ đĩa
            var wildcardReplaced = Regex.Replace(cleaned, uidPattern, "*", RegexOptions.IgnoreCase);
            ExpandWildcards(wildcardReplaced, results);

            // 3. Nếu thẻ placeholder nằm ở thư mục cuối cùng (ví dụ: ...\SaveGames\{{p|uid}}),
            // cũng thêm cả thư mục cha (...\SaveGames) nếu nó KHÔNG PHẢI thư mục container hệ thống (như userdata)
            var parentPath = Regex.Replace(cleaned, @"[\\/](?:" + uidPattern + @")[\\/]?$", "", RegexOptions.IgnoreCase);
            if (!string.Equals(parentPath, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSystemOrContainerDirectory(parentPath))
                {
                    ExpandWildcards(parentPath, results);
                }
            }
        }
        else
        {
            ExpandWildcards(cleaned, results);
        }

        return results.ToList();
    }

    private static string ReplaceTag(string text, string tagName, string replacement)
    {
        // Hỗ trợ cả {{p|tagName}} và {{p|tagName\subpath}} hoặc {{p|tagName/subpath}}
        var pattern = @"\{\{p\|" + Regex.Escape(tagName) + @"(?:[\\/]([^}]+))?\}\}";
        text = Regex.Replace(text, pattern, m =>
        {
            if (m.Groups[1].Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
            {
                var sub = m.Groups[1].Value.Trim().Replace('/', '\\');
                return Path.Combine(replacement, sub);
            }
            return replacement;
        }, RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"<" + Regex.Escape(tagName) + @">", replacement, RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>
    /// Chuyển đổi đường dẫn tuyệt đối (trên máy hiện tại) thành đường dẫn động có chứa Windows Environment Variable
    /// (%LOCALAPPDATA%, %APPDATA%, %USERPROFILE%, %PROGRAMDATA%, %PUBLIC%).
    /// Giúp lưu trữ vào DB hoặc file manifest chuẩn Windows, an toàn khi mang sang máy khác.
    /// </summary>
    public static string NormalizePathToPlaceholder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var normalized = path.Trim().Replace('/', '\\');

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.Combine(userProfile, "AppData", "LocalLow");
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var savedGames = Path.Combine(userProfile, "Saved Games");
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // Danh sách ánh xạ thư mục sang biến môi trường Windows (%...%)
        // Sắp xếp thư mục con/chuyên sâu lên trước để ưu tiên match (LocalLow, LocalAppData, AppData trước UserProfile)
        var mappings = new List<(string FolderPath, string EnvVar)>
        {
            (localLow, @"%USERPROFILE%\AppData\LocalLow"),
            (localAppData, "%LOCALAPPDATA%"),
            (appData, "%APPDATA%"),
            (savedGames, @"%USERPROFILE%\Saved Games"),
            (myDocuments, @"%USERPROFILE%\Documents"),
            (publicFolder, "%PUBLIC%"),
            (programData, "%PROGRAMDATA%"),
            (userProfile, "%USERPROFILE%")
        };

        foreach (var (folderPath, envVar) in mappings)
        {
            if (string.IsNullOrEmpty(folderPath)) continue;

            var cleanFolder = folderPath.TrimEnd('\\');
            if (normalized.Equals(cleanFolder, StringComparison.OrdinalIgnoreCase))
            {
                return envVar;
            }

            if (normalized.StartsWith(cleanFolder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(cleanFolder.Length);
                return envVar + relative;
            }
        }

        // Fallback: Tự động chuyển đổi nếu đường dẫn thuộc về User/Máy tính khác
        // (ví dụ: C:\Users\TuanNguyen\... hoặc C:\User\AnotherUser\... hoặc trên ổ đĩa khác)
        var regexMappings = new (string Pattern, string Replacement)[]
        {
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\AppData\\LocalLow(?=\\|$)", @"%USERPROFILE%\AppData\LocalLow"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\AppData\\Local(?=\\|$)", "%LOCALAPPDATA%"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\AppData\\Roaming(?=\\|$)", "%APPDATA%"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\Saved Games(?=\\|$)", @"%USERPROFILE%\Saved Games"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\(?:Documents|My Documents)(?=\\|$)", @"%USERPROFILE%\Documents"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+\\Desktop(?=\\|$)", @"%USERPROFILE%\Desktop"),
            (@"^[a-zA-Z]:\\Users?\\Public(?=\\|$)", "%PUBLIC%"),
            (@"^[a-zA-Z]:\\ProgramData(?=\\|$)", "%PROGRAMDATA%"),
            (@"^[a-zA-Z]:\\Program Files \(x86\)(?=\\|$)", "%ProgramFiles(x86)%"),
            (@"^[a-zA-Z]:\\Program Files(?=\\|$)", "%ProgramFiles%"),
            (@"^[a-zA-Z]:\\Users?\\[^\\]+(?=\\|$)", "%USERPROFILE%")
        };

        foreach (var (pattern, replacement) in regexMappings)
        {
            if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase))
            {
                return Regex.Replace(normalized, pattern, replacement, RegexOptions.IgnoreCase);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Giải mã đường dẫn dạng biến môi trường (%APPDATA%, %LOCALAPPDATA%, %USERPROFILE%,...) 
    /// thành đường dẫn tuyệt đối trên máy hiện tại.
    /// </summary>
    public static string RemapPathToCurrentMachine(string path, string? steamPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var cleaned = path.Trim().Replace('/', '\\');

        // Mở rộng các biến môi trường chuẩn Windows (%APPDATA%, %LOCALAPPDATA%, %USERPROFILE%, v.v.)
        if (cleaned.Contains('%'))
        {
            cleaned = Environment.ExpandEnvironmentVariables(cleaned);
        }
        else
        {
            // Tự động chuẩn hóa nếu là đường dẫn tuyệt đối của user/máy khác chưa migrate
            var normalized = NormalizePathToPlaceholder(cleaned);
            if (!string.Equals(normalized, cleaned, StringComparison.OrdinalIgnoreCase) && normalized.Contains('%'))
            {
                cleaned = Environment.ExpandEnvironmentVariables(normalized);
            }
        }

        // Hỗ trợ thêm các thẻ nội bộ {{p|...}} nếu có
        if (cleaned.Contains("{{p|", StringComparison.OrdinalIgnoreCase) || cleaned.Contains('<'))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localLow = Path.Combine(userProfile, "AppData", "LocalLow");
            var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var savedGames = Path.Combine(userProfile, "Saved Games");
            var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var steam = !string.IsNullOrEmpty(steamPath) ? steamPath : @"C:\Program Files (x86)\Steam";

            cleaned = ReplaceTag(cleaned, "appdata", appData);
            cleaned = ReplaceTag(cleaned, "localappdata", localAppData);
            cleaned = ReplaceTag(cleaned, "locallow", localLow);
            cleaned = ReplaceTag(cleaned, "userprofile", userProfile);
            cleaned = ReplaceTag(cleaned, "user", userProfile);
            cleaned = ReplaceTag(cleaned, "username", Environment.UserName);
            cleaned = ReplaceTag(cleaned, "documents", myDocuments);
            cleaned = ReplaceTag(cleaned, "savedgames", savedGames);
            cleaned = ReplaceTag(cleaned, "public", publicFolder);
            cleaned = ReplaceTag(cleaned, "programdata", programData);
            cleaned = ReplaceTag(cleaned, "programfiles", programFiles);
            cleaned = ReplaceTag(cleaned, "programfiles(x86)", programFilesX86);
            cleaned = ReplaceTag(cleaned, "windir", windowsDir);
            cleaned = ReplaceTag(cleaned, "systemroot", windowsDir);
            cleaned = ReplaceTag(cleaned, "steam", steam);
            cleaned = ReplaceTag(cleaned, "root", steam);

            cleaned = Environment.ExpandEnvironmentVariables(cleaned);
        }

        cleaned = cleaned.Replace('/', '\\');

        // Nếu ổ đĩa chỉ định không tồn tại trên máy (vd: D:\ không có), tự động chuyển về ổ đĩa hệ thống
        if (cleaned.Length >= 3 && cleaned[1] == ':' && cleaned[2] == '\\')
        {
            var driveLetter = cleaned.Substring(0, 3);
            try
            {
                var driveInfo = new DriveInfo(driveLetter);
                if (!driveInfo.IsReady)
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                    var pathWithoutDrive = cleaned.Substring(3);
                    cleaned = Path.Combine(systemDrive, pathWithoutDrive);
                }
            }
            catch
            {
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                var pathWithoutDrive = cleaned.Substring(3);
                cleaned = Path.Combine(systemDrive, pathWithoutDrive);
            }
        }

        return cleaned;
    }

    private void ExpandWildcards(string path, HashSet<string> results)
    {
        // Clean trailing backslashes if not root
        path = path.TrimEnd('\\', '/');

        if (!path.Contains('*') && !path.Contains('?'))
        {
            results.Add(path);
            return;
        }

        try
        {
            var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string currentRoot;
            int startIndex;

            if (parts[0].Contains(':'))
            {
                currentRoot = parts[0] + "\\";
                startIndex = 1;
            }
            else
            {
                currentRoot = Path.GetPathRoot(path) ?? "C:\\";
                startIndex = 0;
            }

            var currentList = new List<string> { currentRoot.TrimEnd('\\') };

            for (int i = startIndex; i < parts.Length; i++)
            {
                var part = parts[i];
                var nextList = new List<string>();

                foreach (var parent in currentList)
                {
                    if (!Directory.Exists(parent)) continue;

                    if (part.Contains('*') || part.Contains('?'))
                    {
                        var matches = Directory.GetDirectories(parent, part);
                        nextList.AddRange(matches);

                        if (i == parts.Length - 1)
                        {
                            var fileMatches = Directory.GetFiles(parent, part);
                            if (fileMatches.Length > 0)
                            {
                                results.Add(parent);
                            }
                            nextList.AddRange(fileMatches);
                        }
                    }
                    else
                    {
                        var combined = Path.Combine(parent, part);
                        nextList.Add(combined);
                    }
                }

                currentList = nextList;
                if (currentList.Count == 0) break;
            }

            foreach (var match in currentList)
            {
                results.Add(match);
            }
        }
        catch
        {
            // If wildcard expansion fails, keep raw path
            results.Add(path);
        }
    }

    public List<DetectedPathItem> InspectDetectedPathItems(IEnumerable<string> candidatePaths)
    {
        var items = new List<DetectedPathItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Chuẩn hóa và lọc các đường dẫn tồn tại trước
        var existingDirectories = new List<string>();
        var existingFiles = new List<string>();

        foreach (var p in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var concrete = RemapPathToCurrentMachine(p);
            var normalized = concrete.Trim().TrimEnd('\\', '/');
            if (seen.Contains(normalized)) continue;

            // Bỏ qua các thư mục hệ thống hoặc container chung như D:\steam\userdata hoặc %APPDATA%
            if (IsSystemOrContainerDirectory(normalized)) continue;

            if (Directory.Exists(concrete))
            {
                seen.Add(normalized);
                existingDirectories.Add(normalized);
            }
            else if (File.Exists(concrete))
            {
                seen.Add(normalized);
                existingFiles.Add(normalized);
            }
        }

        // Tối ưu hóa: Nếu thư mục cha đã tồn tại (ví dụ: ...\Sandfall\Saved\SaveGames),
        // thì không cần thêm trùng lặp thư mục con bên trong nó (ví dụ: ...\SaveGames\76561197960271872).
        // Sắp xếp theo độ dài ngắn nhất trước để ưu tiên thư mục gốc cha.
        existingDirectories.Sort((a, b) => a.Length.CompareTo(b.Length));
        var prunedDirectories = new List<string>();
        foreach (var dir in existingDirectories)
        {
            bool isSubdirOfExisting = prunedDirectories.Any(parent =>
                dir.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase));

            if (!isSubdirOfExisting)
            {
                prunedDirectories.Add(dir);
            }
        }

        foreach (var p in prunedDirectories)
        {
            int fileCount = 0;
            long totalBytes = 0;
            try
            {
                var dirInfo = new DirectoryInfo(p);
                var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                fileCount = files.Length;
                totalBytes = files.Sum(f => f.Length);
            }
            catch
            {
                // Permission or read error
            }

            items.Add(new DetectedPathItem
            {
                Path = p,
                FileCount = fileCount,
                TotalSizeBytes = totalBytes,
                IsSelected = totalBytes > 0 && fileCount > 0
            });
        }

        foreach (var p in existingFiles)
        {
            bool isInsidePrunedDir = prunedDirectories.Any(parent =>
                p.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase));

            if (!isInsidePrunedDir)
            {
                long totalBytes = 0;
                try
                {
                    var fileInfo = new FileInfo(p);
                    totalBytes = fileInfo.Length;
                }
                catch
                {
                    // Ignore
                }

                items.Add(new DetectedPathItem
                {
                    Path = p,
                    FileCount = 1,
                    TotalSizeBytes = totalBytes,
                    IsSelected = totalBytes > 0
                });
            }
        }

        return items;
    }

    public (List<string> ExistingPaths, long TotalSizeBytes, int FileCount) InspectExistingData(IEnumerable<string> candidatePaths)
    {
        var items = InspectDetectedPathItems(candidatePaths);
        var existing = items.Select(i => i.Path).ToList();
        var totalBytes = items.Sum(i => i.TotalSizeBytes);
        var fileCount = items.Sum(i => i.FileCount);

        return (existing, totalBytes, fileCount);
    }
}
