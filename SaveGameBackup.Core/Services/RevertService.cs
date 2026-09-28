using System.IO.Compression;
using System.Text.Json;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Thông tin bản ghi điểm hoàn tác (Revert Point) được lưu dự phòng trước khi Restore.
/// </summary>
public class RevertPointInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public DateTime CreatedAt { get; set; }
    public long TotalSizeBytes { get; set; }
    public string FormattedDate => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    public string DisplaySize => FormatSize(TotalSizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

/// <summary>
/// Manifest bên trong file nén Revert ZIP để ghi nhớ vị trí khôi phục chính xác của từng thư mục.
/// </summary>
public class RevertManifest
{
    public string GameName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<RevertItemEntry> Items { get; set; } = new();
}

public class RevertItemEntry
{
    public string SubFolder { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}

/// <summary>
/// Dịch vụ quản lý các điểm hoàn tác (Revert Point) an toàn trước khi khôi phục bản sao lưu (Restore).
/// Tự động nén dữ liệu save hiện có trên máy trước khi chép đè, cung cấp tính năng Revert và Clear Revert.
/// </summary>
public static class RevertService
{
    private const string ManifestFileName = "revert_manifest.json";

    /// <summary>
    /// Thư mục gốc chứa toàn bộ các bản revert của tất cả game ({ProjectRoot}/Reverts).
    /// </summary>
    public static string GetRevertsRootDirectory()
    {
        var root = DatabaseService.GetDefaultProjectRoot();
        var dir = Path.Combine(root, "data", "reverts");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    /// <summary>
    /// Thư mục chứa các bản revert riêng cho game cụ thể ({ProjectRoot}/Reverts/{SanitizedGameName}).
    /// </summary>
    public static string GetRevertDirectory(string gameName, bool createIfNotExists = false)
    {
        var sanitized = SanitizeFolderName(gameName);
        var dir = Path.Combine(GetRevertsRootDirectory(), sanitized);
        if (createIfNotExists && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    /// <summary>
    /// Kiểm tra xem game có bản hoàn tác (Revert Point) nào đang khả dụng không.
    /// </summary>
    public static bool HasRevertPoint(string gameName)
    {
        return GetLatestRevertPoint(gameName) != null;
    }

    /// <summary>
    /// Lấy thông tin bản hoàn tác mới nhất của game.
    /// </summary>
    public static RevertPointInfo? GetLatestRevertPoint(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        try
        {
            var dir = GetRevertDirectory(gameName);
            if (!Directory.Exists(dir)) return null;

            var files = Directory.GetFiles(dir, "revert_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Name)
                .ToList();

            if (files.Count == 0) return null;

            var latest = files[0];

            // Đảm bảo chỉ có duy nhất 1 bản revert: tự động dọn dẹp các file cũ hơn nếu có
            if (files.Count > 1)
            {
                for (int i = 1; i < files.Count; i++)
                {
                    DeleteRevertFile(files[i].FullName);
                }
            }

            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(latest.Name);
            DateTime createdAt = latest.LastWriteTime;
            if (fileNameWithoutExt.StartsWith("revert_") &&
                DateTime.TryParseExact(fileNameWithoutExt["revert_".Length..], "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                createdAt = parsedDate;
            }

            return new RevertPointInfo
            {
                FilePath = latest.FullName,
                CreatedAt = createdAt,
                TotalSizeBytes = latest.Length
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Xóa toàn bộ các file nén Revert dự phòng của game để giải phóng dung lượng ổ đĩa.
    /// </summary>
    public static void ClearRevertPoints(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return;

        try
        {
            var dir = GetRevertDirectory(gameName);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(file);
                        if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                            File.SetAttributes(file, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                        File.Delete(file);
                    }
                    catch { }
                }

                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    System.Threading.Thread.Sleep(50);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể xóa thư mục revert cho {Game}: {Message}", gameName, ex.Message);
        }
    }

    /// <summary>
    /// Xóa một file nén Revert cụ thể khi không còn cần thiết (ví dụ sau khi auto-revert thành công).
    /// </summary>
    public static void DeleteRevertFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể xóa file revert {FilePath}: {Message}", filePath, ex.Message);
        }
    }

    /// <summary>
    /// Quét các thư mục đích của save game hiện tại. Nếu có dữ liệu, tự động nén thành file revert_{timestamp}.zip
    /// để tạo điểm hoàn tác an toàn trước khi bị chép đè bởi Restore.
    /// Trả về đường dẫn file revert đã tạo (hoặc null nếu máy chưa có dữ liệu save cũ nào để nén).
    /// </summary>
    public static async Task<string?> CreateRevertPointAsync(
        string gameName,
        List<RestoreItemTarget> itemsToRestore,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName) || itemsToRestore == null || itemsToRestore.Count == 0)
        {
            return null;
        }

        var validTargets = itemsToRestore
            .Where(t => t.IsSelected && !string.IsNullOrWhiteSpace(t.RestoreDestinationPath) && 
                        (Directory.Exists(t.RestoreDestinationPath) || File.Exists(t.RestoreDestinationPath)))
            .ToList();

        if (validTargets.Count == 0)
        {
            return null;
        }

        // Kiểm tra xem có file nào bên trong các thư mục/tệp đích không
        var filesToBackup = new List<(string SourceFile, string SubFolder, string DestinationPath, bool IsSingleFile)>();
        long totalBytes = 0;

        for (int i = 0; i < validTargets.Count; i++)
        {
            var target = validTargets[i];
            var subFolder = $"target_{i}";

            if (File.Exists(target.RestoreDestinationPath))
            {
                var fi = new FileInfo(target.RestoreDestinationPath);
                filesToBackup.Add((target.RestoreDestinationPath, subFolder, target.RestoreDestinationPath, true));
                totalBytes += fi.Length;
            }
            else if (Directory.Exists(target.RestoreDestinationPath))
            {
                var files = Directory.GetFiles(target.RestoreDestinationPath, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    filesToBackup.Add((f, subFolder, target.RestoreDestinationPath, false));
                    totalBytes += fi.Length;
                }
            }
        }

        if (filesToBackup.Count == 0)
        {
            // Thư mục/tệp save hiện đang trống, không có dữ liệu cũ cần lưu dự phòng
            return null;
        }

        progress?.Report(new BackupProgress
        {
            Percent = 2,
            Message = "Đang tạo điểm hoàn tác an toàn (Revert) cho dữ liệu save hiện có..."
        });

        // Trước khi tạo file revert: xóa thư mục revert của game nếu đã tồn tại rồi mới tạo thư mục và file mới
        ClearRevertPoints(gameName);

        var revertDir = GetRevertDirectory(gameName, createIfNotExists: true);
        var now = DateTime.Now;
        var creationTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        var timestamp = creationTime.ToString("yyyyMMdd_HHmmss");
        var revertZipPath = Path.Combine(revertDir, $"revert_{timestamp}.zip");

        var tempStageDir = Path.Combine(Path.GetTempPath(), $"SaveVault_Revert_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStageDir);

        try
        {
            var manifest = new RevertManifest
            {
                GameName = gameName,
                CreatedAt = creationTime
            };

            for (int i = 0; i < validTargets.Count; i++)
            {
                var target = validTargets[i];
                var subFolder = $"target_{i}";
                var subDir = Path.Combine(tempStageDir, subFolder);
                Directory.CreateDirectory(subDir);

                var targetFiles = filesToBackup.Where(fb => fb.SubFolder == subFolder).ToList();
                long subTotalSize = 0;

                foreach (var fb in targetFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destFilePath;
                    if (fb.IsSingleFile)
                    {
                        destFilePath = Path.Combine(subDir, Path.GetFileName(fb.SourceFile));
                    }
                    else
                    {
                        var rel = Path.GetRelativePath(target.RestoreDestinationPath, fb.SourceFile);
                        destFilePath = Path.Combine(subDir, rel);
                    }

                    var parent = Path.GetDirectoryName(destFilePath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    File.Copy(fb.SourceFile, destFilePath, true);
                    subTotalSize += new FileInfo(fb.SourceFile).Length;
                }

                manifest.Items.Add(new RevertItemEntry
                {
                    SubFolder = subFolder,
                    DestinationPath = target.RestoreDestinationPath,
                    FileCount = targetFiles.Count,
                    TotalSizeBytes = subTotalSize
                });
            }

            // Ghi manifest
            var manifestPath = Path.Combine(tempStageDir, ManifestFileName);
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);

            // Nén tempStageDir vào revertZipPath
            if (File.Exists(revertZipPath)) File.Delete(revertZipPath);
            ZipFile.CreateFromDirectory(tempStageDir, revertZipPath, CompressionLevel.Optimal, false);
            try
            {
                File.SetCreationTime(revertZipPath, creationTime);
                File.SetLastWriteTime(revertZipPath, creationTime);
            }
            catch { }

            // Đảm bảo chỉ có 1 bản revert duy nhất: xóa sạch toàn bộ các bản revert cũ trước đó của game này
            try
            {
                var existingRevertFiles = Directory.GetFiles(revertDir, "revert_*.zip");
                foreach (var oldFile in existingRevertFiles)
                {
                    if (!string.Equals(oldFile, revertZipPath, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteRevertFile(oldFile);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi khi dọn dẹp các bản revert cũ của {Game}: {Message}", gameName, ex.Message);
            }

            LoggingService.LogAction("Revert_Point_Created", new
            {
                Game = gameName,
                ZipPath = revertZipPath,
                TotalFiles = filesToBackup.Count,
                TotalBytes = totalBytes
            });

            return revertZipPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStageDir))
                {
                    Directory.Delete(tempStageDir, true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Thực hiện hoàn tác (Revert): Giải nén dữ liệu từ điểm hoàn tác và chép đè trở lại các thư mục save game.
    /// </summary>
    public static async Task<bool> RevertGameSaveAsync(
        string gameName,
        string? revertZipPath = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return false;

        var targetZip = revertZipPath;
        if (string.IsNullOrWhiteSpace(targetZip))
        {
            var latest = GetLatestRevertPoint(gameName);
            targetZip = latest?.FilePath;
        }

        if (string.IsNullOrWhiteSpace(targetZip) || !File.Exists(targetZip))
        {
            throw new FileNotFoundException($"Không tìm thấy file nén hoàn tác cho game '{gameName}'!");
        }

        progress?.Report(new BackupProgress { Percent = 10, Message = "Đang chuẩn bị giải nén bản hoàn tác..." });

        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"SaveVault_RevertExtract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            ZipFile.ExtractToDirectory(targetZip, tempExtractDir, true);

            var manifestPath = Path.Combine(tempExtractDir, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Bản nén hoàn tác không hợp lệ (thiếu file manifest)!");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<RevertManifest>(manifestJson);

            if (manifest == null || manifest.Items.Count == 0)
            {
                throw new InvalidOperationException("Nội dung hoàn tác rỗng!");
            }

            int totalItems = manifest.Items.Count;
            for (int i = 0; i < totalItems; i++)
            {
                var item = manifest.Items[i];
                var subFolderDir = Path.Combine(tempExtractDir, item.SubFolder);
                if (!Directory.Exists(subFolderDir)) continue;

                var destTarget = item.DestinationPath;
                var files = Directory.GetFiles(subFolderDir, "*", SearchOption.AllDirectories);

                // Nếu đích đến là 1 file đơn
                if (files.Length == 1 && (File.Exists(destTarget) || (!Directory.Exists(destTarget) && Path.HasExtension(destTarget))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var f = files[0];
                    var parentDir = Path.GetDirectoryName(destTarget);
                    if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                    if (File.Exists(destTarget))
                    {
                        try
                        {
                            var attr = File.GetAttributes(destTarget);
                            if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                                File.SetAttributes(destTarget, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                        }
                        catch { }
                    }

                    File.Copy(f, destTarget, true);
                    continue;
                }

                var destDir = destTarget;
                Directory.CreateDirectory(destDir);

                // Tập hợp các file tương đối vốn có ban đầu
                var expectedRelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int fi = 0; fi < files.Length; fi++)
                {
                    expectedRelFiles.Add(Path.GetRelativePath(subFolderDir, files[fi]));
                }

                // Xóa những file phát sinh mới từ đợt Restore mà trước đó không hề có trong bản revert gốc
                if (Directory.Exists(destDir))
                {
                    var currentDestFiles = Directory.GetFiles(destDir, "*", SearchOption.AllDirectories);
                    foreach (var cdf in currentDestFiles)
                    {
                        var rel = Path.GetRelativePath(destDir, cdf);
                        if (!expectedRelFiles.Contains(rel))
                        {
                            try
                            {
                                var attr = File.GetAttributes(cdf);
                                if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                                    File.SetAttributes(cdf, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                                File.Delete(cdf);
                            }
                            catch { }
                        }
                    }
                }

                for (int fi = 0; fi < files.Length; fi++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var f = files[fi];
                    var rel = Path.GetRelativePath(subFolderDir, f);
                    var destFile = Path.Combine(destDir, rel);

                    var parentDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                    if (File.Exists(destFile))
                    {
                        try
                        {
                            var attr = File.GetAttributes(destFile);
                            if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                                File.SetAttributes(destFile, attr & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                        }
                        catch { }
                    }

                    File.Copy(f, destFile, true);

                    int pct = 20 + (int)(((double)(fi + 1) / Math.Max(1, files.Length)) * 75.0);
                    progress?.Report(new BackupProgress
                    {
                        Percent = Math.Clamp(pct, 20, 95),
                        CurrentFile = rel,
                        Message = $"[{i + 1}/{totalItems}] Đang hoàn tác file: {Path.GetFileName(f)}"
                    });
                }
            }

            progress?.Report(new BackupProgress { Percent = 100, Message = "Hoàn tác save game thành công!" });
            LoggingService.LogAction("Revert_Completed", new { Game = gameName, ZipPath = targetZip });

            // Khi đã hoàn tác save game thành công thì xóa đi toàn bộ thư mục revert của game đó
            ClearRevertPoints(gameName);

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, true);
                }
            }
            catch { }
        }
    }

    private static string SanitizeFolderName(string name) => BackupService.SanitizeFolderName(name);
}

