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
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
        : AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Thư mục temp nằm ngay bên trong thư mục ứng dụng theo yêu cầu.
    /// </summary>
    public string TempDirectory
    {
        get
        {
            var tempDir = Path.Combine(AppDirectory, "temp");
            if (!Directory.Exists(tempDir))
            {
                try { Directory.CreateDirectory(tempDir); } catch { }
            }
            return tempDir;
        }
    }

    /// <summary>
    /// Lấy phiên bản hiện tại của ứng dụng.
    /// </summary>
    public virtual string GetCurrentVersion()
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
    /// Xóa phiên bản đã bỏ qua để cho phép nhận thông báo trở lại.
    /// </summary>
    public void ResetSkippedVersion()
    {
        try
        {
            var config = AppConfigService.GetConfig();
            config.SkippedUpdateVersion = null;
            AppConfigService.SaveConfig(config);
        }
        catch { }
    }

    /// <summary>
    /// Tải gói cập nhật publish.zip về thư mục temp trong ứng dụng.
    /// </summary>
    public async Task<string> DownloadUpdatePackageAsync(
        string downloadUrl,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = TempDirectory;
        var zipFilePath = Path.Combine(tempDir, "publish.zip");

        if (File.Exists(zipFilePath))
        {
            try { File.Delete(zipFilePath); } catch { }
        }

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalDownloaded = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalDownloaded += bytesRead;

            if (totalBytes > 0)
            {
                int percent = (int)Math.Clamp((double)totalDownloaded / totalBytes * 100, 0, 100);
                progress?.Report(new UpdateDownloadProgress(percent, totalDownloaded, totalBytes, $"Đang tải xuống... {percent}%"));
            }
            else
            {
                progress?.Report(new UpdateDownloadProgress(50, totalDownloaded, -1, "Đang tải xuống bản cập nhật..."));
            }
        }

        progress?.Report(new UpdateDownloadProgress(100, totalDownloaded, totalBytes, "Tải xuống hoàn tất!"));
        return zipFilePath;
    }

    /// <summary>
    /// Giải nén file zip chứa các file build publish vào thư mục temp/extracted/.
    /// Trả về đường dẫn thư mục chứa trực tiếp các file build publish.
    /// </summary>
    public string ExtractUpdatePackage(string zipFilePath)
    {
        var tempDir = TempDirectory;
        var extractDir = Path.Combine(tempDir, "extracted");

        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, true); } catch { }
        }
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(zipFilePath, extractDir, true);

        // Trường hợp tải repo archive zip từ GitHub (nội dung nằm trong subfolder tên repo-branch)
        var subDirs = Directory.GetDirectories(extractDir);
        var rootFiles = Directory.GetFiles(extractDir);
        if (rootFiles.Length == 0 && subDirs.Length == 1)
        {
            return subDirs[0];
        }

        return extractDir;
    }

    /// <summary>
    /// Tạo file update_runner.cmd trong thư mục temp để chép đè các file build và khởi động lại app.
    /// </summary>
    public string GenerateRunnerScript(string extractedSourceDir, int targetProcessId)
    {
        var tempDir = TempDirectory;
        var appDir = AppDirectory;
        var scriptPath = Path.Combine(tempDir, "update_runner.cmd");

        // Tạo nội dung script cmd tương thích mọi phiên bản Windows
        var scriptContent = $@"@echo off
chcp 65001 >nul
title SaveVault Auto-Updater
timeout /t 1 /nobreak >nul

:wait_loop
tasklist /fi ""PID eq {targetProcessId}"" 2>nul | find ""{targetProcessId}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

timeout /t 1 /nobreak >nul

rem Chép đè toàn bộ các file build publish vào thư mục ứng dụng
xcopy /y /e /s /r /h ""{extractedSourceDir}\*"" ""{appDir}\"" >nul

rem Khởi động lại SaveVault.exe phiên bản mới
start """" ""{Path.Combine(appDir, "SaveVault.exe")}""

rem Dọn dẹp thư mục extracted và file zip trong temp
rmdir /s /q ""{Path.Combine(tempDir, "extracted")}"" 2>nul
del /q /f ""{Path.Combine(tempDir, "publish.zip")}"" 2>nul
exit
";

        File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);
        return scriptPath;
    }

    /// <summary>
    /// Kích hoạt script updater chạy ngầm và chuẩn bị đóng app.
    /// </summary>
    public void LaunchRunnerScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = TempDirectory
        };

        Process.Start(psi);
    }
}
