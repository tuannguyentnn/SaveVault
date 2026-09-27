using System;
using System.Collections.Generic;

namespace SaveGameBackup.Core.Models;

/// <summary>
/// Chứa thông tin về phiên bản cập nhật được phân phối từ repository.
/// </summary>
public class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string MinSupportedVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public List<string> Changelog { get; set; } = new();
    public long PackageSizeBytes { get; set; }

    public string FormattedChangelog => Changelog.Count > 0 
        ? string.Join(Environment.NewLine, Changelog) 
        : "Bản cập nhật tối ưu hóa hệ thống và sửa lỗi.";
}

/// <summary>
/// Tiến trình tải xuống gói cập nhật.
/// </summary>
public class UpdateDownloadProgress
{
    public int Percent { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    public UpdateDownloadProgress(int percent, long bytesDownloaded, long totalBytes, string statusMessage)
    {
        Percent = percent;
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        StatusMessage = statusMessage;
    }

    public string DownloadedSizeText => TotalBytes > 0
        ? $"{(double)BytesDownloaded / (1024 * 1024):0.1} MB / {(double)TotalBytes / (1024 * 1024):0.1} MB"
        : $"{(double)BytesDownloaded / (1024 * 1024):0.1} MB";
}
