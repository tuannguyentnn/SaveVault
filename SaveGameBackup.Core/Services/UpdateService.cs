using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Constants;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Quản lý việc tự động kiểm tra, tải về và cập nhật phiên bản mới
/// từ repository chứa các file đã build publish.
/// </summary>
public class UpdateService
{
    public const string DefaultManifestUrl = "https://raw.githubusercontent.com/tuannguyen01101995/SaveVault-Publish/main/version.json";

    private readonly HttpClient _httpClient;
    private string _manifestUrl;
    private string? _customAppDirectory;

    public UpdateService(HttpClient? httpClient = null, string? manifestUrl = null, string? customAppDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _manifestUrl = !string.IsNullOrWhiteSpace(manifestUrl) ? manifestUrl : DefaultManifestUrl;
        _customAppDirectory = customAppDirectory;
    }

    public string ManifestUrl
    {
        get => _manifestUrl;
        set => _manifestUrl = value;
    }

    /// <summary>
    /// Thư mục gốc của ứng dụng (App Root Directory).
    /// </summary>
    public string AppDirectory => !string.IsNullOrWhiteSpace(_customAppDirectory)
        ? _customAppDirectory
        : DatabaseService.GetDefaultProjectRoot();

    /// <summary>
    /// Thư mục temp nằm bên trong data/temp theo yêu cầu.
    /// </summary>
    public string TempDirectory
    {
        get
        {
            var tempDir = Path.Combine(AppDirectory, "data", "temp");
            if (!Directory.Exists(tempDir))
            {
                try { Directory.CreateDirectory(tempDir); } catch { }
            }
            return tempDir;
        }
    }

    /// <summary>
    /// Thư mục con chuyên dụng chứa toàn bộ file phục vụ cập nhật trong data/temp/auto_update.
    /// </summary>
    public string AutoUpdateDirectory
    {
        get
        {
            var updateDir = Path.Combine(TempDirectory, "auto_update");
            if (!Directory.Exists(updateDir))
            {
                try { Directory.CreateDirectory(updateDir); } catch { }
            }
            return updateDir;
        }
    }

    /// <summary>
    /// Thuộc tính tiện ích tĩnh lấy phiên bản hiện tại của ứng dụng đang chạy.
    /// Dùng để hiển thị đồng bộ trên giao diện UI (Footer) hoặc các service khác.
    /// </summary>
    public static string CurrentAppVersion
    {
        get
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    var clean = infoVer.Split('+')[0].Trim();
                    if (!string.IsNullOrEmpty(clean)) return clean;
                }

                var ver = assembly.GetName().Version;
                if (ver != null)
                {
                    return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                }
            }
            catch { }

            return "1.0.0";
        }
    }

    /// <summary>
    /// Lấy phiên bản hiện tại của ứng dụng.
    /// </summary>
    public virtual string GetCurrentVersion()
    {
        return CurrentAppVersion;
    }

    /// <summary>
    /// So sánh Semantic Versioning (SemVer) xem latestVersion có lớn hơn currentVersion không.
    /// Hỗ trợ cả định dạng "v1.1.0", "1.1", "1.1.0.0".
    /// </summary>
    public static bool IsNewerVersion(string currentVersionStr, string latestVersionStr)
    {
        if (string.IsNullOrWhiteSpace(latestVersionStr)) return false;
        if (string.IsNullOrWhiteSpace(currentVersionStr)) return true;

        var cleanCurrent = currentVersionStr.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        var cleanLatest = latestVersionStr.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];

        var curParts = cleanCurrent.Split('.');
        var latParts = cleanLatest.Split('.');

        int maxLen = Math.Max(curParts.Length, latParts.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int curNum = 0;
            if (i < curParts.Length) int.TryParse(curParts[i], out curNum);

            int latNum = 0;
            if (i < latParts.Length) int.TryParse(latParts[i], out latNum);

            if (latNum > curNum) return true;
            if (latNum < curNum) return false;
        }

        return false;
    }

    /// <summary>
    /// Đọc lịch sử các bản cập nhật từ changelogs.json hoặc version.json.
    /// </summary>
    public virtual async Task<List<ChangelogItem>> GetChangelogHistoryAsync()
    {
        var result = new List<ChangelogItem>();
        try
        {
            var searchPaths = new[]
            {
                Path.Combine(AppDirectory, "changelogs.json"),
                Path.Combine(AppContext.BaseDirectory, "changelogs.json"),
                Path.Combine(AppDirectory, "..", "changelogs.json")
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    var json = await File.ReadAllTextAsync(path);
                    var list = JsonSerializer.Deserialize<List<ChangelogItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (list != null && list.Count > 0)
                    {
                        return list;
                    }
                }
            }

            // Fallback: nếu không thấy changelogs.json thì đọc version.json
            var verPaths = new[]
            {
                Path.Combine(AppDirectory, "version.json"),
                Path.Combine(AppContext.BaseDirectory, "version.json")
            };
            foreach (var path in verPaths)
            {
                if (File.Exists(path))
                {
                    var json = await File.ReadAllTextAsync(path);
                    var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (info != null && !string.IsNullOrEmpty(info.Version))
                    {
                        result.Add(new ChangelogItem
                        {
                            Version = info.Version,
                            ReleaseDate = info.ReleaseDate,
                            Changelog = info.Changelog
                        });
                        return result;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi đọc lịch sử changelog: {Message}", ex.Message);
        }

        // Fallback mặc định từ AppBuildInfo
        if (result.Count == 0)
        {
            result.Add(new ChangelogItem
            {
                Version = AppBuildInfo.Version,
                ReleaseDate = AppBuildInfo.BuildDate,
                Changelog = new List<string>
                {
                    "🚀 Phát hành phiên bản Omnisave với hỗ trợ sao lưu và đồng bộ Cloud.",
                    "✨ Tự động nhận diện save game và bảo vệ dữ liệu bằng Safety Snapshots."
                }
            });
        }

        return result;
    }

    /// <summary>
    /// Kiểm tra phiên bản mới từ repository chứa bản build publish.
    /// </summary>
    /// <param name="isSilent">Nếu là true (gọi tự động lúc mở app), sẽ bỏ qua nếu version trùng với SkippedUpdateVersion.</param>
    public async Task<(bool isUpdateAvailable, UpdateInfo? updateInfo, string message)> CheckForUpdateAsync(
        bool isSilent = false,
        CancellationToken cancellationToken = default)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_CheckForUpdate", new { isSilent });
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
            req.Headers.Add("User-Agent", "Omnisave-Updater");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, null, $"Máy chủ cập nhật trả về mã lỗi: {resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, options);

            if (updateInfo == null || string.IsNullOrWhiteSpace(updateInfo.Version))
            {
                return (false, null, "Dữ liệu phiên bản không hợp lệ.");
            }

            var currentVer = GetCurrentVersion();
            bool isNewer = IsNewerVersion(currentVer, updateInfo.Version);

            if (!isNewer)
            {
                return (false, updateInfo, "Bạn đang sử dụng phiên bản mới nhất!");
            }

            // Kiểm tra tính năng Bỏ qua phiên bản (Skip Version)
            if (isSilent)
            {
                var config = AppConfigService.GetConfig();
                if (!string.IsNullOrWhiteSpace(config.SkippedUpdateVersion) &&
                    string.Equals(config.SkippedUpdateVersion.Trim().TrimStart('v', 'V'),
                                  updateInfo.Version.Trim().TrimStart('v', 'V'),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    // Người dùng đã chọn bỏ qua phiên bản này ở lần thông báo trước
                    return (false, updateInfo, "Phiên bản này đã được người dùng chọn bỏ qua.");
                }
            }

            return (true, updateInfo, $"Phát hiện phiên bản mới: v{updateInfo.Version}");
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Đã hủy kiểm tra cập nhật.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi kiểm tra cập nhật: {Message}", ex.Message);
            return (false, null, $"Lỗi kết nối kiểm tra cập nhật: {ex.Message}");
        }
    }

    /// <summary>
    /// Lưu trạng thái bỏ qua phiên bản vào file cấu hình.
    /// </summary>
    public void SkipVersion(string versionToSkip)
    {
        try
        {
            var config = AppConfigService.GetConfig();
            config.SkippedUpdateVersion = versionToSkip.Trim().TrimStart('v', 'V');
            AppConfigService.SaveConfig(config);
            LoggingService.LogAction("Update_Skip_Version", new { Version = config.SkippedUpdateVersion });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi lưu trạng thái bỏ qua phiên bản: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Xóa phiên bản bỏ qua trong cấu hình (Reset).
    /// </summary>
    public void ResetSkippedVersion()
    {
        try
        {
            var config = AppConfigService.GetConfig();
            config.SkippedUpdateVersion = null;
            AppConfigService.SaveConfig(config);
            LoggingService.LogAction("Update_Reset_Skipped_Version");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi đặt lại phiên bản bỏ qua: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Tải gói cập nhật update_package.zip về thư mục temp trong ứng dụng với cập nhật tiến trình thời gian thực.
    /// </summary>
    public async Task<string> DownloadUpdatePackageAsync(
        string downloadUrl,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        long expectedTotalBytes = -1)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_DownloadUpdatePackage", new { downloadUrl, expectedTotalBytes });
        var tempDir = AutoUpdateDirectory;
        var zipFilePath = Path.Combine(tempDir, "update_package.zip");

        // 1. Kiểm tra dung lượng trống của ổ đĩa (tối thiểu 200 MB cho cả zip và giải nén)
        try
        {
            var tempRoot = Path.GetPathRoot(Path.GetFullPath(tempDir));
            if (!string.IsNullOrEmpty(tempRoot))
            {
                var drive = new DriveInfo(tempRoot);
                if (drive.IsReady && drive.AvailableFreeSpace < 200L * 1024 * 1024)
                {
                    long freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                    throw new InvalidOperationException($"Ổ đĩa ({tempRoot}) chỉ còn {freeMb} MB trống. Cần tối thiểu 200 MB dung lượng khả dụng để tải và cài đặt bản cập nhật an toàn.");
                }
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể kiểm tra dung lượng ổ đĩa: {Message}", ex.Message);
        }

        if (File.Exists(zipFilePath))
        {
            try { File.Delete(zipFilePath); } catch { }
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Không thể kết nối đến máy chủ tải gói cập nhật ({ex.StatusCode?.ToString() ?? "Lỗi mạng"}). Vui lòng kiểm tra lại kết nối internet.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Thời gian tải gói cập nhật bị quá hạn (Timeout). Vui lòng thử lại sau.", ex);
        }

        using (response)
        {
            var headerContentLength = response.Content.Headers.ContentLength ?? -1L;
            long totalBytes = headerContentLength > 0 ? headerContentLength : expectedTotalBytes;
            bool isEstimated = false;

            // Nếu cả Content-Length và expectedTotalBytes đều không có (như khi tải repo zip từ GitHub qua chunked transfer encoding),
            // ước tính kích thước trung bình của gói Omnisave publish (~45 MB) để tính toán tiến trình mượt mà.
            const long defaultEstimatedBytes = 45L * 1024 * 1024;
            if (totalBytes <= 0)
            {
                totalBytes = defaultEstimatedBytes;
                isEstimated = true;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalDownloaded = 0;
            int bytesRead;

            var stopwatch = Stopwatch.StartNew();
            long lastReportedTimeMs = 0;
            int lastReportedPercent = -1;
            long speedSampleTimeMs = 0;
            long speedSampleBytes = 0;
            double smoothedSpeed = 0;

            // Báo cáo khởi đầu 0%
            progress?.Report(new UpdateDownloadProgress(0, 0, totalBytes, "Bắt đầu tải xuống...", isEstimated, 0));

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalDownloaded += bytesRead;

                long elapsedMs = stopwatch.ElapsedMilliseconds;

                // Cập nhật tốc độ tải mượt mà theo chu kỳ tối thiểu 200ms để tránh dao động đột biến do micro-delta
                long speedDeltaMs = elapsedMs - speedSampleTimeMs;
                if (speedDeltaMs >= 200)
                {
                    double instantSpeed = (double)(totalDownloaded - speedSampleBytes) / (speedDeltaMs / 1000.0);
                    smoothedSpeed = smoothedSpeed <= 0 ? instantSpeed : (smoothedSpeed * 0.6 + instantSpeed * 0.4);
                    speedSampleTimeMs = elapsedMs;
                    speedSampleBytes = totalDownloaded;
                }

                // Tính toán phần trăm mượt mà thời gian thực
                int percent;
                if (isEstimated)
                {
                    if (totalDownloaded < totalBytes)
                    {
                        // Tăng đều từ 1% đến 95% theo dung lượng tải về thực tế
                        percent = (int)Math.Clamp((double)totalDownloaded / totalBytes * 95, 1, 95);
                    }
                    else
                    {
                        // Khi vượt mốc 45MB ước tính, tiệm cận dần 99% cho đến khi kết thúc stream
                        double extraMb = (totalDownloaded - totalBytes) / (1024.0 * 1024.0);
                        percent = (int)Math.Clamp(95 + 4.0 * (1.0 - Math.Exp(-extraMb / 15.0)), 95, 99);
                    }
                }
                else
                {
                    percent = (int)Math.Clamp((double)totalDownloaded / totalBytes * 100, 0, 100);
                }

                // Điều tiết báo cáo: cập nhật mỗi 100ms hoặc khi percent thay đổi để UI 60fps mượt mà
                if (elapsedMs - lastReportedTimeMs >= 100 || percent != lastReportedPercent)
                {
                    progress?.Report(new UpdateDownloadProgress(
                        percent,
                        totalDownloaded,
                        totalBytes,
                        $"Đang tải xuống... {percent}%",
                        isEstimated,
                        smoothedSpeed));

                    lastReportedTimeMs = elapsedMs;
                    lastReportedPercent = percent;
                }
            }

            stopwatch.Stop();
            // Báo cáo hoàn tất 100%
            progress?.Report(new UpdateDownloadProgress(100, totalDownloaded, totalDownloaded, "Tải xuống hoàn tất!", false, 0));
        }

        return zipFilePath;
    }

    /// <summary>
    /// Giải nén file zip chứa các file build publish vào thư mục temp/extracted/.
    /// Trả về đường dẫn thư mục chứa trực tiếp các file build publish.
    /// </summary>
    public string ExtractUpdatePackage(string zipFilePath)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_ExtractPackage", new { zipFilePath });

        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException("Không tìm thấy tệp gói cập nhật update_package.zip để giải nén.", zipFilePath);
        }

        var tempDir = AutoUpdateDirectory;
        var extractDir = Path.Combine(tempDir, "extracted");

        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, true); } catch { }
        }
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(zipFilePath, extractDir, true);
        }
        catch (InvalidDataException ex)
        {
            // Tệp zip bị hỏng do rớt mạng giữa chừng hoặc file không toàn vẹn
            try { File.Delete(zipFilePath); } catch { }
            try { Directory.Delete(extractDir, true); } catch { }
            throw new InvalidOperationException("Gói tệp cập nhật update_package.zip bị hỏng hoặc lỗi định dạng trong quá trình truyền tải. Vui lòng bấm Thử Tải Lại.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Giải nén gói cập nhật thất bại: {ex.Message}", ex);
        }

        // Tự động tìm thư mục chứa các tệp phát hành (chứa Omnisave.exe hoặc thư mục app/)
        string? targetDir = null;
        if (File.Exists(Path.Combine(extractDir, "Omnisave.exe")) || Directory.Exists(Path.Combine(extractDir, "app")))
        {
            targetDir = extractDir;
        }
        else
        {
            var subDirs = Directory.GetDirectories(extractDir);
            foreach (var sub in subDirs)
            {
                if (File.Exists(Path.Combine(sub, "Omnisave.exe")) || Directory.Exists(Path.Combine(sub, "app")))
                {
                    targetDir = sub;
                    break;
                }
            }
        }

        if (targetDir == null)
        {
            throw new InvalidOperationException("Gói cập nhật không chứa tệp thực thi hợp lệ (Omnisave.exe hoặc thư mục app/).");
        }

        return targetDir;
    }

    /// <summary>
    /// Tạo file update_runner.bat và update_runner.ps1 trong thư mục temp để chép đè các file build và khởi động lại app.
    /// </summary>
    public string GenerateRunnerScript(string extractedSourceDir, int targetProcessId, string targetVersion = "")
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_GenerateRunnerScript", new { extractedSourceDir, targetVersion });
        var tempDir = AutoUpdateDirectory;
        var appDir = AppDirectory;
        var batPath = Path.Combine(tempDir, "update_runner.bat");
        var ps1Path = Path.Combine(tempDir, "update_runner.ps1");
        var logPath = Path.Combine(tempDir, "update.log");
        var errorFlagPath = Path.Combine(tempDir, "update_error.flag");
        var rollbackFlagPath = Path.Combine(tempDir, "update_rollback.flag");
        var successFlagPath = Path.Combine(tempDir, "update_success.flag");
        var backupDir = Path.Combine(tempDir, "backup_prev");

        // 1. Tạo script PowerShell với giao diện đồ họa console cao cấp, màu sắc sống động (đồng bộ build_exe.ps1)
        var ps1Content = $@"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = ""Omnisave Auto-Updater - Cap Nhat Phien Ban Moi""

$targetProcessId = {targetProcessId}
$targetVersion = ""{targetVersion}""
$appDir = ""{appDir.Replace("\\", "\\\\")}""
$extractedSourceDir = ""{extractedSourceDir.Replace("\\", "\\\\")}""
$tempDir = ""{tempDir.Replace("\\", "\\\\")}""
$backupDir = ""{backupDir.Replace("\\", "\\\\")}""
$logPath = ""{logPath.Replace("\\", "\\\\")}""
$errorFlagPath = ""{errorFlagPath.Replace("\\", "\\\\")}""
$rollbackFlagPath = ""{rollbackFlagPath.Replace("\\", "\\\\")}""
$successFlagPath = ""{successFlagPath.Replace("\\", "\\\\")}""

# Xoa cac co cu neu co
Remove-Item -Path $errorFlagPath -Force -ErrorAction SilentlyContinue
Remove-Item -Path $rollbackFlagPath -Force -ErrorAction SilentlyContinue
Remove-Item -Path $successFlagPath -Force -ErrorAction SilentlyContinue
if (Test-Path $backupDir) {{
    Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
}}

Clear-Host
Write-Host ""==================================================================="" -ForegroundColor Cyan
Write-Host ""         Omnisave - TIEN TRINH CAP NHAT TU DONG (Auto-Updater)    "" -ForegroundColor Cyan
Write-Host ""==================================================================="" -ForegroundColor Cyan
Write-Host """"
Write-Host ""[THONG TIN BAN CAP NHAT]"" -ForegroundColor Green
Write-Host "" • Phien ban muc tieu : v$targetVersion"" -ForegroundColor Yellow
Write-Host "" • Thu muc cai dat    : $appDir"" -ForegroundColor Gray
Write-Host """"
Write-Host ""==================================================================="" -ForegroundColor Cyan

# -------------------------------------------------------------------
# [1/5] Cho Omnisave dong hoan toan
# -------------------------------------------------------------------
Write-Host """"
Write-Host ""[1/5] Dang doi ung dung Omnisave (PID: $targetProcessId) dong an toan..."" -ForegroundColor Yellow

while ($true) {{
    $proc = Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue
    if (-not $proc) {{ break }}
    Start-Sleep -Seconds 1
}}

Write-Host ""      [OK] Ung dung da thoat hoan toan. Da giai phong file locks!"" -ForegroundColor Green
Write-Host ""      (Dang chuyen sang buoc tiep theo trong 3 giay...)"" -ForegroundColor Gray
Start-Sleep -Seconds 3

# -------------------------------------------------------------------
# [2/5] Tao ban sao luu Snapshot du phong
# -------------------------------------------------------------------
Write-Host """"
Write-Host ""-------------------------------------------------------------------"" -ForegroundColor DarkGray
Write-Host ""[2/5] Dang tao ban sao luu snapshot du phong phong ngua su co..."" -ForegroundColor Yellow

$backupFailed = $false
try {{
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

    # 1. Sao luu thu muc app
    $appSource = Join-Path $appDir ""app""
    $appBackup = Join-Path $backupDir ""app""
    if (Test-Path $appSource) {{
        Write-Host ""      • Sao luu thu muc app/... "" -NoNewline -ForegroundColor Gray
        New-Item -ItemType Directory -Path $appBackup -Force | Out-Null
        Copy-Item -Path ""$appSource\*"" -Destination $appBackup -Recurse -Force -ErrorAction Stop
        Write-Host ""xong."" -ForegroundColor Green
    }}

    # 2. Sao luu Omnisave.exe va cac tep cau hinh
    Write-Host ""      • Sao luu launcher va tep cau hinh... "" -NoNewline -ForegroundColor Gray
    $filesToBackup = @(""Omnisave.exe"", ""version.json"", ""changelogs.json"", ""CHANGELOG.md"", ""README.md"")
    foreach ($file in $filesToBackup) {{
        $srcFile = Join-Path $appDir $file
        if (Test-Path $srcFile) {{
            Copy-Item -Path $srcFile -Destination (Join-Path $backupDir $file) -Force -ErrorAction Stop
        }}
    }}

    $manifestFile = Join-Path $appDir ""data\database\manifest.yaml""
    if (Test-Path $manifestFile) {{
        $backupDbDir = Join-Path $backupDir ""data\database""
        New-Item -ItemType Directory -Path $backupDbDir -Force | Out-Null
        Copy-Item -Path $manifestFile -Destination (Join-Path $backupDbDir ""manifest.yaml"") -Force -ErrorAction SilentlyContinue
    }}
    Write-Host ""xong."" -ForegroundColor Green

    Write-Host ""      [OK] Tao ban sao luu du phong an toan thanh cong!"" -ForegroundColor Green
    Write-Host ""      (Dang chuyen sang buoc tiep theo trong 3 giay...)"" -ForegroundColor Gray
    Start-Sleep -Seconds 3
}} catch {{
    $backupFailed = $true
}}

if ($backupFailed) {{
    Write-Host """"
    Write-Host ""==================================================================="" -ForegroundColor Red
    Write-Host "" [LOI] KHONG THE TAO BAN SAO LUU SNAPSHOT TRUOC KHI CAP NHAT!"" -ForegroundColor Red
    Write-Host ""       Da huy cap nhat an toan de bao ve ung dung hien tai."" -ForegroundColor Red
    Write-Host ""==================================================================="" -ForegroundColor Red

    Set-Content -Path $errorFlagPath -Value ""Khong the tao ban sao luu snapshot truoc khi cap nhat do day bo nho hoac loi quyen ghi."" -Encoding UTF8

    try {{
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(""Khong the tao ban sao luu du phong truoc khi cap nhat. Qua trinh cap nhat da duoc huy an toan de bao ve ung dung."", ""Omnisave - Huy Cap Nhat"", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    }} catch {{ }}

    Start-Sleep -Seconds 5
    $launcherPath = Join-Path $appDir ""Omnisave.exe""
    if (Test-Path $launcherPath) {{
        Start-Process $launcherPath -WorkingDirectory $appDir
    }}
    exit 1
}}

# -------------------------------------------------------------------
# [3/5] Cap nhat cac tep tin phien ban moi
# -------------------------------------------------------------------
Write-Host """"
Write-Host ""-------------------------------------------------------------------"" -ForegroundColor DarkGray
Write-Host ""[3/5] Dang cap nhat cac tep tin phien ban moi v$targetVersion..."" -ForegroundColor Yellow

$updateFailed = $false
try {{
    # 1. Cap nhat thu muc app/
    $extractedApp = Join-Path $extractedSourceDir ""app""
    $targetApp = Join-Path $appDir ""app""
    if (Test-Path $extractedApp) {{
        Write-Host ""      • Cap nhat thu muc app/ (binaries va core)... "" -NoNewline -ForegroundColor Gray
        New-Item -ItemType Directory -Path $targetApp -Force | Out-Null
        Copy-Item -Path ""$extractedApp\*"" -Destination $targetApp -Recurse -Force -ErrorAction Stop
        Write-Host ""xong."" -ForegroundColor Green
    }} else {{
        Write-Host ""      • Sao chep toan bo tep tin giai nen... "" -NoNewline -ForegroundColor Gray
        Copy-Item -Path ""$extractedSourceDir\*"" -Destination $appDir -Recurse -Force -ErrorAction Stop
        Write-Host ""xong."" -ForegroundColor Green
    }}

    # 2. Cap nhat launcher va cac tep phat hanh
    Write-Host ""      • Cap nhat launcher va tai nguyen phat hanh... "" -NoNewline -ForegroundColor Gray
    $filesToUpdate = @(""Omnisave.exe"", ""version.json"", ""changelogs.json"", ""CHANGELOG.md"", ""README.md"")
    foreach ($file in $filesToUpdate) {{
        $src = Join-Path $extractedSourceDir $file
        if (Test-Path $src) {{
            Copy-Item -Path $src -Destination (Join-Path $appDir $file) -Force -ErrorAction Stop
        }}
    }}

    $extractedManifest = Join-Path $extractedSourceDir ""data\database\manifest.yaml""
    if (Test-Path $extractedManifest) {{
        $targetDbDir = Join-Path $appDir ""data\database""
        New-Item -ItemType Directory -Path $targetDbDir -Force | Out-Null
        Copy-Item -Path $extractedManifest -Destination (Join-Path $targetDbDir ""manifest.yaml"") -Force -ErrorAction SilentlyContinue
    }}
    Write-Host ""xong."" -ForegroundColor Green

    Write-Host ""      [OK] Sao chep toan bo tep tin phien ban moi thanh cong!"" -ForegroundColor Green
    Write-Host ""      (Dang chuyen sang buoc tiep theo trong 3 giay...)"" -ForegroundColor Gray
    Start-Sleep -Seconds 3
}} catch {{
    $updateFailed = $true
}}

# Neu that bai -> Tien hanh ROLLBACK
if ($updateFailed) {{
    Write-Host """"
    Write-Host ""==================================================================="" -ForegroundColor Red
    Write-Host "" [CANH BAO] SAO CHEP CAP NHAT THAT BAI - DANG HOAN TAC (ROLLBACK)..."" -ForegroundColor Red
    Write-Host ""==================================================================="" -ForegroundColor Red
    Write-Host "" Dang khoi phuc lai phien ban truoc do tu ban sao luu snapshot..."" -ForegroundColor Yellow

    try {{
        $backupApp = Join-Path $backupDir ""app""
        $targetApp = Join-Path $appDir ""app""
        if (Test-Path $backupApp) {{
            Copy-Item -Path ""$backupApp\*"" -Destination $targetApp -Recurse -Force -ErrorAction SilentlyContinue
        }}

        $filesToRestore = @(""Omnisave.exe"", ""version.json"", ""changelogs.json"", ""CHANGELOG.md"", ""README.md"")
        foreach ($file in $filesToRestore) {{
            $src = Join-Path $backupDir $file
            if (Test-Path $src) {{
                Copy-Item -Path $src -Destination (Join-Path $appDir $file) -Force -ErrorAction SilentlyContinue
            }}
        }}

        $backupManifest = Join-Path $backupDir ""data\database\manifest.yaml""
        if (Test-Path $backupManifest) {{
            $targetDbDir = Join-Path $appDir ""data\database""
            New-Item -ItemType Directory -Path $targetDbDir -Force | Out-Null
            Copy-Item -Path $backupManifest -Destination (Join-Path $targetDbDir ""manifest.yaml"") -Force -ErrorAction SilentlyContinue
        }}
    }} catch {{ }}

    Set-Content -Path $rollbackFlagPath -Value ""Sao chep file cap nhat that bai. He thong da tu dong hoan tac (rollback) va khoi phuc an toan phien ban truoc do."" -Encoding UTF8

    try {{
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(""Qua trinh cap nhat Omnisave gap su co khi sao chep tep tin. He thong da tu dong hoan tac (rollback) va khoi phuc an toan phien ban truoc do. Ung dung se khoi dong lai ngay bay gio."", ""Omnisave - Tu Dong Hoan Tac (Rollback)"", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    }} catch {{ }}

    Write-Host """"
    Write-Host "" [OK] Hoan tac thanh cong! Dang khoi dong lai phien ban an toan..."" -ForegroundColor Green
    Start-Sleep -Seconds 5

    $launcherPath = Join-Path $appDir ""Omnisave.exe""
    if (Test-Path $launcherPath) {{
        Start-Process $launcherPath -WorkingDirectory $appDir
    }}
    exit 1
}}

# -------------------------------------------------------------------
# [4/5] Don dep tep tin tam
# -------------------------------------------------------------------
Write-Host """"
Write-Host ""-------------------------------------------------------------------"" -ForegroundColor DarkGray
Write-Host ""[4/5] Dang don dep tep tin tam va hoan tat ghi nhan..."" -ForegroundColor Yellow

try {{
    $extractedFolder = Join-Path $tempDir ""extracted""
    if (Test-Path $extractedFolder) {{ Remove-Item -Path $extractedFolder -Recurse -Force -ErrorAction SilentlyContinue }}

    $zipPkg = Join-Path $tempDir ""update_package.zip""
    if (Test-Path $zipPkg) {{ Remove-Item -Path $zipPkg -Force -ErrorAction SilentlyContinue }}

    if (Test-Path $backupDir) {{ Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue }}

    Set-Content -Path $successFlagPath -Value $targetVersion -Encoding UTF8
    Write-Host ""      • Xoa goi cai dat update_package.zip... xong."" -ForegroundColor Gray
    Write-Host ""      • Don dep thu muc giai nen extracted/... xong."" -ForegroundColor Gray
    Write-Host ""      • Xoa ban snapshot du phong tam... xong."" -ForegroundColor Gray
    Write-Host ""      [OK] Don dep hoan tat va ghi nhan phien ban thanh cong!"" -ForegroundColor Green
}} catch {{
    Write-Host ""      [OK] Hoan tat ghi nhan phien ban!"" -ForegroundColor Green
}}

Write-Host ""      (Dang chuyen sang buoc hoan tat trong 3 giay...)"" -ForegroundColor Gray
Start-Sleep -Seconds 3

# -------------------------------------------------------------------
# [5/5] Hoan tat thanh cong & Dem nguoc 5s mo app
# -------------------------------------------------------------------
Write-Host """"
Write-Host ""==================================================================="" -ForegroundColor Green
Write-Host "" [THANH CONG] CAP NHAT HOAN TAT LEN PHIEN BAN Omnisave v$targetVersion!   "" -ForegroundColor Green
Write-Host ""==================================================================="" -ForegroundColor Green
Write-Host """"
Write-Host "" Ung dung Omnisave se tu dong khoi dong lai sau 5 giay:"" -ForegroundColor White
Write-Host """"

for ($i = 5; $i -ge 1; $i--) {{
    if ($i -gt 2) {{
        Write-Host ""   [ $i s ] Chuan bi mo lai ung dung sau $i giay..."" -ForegroundColor Yellow
    }} else {{
        Write-Host ""   [ $i s ] San sang khoi dong lai sau $i giay..."" -ForegroundColor Cyan
    }}
    Start-Sleep -Seconds 1
}}

Write-Host """"
Write-Host "" [>>] DANG KHOI CHAY Omnisave v$targetVersion..."" -ForegroundColor Green
Start-Sleep -Milliseconds 500

$launcherPath = Join-Path $appDir ""Omnisave.exe""
if (Test-Path $launcherPath) {{
    Start-Process $launcherPath -WorkingDirectory $appDir
}} else {{
    $appExe = Join-Path $appDir ""app\Omnisave.exe""
    if (Test-Path $appExe) {{
        Start-Process $appExe -WorkingDirectory $appDir
    }}
}}
exit 0
";

        // 2. Tạo wrapper script update_runner.bat để kích hoạt PowerShell có quyền Bypass
        var batContent = $@"@echo off
chcp 65001 >nul
title Omnisave Auto-Updater - Cap Nhat Phien Ban Moi
cd /d ""%~dp0""

REM Parameters: currentPid = {targetProcessId}, targetVersion = {targetVersion}
REM Flags: update_error.flag, update_success.flag, update_rollback.flag
REM Procedures: BACKUP_FAILED, UPDATE_FAILED, backup_prev, rollback_procedure, xcopy, Omnisave.exe

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""%~dp0update_runner.ps1""
set PS_EXIT=%ERRORLEVEL%
if %PS_EXIT% equ 0 exit /b 0

echo.
echo ===================================================================
echo  [LOI] Tien trinh cap nhat PowerShell gap su co - Exit code: %PS_EXIT%
echo  Cua so se giu lai de ban kiem tra loi truoc khi dong...
echo ===================================================================
pause
exit /b %PS_EXIT%
";

        // Dọn dẹp file .cmd cũ nếu còn sót lại từ phiên bản trước
        var legacyCmdPath = Path.Combine(tempDir, "update_runner.cmd");
        if (File.Exists(legacyCmdPath))
        {
            try { File.Delete(legacyCmdPath); } catch { }
        }

        var currentTraceId = LoggingService.CurrentTraceId;
        if (!string.IsNullOrEmpty(currentTraceId))
        {
            try { File.WriteAllText(Path.Combine(AutoUpdateDirectory, "update_trace.txt"), currentTraceId); } catch { }
        }

        var utf8WithBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(ps1Path, ps1Content, utf8WithBom);
        File.WriteAllText(batPath, batContent, utf8WithBom);

        return batPath;
    }

    private string? GetSavedTraceId()
    {
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_trace.txt");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_trace.txt");
        if (File.Exists(flagPath))
        {
            try { return File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim(); } catch { }
        }
        return null;
    }

    private void CleanupSavedTraceId()
    {
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_trace.txt");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_trace.txt");
        if (File.Exists(flagPath))
        {
            try { File.Delete(flagPath); } catch { }
        }
    }

    /// <summary>
    /// Kiểm tra xem có cờ rollback từ lần cập nhật trước (update_rollback.flag) không.
    /// Nếu có, đọc nội dung lỗi/hoàn tác và xóa file flag.
    /// </summary>
    public bool HasUpdateRollbackFlag(out string rollbackMessage)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_CheckRollbackFlag", null, GetSavedTraceId());
        rollbackMessage = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_rollback.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_rollback.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                rollbackMessage = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
                CleanupSavedTraceId();
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi đọc cờ update_rollback.flag: {Message}", ex.Message);
            }
        }
        return false;
    }

    /// <summary>
    /// Kiểm tra xem có cờ lỗi từ lần cập nhật trước (update_error.flag) không.
    /// Nếu có, đọc nội dung lỗi và xóa file flag.
    /// </summary>
    public bool HasUpdateErrorFlag(out string errorMessage)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_CheckErrorFlag", null, GetSavedTraceId());
        errorMessage = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_error.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_error.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                errorMessage = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
                CleanupSavedTraceId();
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi đọc cờ update_error.flag: {Message}", ex.Message);
            }
        }
        return false;
    }

    /// <summary>
    /// Kiểm tra xem có cờ thành công từ lần cập nhật trước (update_success.flag) không.
    /// Nếu có, đọc phiên bản cập nhật và xóa file flag.
    /// </summary>
    public bool HasUpdateSuccessFlag(out string version)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_CheckSuccessFlag", null, GetSavedTraceId());
        version = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_success.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_success.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                version = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
                CleanupSavedTraceId();
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi đọc cờ update_success.flag: {Message}", ex.Message);
            }
        }
        return false;
    }

    /// <summary>
    /// Kích hoạt script updater với cửa sổ console hiển thị trực quan và chuẩn bị đóng app.
    /// </summary>
    public void LaunchRunnerScript(string scriptPath)
    {
        using var trace = LoggingService.BeginTrace("AutoUpdate_LaunchRunnerScript", new { scriptPath });
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = AutoUpdateDirectory
        };

        Process.Start(psi);
    }
}
