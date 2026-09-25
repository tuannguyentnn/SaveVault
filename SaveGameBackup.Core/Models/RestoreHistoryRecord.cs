namespace SaveGameBackup.Core.Models;

/// <summary>
/// Đại diện cho một bản ghi lịch sử khôi phục (Restore History) trong cơ sở dữ liệu.
/// </summary>
public class RestoreHistoryRecord
{
    public long Id { get; set; }
    public string GameName { get; set; } = string.Empty;
    public long? BackupHistoryDetailId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public DateTime RestoreDate { get; set; } = DateTime.Now;

    /// <summary>
    /// Trạng thái khôi phục:
    /// 'Success': Khôi phục thành công.
    /// 'Failed': Khôi phục thất bại (không có revert point).
    /// 'Failed_Reverted': Khôi phục thất bại, nhưng đã hoàn tác thành công dữ liệu cũ về ban đầu.
    /// 'Failed_RevertFailed': Khôi phục thất bại và hoàn tác cũng thất bại.
    /// </summary>
    public string Status { get; set; } = "Success";

    /// <summary>
    /// Đường dẫn file ZIP hoàn tác an toàn (được lưu lại nếu restore thành công hoặc khi hoàn tác gặp lỗi).
    /// </summary>
    public string? RevertZipPath { get; set; }

    /// <summary>
    /// Danh sách đường dẫn thư mục đích được chọn để khôi phục (định dạng JSON).
    /// </summary>
    public string? RestoredPathsJson { get; set; }

    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsCloud { get; set; }
    public string? CloudProvider { get; set; }

    public string FormattedDate => RestoreDate.ToString("dd/MM/yyyy HH:mm:ss");
    public string FormattedSize => FormatSize(TotalSizeBytes);

    public string StatusBadgeText => Status switch
    {
        "Success" => "Thành công",
        "Failed_Reverted" => "Thất bại (Đã hoàn tác an toàn)",
        "Failed_RevertFailed" => "Sự cố hoàn tác",
        _ => "Thất bại"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
