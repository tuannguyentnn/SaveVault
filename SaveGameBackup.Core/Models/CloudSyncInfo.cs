using System;

namespace SaveGameBackup.Core.Models;

/// <summary>
/// Đại diện cho thông tin đồng bộ của một bản sao lưu (snapshot) lên một nhà cung cấp Cloud cụ thể.
/// Cho phép 1 snapshot có thể đồng bộ lên nhiều Cloud khác nhau (Google Drive, OneDrive).
/// </summary>
public class CloudSyncInfo
{
    public string Provider { get; set; } = string.Empty; // "GoogleDrive", "OneDrive"
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime SyncDate { get; set; } = DateTime.Now;
    public string? WebViewUrl { get; set; }

    public string ProviderBadgeIcon => Provider.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ? "☁️ OneDrive" : "☁️ Google Drive";

    public string FormattedSyncDate => SyncDate.ToString("dd/MM/yyyy HH:mm");
}
