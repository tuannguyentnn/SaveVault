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
    public long? PackageSizeBytes { get; set; }
    public List<string> Changelog { get; set; } = new();

    public string FormattedChangelog => Changelog.Count > 0 
        ? string.Join(Environment.NewLine, Changelog) 
        : "Bản cập nhật tối ưu hóa hệ thống và sửa lỗi.";
}

/// <summary>
/// Tiến trình tải xuống gói cập nhật thời gian thực.
/// </summary>
public class UpdateDownloadProgress
{
    public int Percent { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public bool IsEstimatedTotal { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    public UpdateDownloadProgress(
        int percent, 
        long bytesDownloaded, 
        long totalBytes, 
        string statusMessage,
        bool isEstimatedTotal = false,
        double speedBytesPerSecond = 0)
    {
        Percent = percent;
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        StatusMessage = statusMessage;
        IsEstimatedTotal = isEstimatedTotal;
        SpeedBytesPerSecond = speedBytesPerSecond;
    }

    public string DownloadedSizeText
    {
        get
        {
            var downloadedMb = (BytesDownloaded / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            string speedText;
            if (SpeedBytesPerSecond >= 1024 * 1024)
            {
                speedText = $" • {(SpeedBytesPerSecond / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB/s";
            }
            else if (SpeedBytesPerSecond >= 1024)
            {
                speedText = $" • {(SpeedBytesPerSecond / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} KB/s";
            }
            else
            {
                speedText = "";
            }

            if (TotalBytes > 0)
            {
                var totalMb = (TotalBytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                var prefix = IsEstimatedTotal ? "~" : "";
                return $"{downloadedMb} MB / {prefix}{totalMb} MB{speedText}";
            }

            return $"{downloadedMb} MB{speedText}";
        }
    }
}

/// <summary>
/// Đại diện cho một bản ghi trong lịch sử changelog (từ changelogs.json).
/// </summary>
public class ChangelogItem
{
    public string Version { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public List<string> Changelog { get; set; } = new();
}

