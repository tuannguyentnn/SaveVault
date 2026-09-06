using System.Text.RegularExpressions;
using Microsoft.Win32;

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
        // Remove trailing or leading markup if any
        cleaned = Regex.Replace(cleaned, @"^\{\{file\|", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\}\}$", "");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var savedGames = Path.Combine(userProfile, "Saved Games");
        var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var steamPath = GetSteamPath();

        // Replace common tags
        cleaned = ReplaceTag(cleaned, "appdata", appData);
        cleaned = ReplaceTag(cleaned, "localappdata", localAppData);
        cleaned = ReplaceTag(cleaned, "userprofile", userProfile);
        cleaned = ReplaceTag(cleaned, "user", userProfile);
        cleaned = ReplaceTag(cleaned, "documents", myDocuments);
        cleaned = ReplaceTag(cleaned, "savedgames", savedGames);
        cleaned = ReplaceTag(cleaned, "public", publicFolder);
        cleaned = ReplaceTag(cleaned, "programdata", programData);
        cleaned = ReplaceTag(cleaned, "steam", steamPath);

        // Windows Environment Variables (%APPDATA%, %LOCALAPPDATA%, etc.)
        cleaned = Environment.ExpandEnvironmentVariables(cleaned);

        // Standardize slashes
        cleaned = cleaned.Replace('/', '\\');

        // Check if there are user ID placeholders or wildcards
        var hasUidPlaceholder = Regex.IsMatch(cleaned, @"\{\{p\|uid\}\}|<user-id>|<steam-id>|%USERID%", RegexOptions.IgnoreCase);
        if (hasUidPlaceholder)
        {
            var steamUserData = Path.Combine(steamPath, "userdata");
            if (Directory.Exists(steamUserData))
            {
                var userDirs = Directory.GetDirectories(steamUserData);
                foreach (var uDir in userDirs)
                {
                    var userId = Path.GetFileName(uDir);
                    var replaced = Regex.Replace(cleaned, @"\{\{p\|uid\}\}|<user-id>|<steam-id>|%USERID%", userId, RegexOptions.IgnoreCase);
                    ExpandWildcards(replaced, results);
                }
            }
            else
            {
                var replaced = Regex.Replace(cleaned, @"\{\{p\|uid\}\}|<user-id>|<steam-id>|%USERID%", "*", RegexOptions.IgnoreCase);
                ExpandWildcards(replaced, results);
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
        var pattern = @"\{\{p\|" + Regex.Escape(tagName) + @"\}\}";
        text = Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase);
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

    public (List<string> ExistingPaths, long TotalSizeBytes, int FileCount) InspectExistingData(IEnumerable<string> candidatePaths)
    {
        var existing = new List<string>();
        long totalBytes = 0;
        int fileCount = 0;

        foreach (var p in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;

            if (Directory.Exists(p))
            {
                existing.Add(p);
                try
                {
                    var dirInfo = new DirectoryInfo(p);
                    var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                    fileCount += files.Length;
                    totalBytes += files.Sum(f => f.Length);
                }
                catch
                {
                    // Permission or read error
                }
            }
            else if (File.Exists(p))
            {
                existing.Add(p);
                try
                {
                    var fileInfo = new FileInfo(p);
                    fileCount++;
                    totalBytes += fileInfo.Length;
                }
                catch
                {
                    // Ignore
                }
            }
        }

        return (existing, totalBytes, fileCount);
    }
}
