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
    /// Kiểm tra phiên bản mới từ repository chứa bản build publish.
    /// </summary>
    /// <param name="isSilent">Nếu là true (gọi tự động lúc mở app), sẽ bỏ qua nếu version trùng với SkippedUpdateVersion.</param>
    public async Task<(bool isUpdateAvailable, UpdateInfo? updateInfo, string message)> CheckForUpdateAsync(
        bool isSilent = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _manifestUrl);
            req.Headers.Add("User-Agent", "SaveVault-Updater");

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
            // ước tính kích thước trung bình của gói SaveVault publish (~45 MB) để tính toán tiến trình mượt mà.
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
            long lastReportedBytes = 0;
            int lastReportedPercent = -1;
            double smoothedSpeed = 0;

            // Báo cáo khởi đầu 0%
            progress?.Report(new UpdateDownloadProgress(0, 0, totalBytes, "Bắt đầu tải xuống...", isEstimated, 0));

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalDownloaded += bytesRead;

                long elapsedMs = stopwatch.ElapsedMilliseconds;

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
                    long deltaMs = elapsedMs - lastReportedTimeMs;
                    if (deltaMs > 0)
                    {
                        double instantSpeed = (double)(totalDownloaded - lastReportedBytes) / (deltaMs / 1000.0);
                        smoothedSpeed = smoothedSpeed <= 0 ? instantSpeed : (smoothedSpeed * 0.7 + instantSpeed * 0.3);
                    }

                    progress?.Report(new UpdateDownloadProgress(
                        percent,
                        totalDownloaded,
                        totalBytes,
                        $"Đang tải xuống... {percent}%",
                        isEstimated,
                        smoothedSpeed));

                    lastReportedTimeMs = elapsedMs;
                    lastReportedBytes = totalDownloaded;
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

        // Tự động tìm thư mục chứa các tệp phát hành (chứa SaveVault.exe hoặc thư mục app/)
        string? targetDir = null;
        if (File.Exists(Path.Combine(extractDir, "SaveVault.exe")) || Directory.Exists(Path.Combine(extractDir, "app")))
        {
            targetDir = extractDir;
        }
        else
        {
            var subDirs = Directory.GetDirectories(extractDir);
            foreach (var sub in subDirs)
            {
                if (File.Exists(Path.Combine(sub, "SaveVault.exe")) || Directory.Exists(Path.Combine(sub, "app")))
                {
                    targetDir = sub;
                    break;
                }
            }
        }

        if (targetDir == null)
        {
            throw new InvalidOperationException("Gói cập nhật không chứa tệp thực thi hợp lệ (SaveVault.exe hoặc thư mục app/).");
        }

        return targetDir;
    }

    /// <summary>
    /// Tạo file update_runner.cmd trong thư mục temp để chép đè các file build và khởi động lại app.
    /// </summary>
    public string GenerateRunnerScript(string extractedSourceDir, int targetProcessId, string targetVersion = "")
    {
        var tempDir = AutoUpdateDirectory;
        var appDir = AppDirectory;
        var scriptPath = Path.Combine(tempDir, "update_runner.cmd");
        var logPath = Path.Combine(tempDir, "update.log");
        var errorFlagPath = Path.Combine(tempDir, "update_error.flag");
        var rollbackFlagPath = Path.Combine(tempDir, "update_rollback.flag");
        var successFlagPath = Path.Combine(tempDir, "update_success.flag");
        var backupDir = Path.Combine(tempDir, "backup_prev");

        // Tạo nội dung script cmd tương thích mọi phiên bản Windows với cơ chế Snapshot & Rollback bảo vệ 100%
        var scriptContent = $@"@echo off
chcp 65001 >nul
color 0B
title SaveVault Auto-Updater - Dang Cap Nhat Phien Ban Moi
cd /d ""{tempDir}""

set ""LOG_FILE={logPath}""
set ""ERROR_FLAG={errorFlagPath}""
set ""ROLLBACK_FLAG={rollbackFlagPath}""
set ""SUCCESS_FLAG={successFlagPath}""
set ""BACKUP_DIR={backupDir}""

del /f /q ""%ERROR_FLAG%"" 2>nul
del /f /q ""%ROLLBACK_FLAG%"" 2>nul
del /f /q ""%SUCCESS_FLAG%"" 2>nul
if exist ""%BACKUP_DIR%"" rmdir /s /q ""%BACKUP_DIR%"" 2>nul

echo ===================================================================
echo               SaveVault - TIEN TRINH CAP NHAT TU DONG               
echo ===================================================================
echo.
echo  [THONG TIN]
echo   - Phien ban muc tieu : {targetVersion}
echo   - Thu muc cai dat    : {appDir}
echo.
echo -------------------------------------------------------------------
echo  [1/5] Dang doi SaveVault (PID: {targetProcessId}) dong an toan...

:wait_loop
tasklist /fi ""PID eq {targetProcessId}"" 2>nul | find ""{targetProcessId}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

echo        Ung dung cu da thoat. Dang giai phong file locks...
timeout /t 1 /nobreak >nul

set BACKUP_FAILED=0
set UPDATE_FAILED=0

echo.
echo  [2/5] Tao ban sao luu snapshot phien ban hien tai (du phong su co)...
if not exist ""%BACKUP_DIR%"" mkdir ""%BACKUP_DIR%"" 2>nul

if exist ""{appDir}\app"" (
    where robocopy >nul 2>&1
    if not errorlevel 1 (
        robocopy ""{appDir}\app"" ""%BACKUP_DIR%\app"" /E /R:3 /W:1 /NP /NDL /NFL >> ""%LOG_FILE%"" 2>&1
        if errorlevel 8 set BACKUP_FAILED=1
    ) else (
        xcopy /y /e /s /r /h ""{appDir}\app\*"" ""%BACKUP_DIR%\app\"" >> ""%LOG_FILE%"" 2>&1
        if errorlevel 1 set BACKUP_FAILED=1
    )
)

if exist ""{appDir}\SaveVault.exe"" (
    copy /y ""{appDir}\SaveVault.exe"" ""%BACKUP_DIR%\SaveVault.exe"" >> ""%LOG_FILE%"" 2>&1
    if errorlevel 1 set BACKUP_FAILED=1
)
if exist ""{appDir}\version.json"" copy /y ""{appDir}\version.json"" ""%BACKUP_DIR%\version.json"" >> ""%LOG_FILE%"" 2>&1
if exist ""{appDir}\changelogs.json"" copy /y ""{appDir}\changelogs.json"" ""%BACKUP_DIR%\changelogs.json"" >> ""%LOG_FILE%"" 2>&1
if exist ""{appDir}\CHANGELOG.md"" copy /y ""{appDir}\CHANGELOG.md"" ""%BACKUP_DIR%\CHANGELOG.md"" >> ""%LOG_FILE%"" 2>&1
if exist ""{appDir}\README.md"" copy /y ""{appDir}\README.md"" ""%BACKUP_DIR%\README.md"" >> ""%LOG_FILE%"" 2>&1
if exist ""{appDir}\data\database\manifest.yaml"" (
    if not exist ""%BACKUP_DIR%\data\database"" mkdir ""%BACKUP_DIR%\data\database"" 2>nul
    copy /y ""{appDir}\data\database\manifest.yaml"" ""%BACKUP_DIR%\data\database\manifest.yaml"" >> ""%LOG_FILE%"" 2>&1
)

if %BACKUP_FAILED% neq 0 (
    color 0C
    echo  [LOI] Khong the tao ban sao luu truoc khi cap nhat!
    echo        Da huy cap nhat an toan de bao ve ung dung hien tai.
    echo Khong the tao ban sao luu truoc khi cap nhat (co the do day bo nho hoac quyen ghi). Da huy de bao ve ung dung. > ""%ERROR_FLAG%""
    powershell -NoProfile -Command ""Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('Khong the tao ban sao luu du phong truoc khi cap nhat (co the do o dia day). Qua trinh cap nhat da duoc huy an toan de bao ve ung dung.', 'SaveVault - Huy Cap Nhat', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)"" 2>nul
    timeout /t 3 >nul
    cd /d ""{appDir}""
    if exist ""{Path.Combine(appDir, "SaveVault.exe")}"" (
        start """" ""{Path.Combine(appDir, "SaveVault.exe")}""
    ) else if exist ""{Path.Combine(appDir, "app", "SaveVault.exe")}"" (
        start """" ""{Path.Combine(appDir, "app", "SaveVault.exe")}""
    )
    goto end_script
)

echo.
echo  [3/5] Dang sao chep cac tep tin phien ban moi...
echo        - Dang cap nhat thu muc app/ (binaries chinh)...
if exist ""{extractedSourceDir}\app"" (
    where robocopy >nul 2>&1
    if not errorlevel 1 (
        robocopy ""{extractedSourceDir}\app"" ""{appDir}\app"" /E /R:5 /W:1 /NP /NDL /NFL >> ""%LOG_FILE%"" 2>&1
        if errorlevel 8 set UPDATE_FAILED=1
    ) else (
        xcopy /y /e /s /r /h ""{extractedSourceDir}\app\*"" ""{appDir}\app\"" >> ""%LOG_FILE%"" 2>&1
        if errorlevel 1 set UPDATE_FAILED=1
    )
) else (
    xcopy /y /e /s /r /h ""{extractedSourceDir}\*"" ""{appDir}\"" >> ""%LOG_FILE%"" 2>&1
    if errorlevel 1 set UPDATE_FAILED=1
)

echo        - Dang cap nhat launcher va cac tep tin phat hanh...
if exist ""{extractedSourceDir}\SaveVault.exe"" (
    copy /y ""{extractedSourceDir}\SaveVault.exe"" ""{appDir}\SaveVault.exe"" >> ""%LOG_FILE%"" 2>&1
    if errorlevel 1 set UPDATE_FAILED=1
)
if exist ""{extractedSourceDir}\version.json"" (
    copy /y ""{extractedSourceDir}\version.json"" ""{appDir}\version.json"" >> ""%LOG_FILE%"" 2>&1
)
if exist ""{extractedSourceDir}\changelogs.json"" (
    copy /y ""{extractedSourceDir}\changelogs.json"" ""{appDir}\changelogs.json"" >> ""%LOG_FILE%"" 2>&1
)
if exist ""{extractedSourceDir}\CHANGELOG.md"" (
    copy /y ""{extractedSourceDir}\CHANGELOG.md"" ""{appDir}\CHANGELOG.md"" >> ""%LOG_FILE%"" 2>&1
)
if exist ""{extractedSourceDir}\README.md"" (
    copy /y ""{extractedSourceDir}\README.md"" ""{appDir}\README.md"" >> ""%LOG_FILE%"" 2>&1
)
if exist ""{extractedSourceDir}\data\database\manifest.yaml"" (
    if not exist ""{appDir}\data\database"" mkdir ""{appDir}\data\database"" 2>nul
    copy /y ""{extractedSourceDir}\data\database\manifest.yaml"" ""{appDir}\data\database\manifest.yaml"" >> ""%LOG_FILE%"" 2>&1
)

if %UPDATE_FAILED% neq 0 goto rollback_procedure

echo.
echo  [4/5] Dang don dep tap tin tam va ban sao luu...
timeout /t 1 /nobreak >nul
rmdir /s /q ""{Path.Combine(tempDir, "extracted")}"" 2>nul
del /q /f ""{Path.Combine(tempDir, "update_package.zip")}"" 2>nul
if exist ""%BACKUP_DIR%"" rmdir /s /q ""%BACKUP_DIR%"" 2>nul
echo {targetVersion} > ""%SUCCESS_FLAG%""

echo.
echo -------------------------------------------------------------------
echo  [5/5] CAP NHAT THANH CONG!
echo        Dang khoi dong lai SaveVault {targetVersion}...
echo -------------------------------------------------------------------
timeout /t 2 /nobreak >nul

cd /d ""{appDir}""
if exist ""{Path.Combine(appDir, "SaveVault.exe")}"" (
    start """" ""{Path.Combine(appDir, "SaveVault.exe")}""
) else if exist ""{Path.Combine(appDir, "app", "SaveVault.exe")}"" (
    start """" ""{Path.Combine(appDir, "app", "SaveVault.exe")}""
)
goto end_script

:rollback_procedure
color 0C
echo.
echo ===================================================================
echo  [CANH BAO] SAO CHEP CAP NHAT THAT BAI - DANG HOAN TAC (ROLLBACK)...
echo ===================================================================
echo        Xem chi tiet loi tai: ""%LOG_FILE%""
echo.
echo  Dang khoi phuc lai phien ban truoc do tu ban sao luu snapshot...

if exist ""%BACKUP_DIR%\app"" (
    where robocopy >nul 2>&1
    if not errorlevel 1 (
        robocopy ""%BACKUP_DIR%\app"" ""{appDir}\app"" /E /R:3 /W:1 /NP /NDL /NFL >> ""%LOG_FILE%"" 2>&1
    ) else (
        xcopy /y /e /s /r /h ""%BACKUP_DIR%\app\*"" ""{appDir}\app\"" >> ""%LOG_FILE%"" 2>&1
    )
)

if exist ""%BACKUP_DIR%\SaveVault.exe"" (
    copy /y ""%BACKUP_DIR%\SaveVault.exe"" ""{appDir}\SaveVault.exe"" >> ""%LOG_FILE%"" 2>&1
)
if exist ""%BACKUP_DIR%\version.json"" copy /y ""%BACKUP_DIR%\version.json"" ""{appDir}\version.json"" >> ""%LOG_FILE%"" 2>&1
if exist ""%BACKUP_DIR%\changelogs.json"" copy /y ""%BACKUP_DIR%\changelogs.json"" ""{appDir}\changelogs.json"" >> ""%LOG_FILE%"" 2>&1
if exist ""%BACKUP_DIR%\CHANGELOG.md"" copy /y ""%BACKUP_DIR%\CHANGELOG.md"" ""{appDir}\CHANGELOG.md"" >> ""%LOG_FILE%"" 2>&1
if exist ""%BACKUP_DIR%\README.md"" copy /y ""%BACKUP_DIR%\README.md"" ""{appDir}\README.md"" >> ""%LOG_FILE%"" 2>&1
if exist ""%BACKUP_DIR%\data\database\manifest.yaml"" (
    if not exist ""{appDir}\data\database"" mkdir ""{appDir}\data\database"" 2>nul
    copy /y ""%BACKUP_DIR%\data\database\manifest.yaml"" ""{appDir}\data\database\manifest.yaml"" >> ""%LOG_FILE%"" 2>&1
)

echo Sao chep file cap nhat that bai. He thong da tu dong hoan tac (rollback) va khoi phuc phien ban truoc do an toan. Xem chi tiet tai data\temp\auto_update\update.log. > ""%ROLLBACK_FLAG%""
powershell -NoProfile -Command ""Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('Qua trinh cap nhat SaveVault gap su co khi sao chep tep tin. He thong da tu dong hoan tac (rollback) va khoi phuc an toan phien ban truoc do. Ung dung se khoi dong lai ngay bay gio.', 'SaveVault - Tu Dong Hoan Tac (Rollback)', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)"" 2>nul

echo.
echo  Hoan tac thanh cong! Dang khoi dong lai phien ban an toan...
timeout /t 2 /nobreak >nul

cd /d ""{appDir}""
if exist ""{Path.Combine(appDir, "SaveVault.exe")}"" (
    start """" ""{Path.Combine(appDir, "SaveVault.exe")}""
) else if exist ""{Path.Combine(appDir, "app", "SaveVault.exe")}"" (
    start """" ""{Path.Combine(appDir, "app", "SaveVault.exe")}""
)

:end_script
exit
";

        File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);
        return scriptPath;
    }

    /// <summary>
    /// Kiểm tra xem có cờ rollback từ lần cập nhật trước (update_rollback.flag) không.
    /// Nếu có, đọc nội dung lỗi/hoàn tác và xóa file flag.
    /// </summary>
    public bool HasUpdateRollbackFlag(out string rollbackMessage)
    {
        rollbackMessage = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_rollback.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_rollback.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                rollbackMessage = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
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
        errorMessage = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_error.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_error.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                errorMessage = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
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
        version = string.Empty;
        var flagPath = Path.Combine(AutoUpdateDirectory, "update_success.flag");
        if (!File.Exists(flagPath)) flagPath = Path.Combine(TempDirectory, "update_success.flag");
        if (File.Exists(flagPath))
        {
            try
            {
                version = File.ReadAllText(flagPath, System.Text.Encoding.UTF8).Trim();
                try { File.Delete(flagPath); } catch { }
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
