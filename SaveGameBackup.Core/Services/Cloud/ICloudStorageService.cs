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
