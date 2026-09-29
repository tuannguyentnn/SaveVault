using System;
using System.Globalization;

namespace SaveGameBackup.Core.Models;

public class CloudStorageQuota
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long? RemainingBytes { get; set; }

    public long FreeBytes => RemainingBytes.HasValue && RemainingBytes.Value > 0
        ? RemainingBytes.Value
        : Math.Max(0, TotalBytes - UsedBytes);

    public double UsedPercent
    {
        get
        {
            if (TotalBytes <= 0) return 0;
            // Tính toán % đã dùng dựa trên FreeBytes để đảm bảo thanh tiến trình luôn tương thích 100% với dung lượng còn trống
            double free = FreeBytes;
            double used = Math.Max(0, TotalBytes - free);
            return Math.Clamp(used / TotalBytes * 100, 0, 100);
        }
    }

    public double FreePercent => Math.Clamp(100.0 - UsedPercent, 0, 100.0);

    public string UsedPercentCss => Math.Clamp(UsedPercent, 2, 100).ToString("0.##", CultureInfo.InvariantCulture);

    public bool HasQuota => TotalBytes > 0 || UsedBytes > 0;

    public string FormattedTotal => TotalBytes > 0 ? FormatBytes(TotalBytes) : "Không giới hạn";
    public string FormattedUsed => FormatBytes(Math.Max(0, TotalBytes > 0 ? TotalBytes - FreeBytes : UsedBytes));
    public string FormattedFree => TotalBytes > 0 ? FormatBytes(FreeBytes) : "Không giới hạn";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        double gb = mb / 1024.0;
        if (gb < 1024) return gb.ToString("F1", CultureInfo.InvariantCulture) + " GB";
        double tb = gb / 1024.0;
        return tb.ToString("F2", CultureInfo.InvariantCulture) + " TB";
    }
}
