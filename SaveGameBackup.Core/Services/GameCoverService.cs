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

    /// <summary>
    /// Cờ bật/tắt tìm ảnh data từ Steam. Mặc định là false (tạm thời ẩn tìm ảnh data từ Steam theo yêu cầu).
    /// </summary>
    public static bool EnableSteamCovers { get; set; } = false;

    /// <summary>
    /// Cho phép can thiệp trạng thái mạng phục vụ Unit Test. Mặc định là null (sử dụng mạng thực tế).
    /// </summary>
    public static bool? ForceNetworkAvailable { get; set; } = null;

    /// <summary>
    /// Kiểm tra kết nối mạng của thiết bị. Nếu không có mạng thì bỏ qua việc tra cứu và tải ảnh online.
    /// </summary>
    public static bool IsNetworkAvailable()
    {
        if (ForceNetworkAvailable.HasValue) return ForceNetworkAvailable.Value;
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kiểm tra mảng bytes có phải là định dạng ảnh hợp lệ (JPEG, PNG, WebP, GIF, BMP).
    /// </summary>
    public static bool IsValidImageBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 10) return false;
        if (IsHtmlChallenge(bytes)) return false;

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return true;

        // WebP: RIFF .... WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true;

        // GIF: GIF87a hoặc GIF89a
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38) return true;

        // BMP: BM
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return true;

        return false;
    }

    /// <summary>
    /// Kiểm tra file ảnh có tồn tại trên đĩa và có nội dung ảnh hợp lệ (không phải 0 byte hay html rác).
    /// </summary>
    public static bool IsValidImageFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length < 10) return false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[16];
            int read = stream.Read(header, 0, 16);
            if (read < 10) return false;

            return IsValidImageBytes(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Dịch vụ trình duyệt ngầm WebView2 Headless để vượt qua Cloudflare Challenge khi tra cứu và tải ảnh.
    /// </summary>
    public static IHeadlessBrowserService? HeadlessBrowser { get; set; }

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
    /// Lấy đường dẫn thư mục tạm Temp/covers để lưu ảnh bìa vừa tìm kiếm.
    /// </summary>
    public static string GetTempCoverDirectory()
    {
        var rootDir = DatabaseService.GetDefaultProjectRoot();
        var tempDir = Path.Combine(rootDir, "Temp", "covers");
        if (!Directory.Exists(tempDir))
        {
            try
            {
                Directory.CreateDirectory(tempDir);
            }
            catch { /* Ignore */ }
        }
        return tempDir;
    }

    /// <summary>
    /// Trả về đường dẫn file ảnh cover tạm trong Temp/covers/.
    /// </summary>
    public static string GetTempCoverFilePath(string gameName)
    {
        var sanitized = SanitizeFileName(gameName);
        return Path.Combine(GetTempCoverDirectory(), $"{sanitized}.jpg");
    }

    /// <summary>
    /// Chuyển đổi đường dẫn ảnh tạm sang URL ảo an toàn của WebView2 (https://tempcovers.local/[filename]).
    /// BỎ HOÀN TOÀN Base64 Data URL, hiển thị trực tiếp từ file đĩa cục bộ.
    /// </summary>
    public static string? GetTempCoverImageUri(string? tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath)) return null;
        var fileName = Path.GetFileName(tempFilePath);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return $"https://tempcovers.local/{fileName}";
    }

    /// <summary>
    /// Xóa toàn bộ file ảnh tạm trong thư mục Temp/covers/ và loại bỏ các file cover hỏng 0-byte trong Covers/.
    /// Được gọi tại 3 thời điểm: khi mở app, khi thoát app, và sau khi backup hoàn tất.
    /// </summary>
    public static void ClearTempCovers()
    {
        try
        {
            var tempDir = GetTempCoverDirectory();
            if (Directory.Exists(tempDir))
            {
                var files = Directory.GetFiles(tempDir);
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            PurgeInvalidCovers();
        }
        catch { }
    }

    /// <summary>
    /// Quét và xóa các file ảnh bị lỗi, 0 byte hoặc không hợp lệ trong thư mục Covers/ và Temp/covers/.
    /// </summary>
    public static void PurgeInvalidCovers()
    {
        try
        {
            var coversDir = GetCoverDirectory();
            if (Directory.Exists(coversDir))
            {
                foreach (var f in Directory.GetFiles(coversDir, "*.jpg"))
                {
                    if (!IsValidImageFile(f))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }

            var tempDir = GetTempCoverDirectory();
            if (Directory.Exists(tempDir))
            {
                foreach (var f in Directory.GetFiles(tempDir, "*.jpg"))
                {
                    if (!IsValidImageFile(f))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }
        catch { }
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
    /// Kiểm tra game đã có file ảnh bìa cục bộ hợp lệ trên máy chưa.
    /// </summary>
    public static bool HasLocalCover(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return false;
        var filePath = GetCoverFilePath(gameName);
        return IsValidImageFile(filePath);
    }

    /// <summary>
    /// Xóa toàn bộ ảnh bìa cục bộ và ảnh bìa tạm của game (được gọi khi xóa toàn bộ game hoặc xóa snapshot cuối cùng).
    /// </summary>
    public static void DeleteCoverForGame(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return;

        try
        {
            var coverPath = GetCoverFilePath(gameName);
            if (File.Exists(coverPath))
            {
                File.Delete(coverPath);
            }
        }
        catch { }

        try
        {
            var tempCoverPath = GetTempCoverFilePath(gameName);
            if (File.Exists(tempCoverPath))
            {
                File.Delete(tempCoverPath);
            }
        }
        catch { }

        _dataUrlCache.TryRemove(gameName, out _);
    }

    /// <summary>
    /// Tra cứu URL ảnh bìa online (ƯU TIÊN PCGAMINGWIKI TRƯỚC -> NẾU KHÔNG CÓ MỚI LẤY TỪ STEAM).
    /// Nếu máy đã có sẵn file cục bộ trong Covers/ thì trả về đường dẫn ảo https://covers.local/... đó.
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
            // 1. Ưu tiên PCGamingWiki trước:
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

            if (!string.IsNullOrEmpty(pcgwCoverUrl))
            {
                return pcgwCoverUrl;
            }

            // 2. Tạm thời ẩn tìm ảnh data từ Steam:
            if (EnableSteamCovers)
            {
                var effectiveSteamAppId = steamAppId ?? extractedSteamAppId;
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = new LudusaviManifestService().FindGame(gameName)?.SteamId;
                }
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = await FetchSteamAppIdFromStoreSearchAsync(gameName, cancellationToken);
                }

                if (!string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    return $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/library_600x900.jpg";
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể tìm link ảnh online cho {Game}: {Message}", gameName, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Tải ảnh bìa về thư mục Temp/covers/ và trả về URI ảo cục bộ (https://tempcovers.local/...) để hiển thị trên giao diện.
    /// Hoàn toàn không dùng Base64.
    /// </summary>
    public static async Task<string?> DownloadToTempCoverAsync(
        string gameName,
        string? onlineCoverUrl = null,
        string? steamAppId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var localFile = GetCoverFilePath(gameName);
        if (File.Exists(localFile) && IsValidImageFile(localFile))
        {
            return GetCoverImageUri(localFile);
        }

        var targetTempFile = GetTempCoverFilePath(gameName);
        if (File.Exists(targetTempFile) && IsValidImageFile(targetTempFile))
        {
            return GetTempCoverImageUri(targetTempFile);
        }

        // Nếu không có mạng thì không tải hình cover từ online
        if (!IsNetworkAvailable())
        {
            return null;
        }

        var effectiveUrl = onlineCoverUrl;
        // Bỏ qua các URI ảo cục bộ (.local) nếu file trên đĩa không tồn tại hoặc ảnh Steam nếu đang tạm ẩn
        if (!string.IsNullOrEmpty(effectiveUrl) && 
            (effectiveUrl.Contains(".local", StringComparison.OrdinalIgnoreCase) || 
             (!EnableSteamCovers && effectiveUrl.Contains("steamstatic.com", StringComparison.OrdinalIgnoreCase)) ||
             !effectiveUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            effectiveUrl = null;
        }

        // Ưu tiên PCGamingWiki trước -> Steam sau:
        // Nếu chưa có URL, tìm kiếm URL theo thứ tự PCGamingWiki trước, Steam sau
        if (string.IsNullOrEmpty(effectiveUrl))
        {
            effectiveUrl = await FindOnlineCoverUrlAsync(gameName, steamAppId, cancellationToken);
        }

        // Thử tải từ effectiveUrl
        if (!string.IsNullOrEmpty(effectiveUrl) && !effectiveUrl.Contains(".local", StringComparison.OrdinalIgnoreCase))
        {
            var downloaded = await DownloadImageAsync(effectiveUrl, targetTempFile, cancellationToken);
            if (downloaded && File.Exists(targetTempFile) && IsValidImageFile(targetTempFile))
            {
                ResizeCoverImage(targetTempFile, 450);
                return GetTempCoverImageUri(targetTempFile);
            }
        }

        // Nếu tải từ effectiveUrl thất bại, fallback sang Steam (tạm thời ẩn nếu EnableSteamCovers = false)
        if (EnableSteamCovers)
        {
            try
            {
                var effectiveSteamAppId = steamAppId;
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = new LudusaviManifestService().FindGame(gameName)?.SteamId;
                }
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = await FetchSteamAppIdFromStoreSearchAsync(gameName, cancellationToken);
                }

                if (!string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    var steamLibraryUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/library_600x900.jpg";
                    if (!string.Equals(effectiveUrl, steamLibraryUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        var downloaded = await DownloadImageAsync(steamLibraryUrl, targetTempFile, cancellationToken);
                        if (downloaded && File.Exists(targetTempFile) && new FileInfo(targetTempFile).Length > 0)
                        {
                            ResizeCoverImage(targetTempFile, 450);
                            return GetTempCoverImageUri(targetTempFile);
                        }
                    }

                    var steamHeaderUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/header.jpg";
                    if (!string.Equals(effectiveUrl, steamHeaderUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        var downloaded = await DownloadImageAsync(steamHeaderUrl, targetTempFile, cancellationToken);
                        if (downloaded && File.Exists(targetTempFile) && new FileInfo(targetTempFile).Length > 0)
                        {
                            ResizeCoverImage(targetTempFile, 450);
                            return GetTempCoverImageUri(targetTempFile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi fallback Steam cho {Game}: {Message}", gameName, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Tự động resize ảnh về chiều rộng chuẩn (mặc định width = 450px),
    /// chiều cao co dãn tự nhiên theo tỉ lệ gốc để không bị méo.
    /// </summary>
    public static void ResizeCoverImage(string filePath, int targetWidth = 450)
    {
        if (!File.Exists(filePath) || !IsValidImageFile(filePath)) return;

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
        if (File.Exists(targetFile) && IsValidImageFile(targetFile))
        {
            return targetFile;
        }
        else if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }

        var coverDir = GetCoverDirectory();
        if (!Directory.Exists(coverDir)) Directory.CreateDirectory(coverDir);

        // 1. Ưu tiên copy từ thư mục Temp/covers/ nếu ảnh đã được tải sẵn ở bước search/preview
        string? tempSourceFile = null;
        var defaultTempFile = GetTempCoverFilePath(gameName);
        if (File.Exists(defaultTempFile) && IsValidImageFile(defaultTempFile))
        {
            tempSourceFile = defaultTempFile;
        }
        else if (!string.IsNullOrEmpty(onlineCoverUrl) && onlineCoverUrl.Contains("tempcovers.local", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(onlineCoverUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            var candidate = Path.Combine(GetTempCoverDirectory(), fileName);
            if (File.Exists(candidate) && IsValidImageFile(candidate))
            {
                tempSourceFile = candidate;
            }
        }

        if (!string.IsNullOrEmpty(tempSourceFile) && File.Exists(tempSourceFile) && IsValidImageFile(tempSourceFile))
        {
            try
            {
                File.Copy(tempSourceFile, targetFile, true);
                _dataUrlCache.TryRemove(targetFile, out _);
                return targetFile;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi sao chép ảnh từ Temp/covers/ cho {Game}: {Message}", gameName, ex.Message);
            }
        }

        // Nếu không có mạng thì không thử tải online
        if (!IsNetworkAvailable())
        {
            return null;
        }

        // 2. Nếu là URL mạng ngoài (http/https thực tế không phải tempcovers.local), tải trực tiếp
        if (!string.IsNullOrEmpty(onlineCoverUrl) && 
            !onlineCoverUrl.Contains("tempcovers.local", StringComparison.OrdinalIgnoreCase) &&
            !onlineCoverUrl.Contains("covers.local", StringComparison.OrdinalIgnoreCase) &&
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

        // 3. Fallback: Tra cứu và tải theo quy trình đầy đủ
        return await EnsureCoverForGameAsync(gameName, steamAppId, cancellationToken);
    }

    /// <summary>
    /// Đảm bảo game có ảnh bìa trên máy:
    /// - Nếu file đã tồn tại: trả về đường dẫn hiện có, không tải lại.
    /// - Nếu chưa có: tra cứu từ PCGamingWiki trước -> Steam sau, tải về máy và resize width 450px.
    /// - Nếu không tìm thấy hoặc offline: trả về null.
    /// </summary>
    public static async Task<string?> EnsureCoverForGameAsync(string gameName, string? steamAppId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var targetFile = GetCoverFilePath(gameName);
        if (File.Exists(targetFile) && IsValidImageFile(targetFile))
        {
            return targetFile;
        }
        else if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }

        if (!IsNetworkAvailable())
        {
            return null;
        }

        try
        {
            // 1. Ưu tiên PCGamingWiki trước
            string? pcgwCoverUrl = null;
            string? extractedSteamAppId = null;

            try
            {
                (pcgwCoverUrl, extractedSteamAppId) = await FetchPCGamingWikiCoverAndSteamIdAsync(gameName, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi tra cứu PCGamingWiki cho {Game}: {Message}", gameName, ex.Message);
            }

            // Nếu có cover từ PCGW, thử tải về trước
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

            // 2. Không có hoặc tải PCGamingWiki thất bại -> Lấy từ Steam (tạm thời ẩn nếu EnableSteamCovers = false)
            if (EnableSteamCovers)
            {
                var effectiveSteamAppId = steamAppId ?? extractedSteamAppId;
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = new LudusaviManifestService().FindGame(gameName)?.SteamId;
                }
                if (string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    effectiveSteamAppId = await FetchSteamAppIdFromStoreSearchAsync(gameName, cancellationToken);
                }

                if (!string.IsNullOrEmpty(effectiveSteamAppId))
                {
                    var steamLibraryUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/library_600x900.jpg";
                    if (await DownloadImageAsync(steamLibraryUrl, targetFile, cancellationToken))
                    {
                        ResizeCoverImage(targetFile, 450);
                        _dataUrlCache.TryRemove(targetFile, out _);
                        return targetFile;
                    }

                    // Thử header Steam (header.jpg) nếu library_600x900 thất bại
                    var steamHeaderUrl = $"https://shared.steamstatic.com/store_item_assets/steam/apps/{effectiveSteamAppId}/header.jpg";
                    if (await DownloadImageAsync(steamHeaderUrl, targetFile, cancellationToken))
                    {
                        ResizeCoverImage(targetFile, 450);
                        _dataUrlCache.TryRemove(targetFile, out _);
                        return targetFile;
                    }
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

    private static async Task<string?> GetWebStringWithFallbackAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var resp = await _httpClient.GetStringAsync(url, cancellationToken);
            if (!string.IsNullOrEmpty(resp) &&
                !resp.Contains("Just a moment...", StringComparison.OrdinalIgnoreCase) &&
                !resp.Contains("cf-chl", StringComparison.OrdinalIgnoreCase))
            {
                return resp;
            }
        }
        catch
        {
            // Bị 403 Forbidden hoặc lỗi mạng
        }

        if (HeadlessBrowser != null && HeadlessBrowser.IsAvailable)
        {
            LoggingService.LogAction("GameCover_Headless_Bypass_Triggered", new { Url = url });
            return await HeadlessBrowser.FetchPageContentAsync(url, 12, cancellationToken);
        }

        return null;
    }

    private static bool IsHtmlChallenge(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 15) return false;
        var len = Math.Min(bytes.Length, 150);
        var preview = System.Text.Encoding.UTF8.GetString(bytes, 0, len);
        return preview.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               preview.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               preview.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string? CoverUrl, string? SteamAppId)> FetchPCGamingWikiCoverAndSteamIdAsync(string gameName, CancellationToken cancellationToken)
    {
        try
        {
            // Bước 1: Tìm trang wiki theo tên game qua OpenSearch
            var searchUrl = $"https://www.pcgamingwiki.com/w/api.php?action=opensearch&search={Uri.EscapeDataString(gameName)}&limit=3&format=json";
            var searchResp = await GetWebStringWithFallbackAsync(searchUrl, cancellationToken);
            if (string.IsNullOrEmpty(searchResp) || searchResp.StartsWith("<")) return (null, null);

            using var searchDoc = JsonDocument.Parse(searchResp);
            var searchRoot = searchDoc.RootElement;
            if (searchRoot.ValueKind != JsonValueKind.Array || searchRoot.GetArrayLength() < 2) return (null, null);

            var titles = searchRoot[1];
            if (titles.GetArrayLength() == 0) return (null, null);
            var pageTitle = titles[0].GetString();
            if (string.IsNullOrEmpty(pageTitle)) return (null, null);

            // Bước 2: Lấy wikitext của trang để tìm trường cover và steam app id (thêm &redirects=1 để xử lý trang chuyển hướng)
            var parseUrl = $"https://www.pcgamingwiki.com/w/api.php?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=wikitext&format=json&redirects=1";
            var parseResp = await GetWebStringWithFallbackAsync(parseUrl, cancellationToken);
            if (string.IsNullOrEmpty(parseResp) || parseResp.StartsWith("<")) return (null, null);

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

            var coverFileName = match.Groups[1].Value.Trim().Trim('[', ']');
            if (coverFileName.Contains('|')) coverFileName = coverFileName.Split('|')[0].Trim();
            if (string.IsNullOrEmpty(coverFileName)) return (null, steamId);

            // Bước 3: Gọi MediaWiki imageinfo API để lấy direct link tải ảnh thật
            var fileTitle = coverFileName.StartsWith("File:", StringComparison.OrdinalIgnoreCase)
                ? coverFileName
                : $"File:{coverFileName}";

            var imageInfoUrl = $"https://www.pcgamingwiki.com/w/api.php?action=query&titles={Uri.EscapeDataString(fileTitle)}&prop=imageinfo&iiprop=url&format=json&redirects=1";
            var imageInfoResp = await GetWebStringWithFallbackAsync(imageInfoUrl, cancellationToken);
            if (string.IsNullOrEmpty(imageInfoResp) || imageInfoResp.StartsWith("<")) return (null, steamId);

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
        if (!IsNetworkAvailable()) return false;

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            // 1. Thử tải trước qua HttpClient
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes != null && bytes.Length > 0 && IsValidImageBytes(bytes))
                    {
                        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
                        return true;
                    }
                }
            }
            catch { /* Tiếp tục thử fallback */ }

            // 2. Thử tải qua WebView2 Headless Browser (vượt Cloudflare TLS fingerprinting trên images.pcgamingwiki.com)
            if (HeadlessBrowser != null && HeadlessBrowser.IsAvailable)
            {
                try
                {
                    LoggingService.LogAction("GameCover_Headless_ImageDownload_Triggered", new { Url = url });
                    var bytes = await HeadlessBrowser.FetchImageBytesAsync(url, 12, cancellationToken);
                    if (bytes != null && bytes.Length > 0 && IsValidImageBytes(bytes))
                    {
                        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("WebView2 tải ảnh bìa thất bại cho {Url}: {Error}", url, ex.Message);
                }
            }

            // 3. Fallback: Dùng curl.exe (có sẵn trên Windows 10/11)
            try
            {
                var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var curlPath = Path.Combine(systemDir, "curl.exe");
                if (!File.Exists(curlPath))
                {
                    curlPath = "curl.exe";
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
                        var fileBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
                        if (IsValidImageBytes(fileBytes))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Curl fallback tải ảnh thất bại cho {Url}: {Error}", url, ex.Message);
            }
        }
        catch { }
        finally
        {
            // Bảo đảm dọn dẹp sạch sẽ, không để lại file rác / 0 byte / file hỏng trên đĩa nếu tải thất bại
            if (File.Exists(targetPath) && !IsValidImageFile(targetPath))
            {
                try { File.Delete(targetPath); } catch { }
            }
        }

        return false;
    }

    private static async Task<string?> FetchSteamAppIdFromStoreSearchAsync(string gameName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        // 1. Thử qua Steam Community SearchApps (Không bị nhà mạng chặn TCP Reset, trả về JSON trực tiếp)
        try
        {
            var communityUrl = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(gameName.Trim())}";
            var response = await _httpClient.GetStringAsync(communityUrl, cancellationToken);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (first.TryGetProperty("appid", out var appIdProp))
                {
                    var idStr = appIdProp.GetString();
                    if (!string.IsNullOrEmpty(idStr))
                    {
                        return idStr;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi tra cứu Steam Community Search cho {Game}: {Message}", gameName, ex.Message);
        }

        // 2. Fallback: Thử Steam Store Search API
        try
        {
            var searchUrl = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(gameName)}&l=english&cc=US";
            var response = await _httpClient.GetStringAsync(searchUrl, cancellationToken);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var firstItem = items[0];
                if (firstItem.TryGetProperty("id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.Number)
                    {
                        return idProp.GetInt64().ToString();
                    }
                    if (idProp.ValueKind == JsonValueKind.String)
                    {
                        return idProp.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi tra cứu Steam Store Search cho {Game}: {Message}", gameName, ex.Message);
        }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        clean = clean.Replace(' ', '_').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(clean) ? "game_cover" : clean;
    }
}
