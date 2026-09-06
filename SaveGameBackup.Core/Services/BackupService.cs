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

    public async Task RestoreAsync(BackupRecord record, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (record.IsCompressed)
        {
            if (!File.Exists(record.BackupPath))
                throw new FileNotFoundException($"Không tìm thấy file zip backup: {record.BackupPath}");

            progress?.Report(new BackupProgress { Percent = 10, Message = "Đang giải nén dữ liệu sao lưu..." });

            var tempExtractDir = Path.Combine(Path.GetTempPath(), "SaveBackup_Restore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                ZipFile.ExtractToDirectory(record.BackupPath, tempExtractDir);
                await RestoreFromDirectoryAsync(tempExtractDir, record, progress, cancellationToken);
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

            await RestoreFromDirectoryAsync(record.BackupPath, record, progress, cancellationToken);
        }

        progress?.Report(new BackupProgress { Percent = 100, Message = "Khôi phục thành công!" });
    }

    private static async Task RestoreFromDirectoryAsync(string backupDir, BackupRecord record, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(json);

                if (manifest != null && manifest.Items.Count > 0)
                {
                    var recordPaths = record.SourcePathsList;
                    for (int i = 0; i < manifest.Items.Count; i++)
                    {
                        var item = manifest.Items[i];
                        var itemSourceDir = string.IsNullOrEmpty(item.SubFolder)
                            ? backupDir
                            : Path.Combine(backupDir, item.SubFolder);

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
                            destPath = item.SourcePath;
                        }

                        if (Directory.Exists(itemSourceDir))
                        {
                            await CopyAllFilesAsync(itemSourceDir, destPath, progress, cancellationToken, skipManifest: string.IsNullOrEmpty(item.SubFolder));
                        }
                    }
                    return;
                }
            }
            catch
            {
                // Fallback to record.SourcePath if manifest parsing fails
            }
        }

        // Legacy fallback without manifest
        var paths = record.SourcePathsList;
        if (paths.Count > 0)
        {
            await CopyAllFilesAsync(backupDir, paths[0], progress, cancellationToken, skipManifest: true);
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
