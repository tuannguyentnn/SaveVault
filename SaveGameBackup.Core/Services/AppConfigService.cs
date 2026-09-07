using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Đại diện cho toàn bộ cấu hình ứng dụng được lưu trữ tập trung tại app_config.json
/// Các trường dữ liệu nhạy cảm (OAuth token, Client ID, Client Secret) được mã hóa bằng AES-256 + Salt.
/// </summary>
public class AppConfigFile
{
    // Cấu hình sao lưu chung
    public string? DatabasePath { get; set; }
    public string? BackupRootDirectory { get; set; }
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = true; // Mặc định và bắt buộc nén zip 100%

    // Nhà cung cấp Cloud hiện thời ("GoogleDrive" hoặc "OneDrive")
    public string ActiveCloudProvider { get; set; } = "GoogleDrive";

    // Cấu hình Microsoft OneDrive (được mã hóa với Salt)
    public string? OneDriveClientIdProtected { get; set; }
    public string? OneDriveRefreshTokenProtected { get; set; }
    public string? OneDriveAccountEmail { get; set; }

    // Cấu hình Google Drive (được mã hóa với Salt)
    public string? GoogleDriveClientIdProtected { get; set; }
    public string? GoogleDriveClientSecretProtected { get; set; }
    public string? GoogleDriveRefreshTokenProtected { get; set; }
    public string? GoogleDriveAccountEmail { get; set; }

    // Các thuộc tính tiện ích (JsonIgnore) tự động mã hóa / giải mã trong suốt
    [JsonIgnore]
    public string OneDriveClientId
    {
        get => SecurityHelper.DecryptWithSalt(OneDriveClientIdProtected);
        set => OneDriveClientIdProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string OneDriveRefreshToken
    {
        get => SecurityHelper.DecryptWithSalt(OneDriveRefreshTokenProtected);
        set => OneDriveRefreshTokenProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string GoogleDriveClientId
    {
        get => SecurityHelper.DecryptWithSalt(GoogleDriveClientIdProtected);
        set => GoogleDriveClientIdProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string GoogleDriveClientSecret
    {
        get => SecurityHelper.DecryptWithSalt(GoogleDriveClientSecretProtected);
        set => GoogleDriveClientSecretProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string GoogleDriveRefreshToken
    {
        get => SecurityHelper.DecryptWithSalt(GoogleDriveRefreshTokenProtected);
        set => GoogleDriveRefreshTokenProtected = SecurityHelper.EncryptWithSalt(value);
    }
}

/// <summary>
/// Quản lý việc đọc, ghi và bộ nhớ đệm RAM của file cấu hình app_config.json
/// </summary>
public static class AppConfigService
{
    private const string ConfigFileName = "app_config.json";
    private static AppConfigFile? _cachedConfig;
    private static readonly object _lock = new();

    public static string GetConfigFilePath()
    {
        return Path.Combine(DatabaseService.GetDefaultProjectRoot(), ConfigFileName);
    }

    /// <summary>
    /// Lấy cấu hình từ bộ nhớ đệm RAM. Nếu chưa nạp thì nạp từ app_config.json vào RAM.
    /// </summary>
    public static AppConfigFile GetConfig()
    {
        lock (_lock)
        {
            if (_cachedConfig != null) return _cachedConfig;
            _cachedConfig = LoadConfigFromFile();
            return _cachedConfig;
        }
    }

    /// <summary>
    /// Đọc trực tiếp từ file app_config.json và cập nhật cache trong RAM.
    /// </summary>
    public static AppConfigFile LoadConfig()
    {
        lock (_lock)
        {
            _cachedConfig = LoadConfigFromFile();
            return _cachedConfig;
        }
    }

    private static AppConfigFile LoadConfigFromFile()
    {
        try
        {
            var path = GetConfigFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfigFile>(json);
                if (config != null) return config;
            }
        }
        catch { }

        return new AppConfigFile();
    }

    public static string? GetConfiguredDatabasePath()
    {
        var config = GetConfig();
        return !string.IsNullOrWhiteSpace(config.DatabasePath) ? config.DatabasePath : null;
    }

    /// <summary>
    /// Ghi cấu hình ra app_config.json và cập nhật ngay vào RAM cache.
    /// </summary>
    public static void SaveConfig(AppConfigFile config)
    {
        lock (_lock)
        {
            _cachedConfig = config;
            try
            {
                var path = GetConfigFilePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }

    public static void SaveDatabasePath(string dbPath)
    {
        var config = GetConfig();
        config.DatabasePath = dbPath;
        SaveConfig(config);
    }

    /// <summary>
    /// Reset bộ nhớ đệm RAM của cấu hình để buộc lần sau phải nạp lại từ file.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedConfig = null;
        }
    }

    /// <summary>
    /// Trợ giúp đọc setting theo Key dạng chuỗi, lưu trữ tập trung trong app_config.json thay vì SQLite
    /// </summary>
    public static string? GetSetting(string key, string? defaultValue = null)
    {
        var config = GetConfig();
        return key.ToLowerInvariant() switch
        {
            "onedrive_client_id" => config.OneDriveClientId,
            "onedrive_refresh_token" => config.OneDriveRefreshToken,
            "onedrive_account_email" => config.OneDriveAccountEmail ?? defaultValue,
            "gdrive_client_id" => config.GoogleDriveClientId,
            "gdrive_client_secret" => config.GoogleDriveClientSecret,
            "gdrive_refresh_token" => config.GoogleDriveRefreshToken,
            "gdrive_account_email" => config.GoogleDriveAccountEmail ?? defaultValue,
            "active_cloud_provider" => config.ActiveCloudProvider,
            "backuprootdirectory" => config.BackupRootDirectory ?? defaultValue,
            "createtimestampsubfolder" => config.CreateTimestampSubfolder.ToString(),
            "autocompresszip" => "true",
            _ => defaultValue
        };
    }

    /// <summary>
    /// Trợ giúp lưu setting theo Key dạng chuỗi vào app_config.json thay vì SQLite
    /// </summary>
    public static void SaveSetting(string key, string value)
    {
        var config = GetConfig();
        switch (key.ToLowerInvariant())
        {
            case "onedrive_client_id":
                config.OneDriveClientId = value;
                break;
            case "onedrive_refresh_token":
                config.OneDriveRefreshToken = value;
                break;
            case "onedrive_account_email":
                config.OneDriveAccountEmail = value;
                break;
            case "gdrive_client_id":
                config.GoogleDriveClientId = value;
                break;
            case "gdrive_client_secret":
                config.GoogleDriveClientSecret = value;
                break;
            case "gdrive_refresh_token":
                config.GoogleDriveRefreshToken = value;
                break;
            case "gdrive_account_email":
                config.GoogleDriveAccountEmail = value;
                break;
            case "active_cloud_provider":
                config.ActiveCloudProvider = value;
                break;
            case "backuprootdirectory":
                config.BackupRootDirectory = value;
                break;
            case "createtimestampsubfolder":
                if (bool.TryParse(value, out var b)) config.CreateTimestampSubfolder = b;
                break;
        }
        SaveConfig(config);
    }
}
