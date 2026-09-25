using System.IO.Compression;
using System.Text.Json;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services.Cloud;

namespace SaveGameBackup.Core.Services;

public class BackupProgress
{
    public int Percent { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long ProcessedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int ProcessedItems { get; set; }
    public int TotalItems { get; set; }
    public string SpeedText { get; set; } = string.Empty;
}

public class BackupService
{
    private const string ManifestFileName = "backup_manifest.json";
    private readonly DatabaseService _databaseService;

    public BackupService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Game_Backup" : sanitized;
    }

    public Task<BackupRecord> BackupGameAsync(
        GameSaveInfo gameInfo,
        AppSettings settings,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return BackupGameAsync(gameInfo, settings, null, progress, cancellationToken);
    }

    public async Task<BackupRecord> BackupGameAsync(
        GameSaveInfo gameInfo,
        AppSettings settings,
        List<string>? selectedPaths,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? onlineCoverUrl = null)
    {
        var pathsToBackup = selectedPaths != null && selectedPaths.Count > 0
            ? selectedPaths
            : gameInfo.DetectedPathItems.Where(p => p.IsSelected).Select(p => p.Path).ToList();

        if (pathsToBackup.Count == 0)
        {
            pathsToBackup = gameInfo.DetectedPathsOnDisk;
        }

        if (pathsToBackup.Count == 0)
        {
            throw new InvalidOperationException($"Vui lòng chọn ít nhất 1 thư mục save của game '{gameInfo.GameName}' để sao lưu!");
        }

        var sanitizedName = SanitizeFolderName(gameInfo.GameName);
        var backupDateTime = DateTime.Now;
        var timestamp = backupDateTime.ToString("yyyy-MM-dd_HH-mm-ss");

        var rootBackupDir = string.IsNullOrWhiteSpace(settings.BackupRootDirectory)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups")
            : settings.BackupRootDirectory;

        Directory.CreateDirectory(rootBackupDir);

        // Bắt buộc nén zip 100%: Mọi bản sao lưu đều nạp dữ liệu vào thư mục tạm stage trước khi nén thành zip
        string targetFolder = Path.Combine(Path.GetTempPath(), "SaveBackup_Stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetFolder);

        int totalCopiedFiles = 0;
        long totalCopiedBytes = 0;

        // Tính trước tổng số file và dung lượng để báo % chính xác tuyệt đối
        int totalSourceFiles = 0;
        long totalSourceBytes = 0;
        foreach (var p in pathsToBackup)
        {
            if (Directory.Exists(p))
            {
                var files = Directory.GetFiles(p, "*", SearchOption.AllDirectories);
                totalSourceFiles += files.Length;
                foreach (var f in files)
                {
                    try { totalSourceBytes += new FileInfo(f).Length; } catch { }
                }
            }
            else if (File.Exists(p))
            {
                totalSourceFiles++;
                try { totalSourceBytes += new FileInfo(p).Length; } catch { }
            }
        }

        var manifest = new BackupManifest
        {
            GameName = gameInfo.GameName,
            BackupDate = backupDateTime
        };

        int copyMaxPercent = 50;
        progress?.Report(new BackupProgress 
        { 
            Percent = 5, 
            TotalBytes = totalSourceBytes,
            TotalItems = totalSourceFiles,
            Message = "Chuẩn bị sao lưu các vị trí đã chọn..." 
        });

        for (int i = 0; i < pathsToBackup.Count; i++)
        {
            var src = pathsToBackup[i];
            int itemFiles = 0;
            long itemBytes = 0;

            string subFolder = pathsToBackup.Count > 1
                ? $"{i + 1}_{SanitizeFolderName(Path.GetFileName(src.TrimEnd('\\', '/')))}"
                : string.Empty;

            var destSubDir = string.IsNullOrEmpty(subFolder)
                ? targetFolder
                : Path.Combine(targetFolder, subFolder);

            Directory.CreateDirectory(destSubDir);

            if (Directory.Exists(src))
            {
                var allFiles = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
                for (int fIndex = 0; fIndex < allFiles.Length; fIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = allFiles[fIndex];
                    var relative = Path.GetRelativePath(src, file);
                    var destFilePath = Path.Combine(destSubDir, relative);

                    var fileDir = Path.GetDirectoryName(destFilePath);
                    if (!string.IsNullOrEmpty(fileDir)) Directory.CreateDirectory(fileDir);

                    File.Copy(file, destFilePath, true);

                    var fInfo = new FileInfo(file);
                    totalCopiedFiles++;
                    totalCopiedBytes += fInfo.Length;
                    itemFiles++;
                    itemBytes += fInfo.Length;

                    var pct = totalSourceBytes > 0
                        ? 5 + (int)((double)totalCopiedBytes / totalSourceBytes * (copyMaxPercent - 5))
                        : 5 + (int)((double)totalCopiedFiles / Math.Max(1, totalSourceFiles) * (copyMaxPercent - 5));

                    progress?.Report(new BackupProgress
                    {
                        Percent = Math.Clamp(pct, 5, copyMaxPercent),
                        CurrentFile = relative,
                        ProcessedBytes = totalCopiedBytes,
                        TotalBytes = totalSourceBytes,
                        ProcessedItems = totalCopiedFiles,
                        TotalItems = totalSourceFiles,
                        Message = $"Đang sao lưu ({totalCopiedFiles}/{totalSourceFiles}): {Path.GetFileName(file)}"
                    });
                }
            }
            else if (File.Exists(src))
            {
                var fileName = Path.GetFileName(src);
                var destFilePath = Path.Combine(destSubDir, fileName);
                File.Copy(src, destFilePath, true);

                var fInfo = new FileInfo(src);
                totalCopiedFiles++;
                totalCopiedBytes += fInfo.Length;
                itemFiles++;
                itemBytes += fInfo.Length;

                progress?.Report(new BackupProgress
                {
                    Percent = copyMaxPercent,
                    CurrentFile = fileName,
                    ProcessedBytes = totalCopiedBytes,
                    TotalBytes = totalSourceBytes,
                    ProcessedItems = totalCopiedFiles,
                    TotalItems = totalSourceFiles,
                    Message = $"Đang sao lưu file: {fileName}"
                });
            }

            manifest.Items.Add(new BackupManifestItem
            {
                SubFolder = subFolder,
                SourcePath = src,
                FileCount = itemFiles,
                TotalSizeBytes = itemBytes
            });
        }

        // Serialize manifest to JSON string for 100% database storage (KHÔNG ghi file manifest ra đĩa hay vào zip)
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

        string finalBackupPath;

        // Auto compress to Zip if enabled (nén với tiến trình % chính xác từ 50% -> 95%)
        var zipDir = Path.Combine(rootBackupDir, sanitizedName);
        Directory.CreateDirectory(zipDir);
        var zipFilePath = Path.Combine(zipDir, $"{sanitizedName}_{timestamp}.zip");

        await CreateZipFromDirectoryWithProgressAsync(
            targetFolder, 
            zipFilePath, 
            progress, 
            startPercent: 50, 
            endPercent: 92, 
            cancellationToken);

        // Clean up staging/temp uncompressed folder
        try { Directory.Delete(targetFolder, true); } catch { /* Ignore */ }

        finalBackupPath = zipFilePath;

        // Chuẩn hóa thuộc tính thời gian file zip trên ổ đĩa trùng khớp từng giây với backupDateTime
        try
        {
            File.SetLastWriteTime(finalBackupPath, backupDateTime);
            File.SetCreationTime(finalBackupPath, backupDateTime);
        }
        catch { /* Ignore */ }

        var sourcePathRecord = pathsToBackup.Count == 1
            ? pathsToBackup[0]
            : string.Join(" | ", pathsToBackup);

        // Báo tiến trình tải và chuẩn hóa ảnh bìa game (width = 450px)
        progress?.Report(new BackupProgress 
        { 
            Percent = 94, 
            TotalBytes = totalCopiedBytes,
            TotalItems = totalCopiedFiles,
            Message = "Đang tải và tối ưu ảnh bìa game..." 
        });

        string? coverPath = null;
        try
        {
            var effectiveOnlineCoverUrl = onlineCoverUrl ?? gameInfo.OnlineCoverUrl;
            coverPath = await GameCoverService.DownloadAndProcessCoverAsync(
                gameInfo.GameName, 
                effectiveOnlineCoverUrl, 
                gameInfo.SteamAppId, 
                cancellationToken);
        }
        catch { /* Bỏ qua nếu lỗi mạng để không chặn quá trình sao lưu */ }

        if (!string.IsNullOrEmpty(coverPath) && !GameCoverService.IsValidImageFile(coverPath))
        {
            coverPath = null;
        }

        progress?.Report(new BackupProgress 
        { 
            Percent = 98, 
            TotalBytes = totalCopiedBytes,
            TotalItems = totalCopiedFiles,
            Message = "Đang lưu thông tin lịch sử sao lưu..." 
        });

        // Lưu vào bảng backup_history (Master) và backup_history_details (Detail) với ManifestJson
        var detail = new BackupHistoryDetail
        {
            GameName = gameInfo.GameName,
            BackupPath = new BackupPathLocations { LocalPath = finalBackupPath }.ToJson(),
            SourcePath = sourcePathRecord,
            SavePaths = JsonSerializer.Serialize(pathsToBackup),
            ManifestJson = manifestJson,
            FileCount = totalCopiedFiles,
            TotalSizeBytes = totalCopiedBytes,
            BackupDate = backupDateTime,
            IsCompressed = true,
            Status = "Success",
            Note = $"Đã backup {totalCopiedFiles} file từ {pathsToBackup.Count} vị trí lưu.",
            CoverUrl = coverPath,
            CoverPath = coverPath
        };

        var detailId = await _databaseService.InsertOrUpdateBackupHistoryAsync(detail);
        detail.Id = detailId;

        // Trả về BackupRecord cho tương thích giao diện và test
        var record = new BackupRecord
        {
            Id = detailId,
            GameName = gameInfo.GameName,
            BackupPath = finalBackupPath,
            SourcePath = sourcePathRecord,
            SavePaths = JsonSerializer.Serialize(pathsToBackup),
            FileCount = totalCopiedFiles,
            TotalSizeBytes = totalCopiedBytes,
            BackupDate = backupDateTime,
            IsCompressed = true,
            Status = "Success",
            Note = detail.Note,
            CoverPath = coverPath
        };

        // CHỈ THÊM VÀO CACHE KHI BACKUP LẦN ĐẦU TIÊN (nếu chưa từng có trong cache)
        try
        {
            var existingCache = await _databaseService.GetCachedGameAsync(gameInfo.GameName);
            if (existingCache == null)
            {
                if (!string.IsNullOrEmpty(coverPath) && string.IsNullOrEmpty(gameInfo.OnlineCoverUrl))
                {
                    gameInfo.OnlineCoverUrl = coverPath;
                }
                if (pathsToBackup.Count > 0 && gameInfo.RawPatterns.Count == 0)
                {
                    gameInfo.RawPatterns = new List<string>(pathsToBackup);
                    gameInfo.ResolvedPaths = new List<string>(pathsToBackup);
                }
                await _databaseService.SaveGameCacheAsync(gameInfo);
                LoggingService.LogAction("Game_Cached_On_First_Backup_Success", new { Game = gameInfo.GameName });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi lưu cache game lần đầu sau khi backup thành công cho {Game}: {Message}", gameInfo.GameName, ex.Message);
        }

        progress?.Report(new BackupProgress { Percent = 100, Message = "Sao lưu hoàn tất thành công!" });

        return record;
    }

    public async Task<CloudUploadResult> SyncSnapshotToCloudAsync(
        BackupHistoryDetail detail,
        ICloudStorageService cloudService,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (detail == null) throw new ArgumentNullException(nameof(detail));
        if (cloudService == null) throw new ArgumentNullException(nameof(cloudService));

        string fileToUpload = detail.LocalBackupPath;
        string? tempZipToCleanup = null;

        if (!detail.IsCompressed && Directory.Exists(fileToUpload))
        {
            progress?.Report(new BackupProgress { Percent = 5, Message = "Đang đóng gói file zip để đồng bộ Cloud..." });
            tempZipToCleanup = Path.Combine(Path.GetTempPath(), $"{SanitizeFolderName(detail.GameName)}_{detail.BackupDate:yyyy-MM-dd_HH-mm-ss}.zip");
            await CreateZipFromDirectoryWithProgressAsync(fileToUpload, tempZipToCleanup, progress, 5, 25, cancellationToken);
            fileToUpload = tempZipToCleanup;
        }

        if (!File.Exists(fileToUpload))
        {
            throw new FileNotFoundException($"Không tìm thấy file sao lưu trên ổ đĩa để đồng bộ: {fileToUpload}");
        }

        try
        {
            var remoteGameFolder = SanitizeFolderName(detail.GameName);
            var uploadResult = await cloudService.UploadFileAsync(fileToUpload, remoteGameFolder, progress, cancellationToken);

            if (uploadResult.Success)
            {
                var syncDate = DateTime.UtcNow;

                var webUrl = uploadResult.WebUrl;
                if (string.IsNullOrEmpty(webUrl) && !string.IsNullOrEmpty(uploadResult.FileId))
                {
                    webUrl = cloudService.ProviderName.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                        ? $"https://onedrive.live.com/?id={Uri.EscapeDataString(uploadResult.FileId)}"
                        : $"https://drive.google.com/file/d/{Uri.EscapeDataString(uploadResult.FileId)}/view";
                }

                // Cập nhật hoặc thêm mới provider vào CloudSyncList
                var existing = detail.CloudSyncList.FirstOrDefault(c => c.Provider.Equals(cloudService.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.FileId = uploadResult.FileId;
                    existing.FileName = uploadResult.FileName;
                    existing.SyncDate = syncDate;
                    existing.WebViewUrl = webUrl;
                }
                else
                {
                    detail.CloudSyncList.Add(new CloudSyncInfo
                    {
                        Provider = cloudService.ProviderName,
                        FileId = uploadResult.FileId,
                        FileName = uploadResult.FileName,
                        SyncDate = syncDate,
                        WebViewUrl = webUrl
                    });
                }

                if (!string.IsNullOrEmpty(webUrl))
                {
                    detail.SetCloudUrl(cloudService.ProviderName, webUrl);
                }

                detail.CloudSyncJson = JsonSerializer.Serialize(detail.CloudSyncList);
                detail.IsCloudSynced = true;
                detail.CloudProvider = string.Join(", ", detail.CloudSyncList.Select(c => c.Provider));
                detail.CloudFileId = uploadResult.FileId;
                detail.CloudFileName = uploadResult.FileName;
                detail.CloudSyncDate = syncDate;

                await _databaseService.UpdateCloudSyncDetailAsync(
                    detail.Id,
                    true,
                    detail.CloudProvider,
                    detail.CloudFileId,
                    detail.CloudFileName,
                    syncDate,
                    detail.CloudSyncJson,
                    detail.BackupPath);
            }

            return uploadResult;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempZipToCleanup) && File.Exists(tempZipToCleanup))
            {
                try { File.Delete(tempZipToCleanup); } catch { }
            }
        }
    }

    public List<RestoreItemTarget> GetRestoreItemsFromBackup(BackupRecord record)
    {
        var result = new List<RestoreItemTarget>();

        // 1. Try manifest from ZIP or Directory
        if (record.IsCompressed && File.Exists(record.BackupPath))
        {
            try
            {
                using var archive = ZipFile.OpenRead(record.BackupPath);
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));

                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                    if (manifest != null && manifest.Items.Count > 0)
                    {
                        var recordPaths = record.SourcePathsList;
                        for (int i = 0; i < manifest.Items.Count; i++)
                        {
                            var item = manifest.Items[i];
                            string destPath;
                            if (manifest.Items.Count == 1 && !string.IsNullOrWhiteSpace(record.SourcePath))
                            {
                                destPath = record.SourcePath;
                            }
                            else if (recordPaths.Count > i && !string.IsNullOrWhiteSpace(recordPaths[i]))
                            {
                                destPath = recordPaths[i];
                            }
                            else
                            {
                                destPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : record.SourcePath;
                            }

                            var origPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : destPath;

                            result.Add(new RestoreItemTarget
                            {
                                IsSelected = true,
                                OriginalSourcePath = origPath,
                                RestoreDestinationPath = destPath,
                                SubFolder = item.SubFolder,
                                FileCount = item.FileCount,
                                TotalSizeBytes = item.TotalSizeBytes
                            });
                        }
                        return result;
                    }
                }
            }
            catch { /* fallback below */ }
        }
        else if (Directory.Exists(record.BackupPath))
        {
            var manifestPath = Path.Combine(record.BackupPath, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                    if (manifest != null && manifest.Items.Count > 0)
                    {
                        var recordPaths = record.SourcePathsList;
                        for (int i = 0; i < manifest.Items.Count; i++)
                        {
                            var item = manifest.Items[i];
                            string destPath;
                            if (manifest.Items.Count == 1 && !string.IsNullOrWhiteSpace(record.SourcePath))
                            {
                                destPath = record.SourcePath;
                            }
                            else if (recordPaths.Count > i && !string.IsNullOrWhiteSpace(recordPaths[i]))
                            {
                                destPath = recordPaths[i];
                            }
                            else
                            {
                                destPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : record.SourcePath;
                            }

                            var origPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : destPath;

                            result.Add(new RestoreItemTarget
                            {
                                IsSelected = true,
                                OriginalSourcePath = origPath,
                                RestoreDestinationPath = destPath,
                                SubFolder = item.SubFolder,
                                FileCount = item.FileCount,
                                TotalSizeBytes = item.TotalSizeBytes
                            });
                        }
                        return result;
                    }
                }
                catch { /* fallback below */ }
            }
        }

        // 2. Fallback to SourcePathsList or SourcePath
        var paths = record.SourcePathsList;
        if (paths.Count > 0)
        {
            int avgFiles = Math.Max(1, record.FileCount / paths.Count);
            long avgBytes = Math.Max(0, record.TotalSizeBytes / paths.Count);

            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i];
                string subFolder = paths.Count > 1
                    ? $"{i + 1}_{SanitizeFolderName(Path.GetFileName(p.TrimEnd('\\', '/')))}"
                    : string.Empty;

                result.Add(new RestoreItemTarget
                {
                    IsSelected = true,
                    OriginalSourcePath = p,
                    RestoreDestinationPath = p,
                    SubFolder = subFolder,
                    FileCount = avgFiles,
                    TotalSizeBytes = avgBytes
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(record.SourcePath))
        {
            result.Add(new RestoreItemTarget
            {
                IsSelected = true,
                OriginalSourcePath = record.SourcePath,
                RestoreDestinationPath = record.SourcePath,
                SubFolder = string.Empty,
                FileCount = record.FileCount,
                TotalSizeBytes = record.TotalSizeBytes
            });
        }

        return result;
    }

    public List<RestoreItemTarget> GetRestoreItemsFromBackup(BackupHistoryDetail detail)
    {
        var result = new List<RestoreItemTarget>();

        // 1. Phục hồi trực tiếp từ ManifestJson trong SQLite database (KHÔNG đọc ổ đĩa hay tệp zip)
        if (!string.IsNullOrWhiteSpace(detail.ManifestJson))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<BackupManifest>(detail.ManifestJson);
                if (manifest?.Items != null && manifest.Items.Count > 0)
                {
                    var detailPaths = detail.SavePathsList;
                    for (int i = 0; i < manifest.Items.Count; i++)
                    {
                        var item = manifest.Items[i];
                        string destPath;
                        if (manifest.Items.Count == 1 && !string.IsNullOrWhiteSpace(detail.SourcePath))
                        {
                            destPath = detail.SourcePath;
                        }
                        else if (detailPaths.Count > i && !string.IsNullOrWhiteSpace(detailPaths[i]))
                        {
                            destPath = detailPaths[i];
                        }
                        else
                        {
                            destPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : detail.SourcePath;
                        }

                        var origPath = !string.IsNullOrWhiteSpace(item.SourcePath) ? item.SourcePath : destPath;

                        result.Add(new RestoreItemTarget
                        {
                            IsSelected = true,
                            OriginalSourcePath = origPath,
                            RestoreDestinationPath = destPath,
                            SubFolder = item.SubFolder,
                            FileCount = item.FileCount,
                            TotalSizeBytes = item.TotalSizeBytes
                        });
                    }
                    return result;
                }
            }
            catch { /* fallback below */ }
        }

        // 2. Fallback sang SavePathsList
        var paths = detail.SavePathsList;
        if (paths.Count > 0)
        {
            int avgFiles = Math.Max(1, detail.FileCount / paths.Count);
            long avgBytes = Math.Max(0, detail.TotalSizeBytes / paths.Count);

            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i];
                string subFolder = paths.Count > 1
                    ? $"{i + 1}_{SanitizeFolderName(Path.GetFileName(p.TrimEnd('\\', '/')))}"
                    : string.Empty;

                result.Add(new RestoreItemTarget
                {
                    IsSelected = true,
                    OriginalSourcePath = p,
                    RestoreDestinationPath = p,
                    SubFolder = subFolder,
                    FileCount = avgFiles,
                    TotalSizeBytes = avgBytes
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(detail.SourcePath))
        {
            result.Add(new RestoreItemTarget
            {
                IsSelected = true,
                OriginalSourcePath = detail.SourcePath,
                RestoreDestinationPath = detail.SourcePath,
                SubFolder = string.Empty,
                FileCount = detail.FileCount,
                TotalSizeBytes = detail.TotalSizeBytes
            });
        }

        return result;
    }

    public Task RestoreAsync(
        BackupHistoryDetail detail, 
        IProgress<BackupProgress>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        var items = GetRestoreItemsFromBackup(detail);
        return RestoreAsync(detail, items, restoreFromCloud: false, cloudService: null, progress, cancellationToken);
    }

    public Task RestoreAsync(
        BackupHistoryDetail detail,
        List<RestoreItemTarget> itemsToRestore,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RestoreAsync(detail, itemsToRestore, restoreFromCloud: false, cloudService: null, progress, cancellationToken);
    }

    public async Task RestoreAsync(
        BackupHistoryDetail detail,
        List<RestoreItemTarget> itemsToRestore,
        bool restoreFromCloud,
        ICloudStorageService? cloudService = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selectedItems = itemsToRestore
            .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.RestoreDestinationPath))
            .ToList();

        if (selectedItems.Count == 0)
        {
            throw new InvalidOperationException("Không có vị trí lưu nào được chọn để khôi phục!");
        }

        await ExecuteRestoreWithAutoRevertAsync(
            gameName: detail.GameName,
            detailId: detail.Id,
            sourcePath: detail.LocalBackupPath,
            fileCount: detail.FileCount,
            totalSizeBytes: detail.TotalSizeBytes,
            isCloud: restoreFromCloud,
            cloudProvider: cloudService?.DisplayName ?? detail.CloudProvider,
            selectedItems: selectedItems,
            restoreAction: async (innerProgress, ct) =>
            {
                string zipFilePath = detail.LocalBackupPath;
                string? tempDownloadedZip = null;

                try
                {
                    if (restoreFromCloud)
                    {
                        if (cloudService == null)
                        {
                            throw new InvalidOperationException("Chưa cấu hình dịch vụ lưu trữ đám mây để tải bản sao lưu!");
                        }
                        var syncInfo = detail.CloudSyncList.FirstOrDefault(c => c.Provider.Equals(cloudService.ProviderName, StringComparison.OrdinalIgnoreCase));
                        if (syncInfo == null && !string.IsNullOrEmpty(detail.CloudSyncJson))
                        {
                            try
                            {
                                var parsed = JsonSerializer.Deserialize<List<CloudSyncInfo>>(detail.CloudSyncJson);
                                syncInfo = parsed?.FirstOrDefault(c => c.Provider.Equals(cloudService.ProviderName, StringComparison.OrdinalIgnoreCase));
                            }
                            catch { }
                        }
                        var cloudFileId = syncInfo?.FileId ?? detail.CloudFileId;
                        if (string.IsNullOrEmpty(cloudFileId))
                        {
                            throw new InvalidOperationException($"Bản sao lưu này chưa có mã tệp trên {cloudService.DisplayName}!");
                        }

                        tempDownloadedZip = Path.Combine(Path.GetTempPath(), $"SaveVault_CloudDl_{Guid.NewGuid():N}.zip");
                        innerProgress?.Report(new BackupProgress { Percent = 0, Message = $"Bắt đầu tải từ {cloudService.DisplayName}..." });

                        var dlProgress = new Progress<BackupProgress>(p =>
                        {
                            innerProgress?.Report(new BackupProgress
                            {
                                Percent = (int)(p.Percent * 0.5), // 0% -> 50%
                                CurrentFile = p.CurrentFile,
                                ProcessedBytes = p.ProcessedBytes,
                                TotalBytes = p.TotalBytes,
                                SpeedText = p.SpeedText,
                                Message = $"[1/2] {p.Message}"
                            });
                        });

                        await cloudService.DownloadFileAsync(cloudFileId, tempDownloadedZip, dlProgress, ct);
                        zipFilePath = tempDownloadedZip;
                    }

                    if (detail.IsCompressed || restoreFromCloud)
                    {
                        if (!File.Exists(zipFilePath))
                            throw new FileNotFoundException($"Không tìm thấy file zip backup: {zipFilePath}");

                        int extractStart = restoreFromCloud ? 50 : 10;
                        int extractEnd = restoreFromCloud ? 75 : 40;

                        innerProgress?.Report(new BackupProgress { Percent = extractStart, Message = "Đang chuẩn bị giải nén dữ liệu sao lưu..." });

                        var tempExtractDir = Path.Combine(Path.GetTempPath(), "SaveBackup_Restore_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempExtractDir);

                        try
                        {
                            await ExtractZipWithProgressAsync(zipFilePath, tempExtractDir, innerProgress, extractStart, extractEnd, ct);

                            var restoreProgress = new Progress<BackupProgress>(p =>
                            {
                                var start = extractEnd;
                                var span = 99 - start;
                                var scaled = start + (int)(p.Percent / 100.0 * span);
                                innerProgress?.Report(new BackupProgress
                                {
                                    Percent = Math.Clamp(scaled, start, 99),
                                    CurrentFile = p.CurrentFile,
                                    ProcessedBytes = p.ProcessedBytes,
                                    TotalBytes = p.TotalBytes,
                                    Message = restoreFromCloud ? $"[2/2] {p.Message}" : p.Message
                                });
                            });

                            await RestoreSelectedFromDirectoryAsync(tempExtractDir, selectedItems, restoreProgress, ct);
                        }
                        finally
                        {
                            try { Directory.Delete(tempExtractDir, true); } catch { /* Ignore */ }
                        }
                    }
                    else
                    {
                        var localDirPath = detail.LocalBackupPath;
                        if (!Directory.Exists(localDirPath))
                            throw new DirectoryNotFoundException($"Không tìm thấy thư mục backup: {localDirPath}");

                        await RestoreSelectedFromDirectoryAsync(localDirPath, selectedItems, innerProgress, ct);
                    }
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempDownloadedZip) && File.Exists(tempDownloadedZip))
                    {
                        try { File.Delete(tempDownloadedZip); } catch { /* Ignore */ }
                    }
                }
            },
            progress: progress,
            cancellationToken: cancellationToken);
    }

    public Task RestoreAsync(BackupRecord record, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = GetRestoreItemsFromBackup(record);
        return RestoreAsync(record, items, progress, cancellationToken);
    }

    public async Task RestoreAsync(
        BackupRecord record,
        List<RestoreItemTarget> itemsToRestore,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selectedItems = itemsToRestore
            .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.RestoreDestinationPath))
            .ToList();

        if (selectedItems.Count == 0)
        {
            throw new InvalidOperationException("Không có vị trí lưu nào được chọn để khôi phục!");
        }

        await ExecuteRestoreWithAutoRevertAsync(
            gameName: record.GameName,
            detailId: record.Id > 0 ? record.Id : null,
            sourcePath: record.BackupPath,
            fileCount: record.FileCount,
            totalSizeBytes: record.TotalSizeBytes,
            isCloud: false,
            cloudProvider: null,
            selectedItems: selectedItems,
            restoreAction: async (innerProgress, ct) =>
            {
                if (record.IsCompressed)
                {
                    if (!File.Exists(record.BackupPath))
                        throw new FileNotFoundException($"Không tìm thấy file zip backup: {record.BackupPath}");

                    innerProgress?.Report(new BackupProgress { Percent = 10, Message = "Đang giải nén dữ liệu sao lưu..." });

                    var tempExtractDir = Path.Combine(Path.GetTempPath(), "SaveBackup_Restore_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempExtractDir);

                    try
                    {
                        await ExtractZipWithProgressAsync(record.BackupPath, tempExtractDir, innerProgress, 10, 40, ct);

                        var restoreProgress = new Progress<BackupProgress>(p =>
                        {
                            var scaled = 40 + (int)(p.Percent / 100.0 * 59);
                            innerProgress?.Report(new BackupProgress
                            {
                                Percent = Math.Clamp(scaled, 40, 99),
                                CurrentFile = p.CurrentFile,
                                Message = p.Message
                            });
                        });

                        await RestoreSelectedFromDirectoryAsync(tempExtractDir, selectedItems, restoreProgress, ct);
                    }
                    finally
                    {
                        try { Directory.Delete(tempExtractDir, true); } catch { /* Ignore */ }
                    }
                }
                else
                {
                    if (!Directory.Exists(record.BackupPath))
                        throw new DirectoryNotFoundException($"Không tìm thấy thư mục backup: {record.BackupPath}");

                    await RestoreSelectedFromDirectoryAsync(record.BackupPath, selectedItems, innerProgress, ct);
                }
            },
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Thực hiện chu trình Restore an toàn:
    /// 1/ Nén zip folder save gốc hiện có làm điểm hoàn tác (Revert Point).
    /// 2/ Restore lại save từ backup.
    /// 2.1/ Thành công: Ghi nhận restore_history (Status = Success, RevertZipPath = revertZip) và hoàn tất.
    /// 2.2/ Thất bại: Sử dụng file revert đã nén để tự động phục hồi lại như ban đầu.
    /// 2.2.1/ Nếu restore revert cũng bị lỗi: Ngưng toàn bộ, giữ file revert và báo lỗi.
    /// 2.2.2/ Nếu restore revert thành công: Xóa file revert đó đi, ghi nhận restore_history và báo lỗi an toàn.
    /// </summary>
    private async Task ExecuteRestoreWithAutoRevertAsync(
        string gameName,
        long? detailId,
        string sourcePath,
        int fileCount,
        long totalSizeBytes,
        bool isCloud,
        string? cloudProvider,
        List<RestoreItemTarget> selectedItems,
        Func<IProgress<BackupProgress>?, CancellationToken, Task> restoreAction,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 1/ Nén zip folder save gốc hiện có trên máy làm điểm hoàn tác an toàn trước khi chép đè
        string? revertZip = null;
        try
        {
            revertZip = await RevertService.CreateRevertPointAsync(gameName, selectedItems, progress, cancellationToken);
            if (!string.IsNullOrEmpty(revertZip))
            {
                LoggingService.LogAction("Safety_Revert_Created", new { Game = gameName, Path = revertZip });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể tạo điểm hoàn tác an toàn trước khi Restore cho {Game}: {Message}", gameName, ex.Message);
        }

        // 2/ Restore lại save từ backup
        bool restoreSucceeded = false;
        Exception? restoreException = null;

        try
        {
            await restoreAction(progress, cancellationToken);
            restoreSucceeded = true;
        }
        catch (Exception ex)
        {
            restoreException = ex;
        }

        // 2.1/ Thành công thì oke xong
        if (restoreSucceeded)
        {
            try
            {
                var record = new RestoreHistoryRecord
                {
                    GameName = gameName,
                    BackupHistoryDetailId = detailId,
                    SourcePath = sourcePath,
                    RestoreDate = DateTime.Now,
                    Status = "Success",
                    RevertZipPath = revertZip,
                    RestoredPathsJson = JsonSerializer.Serialize(selectedItems.Select(x => x.RestoreDestinationPath)),
                    FileCount = fileCount,
                    TotalSizeBytes = totalSizeBytes,
                    IsCloud = isCloud,
                    CloudProvider = cloudProvider
                };
                await _databaseService.InsertRestoreHistoryAsync(record);
            }
            catch (Exception dbEx)
            {
                LoggingService.Warn("Không thể ghi lịch sử restore_history cho {Game}: {Message}", gameName, dbEx.Message);
            }

            progress?.Report(new BackupProgress { Percent = 100, Message = "Khôi phục hoàn tất!" });
            return;
        }

        // 2.2/ Thất bại: dùng lại file revert đã nén đó, restore lại như ban đầu
        if (!string.IsNullOrEmpty(revertZip) && File.Exists(revertZip))
        {
            bool revertSucceeded = false;
            Exception? revertException = null;

            try
            {
                progress?.Report(new BackupProgress { Percent = 50, Message = "Khôi phục gặp sự cố! Đang tự động hoàn tác về trạng thái ban đầu..." });
                revertSucceeded = await RevertService.RevertGameSaveAsync(gameName, revertZip, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                revertException = ex;
            }

            if (!revertSucceeded || revertException != null)
            {
                // 2.2.1/ Nếu restore revert cũng bị lỗi thì ngưng toàn bộ và báo lỗi
                var errorMsg = $"Khôi phục thất bại ({restoreException!.Message}) và hoàn tác tự động cũng gặp lỗi ({revertException?.Message ?? "Không rõ nguyên nhân"}). File hoàn tác an toàn được giữ tại: {revertZip}";

                try
                {
                    var historyRecord = new RestoreHistoryRecord
                    {
                        GameName = gameName,
                        BackupHistoryDetailId = detailId,
                        SourcePath = sourcePath,
                        RestoreDate = DateTime.Now,
                        Status = "Failed_RevertFailed",
                        RevertZipPath = revertZip,
                        RestoredPathsJson = JsonSerializer.Serialize(selectedItems.Select(x => x.RestoreDestinationPath)),
                        FileCount = fileCount,
                        TotalSizeBytes = totalSizeBytes,
                        ErrorMessage = errorMsg,
                        IsCloud = isCloud,
                        CloudProvider = cloudProvider
                    };
                    await _databaseService.InsertRestoreHistoryAsync(historyRecord);
                }
                catch { }

                LoggingService.Error(revertException ?? restoreException, "Restore and Revert both failed for {Game}. Safety zip preserved at {Zip}", gameName, revertZip);
                throw new InvalidOperationException(errorMsg, revertException ?? restoreException);
            }
            else
            {
                // 2.2.2/ Nếu restore revert oke thì xóa thư mục revert của game đó đi
                RevertService.ClearRevertPoints(gameName);

                try
                {
                    var historyRecord = new RestoreHistoryRecord
                    {
                        GameName = gameName,
                        BackupHistoryDetailId = detailId,
                        SourcePath = sourcePath,
                        RestoreDate = DateTime.Now,
                        Status = "Failed_Reverted",
                        RevertZipPath = null,
                        RestoredPathsJson = JsonSerializer.Serialize(selectedItems.Select(x => x.RestoreDestinationPath)),
                        FileCount = fileCount,
                        TotalSizeBytes = totalSizeBytes,
                        ErrorMessage = $"Khôi phục thất bại: {restoreException!.Message}. Dữ liệu save ban đầu đã được hoàn tác an toàn về trạng thái gốc.",
                        IsCloud = isCloud,
                        CloudProvider = cloudProvider
                    };
                    await _databaseService.InsertRestoreHistoryAsync(historyRecord);
                }
                catch { }

                LoggingService.Warn("Restore failed for {Game}, but successfully auto-reverted to original state: {Message}", gameName, restoreException!.Message);
                throw new InvalidOperationException($"Khôi phục thất bại: {restoreException!.Message}. Tuy nhiên hệ thống đã hoàn tác an toàn dữ liệu save của bạn về trạng thái ban đầu.", restoreException);
            }
        }
        else
        {
            try
            {
                var historyRecord = new RestoreHistoryRecord
                {
                    GameName = gameName,
                    BackupHistoryDetailId = detailId,
                    SourcePath = sourcePath,
                    RestoreDate = DateTime.Now,
                    Status = "Failed",
                    RevertZipPath = null,
                    RestoredPathsJson = JsonSerializer.Serialize(selectedItems.Select(x => x.RestoreDestinationPath)),
                    FileCount = fileCount,
                    TotalSizeBytes = totalSizeBytes,
                    ErrorMessage = restoreException!.Message,
                    IsCloud = isCloud,
                    CloudProvider = cloudProvider
                };
                await _databaseService.InsertRestoreHistoryAsync(historyRecord);
            }
            catch { }

            throw new InvalidOperationException($"Khôi phục thất bại: {restoreException!.Message}", restoreException);
        }
    }

    private static List<CloudSyncInfo> GetAllCloudTargets(BackupHistoryDetail detail, ICloudStorageService? fallbackService)
    {
        var targets = new List<CloudSyncInfo>();

        if (detail.CloudSyncList != null && detail.CloudSyncList.Count > 0)
        {
            targets.AddRange(detail.CloudSyncList);
        }
        else if (!string.IsNullOrWhiteSpace(detail.CloudSyncJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<CloudSyncInfo>>(detail.CloudSyncJson);
                if (parsed != null && parsed.Count > 0)
                {
                    targets.AddRange(parsed);
                }
            }
            catch { }
        }

        // Fallback nếu CloudSyncList rỗng nhưng detail có CloudFileId
        if (targets.Count == 0 && !string.IsNullOrWhiteSpace(detail.CloudFileId))
        {
            var providerName = !string.IsNullOrWhiteSpace(detail.CloudProvider)
                ? detail.CloudProvider
                : (fallbackService?.ProviderName ?? "GoogleDrive");

            if (providerName.Contains(','))
            {
                var parts = providerName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    targets.Add(new CloudSyncInfo
                    {
                        Provider = p,
                        FileId = detail.CloudFileId,
                        FileName = detail.CloudFileName
                    });
                }
            }
            else
            {
                targets.Add(new CloudSyncInfo
                {
                    Provider = providerName,
                    FileId = detail.CloudFileId,
                    FileName = detail.CloudFileName
                });
            }
        }

        foreach (var kvp in detail.CloudUrls)
        {
            if (!targets.Any(t => t.Provider.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(new CloudSyncInfo
                {
                    Provider = kvp.Key,
                    WebViewUrl = kvp.Value
                });
            }
        }

        return targets;
    }

    private static ICloudStorageService? ResolveCloudProvider(
        string? providerName, 
        ICloudStorageService? fallbackService, 
        Func<string, ICloudStorageService?>? resolver)
    {
        if (resolver != null && !string.IsNullOrWhiteSpace(providerName))
        {
            var resolved = resolver(providerName);
            if (resolved != null) return resolved;
        }

        if (fallbackService != null)
        {
            if (string.IsNullOrWhiteSpace(providerName) || 
                fallbackService.ProviderName.Contains(providerName, StringComparison.OrdinalIgnoreCase) ||
                providerName.Contains(fallbackService.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return fallbackService;
            }
        }

        return fallbackService;
    }

    public async Task DeleteSnapshotWithProgressAsync(
        BackupHistoryDetail detail,
        bool deleteLocal = true,
        IEnumerable<string>? cloudProvidersToDelete = null,
        bool deleteFromCloud = false,
        ICloudStorageService? cloudService = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, ICloudStorageService?>? cloudServiceResolver = null)
    {
        if (detail == null) return;

        progress?.Report(new BackupProgress { Percent = 5, Message = "Bắt đầu xử lý xóa bản sao lưu..." });

        // 1. Xóa file vật lý trên đĩa nếu deleteLocal == true
        if (deleteLocal)
        {
            var localPath = detail.LocalBackupPath;
            if (File.Exists(localPath))
            {
                progress?.Report(new BackupProgress { Percent = 30, Message = "Đang xóa file sao lưu trên ổ đĩa..." });
                try
                {
                    File.SetAttributes(localPath, FileAttributes.Normal);
                    File.Delete(localPath);
                }
                catch { }

                // Dọn dẹp thư mục game cha nếu rỗng
                try
                {
                    var parentDir = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        if (Directory.GetFiles(parentDir).Length == 0 && Directory.GetDirectories(parentDir).Length == 0)
                        {
                            Directory.Delete(parentDir);
                        }
                    }
                }
                catch { }
            }
            else if (Directory.Exists(localPath))
            {
                await DeleteDirectoryWithProgressAsync(localPath, progress, 10, 50, cancellationToken);

                // Dọn dẹp thư mục cha nếu rỗng
                try
                {
                    var parentDir = Path.GetDirectoryName(localPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        if (Directory.GetFiles(parentDir).Length == 0 && Directory.GetDirectories(parentDir).Length == 0)
                        {
                            Directory.Delete(parentDir);
                        }
                    }
                }
                catch { }
            }

            // Gỡ bỏ LocalPath khỏi BackupPathLocations
            detail.SetLocalPath(null);
            progress?.Report(new BackupProgress { Percent = 55, Message = "Đã xóa dữ liệu trên ổ đĩa cục bộ." });
        }

        // 2. Xóa trên Cloud theo danh sách providers được chọn
        var cloudTargets = GetAllCloudTargets(detail, cloudService);
        var providersToDel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cloudProvidersToDelete != null)
        {
            foreach (var p in cloudProvidersToDelete)
            {
                if (!string.IsNullOrWhiteSpace(p)) providersToDel.Add(p);
            }
        }
        else if (deleteFromCloud)
        {
            foreach (var t in cloudTargets)
            {
                if (!string.IsNullOrWhiteSpace(t.Provider)) providersToDel.Add(t.Provider);
            }
        }

        if (providersToDel.Count > 0 && cloudTargets.Count > 0)
        {
            var targetsToDelete = cloudTargets.Where(t => providersToDel.Contains(t.Provider)).ToList();
            int total = targetsToDelete.Count;

            for (int ci = 0; ci < total; ci++)
            {
                var target = targetsToDelete[ci];
                if (string.IsNullOrEmpty(target.FileId)) continue;

                var targetService = ResolveCloudProvider(target.Provider, cloudService, cloudServiceResolver);
                var providerDisplay = targetService?.DisplayName ?? target.Provider;

                int stepStart = 60 + (int)((double)ci / Math.Max(1, total) * 25);
                int stepEnd = 60 + (int)((double)(ci + 1) / Math.Max(1, total) * 25);

                progress?.Report(new BackupProgress
                {
                    Percent = stepStart,
                    Message = total > 1
                        ? $"Đang xóa file trên {providerDisplay} ({ci + 1}/{total})..."
                        : $"Đang xóa file trên {providerDisplay}..."
                });

                if (targetService != null)
                {
                    try
                    {
                        await targetService.DeleteFileAsync(target.FileId, cancellationToken);
                        progress?.Report(new BackupProgress
                        {
                            Percent = stepEnd,
                            Message = $"Đã xóa file trên {providerDisplay} thành công."
                        });
                        LoggingService.LogAction("Cloud_File_Deleted", new { Provider = target.Provider, FileId = target.FileId });
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn("Cảnh báo: Không thể xóa file trên cloud {Provider} (FileId: {FileId}): {Message}", target.Provider, target.FileId, ex.Message);
                        progress?.Report(new BackupProgress
                        {
                            Percent = stepEnd,
                            Message = $"Cảnh báo: Không thể xóa trên {providerDisplay} ({ex.Message})"
                        });
                    }
                }
                else
                {
                    LoggingService.Warn("Không tìm thấy dịch vụ cloud tương ứng cho provider: {Provider}", target.Provider);
                }

                // Gỡ bỏ cloud khỏi detail
                detail.RemoveCloudUrl(target.Provider);
                detail.CloudSyncList.RemoveAll(c => c.Provider.Equals(target.Provider, StringComparison.OrdinalIgnoreCase));
            }

            // Đồng bộ lại metadata cloud của detail
            detail.IsCloudSynced = detail.CloudSyncList.Count > 0;
            detail.CloudProvider = string.Join(", ", detail.CloudSyncList.Select(c => c.Provider));
            var firstRemaining = detail.CloudSyncList.FirstOrDefault();
            detail.CloudFileId = firstRemaining?.FileId;
            detail.CloudFileName = firstRemaining?.FileName;
            detail.CloudSyncDate = firstRemaining?.SyncDate;
            detail.CloudSyncJson = detail.CloudSyncList.Count > 0 ? JsonSerializer.Serialize(detail.CloudSyncList) : null;
        }

        // 3. Database: Nếu còn ít nhất 1 bản lưu (local hoặc cloud) thì chỉ cập nhật record; nếu không còn bản nào thì xóa hẳn khỏi SQLite
        if (detail.HasAnyBackup)
        {
            progress?.Report(new BackupProgress { Percent = 90, Message = "Đang cập nhật vị trí lưu trong SQLite..." });
            await _databaseService.UpdateSnapshotLocationsAsync(detail.Id, detail);
            progress?.Report(new BackupProgress { Percent = 100, Message = "Đã cập nhật vị trí lưu của bản sao lưu!" });
        }
        else
        {
            progress?.Report(new BackupProgress { Percent = 90, Message = "Đang dọn dẹp cơ sở dữ liệu SQLite..." });
            bool masterDeleted = await _databaseService.DeleteHistoryDetailAsync(detail.Id, detail.GameHistoryId);
            if (masterDeleted)
            {
                var gameName = detail.GameName;
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    GameCoverService.DeleteCoverForGame(gameName);
                    RevertService.ClearRevertPoints(gameName);
                }
            }
            progress?.Report(new BackupProgress { Percent = 100, Message = "Xóa bản sao lưu hoàn tất!" });
        }
    }

    public async Task DeleteGameHistoryWithProgressAsync(
        GameHistoryEntry gameHistory,
        bool deleteLocal = true,
        IEnumerable<string>? cloudProvidersToDelete = null,
        bool deleteFromCloud = false,
        ICloudStorageService? cloudService = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, ICloudStorageService?>? cloudServiceResolver = null)
    {
        if (gameHistory == null) return;

        progress?.Report(new BackupProgress { Percent = 5, Message = $"Bắt đầu xóa toàn bộ lịch sử game '{gameHistory.GameName}'..." });

        var details = await _databaseService.GetHistoryDetailsByGameIdAsync(gameHistory.Id);

        // 1. Xóa các file bản sao lưu trên đĩa hoặc xóa các folder trong gameHistory.BackupFolderPaths
        if (deleteLocal)
        {
            for (int i = 0; i < details.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var d = details[i];
                var localPath = d.LocalBackupPath;

                int startPct = 5 + (int)((double)i / Math.Max(1, details.Count) * 40);
                int endPct = 5 + (int)((double)(i + 1) / Math.Max(1, details.Count) * 40);

                if (File.Exists(localPath))
                {
                    try { File.Delete(localPath); } catch { }
                }
                else if (Directory.Exists(localPath))
                {
                    try { Directory.Delete(localPath, true); } catch { }
                }

                progress?.Report(new BackupProgress
                {
                    Percent = endPct,
                    Message = $"Đã xóa file bản lưu {i + 1}/{details.Count}..."
                });
            }

            // Xóa toàn bộ các folder game được lưu trữ trong gameHistory.BackupFolderPaths
            var folders = gameHistory.BackupFolderPaths;
            if (folders.Count == 0 && !string.IsNullOrWhiteSpace(gameHistory.LatestBackupPath))
            {
                var fallback = Path.GetDirectoryName(gameHistory.LatestBackupPath);
                if (!string.IsNullOrEmpty(fallback)) folders.Add(fallback);
            }

            foreach (var folder in folders)
            {
                if (Directory.Exists(folder))
                {
                    try
                    {
                        await DeleteDirectoryWithProgressAsync(folder, progress, 45, 60, cancellationToken);
                    }
                    catch { }
                }
            }
        }

        // 2. Xóa trên Cloud
        var providersToDel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cloudProvidersToDelete != null)
        {
            foreach (var p in cloudProvidersToDelete)
            {
                if (!string.IsNullOrWhiteSpace(p)) providersToDel.Add(p);
            }
        }
        else if (deleteFromCloud)
        {
            if (cloudService != null && !string.IsNullOrWhiteSpace(cloudService.ProviderName))
            {
                providersToDel.Add(cloudService.ProviderName);
            }
            foreach (var d in details)
            {
                foreach (var t in GetAllCloudTargets(d, cloudService))
                {
                    if (!string.IsNullOrWhiteSpace(t.Provider)) providersToDel.Add(t.Provider);
                }
                foreach (var k in d.CloudUrls.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(k)) providersToDel.Add(k);
                }
                if (!string.IsNullOrWhiteSpace(d.CloudProvider))
                {
                    foreach (var cp in d.CloudProvider.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(cp)) providersToDel.Add(cp);
                    }
                }
            }

            if (cloudServiceResolver != null)
            {
                foreach (var p in new[] { "GoogleDrive", "OneDrive" })
                {
                    var prov = cloudServiceResolver(p);
                    if (prov != null && prov.IsAuthenticated)
                    {
                        providersToDel.Add(p);
                    }
                }
            }
        }

        if (providersToDel.Count > 0)
        {
            // Xóa các file chi tiết của từng snapshot
            for (int i = 0; i < details.Count; i++)
            {
                var d = details[i];
                var targets = GetAllCloudTargets(d, cloudService).Where(t => providersToDel.Contains(t.Provider)).ToList();
                foreach (var target in targets)
                {
                    if (string.IsNullOrEmpty(target.FileId)) continue;
                    var targetService = ResolveCloudProvider(target.Provider, cloudService, cloudServiceResolver);
                    if (targetService != null)
                    {
                        try
                        {
                            await targetService.DeleteFileAsync(target.FileId, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Warn("Cảnh báo: Không thể xóa file trên cloud {Provider}: {Message}", target.Provider, ex.Message);
                        }
                    }
                }
            }

            // Gọi DeleteFolderAsync xóa cả thư mục game trên cloud
            var remoteGameFolder = SanitizeFolderName(gameHistory.GameName);
            foreach (var provider in providersToDel)
            {
                var targetService = ResolveCloudProvider(provider, cloudService, cloudServiceResolver);
                if (targetService != null)
                {
                    progress?.Report(new BackupProgress
                    {
                        Percent = 80,
                        Message = $"Đang xóa thư mục game '{remoteGameFolder}' trên {targetService.DisplayName}..."
                    });
                    try
                    {
                        await targetService.DeleteFolderAsync(remoteGameFolder, cancellationToken);
                        LoggingService.LogAction("Cloud_Game_Folder_Deleted", new { Game = gameHistory.GameName, Provider = provider });
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn("Không thể xóa thư mục game trên {Provider}: {Message}", provider, ex.Message);
                    }
                }
            }
        }

        progress?.Report(new BackupProgress { Percent = 90, Message = "Đang dọn dẹp cơ sở dữ liệu SQLite..." });
        await _databaseService.DeleteGameHistoryAsync(gameHistory.Id);
        GameCoverService.DeleteCoverForGame(gameHistory.GameName);
        RevertService.ClearRevertPoints(gameHistory.GameName);

        progress?.Report(new BackupProgress { Percent = 100, Message = $"Đã xóa sạch toàn bộ lịch sử game '{gameHistory.GameName}'!" });
    }

    private static async Task RestoreSelectedFromDirectoryAsync(
        string backupDir,
        List<RestoreItemTarget> selectedItems,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < selectedItems.Count; i++)
        {
            var item = selectedItems[i];
            var destPath = item.RestoreDestinationPath;
            if (string.IsNullOrWhiteSpace(destPath)) continue;

            string itemSourceDir = string.IsNullOrEmpty(item.SubFolder)
                ? backupDir
                : Path.Combine(backupDir, item.SubFolder);

            if (!Directory.Exists(itemSourceDir))
            {
                if (selectedItems.Count == 1 && Directory.Exists(backupDir))
                {
                    itemSourceDir = backupDir;
                }
                else
                {
                    var matchingDir = Directory.GetDirectories(backupDir, $"{i + 1}_*").FirstOrDefault();
                    if (matchingDir != null)
                    {
                        itemSourceDir = matchingDir;
                    }
                    else
                    {
                        continue;
                    }
                }
            }

            var itemProgress = new Progress<BackupProgress>(p =>
            {
                var overallPct = (int)((i * 100.0 / selectedItems.Count) + (p.Percent / (double)selectedItems.Count));
                progress?.Report(new BackupProgress
                {
                    Percent = Math.Min(overallPct, 99),
                    CurrentFile = p.CurrentFile,
                    Message = $"[{i + 1}/{selectedItems.Count}] {p.Message}"
                });
            });

            // Xóa sạch dữ liệu trong thư mục/tệp đích cũ trước khi restore mới vào
            if (Directory.Exists(destPath))
            {
                ClearDirectorySafely(destPath);
            }
            else if (File.Exists(destPath))
            {
                DeleteFileSafely(destPath);
            }

            await CopyAllFilesAsync(itemSourceDir, destPath, itemProgress, cancellationToken, skipManifest: string.IsNullOrEmpty(item.SubFolder));
        }
    }

    private static void ClearDirectorySafely(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                try
                {
                    var attr = File.GetAttributes(f);
                    if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                    {
                        File.SetAttributes(f, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                    }
                    File.Delete(f);
                }
                catch { }
            }

            var subDirs = Directory.GetDirectories(dirPath, "*", SearchOption.AllDirectories)
                                   .OrderByDescending(d => d.Length);
            foreach (var sd in subDirs)
            {
                try
                {
                    if (Directory.Exists(sd)) Directory.Delete(sd, true);
                }
                catch { }
            }
        }
        catch
        {
            try
            {
                Directory.Delete(dirPath, true);
                Directory.CreateDirectory(dirPath);
            }
            catch { }
        }
    }

    private static void DeleteFileSafely(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var attr = File.GetAttributes(filePath);
                if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                {
                    File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                }
                File.Delete(filePath);
            }
        }
        catch { }
    }

    private static async Task CopyAllFilesAsync(string sourceDir, string destinationDir, IProgress<BackupProgress>? progress, CancellationToken cancellationToken, bool skipManifest = false)
    {
        Directory.CreateDirectory(destinationDir);
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];

            if (skipManifest && Path.GetFileName(file).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destinationDir, relative);

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(file, destPath, true);

            var pct = (int)((i + 1.0) / files.Length * 95);
            progress?.Report(new BackupProgress
            {
                Percent = pct,
                CurrentFile = relative,
                Message = $"Đang khôi phục: {Path.GetFileName(file)}"
            });

            await Task.Yield();
        }
    }

    private static async Task CreateZipFromDirectoryWithProgressAsync(
        string sourceDirectory,
        string destinationZipPath,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        long totalBytes = 0;
        foreach (var file in files)
        {
            try { totalBytes += new FileInfo(file).Length; } catch { }
        }

        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);

        var zipDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(zipDir)) Directory.CreateDirectory(zipDir);

        using var zipToOpen = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);

        long bytesProcessed = 0;
        var buffer = new byte[64 * 1024];

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);

            using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var entryStream = entry.Open();

            int read;
            while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesProcessed += read;

                double ratio = totalBytes > 0 ? (double)bytesProcessed / totalBytes : (double)(i + 1) / files.Length;
                int pct = startPercent + (int)(ratio * (endPercent - startPercent));

                progress?.Report(new BackupProgress
                {
                    Percent = Math.Clamp(pct, startPercent, endPercent),
                    CurrentFile = relative,
                    ProcessedBytes = bytesProcessed,
                    TotalBytes = totalBytes,
                    ProcessedItems = i + 1,
                    TotalItems = files.Length,
                    Message = $"Đang nén ({i + 1}/{files.Length}): {Path.GetFileName(file)} ({pct}%)"
                });
            }
        }
    }

    private static async Task ExtractZipWithProgressAsync(
        string zipPath,
        string destinationDir,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDir);
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.ToList();

        for (int i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry

            var destPath = Path.Combine(destinationDir, entry.FullName);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            entry.ExtractToFile(destPath, true);

            int pct = startPercent + (int)((i + 1.0) / Math.Max(1, entries.Count) * (endPercent - startPercent));
            progress?.Report(new BackupProgress
            {
                Percent = Math.Clamp(pct, startPercent, endPercent),
                CurrentFile = entry.Name,
                ProcessedItems = i + 1,
                TotalItems = entries.Count,
                Message = $"Đang giải nén: {entry.Name} ({pct}%)"
            });

            await Task.Yield();
        }
    }

    private static async Task DeleteDirectoryWithProgressAsync(
        string directoryPath,
        IProgress<BackupProgress>? progress,
        int startPercent,
        int endPercent,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath)) return;

        var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.SetAttributes(files[i], FileAttributes.Normal);
                File.Delete(files[i]);
            }
            catch { }

            int pct = startPercent + (int)((i + 1.0) / Math.Max(1, files.Length) * (endPercent - startPercent));
            progress?.Report(new BackupProgress
            {
                Percent = Math.Clamp(pct, startPercent, endPercent),
                CurrentFile = Path.GetFileName(files[i]),
                ProcessedItems = i + 1,
                TotalItems = files.Length,
                Message = $"Đang xóa tệp ({i + 1}/{files.Length}): {Path.GetFileName(files[i])}"
            });

            await Task.Yield();
        }

        try
        {
            Directory.Delete(directoryPath, true);
        }
        catch { }
    }

    /// <summary>
    /// Thực hiện hoàn tác (Revert) đưa save game về trạng thái trước lần Restore gần nhất.
    /// </summary>
    public Task<bool> RevertGameSaveAsync(string gameName, string? revertZipPath = null, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return RevertService.RevertGameSaveAsync(gameName, revertZipPath, progress, cancellationToken);
    }

    /// <summary>
    /// Xóa toàn bộ file nén hoàn tác (Revert) của game để giải phóng dung lượng ổ đĩa.
    /// </summary>
    public void ClearRevertPoints(string gameName)
    {
        RevertService.ClearRevertPoints(gameName);
    }

    /// <summary>
    /// Lấy thông tin điểm hoàn tác gần nhất của game (nếu có).
    /// </summary>
    public RevertPointInfo? GetLatestRevertPoint(string gameName)
    {
        return RevertService.GetLatestRevertPoint(gameName);
    }
}

