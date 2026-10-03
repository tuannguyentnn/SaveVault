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

        // Windows Environment Variables (%APPDATA%, %LOCALAPPDATA%, etc.)
        cleaned = Environment.ExpandEnvironmentVariables(cleaned);

        // Standardize slashes
        cleaned = cleaned.Replace('/', '\\');

        // Check if there are user ID placeholders or wildcards
        var uidPattern = @"\{\{p\|(?:uid|steamid|uplayid|originid|gogid|accountid)\}\}|<user-id>|<steam-id>|<account-id>|%USERID%";
        var hasUidPlaceholder = Regex.IsMatch(cleaned, uidPattern, RegexOptions.IgnoreCase);
        if (hasUidPlaceholder)
        {
            // 1. Luôn mở rộng bằng wildcard '*' để tìm bất kỳ thư mục account/user ID thực tế nào trên ổ đĩa
            var wildcardReplaced = Regex.Replace(cleaned, uidPattern, "*", RegexOptions.IgnoreCase);
            ExpandWildcards(wildcardReplaced, results);

            // 2. Nếu thẻ placeholder nằm ở thư mục cuối cùng (ví dụ: ...\SaveGames\{{p|uid}}),
            // cũng thêm cả thư mục cha (...\SaveGames) vì nhiều game hoặc phiên bản lưu trực tiếp tại đây
            var parentPath = Regex.Replace(cleaned, @"[\\/](?:" + uidPattern + @")[\\/]?$", "", RegexOptions.IgnoreCase);
            if (!string.Equals(parentPath, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                ExpandWildcards(parentPath, results);
            }

            // 3. Nếu đường dẫn nằm trong Steam userdata, thử thế thêm các subfolder ID cụ thể trong Steam userdata
            var steamUserData = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(steamUserData) && cleaned.Contains(@"\userdata\", StringComparison.OrdinalIgnoreCase))
            {
                var userDirs = Directory.GetDirectories(steamUserData);
                foreach (var uDir in userDirs)
                {
                    var userId = Path.GetFileName(uDir);
                    var replaced = Regex.Replace(cleaned, uidPattern, userId, RegexOptions.IgnoreCase);
                    ExpandWildcards(replaced, results);
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
            var normalized = p.Trim().TrimEnd('\\', '/');
            if (seen.Contains(normalized)) continue;

            if (Directory.Exists(p))
            {
                seen.Add(normalized);
                existingDirectories.Add(normalized);
            }
            else if (File.Exists(p))
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
                IsSelected = true
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
                    IsSelected = true
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
