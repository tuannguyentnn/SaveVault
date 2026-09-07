using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services.Cloud;

public class CloudUploadResult
{
    public bool Success { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? WebUrl { get; set; }
    public string? ErrorMessage { get; set; }
}

public class CloudFileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public string? WebUrl { get; set; }
}

public interface ICloudStorageService
{
    string ProviderName { get; }
    string DisplayName { get; }
    bool IsAuthenticated { get; }
    string? CurrentAccountEmail { get; }

    Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync();
    Task<string?> GetUserEmailAsync(CancellationToken cancellationToken = default);

    Task<CloudUploadResult> UploadFileAsync(
        string localFilePath,
        string remoteGameFolderName,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> DownloadFileAsync(
        string remoteFileId,
        string localDestinationPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteFileAsync(
        string remoteFileId,
        CancellationToken cancellationToken = default);

    Task<List<CloudFileInfo>> ListBackupsAsync(
        string? remoteGameFolderName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Quản lý kích thước khối tải lên (Chunk Size) tối ưu hóa theo quy chuẩn API của từng Cloud Provider.
/// </summary>
public static class CloudChunkOptimizer
{
    /// <summary>
    /// Google Drive yêu cầu mỗi chunk phải là bội số của 256 KiB (262,144 bytes).
    /// </summary>
    public const int GoogleDriveBlockUnit = 256 * 1024; // 256 KiB

    /// <summary>
    /// Microsoft Graph (OneDrive) yêu cầu mỗi chunk phải là bội số của 320 KiB (327,680 bytes) và không vượt quá 60 MiB.
    /// </summary>
    public const int OneDriveBlockUnit = 320 * 1024; // 320 KiB

    /// <summary>
    /// Tính toán kích thước chunk tối ưu theo dung lượng file và provider.
    /// </summary>
    /// <param name="totalBytes">Tổng dung lượng file (bytes)</param>
    /// <param name="provider">"GoogleDrive" hoặc "OneDrive"</param>
    /// <returns>Kích thước khối chunk chuẩn byte</returns>
    public static int CalculateChunkSize(long totalBytes, string provider)
    {
        bool isOneDrive = string.Equals(provider, "OneDrive", StringComparison.OrdinalIgnoreCase);
        int unit = isOneDrive ? OneDriveBlockUnit : GoogleDriveBlockUnit;

        // Bậc chunk:
        // - File >= 500 MB (1GB+): 96 units (~30.7 MB GDrive / ~31.45 MB OneDrive)
        // - File >= 100 MB:        64 units (~16.3 MB GDrive / ~20.97 MB OneDrive)
        // - File < 100 MB:         32 units (~8.1 MB GDrive / ~10.48 MB OneDrive)
        int multiplier = totalBytes switch
        {
            >= 500L * 1024 * 1024 => 96,
            >= 100L * 1024 * 1024 => 64,
            _ => 32
        };

        return unit * multiplier;
    }
}
