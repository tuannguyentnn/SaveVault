using System.IO.Compression;
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

    public async Task<BackupRecord> BackupGameAsync(
        GameSaveInfo gameInfo,
        AppSettings settings,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (gameInfo.DetectedPathsOnDisk.Count == 0)
        {
            throw new InvalidOperationException($"Không tìm thấy file save nào của game '{gameInfo.GameName}' trên máy tính!");
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
        var primarySource = gameInfo.DetectedPathsOnDisk[0];

        progress?.Report(new BackupProgress { Percent = 5, Message = "Chuẩn bị sao lưu các tệp..." });

        for (int i = 0; i < gameInfo.DetectedPathsOnDisk.Count; i++)
        {
            var src = gameInfo.DetectedPathsOnDisk[i];
            if (Directory.Exists(src))
            {
                var dirName = Path.GetFileName(src.TrimEnd('\\', '/'));
                // If multiple detected sources, place in subfolders named after source dir
                var destSubDir = gameInfo.DetectedPathsOnDisk.Count > 1 
                    ? Path.Combine(targetFolder, dirName)
                    : targetFolder;

                Directory.CreateDirectory(destSubDir);

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

                    var pct = 5 + (int)((fIndex + 1.0) / allFiles.Length * 85);
                    progress?.Report(new BackupProgress
                    {
                        Percent = pct,
                        CurrentFile = relative,
                        Message = $"Đang sao lưu: {Path.GetFileName(file)}"
                    });
                }
            }
            else if (File.Exists(src))
            {
                var fileName = Path.GetFileName(src);
                var destFilePath = Path.Combine(targetFolder, fileName);
                File.Copy(src, destFilePath, true);

                var fInfo = new FileInfo(src);
                totalCopiedFiles++;
                totalCopiedBytes += fInfo.Length;

                progress?.Report(new BackupProgress
                {
                    Percent = 90,
                    CurrentFile = fileName,
                    Message = $"Đang sao lưu file: {fileName}"
                });
            }
        }

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

        var record = new BackupRecord
        {
            GameName = gameInfo.GameName,
            BackupPath = finalBackupPath,
            SourcePath = primarySource,
            FileCount = totalCopiedFiles,
            TotalSizeBytes = totalCopiedBytes,
            BackupDate = DateTime.Now,
            IsCompressed = settings.AutoCompressZip,
            Status = "Thành công",
            Note = $"Đã backup {totalCopiedFiles} file từ {gameInfo.DetectedPathsOnDisk.Count} vị trí lưu."
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
                await CopyAllFilesAsync(tempExtractDir, record.SourcePath, progress, cancellationToken);
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

            await CopyAllFilesAsync(record.BackupPath, record.SourcePath, progress, cancellationToken);
        }

        progress?.Report(new BackupProgress { Percent = 100, Message = "Khôi phục thành công!" });
    }

    private static async Task CopyAllFilesAsync(string sourceDir, string destinationDir, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDir);
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);

        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destinationDir, relative);

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(file, destPath, true);

            var pct = (int)((i + 1.0) / files.Length * 90);
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
