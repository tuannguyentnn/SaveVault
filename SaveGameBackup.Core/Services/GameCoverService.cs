using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Quản lý việc lưu trữ, tải xuống và cung cấp ảnh bìa (Game Cover / Box Art) cục bộ cho game.
/// Đảm bảo hoạt động offline 100% sau khi đã tải, chống xung đột đường dẫn cục bộ trong WebView2.
/// </summary>
public static class GameCoverService
{
    private static readonly ConcurrentDictionary<string, string> _dataUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient _httpClient;

    static GameCoverService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.pcgamingwiki.com/");
    }

    /// <summary>
    /// Lấy đường dẫn thư mục Covers ở gốc ứng dụng.
    /// </summary>
    public static string GetCoverDirectory()
    {
        var rootDir = DatabaseService.GetDefaultProjectRoot();
        var coversDir = Path.Combine(rootDir, "Covers");
        if (!Directory.Exists(coversDir))
        {
            try
            {
                Directory.CreateDirectory(coversDir);
            }
            catch { /* Ignore */ }
        }
        return coversDir;
    }

    /// <summary>
    /// Trả về đường dẫn file ảnh cover cục bộ chuẩn hóa theo tên game.
    /// </summary>
    public static string GetCoverFilePath(string gameName)
    {
        var sanitized = SanitizeFileName(gameName);
        return Path.Combine(GetCoverDirectory(), $"{sanitized}.jpg");
    }

    /// <summary>
    /// Kiểm tra game đã có file ảnh bìa cục bộ trên máy chưa.
    /// </summary>
    public static bool HasLocalCover(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return false;
        var filePath = GetCoverFilePath(gameName);
        return File.Exists(filePath);
    }

    /// <summary>
    /// Tra cứu nhanh URL ảnh bìa online (từ PCGamingWiki hoặc Steam CDN) để xem trước, KHÔNG tải về đĩa.
    /// Nếu máy đã có sẵn file cục bộ thì trả về đường dẫn ảo https://covers.local/... đó.
    /// </summary>
    public static async Task<string?> FindOnlineCoverUrlAsync(string gameName, string? steamAppId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var localFile = GetCoverFilePath(gameName);
        if (File.Exists(localFile))
        {
            return GetCoverImageUri(localFile);
        }

        try
        {
            string? pcgwCoverUrl = null;
            string? extractedSteamAppId = null;

            try
            {
                (pcgwCoverUrl, extractedSteamAppId) = await FetchPCGamingWikiCoverAndSteamIdAsync(gameName, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi tra cứu PCGW preview cho {Game}: {Message}", gameName, ex.Message);
            }

            var effectiveSteamAppId = steamAppId ?? extractedSteamAppId;
            if (!string.IsNullOrEmpty(effectiveSteamAppId))
            {
                return $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/library_600x900.jpg";
            }

            if (!string.IsNullOrEmpty(pcgwCoverUrl))
            {
                return pcgwCoverUrl;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể tìm link ảnh online cho {Game}: {Message}", gameName, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Tự động resize ảnh về chiều rộng chuẩn (mặc định width = 450px),
    /// chiều cao co dãn tự nhiên theo tỉ lệ gốc để không bị méo.
    /// </summary>
    public static void ResizeCoverImage(string filePath, int targetWidth = 450)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            using (var original = Image.FromFile(filePath))
            {
                if (original.Width <= targetWidth) return;

                var targetHeight = (int)Math.Round((double)original.Height * targetWidth / original.Width);
                if (targetHeight <= 0) targetHeight = 1;

                using var resized = new Bitmap(targetWidth, targetHeight);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(original, 0, 0, targetWidth, targetHeight);
                }

                var tempPath = filePath + ".tmp.jpg";
                var encoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

                if (encoder != null)
                {
                    using var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                    resized.Save(tempPath, encoder, encoderParams);
                }
                else
                {
                    resized.Save(tempPath, ImageFormat.Jpeg);
                }

                original.Dispose();
                File.Move(tempPath, filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể resize ảnh {Path}: {Message}", filePath, ex.Message);
        }
    }

    /// <summary>
    /// Tải ảnh bìa (ưu tiên từ onlineCoverUrl đã tìm thấy trước đó),
    /// sau đó resize chuẩn hóa chiều rộng về 450px và lưu vào thư mục Covers.
    /// Trả về đường dẫn file ảnh cục bộ trên máy.
    /// </summary>
    public static async Task<string?> DownloadAndProcessCoverAsync(
        string gameName, 
        string? onlineCoverUrl = null, 
        string? steamAppId = null, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var targetFile = GetCoverFilePath(gameName);
        if (File.Exists(targetFile))
        {
            return targetFile;
        }

        // Nếu đã có sẵn link online tìm được từ bước preview, tải trực tiếp
        if (!string.IsNullOrEmpty(onlineCoverUrl) && 
            (onlineCoverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             onlineCoverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (await DownloadImageAsync(onlineCoverUrl, targetFile, cancellationToken))
                {
                    ResizeCoverImage(targetFile, 450);
                    _dataUrlCache.TryRemove(targetFile, out _);
                    return targetFile;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Tải từ onlineCoverUrl thất bại cho {Game}: {Message}", gameName, ex.Message);
            }
        }

        // Fallback: Tra cứu và tải theo quy trình đầy đủ
        return await EnsureCoverForGameAsync(gameName, steamAppId, cancellationToken);
    }

    /// <summary>
    /// Đảm bảo game có ảnh bìa trên máy:
    /// - Nếu file đã tồn tại: trả về đường dẫn hiện có, không tải lại.
    /// - Nếu chưa có: tra cứu từ PCGamingWiki / Steam, tải về máy và resize width 450px.
    /// - Nếu không tìm thấy: trả về null.
    /// </summary>
    public static async Task<string?> EnsureCoverForGameAsync(string gameName, string? steamAppId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var targetFile = GetCoverFilePath(gameName);
        if (File.Exists(targetFile))
        {
            return targetFile;
        }

        try
        {
            string? pcgwCoverUrl = null;
            string? extractedSteamAppId = null;

            // 1. Thử lấy cover & steamAppId từ PCGamingWiki
            try
            {
                (pcgwCoverUrl, extractedSteamAppId) = await FetchPCGamingWikiCoverAndSteamIdAsync(gameName, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi tra cứu PCGamingWiki cho {Game}: {Message}", gameName, ex.Message);
            }

            var effectiveSteamAppId = steamAppId ?? extractedSteamAppId;

            // 1. Ưu tiên tải từ Steam CDN nếu có Steam AppID (ảnh library dọc chuẩn 600x900, không bị chặn)
            if (!string.IsNullOrEmpty(effectiveSteamAppId))
            {
                var steamLibraryUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/library_600x900.jpg";
                if (await DownloadImageAsync(steamLibraryUrl, targetFile, cancellationToken))
                {
                    ResizeCoverImage(targetFile, 450);
                    _dataUrlCache.TryRemove(targetFile, out _);
                    return targetFile;
                }
            }

            // 2. Thử cover từ PCGW
            if (!string.IsNullOrEmpty(pcgwCoverUrl))
            {
                var success = await DownloadImageAsync(pcgwCoverUrl, targetFile, cancellationToken);
                if (success)
                {
                    ResizeCoverImage(targetFile, 450);
                    _dataUrlCache.TryRemove(targetFile, out _);
                    return targetFile;
                }
            }

            // 3. Thử header Steam (header.jpg) nếu có steamAppId
            if (!string.IsNullOrEmpty(effectiveSteamAppId))
            {
                var steamHeaderUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/header.jpg";
                if (await DownloadImageAsync(steamHeaderUrl, targetFile, cancellationToken))
                {
                    ResizeCoverImage(targetFile, 450);
                    _dataUrlCache.TryRemove(targetFile, out _);
                    return targetFile;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể tải ảnh bìa cho game {Game}: {Message}", gameName, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Chuyển đổi đường dẫn ảnh cục bộ sang URL ảo an toàn của WebView2 (https://covers.local/[filename])
    /// Giúp WebView2 nạp trực tiếp file từ đĩa với hiệu năng cao, không dùng chuỗi Base64.
    /// </summary>
    public static string? GetCoverImageUri(string? coverPathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(coverPathOrUrl)) return null;

        if (coverPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            coverPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            coverPathOrUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return coverPathOrUrl;
        }

        var fileName = Path.GetFileName(coverPathOrUrl);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        return $"https://covers.local/{fileName}";
    }

    /// <summary>
    /// Chuyển đổi đường dẫn ảnh bìa cục bộ thành chuỗi Data URL (data:image/jpeg;base64,...)
    /// Phục vụ hiển thị tức thì trên WebView2 không bị chặn phân quyền file.
    /// </summary>
    public static string? GetCoverDataUrl(string? coverPathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(coverPathOrUrl)) return null;

        // Nếu đã là web URL
        if (coverPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            coverPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            coverPathOrUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return coverPathOrUrl;
        }

        // Nếu là đường dẫn cục bộ (có thể là tên file hoặc đường dẫn đầy đủ)
        string fullPath = coverPathOrUrl;
        if (!Path.IsPathRooted(fullPath))
        {
            fullPath = Path.Combine(GetCoverDirectory(), fullPath);
        }

        if (!File.Exists(fullPath)) return null;

        return _dataUrlCache.GetOrAdd(fullPath, path =>
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0) return string.Empty;
                var base64 = Convert.ToBase64String(bytes);
                return $"data:image/jpeg;base64,{base64}";
            }
            catch
            {
                return string.Empty;
            }
        });
    }

    private static async Task<(string? CoverUrl, string? SteamAppId)> FetchPCGamingWikiCoverAndSteamIdAsync(string gameName, CancellationToken cancellationToken)
    {
        try
        {
            // Bước 1: Tìm trang wiki theo tên game qua OpenSearch
            var searchUrl = $"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(gameName)}&limit=3&format=json";
            var searchResp = await _httpClient.GetStringAsync(searchUrl, cancellationToken);
            using var searchDoc = JsonDocument.Parse(searchResp);
            var searchRoot = searchDoc.RootElement;
            if (searchRoot.ValueKind != JsonValueKind.Array || searchRoot.GetArrayLength() < 2) return (null, null);

            var titles = searchRoot[1];
            if (titles.GetArrayLength() == 0) return (null, null);
            var pageTitle = titles[0].GetString();
            if (string.IsNullOrEmpty(pageTitle)) return (null, null);

            // Bước 2: Lấy wikitext của trang để tìm trường cover và steam app id
            var parseUrl = $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&format=json";
            var parseResp = await _httpClient.GetStringAsync(parseUrl, cancellationToken);
            using var parseDoc = JsonDocument.Parse(parseResp);
            if (!parseDoc.RootElement.TryGetProperty("parse", out var parseEl)) return (null, null);
            if (!parseEl.TryGetProperty("wikitext", out var wtEl)) return (null, null);
            var wikitext = wtEl.GetProperty("*").GetString();
            if (string.IsNullOrEmpty(wikitext)) return (null, null);

            // Trích xuất Steam AppID từ wikitext nếu có
            string? steamId = null;
            var steamMatch = Regex.Match(wikitext, @"(?:\{\{Steam\||store\.steampowered\.com/app/|store\.steampowered\.com/news/app/|steam\s+app\s*id\s*=\s*)([0-9]+)", RegexOptions.IgnoreCase);
            if (steamMatch.Success)
            {
                steamId = steamMatch.Groups[1].Value;
            }

            // Tìm thuộc tính cover = <tên file>
            var match = Regex.Match(wikitext, @"cover\s*=\s*([^\r\n\|\}]+)", RegexOptions.IgnoreCase);
            if (!match.Success) return (null, steamId);

            var coverFileName = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(coverFileName)) return (null, steamId);

            // Bước 3: Gọi MediaWiki imageinfo API để lấy direct link tải ảnh thật
            var fileTitle = coverFileName.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
                ? coverFileName
                : $"File:{coverFileName}";

            var imageInfoUrl = $"https://www.pcgamingwiki.com/w/api.php?action=query&titles={Uri.EscapeDataString(fileTitle)}&prop=imageinfo&iiprop=url&format=json";
            var imageInfoResp = await _httpClient.GetStringAsync(imageInfoUrl, cancellationToken);
            using var imageDoc = JsonDocument.Parse(imageInfoResp);
            if (imageDoc.RootElement.TryGetProperty("query", out var queryEl) &&
                queryEl.TryGetProperty("pages", out var pagesEl))
            {
                foreach (var page in pagesEl.EnumerateObject())
                {
                    if (page.Value.TryGetProperty("imageinfo", out var iiArray) && iiArray.GetArrayLength() > 0)
                    {
                        var firstInfo = iiArray[0];
                        if (firstInfo.TryGetProperty("url", out var urlProp))
                        {
                            return (urlProp.GetString(), steamId);
                        }
                    }
                }
            }

            return (null, steamId);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<bool> DownloadImageAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        // 1. Thử tải trước qua HttpClient
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes != null && bytes.Length > 0)
                {
                    await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
                    return true;
                }
            }
        }
        catch { /* Tiếp tục thử curl fallback */ }

        // 2. Fallback: Dùng curl.exe (có sẵn trên Windows 10/11) để bypass Cloudflare TLS fingerprinting trên images.pcgamingwiki.com
        try
        {
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var curlPath = Path.Combine(systemDir, "curl.exe");
            if (!File.Exists(curlPath))
            {
                curlPath = "curl.exe";
            }

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = $"-s -f -L --max-time 20 -A \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36\" \"{url}\" -o \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(cancellationToken);
                if (proc.ExitCode == 0 && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Curl fallback tải ảnh thất bại cho {Url}: {Error}", url, ex.Message);
        }

        return false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        clean = clean.Replace(' ', '_').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "game_cover" : clean;
    }
}
