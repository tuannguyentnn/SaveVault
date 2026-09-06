using System.IO.Compression;
using System.Text.Json;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class BackupProgress
{
    public int Percent { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
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
        CancellationToken cancellationToken = default)
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
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        var rootBackupDir = string.IsNullOrWhiteSpace(settings.BackupRootDirectory)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups")
            : settings.BackupRootDirectory;

        Directory.CreateDirectory(rootBackupDir);

        string targetFolder;

        if (settings.AutoCompressZip)
        {
            targetFolder = Path.Combine(Path.GetTempPath(), "SaveBackup_Stage_" + Guid.NewGuid().ToString("N"));
        }
        else if (settings.CreateTimestampSubfolder)
        {
            targetFolder = Path.Combine(rootBackupDir, sanitizedName, timestamp);
        }
        else
        {
            targetFolder = Path.Combine(rootBackupDir, sanitizedName);
        }

        Directory.CreateDirectory(targetFolder);

        int totalCopiedFiles = 0;
        long totalCopiedBytes = 0;

        var manifest = new BackupManifest
        {
            GameName = gameInfo.GameName,
            BackupDate = DateTime.Now
        };

        progress?.Report(new BackupProgress { Percent = 5, Message = "Chuẩn bị sao lưu các vị trí đã chọn..." });

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

                    var pct = 5 + (int)((fIndex + 1.0) / allFiles.Length * (80.0 / pathsToBackup.Count) + (i * 80.0 / pathsToBackup.Count));
                    progress?.Report(new BackupProgress
                    {
                        Percent = Math.Min(pct, 90),
                        CurrentFile = relative,
                        Message = $"Đang sao lưu: {Path.GetFileName(file)} ({i + 1}/{pathsToBackup.Count})"
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
                    Percent = 85,
                    CurrentFile = fileName,
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

        // Write manifest file
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(targetFolder, ManifestFileName), manifestJson);

        string finalBackupPath;

        // Auto compress to Zip if enabled
        if (settings.AutoCompressZip)
        {
            progress?.Report(new BackupProgress { Percent = 92, Message = "Đang nén file zip..." });
            var zipDir = Path.Combine(rootBackupDir, sanitizedName);
            Directory.CreateDirectory(zipDir);
            var zipFilePath = Path.Combine(zipDir, $"{sanitizedName}_{timestamp}.zip");

            if (File.Exists(zipFilePath)) File.Delete(zipFilePath);

            ZipFile.CreateFromDirectory(targetFolder, zipFilePath, CompressionLevel.Optimal, false);

            // Clean up staging/temp uncompressed folder
            try { Directory.Delete(targetFolder, true); } catch { /* Ignore */ }

            finalBackupPath = zipFilePath;
        }
        else
        {
            finalBackupPath = targetFolder;
        }

        var sourcePathRecord = pathsToBackup.Count == 1
            ? pathsToBackup[0]
            : string.Join(" | ", pathsToBackup);

        var record = new BackupRecord
        {
            GameName = gameInfo.GameName,
            BackupPath = finalBackupPath,
            SourcePath = sourcePathRecord,
            FileCount = totalCopiedFiles,
            TotalSizeBytes = totalCopiedBytes,
            BackupDate = DateTime.Now,
            IsCompressed = settings.AutoCompressZip,
            Status = "Thành công",
            Note = $"Đã backup {totalCopiedFiles} file từ {pathsToBackup.Count} vị trí lưu."
        };

        var id = await _databaseService.InsertBackupRecordAsync(record);
        record.Id = id;

        progress?.Report(new BackupProgress { Percent = 100, Message = "Sao lưu hoàn tất!" });
        return record;
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

        if (record.IsCompressed)
        {
            if (!File.Exists(record.BackupPath))
                throw new FileNotFoundException($"Không tìm thấy file zip backup: {record.BackupPath}");

            progress?.Report(new BackupProgress { Percent = 10, Message = "Đang giải nén dữ liệu sao lưu tạm thời..." });

            var tempExtractDir = Path.Combine(Path.GetTempPath(), "SaveBackup_Restore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                ZipFile.ExtractToDirectory(record.BackupPath, tempExtractDir);
                await RestoreSelectedFromDirectoryAsync(tempExtractDir, selectedItems, progress, cancellationToken);
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

            await RestoreSelectedFromDirectoryAsync(record.BackupPath, selectedItems, progress, cancellationToken);
        }

        progress?.Report(new BackupProgress { Percent = 100, Message = "Khôi phục hoàn tất!" });
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

            await CopyAllFilesAsync(itemSourceDir, destPath, itemProgress, cancellationToken, skipManifest: string.IsNullOrEmpty(item.SubFolder));
        }
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
}
