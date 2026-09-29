using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.Core.Models;

/// <summary>
/// Đại diện cho toàn bộ tệp db_cloud_backup_history.json lưu trữ danh sách các bản sao lưu database lên Cloud.
/// </summary>
public class DatabaseCloudHistoryFile
{
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<DatabaseCloudBackupEntry> Entries { get; set; } = new();
}

/// <summary>
/// Đại diện cho một mốc sao lưu Database hoàn chỉnh (Snapshot).
/// Nếu người dùng chọn sao lưu lên cả 2 Cloud, bản ghi này sẽ chứa 2 phần tử trong CloudUploads.
/// </summary>
public class DatabaseCloudBackupEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime BackupTime { get; set; } = DateTime.UtcNow;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int GameCount { get; set; }
    public int SnapshotCount { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public List<DatabaseCloudUploadItem> CloudUploads { get; set; } = new();

    // Thuộc tính tiện ích
    public bool HasGoogleDrive => CloudUploads.Any(u => string.Equals(u.Provider, "GoogleDrive", StringComparison.OrdinalIgnoreCase));
    public bool HasOneDrive => CloudUploads.Any(u => string.Equals(u.Provider, "OneDrive", StringComparison.OrdinalIgnoreCase));
    public bool IsMultiCloud => HasGoogleDrive && HasOneDrive;

    [JsonIgnore]
    public string ProvidersDisplay
    {
        get
        {
            if (IsMultiCloud) return "Google Drive & OneDrive (Dự phòng kép)";
            if (HasGoogleDrive) return "Google Drive";
            if (HasOneDrive) return "OneDrive";
            return "Chưa rõ";
        }
    }

    [JsonIgnore]
    public string FormattedSize
    {
        get
        {
            if (FileSizeBytes < 1024) return $"{FileSizeBytes} B";
            if (FileSizeBytes < 1024 * 1024) return $"{FileSizeBytes / 1024.0:F1} KB";
            return $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}

/// <summary>
/// Đại diện cho bản upload của database trên một Cloud Provider cụ thể.
/// Các trường định danh và nhạy cảm (AccountEmail, FileId, WebUrl) được bảo mật 100% bằng AES-256 + Salt.
/// </summary>
public class DatabaseCloudUploadItem
{
    public string Provider { get; set; } = string.Empty; // "GoogleDrive" hoặc "OneDrive"
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // Các trường lưu thực tế trong JSON (được mã hóa AES-256 với Salt ngẫu nhiên và PBKDF2)
    public string? AccountEmailProtected { get; set; }
    public string? FileIdProtected { get; set; }
    public string? WebUrlProtected { get; set; }

    // Các thuộc tính tự động giải mã / mã hóa trong suốt thông qua SecurityHelper
    [JsonIgnore]
    public string? AccountEmail
    {
        get => SecurityHelper.DecryptWithSalt(AccountEmailProtected);
        set => AccountEmailProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string FileId
    {
        get => SecurityHelper.DecryptWithSalt(FileIdProtected) ?? string.Empty;
        set => FileIdProtected = SecurityHelper.EncryptWithSalt(value);
    }

    [JsonIgnore]
    public string? WebUrl
    {
        get => SecurityHelper.DecryptWithSalt(WebUrlProtected);
        set => WebUrlProtected = SecurityHelper.EncryptWithSalt(value);
    }
}
