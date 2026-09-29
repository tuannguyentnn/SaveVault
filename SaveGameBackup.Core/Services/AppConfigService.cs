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
    // Cấu hình sao lưu chung (Database và Backups folder được cố định tại data/ theo chuẩn Portable)
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = true; // Mặc định và bắt buộc nén zip 100%
    public int PageSize { get; set; } = 10; // Số dòng trên mỗi trang cho các bảng phân trang
    public bool UseSqlPagination { get; set; } = true; // true: Phân trang trực tiếp từ SQL (LIMIT/OFFSET), false: RAM Pagination cũ

    // Cấu hình sao lưu Cơ sở dữ liệu (Database) lên Cloud
    public string DatabaseCloudBackupTarget { get; set; } = "GoogleDrive"; // "GoogleDrive", "OneDrive", "Both"
    public int MaxDatabaseCloudBackupsToKeep { get; set; } = 5; // Mặc định giữ 5 bản gần nhất

    // Phiên bản ứng dụng mà người dùng đã chọn Bỏ qua (Skip this version)
    public string? SkippedUpdateVersion { get; set; }

    // Cấu hình tự động cập nhật Ludusavi Manifest
    public bool AutoUpdateLudusaviManifest { get; set; } = true;
    public int LudusaviAutoUpdateDays { get; set; } = 15; // 15 hoặc 30 ngày
    public DateTime? LastLudusaviManifestSync { get; set; }

    // Nhà cung cấp Cloud hiện thời ("GoogleDrive" hoặc "OneDrive")
    public string ActiveCloudProvider { get; set; } = "GoogleDrive";

    // Cấu hình Microsoft OneDrive (được mã hóa với Salt)
    public string? OneDriveClientIdProtected { get; set; }
    public string? OneDriveRefreshTokenProtected { get; set; }
    public string? OneDriveAccessTokenProtected { get; set; }
    public DateTime OneDriveAccessTokenExpiry { get; set; } = DateTime.MinValue;
    public string? OneDriveAccountEmailProtected { get; set; }

    // Cấu hình Google Drive (được mã hóa với Salt)
    public string? GoogleDriveClientIdProtected { get; set; }
    public string? GoogleDriveClientSecretProtected { get; set; }
    public string? GoogleDriveRefreshTokenProtected { get; set; }
    public string? GoogleDriveAccessTokenProtected { get; set; }
    public DateTime GoogleDriveAccessTokenExpiry { get; set; } = DateTime.MinValue;
    public string? GoogleDriveAccountEmailProtected { get; set; }

    // Tương thích ngược: Đọc trường plaintext cũ nếu file JSON cũ chưa được mã hóa email
    [JsonPropertyName("OneDriveAccountEmail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOneDriveAccountEmail
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(OneDriveAccountEmailProtected))
            {
                OneDriveAccountEmailProtected = SecurityHelper.EncryptWithSalt(value);
            }
        }
    }

    [JsonPropertyName("GoogleDriveAccountEmail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGoogleDriveAccountEmail
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(GoogleDriveAccountEmailProtected))
            {
                GoogleDriveAccountEmailProtected = SecurityHelper.EncryptWithSalt(value);
            }
        }
    }

    // Tùy chọn sử dụng API riêng thay vì cấu hình tự động mặc định (1-Click)
    public bool UseCustomOneDriveApi { get; set; } = false;
    public bool UseCustomGoogleDriveApi { get; set; } = true;

    // Các thuộc tính tiện ích (JsonIgnore) tự động mã hóa / giải mã trong suốt
    [JsonIgnore]
    public string OneDriveClientId
    {
        get => SecurityHelper.DecryptWithSalt(OneDriveClientIdProtected);
        set => OneDriveClientIdProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? OneDriveRefreshToken
    {
        get => string.IsNullOrEmpty(OneDriveRefreshTokenProtected) ? null : SecurityHelper.DecryptWithSalt(OneDriveRefreshTokenProtected);
        set => OneDriveRefreshTokenProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? OneDriveAccessToken
    {
        get => string.IsNullOrEmpty(OneDriveAccessTokenProtected) ? null : SecurityHelper.DecryptWithSalt(OneDriveAccessTokenProtected);
        set => OneDriveAccessTokenProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? OneDriveAccountEmail
    {
        get => string.IsNullOrEmpty(OneDriveAccountEmailProtected) ? null : SecurityHelper.DecryptWithSalt(OneDriveAccountEmailProtected);
        set => OneDriveAccountEmailProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
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
    public string? GoogleDriveRefreshToken
    {
        get => string.IsNullOrEmpty(GoogleDriveRefreshTokenProtected) ? null : SecurityHelper.DecryptWithSalt(GoogleDriveRefreshTokenProtected);
        set => GoogleDriveRefreshTokenProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? GoogleDriveAccessToken
    {
        get => string.IsNullOrEmpty(GoogleDriveAccessTokenProtected) ? null : SecurityHelper.DecryptWithSalt(GoogleDriveAccessTokenProtected);
        set => GoogleDriveAccessTokenProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? GoogleDriveAccountEmail
    {
        get => string.IsNullOrEmpty(GoogleDriveAccountEmailProtected) ? null : SecurityHelper.DecryptWithSalt(GoogleDriveAccountEmailProtected);
        set => GoogleDriveAccountEmailProtected = string.IsNullOrEmpty(value) ? null : SecurityHelper.EncryptWithSalt(value);
    }
}

/// <summary>
/// Quản lý việc đọc, ghi và bộ nhớ đệm RAM của file cấu hình app_config.json
/// </summary>
public static class AppConfigService
{
    private const string ConfigFileName = "appconfig.json";
    private static AppConfigFile? _cachedConfig;
    private static readonly object _lock = new();

    public static string GetConfigFilePath()
    {
        return Path.Combine(DatabaseService.GetDefaultProjectRoot(), "data", "config", ConfigFileName);
    }

    /// <summary>
    /// Lấy cấu hình từ bộ nhớ đệm RAM. Nếu chưa nạp thì nạp từ appconfig.json vào RAM.
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
    /// Cập nhật cấu hình có luồng an toàn (thread-safe).
    /// </summary>
    public static void UpdateConfig(Action<AppConfigFile> updateAction)
    {
        lock (_lock)
        {
            var cfg = GetConfig();
            updateAction(cfg);
            SaveConfig(cfg);
        }
    }

    /// <summary>
    /// Đọc trực tiếp từ file appconfig.json và cập nhật cache trong RAM.
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
            // Nếu chưa có data/config/appconfig.json, kiểm tra file legacy ở root (app_config.json hoặc appconfig.json)
            if (!File.Exists(path))
            {
                var legacyRootConfig = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "app_config.json");
                if (File.Exists(legacyRootConfig))
                {
                    path = legacyRootConfig;
                }
            }

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfigFile>(json);
                if (config != null)
                {
                    // Tự động kiểm tra và nâng cấp nếu file config còn chứa field legacy chưa mã hóa
                    if (json.Contains("\"OneDriveAccountEmail\"") || json.Contains("\"GoogleDriveAccountEmail\""))
                    {
                        SaveConfig(config);
                    }
                    return config;
                }
            }
        }
        catch { }

        var defaultConfig = new AppConfigFile();
        try
        {
            SaveConfig(defaultConfig);
        }
        catch { }

        return defaultConfig;
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
            "onedrive_access_token" => config.OneDriveAccessToken,
            "onedrive_account_email" => config.OneDriveAccountEmail ?? defaultValue,
            "gdrive_client_id" => config.GoogleDriveClientId,
            "gdrive_client_secret" => config.GoogleDriveClientSecret,
            "gdrive_refresh_token" => config.GoogleDriveRefreshToken,
            "gdrive_access_token" => config.GoogleDriveAccessToken,
            "gdrive_account_email" => config.GoogleDriveAccountEmail ?? defaultValue,
            "active_cloud_provider" => config.ActiveCloudProvider,
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
            case "onedrive_access_token":
                config.OneDriveAccessToken = value;
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
            case "gdrive_access_token":
                config.GoogleDriveAccessToken = value;
                break;
            case "gdrive_account_email":
                config.GoogleDriveAccountEmail = value;
                break;
            case "active_cloud_provider":
                config.ActiveCloudProvider = value;
                break;
            case "createtimestampsubfolder":
                if (bool.TryParse(value, out var b)) config.CreateTimestampSubfolder = b;
                break;
        }
        SaveConfig(config);
    }
}
