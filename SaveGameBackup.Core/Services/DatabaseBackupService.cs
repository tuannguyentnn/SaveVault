using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services.Cloud;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Dịch vụ quản lý việc tạo Snapshot và Sao lưu / Khôi phục cơ sở dữ liệu SQLite lên Cloud (Google Drive & OneDrive).
/// Áp dụng kỹ thuật SQLite WAL VACUUM INTO, mã hóa AES-256 lịch sử sao lưu và an toàn dữ liệu 100%.
/// </summary>
public class DatabaseBackupService
{
    private readonly DatabaseService _databaseService;
    private readonly CloudManagerService _cloudManager;

    public const string RemoteDatabaseFolderName = "_Database_Backups";

    public DatabaseBackupService(DatabaseService databaseService, CloudManagerService cloudManager)
    {
        _databaseService = databaseService;
        _cloudManager = cloudManager;
    }

    public static string GetHistoryFilePath()
    {
        var root = DatabaseService.GetDefaultProjectRoot();
        var dir = Path.Combine(root, "data", "database");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, "db_cloud_backup_history.json");
    }

    public static string GetTempDirectory()
    {
        var root = DatabaseService.GetDefaultProjectRoot();
        var dir = Path.Combine(root, "data", "temp", "db_cloud_backup");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Đọc danh sách lịch sử sao lưu database từ file db_cloud_backup_history.json.
    /// Toàn bộ các trường định danh nhạy cảm (Email, FileId, WebUrl) được tự động giải mã AES-256 khi đọc.
    /// </summary>
    public async Task<DatabaseCloudHistoryFile> LoadHistoryAsync()
    {
        var filePath = GetHistoryFilePath();
        if (!File.Exists(filePath))
        {
            return new DatabaseCloudHistoryFile();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var result = JsonSerializer.Deserialize<DatabaseCloudHistoryFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return result ?? new DatabaseCloudHistoryFile();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi đọc file lịch sử sao lưu database: {Message}", ex.Message);
            return new DatabaseCloudHistoryFile();
        }
    }

    /// <summary>
    /// Lưu danh sách lịch sử sao lưu database vào file db_cloud_backup_history.json.
    /// Dữ liệu nhạy cảm được bảo vệ an toàn bằng AES-256 + Salt ngẫu nhiên.
    /// </summary>
    public async Task SaveHistoryAsync(DatabaseCloudHistoryFile history)
    {
        var filePath = GetHistoryFilePath();
        history.LastUpdated = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Tạo bản snapshot hoàn chỉnh và sạch của SQLite database đang chạy bằng lệnh 'VACUUM INTO', sau đó nén thành file .zip.
    /// Không gây khóa bảng (non-locking) và tương thích hoàn toàn chế độ WAL.
    /// </summary>
    public async Task<string> CreateDatabaseSnapshotZipAsync(string destinationZipPath, CancellationToken cancellationToken = default)
    {
        var tempDir = GetTempDirectory();
        var tempDbSnapshotPath = Path.Combine(tempDir, $"snapshot_{Guid.NewGuid():N}.db");
        var destDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        try
        {
            // 1. Thực thi VACUUM INTO để sinh snapshot chuẩn từ SQLite connection
            using (var conn = new SqliteConnection($"Data Source={_databaseService.DbPath}"))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO @snapshotPath;";
                    cmd.Parameters.AddWithValue("@snapshotPath", tempDbSnapshotPath);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // 2. Tạo manifest thông tin metadata cho bản sao lưu DB
            var games = await _databaseService.GetGameHistoriesAsync();
            var records = await _databaseService.GetBackupHistoryAsync();

            var meta = new
            {
                BackupTime = DateTime.UtcNow,
                AppVersion = UpdateService.CurrentAppVersion,
                GameCount = games.Count,
                SnapshotCount = records.Count,
                OriginalDbPath = _databaseService.DbPath,
                MachineName = Environment.MachineName
            };
            var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });

            // 3. Nén file snapshot .db và manifest.json vào destinationZipPath
            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            using (var zipStream = new FileStream(destinationZipPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // Thêm file SQLite database snapshot
                var dbEntry = archive.CreateEntry("save_backup.db", CompressionLevel.Optimal);
                using (var entryStream = dbEntry.Open())
                using (var fs = new FileStream(tempDbSnapshotPath, FileMode.Open, FileAccess.Read))
                {
                    await fs.CopyToAsync(entryStream, cancellationToken);
                }

                // Thêm file metadata
                var metaEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
                using (var metaStream = metaEntry.Open())
                using (var sw = new StreamWriter(metaStream, System.Text.Encoding.UTF8))
                {
                    await sw.WriteAsync(metaJson);
                }
            }

            return destinationZipPath;
        }
        finally
        {
            if (File.Exists(tempDbSnapshotPath))
            {
                try { File.Delete(tempDbSnapshotPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Thực hiện sao lưu Database lên Cloud theo lựa chọn ("GoogleDrive", "OneDrive", hoặc "Both").
    /// </summary>
    public async Task<DatabaseCloudBackupEntry> BackupDatabaseToCloudAsync(
        string targetOption, 
        IProgress<BackupProgress>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        using var trace = LoggingService.BeginTrace("Database_BackupDatabaseToCloudAsync", new { TargetOption = targetOption });
        var option = string.IsNullOrWhiteSpace(targetOption) ? "GoogleDrive" : targetOption.Trim();
        var providersToUpload = new List<ICloudStorageService>();

        if (string.Equals(option, "Both", StringComparison.OrdinalIgnoreCase))
        {
            if (_cloudManager.GoogleDrive.IsAuthenticated) providersToUpload.Add(_cloudManager.GoogleDrive);
            if (_cloudManager.OneDrive.IsAuthenticated) providersToUpload.Add(_cloudManager.OneDrive);

            if (providersToUpload.Count == 0)
            {
                throw new InvalidOperationException("Chưa có dịch vụ đám mây nào (Google Drive hoặc OneDrive) được kết nối. Vui lòng đăng nhập ít nhất một dịch vụ trước khi sao lưu.");
            }
        }
        else if (string.Equals(option, "OneDrive", StringComparison.OrdinalIgnoreCase))
        {
            if (!_cloudManager.OneDrive.IsAuthenticated)
            {
                throw new InvalidOperationException("Tài khoản Microsoft OneDrive chưa được đăng nhập. Vui lòng kết nối OneDrive tại tab Cài đặt.");
            }
            providersToUpload.Add(_cloudManager.OneDrive);
        }
        else
        {
            if (!_cloudManager.GoogleDrive.IsAuthenticated)
            {
                throw new InvalidOperationException("Tài khoản Google Drive chưa được đăng nhập. Vui lòng kết nối Google Drive tại tab Cài đặt.");
            }
            providersToUpload.Add(_cloudManager.GoogleDrive);
        }

        progress?.Report(new BackupProgress
        {
            Percent = 10,
            Message = "Đang tạo bản chụp (Snapshot) cơ sở dữ liệu SQLite..."
        });

        // 1. Tạo snapshot zip
        var timestampStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipFileName = $"Omnisave_db_backup_{timestampStr}.zip";
        var tempZipPath = Path.Combine(GetTempDirectory(), zipFileName);

        await CreateDatabaseSnapshotZipAsync(tempZipPath, cancellationToken);
        var fileInfo = new FileInfo(tempZipPath);
        var fileSizeBytes = fileInfo.Length;

        var games = await _databaseService.GetGameHistoriesAsync();
        var records = await _databaseService.GetBackupHistoryAsync();

        var entry = new DatabaseCloudBackupEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            BackupTime = DateTime.UtcNow,
            FileName = zipFileName,
            FileSizeBytes = fileSizeBytes,
            GameCount = games.Count,
            SnapshotCount = records.Count,
            AppVersion = UpdateService.CurrentAppVersion,
            CloudUploads = new List<DatabaseCloudUploadItem>()
        };

        try
        {
            int uploadedCount = 0;
            foreach (var provider in providersToUpload)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new BackupProgress
                {
                    Percent = 30 + (uploadedCount * 30),
                    Message = $"Đang tải database lên {provider.DisplayName}..."
                });

                var uploadResult = await provider.UploadFileAsync(
                    tempZipPath, 
                    RemoteDatabaseFolderName, 
                    progress, 
                    cancellationToken);

                if (uploadResult.Success)
                {
                    var item = new DatabaseCloudUploadItem
                    {
                        Provider = provider.ProviderName,
                        UploadedAt = DateTime.UtcNow,
                        AccountEmail = provider.CurrentAccountEmail, // Tự động mã hóa AES-256
                        FileId = uploadResult.FileId,                 // Tự động mã hóa AES-256
                        WebUrl = uploadResult.WebUrl                  // Tự động mã hóa AES-256
                    };

                    entry.CloudUploads.Add(item);
                    uploadedCount++;
                }
                else
                {
                    LoggingService.Warn("Tải database lên {Provider} thất bại: {Error}", provider.DisplayName, uploadResult.ErrorMessage);
                }
            }

            if (entry.CloudUploads.Count == 0)
            {
                throw new InvalidOperationException("Không thể tải bản sao lưu database lên bất kỳ dịch vụ đám mây nào. Vui lòng kiểm tra lại kết nối mạng.");
            }

            progress?.Report(new BackupProgress
            {
                Percent = 90,
                Message = "Đang lưu trữ lịch sử sao lưu an toàn (Mã hóa AES-256)..."
            });

            // 2. Cập nhật file lịch sử db_cloud_backup_history.json
            var history = await LoadHistoryAsync();
            history.Entries.Insert(0, entry);

            // 3. Áp dụng Retention Policy (Xóa bớt bản cũ trên Cloud nếu vượt quá giới hạn)
            var maxToKeep = Math.Max(1, AppConfigService.GetConfig().MaxDatabaseCloudBackupsToKeep);
            await ApplyRetentionPolicyAsync(history, maxToKeep, cancellationToken);

            await SaveHistoryAsync(history);

            progress?.Report(new BackupProgress
            {
                Percent = 100,
                Message = "Sao lưu cơ sở dữ liệu lên Cloud hoàn tất thành công!"
            });

            LoggingService.LogAction("Database_Cloud_Backup_Success", new
            {
                entry.FileName,
                entry.FileSizeBytes,
                entry.GameCount,
                entry.SnapshotCount,
                Clouds = string.Join(", ", entry.CloudUploads.Select(c => c.Provider))
            });

            return entry;
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Áp dụng chính sách lưu trữ (Retention Policy), giữ lại tối đa 'maxToKeep' (mặc định 5) bản sao lưu database gần nhất.
    /// Nếu vượt quá số lượng tối đa, tự động gọi API xóa file đó trên Cloud và xóa khỏi file lịch sử db_cloud_backup_history.json.
    /// </summary>
    public async Task ApplyRetentionPolicyAsync(DatabaseCloudHistoryFile history, int maxToKeep, CancellationToken ct = default)
    {
        try
        {
            // Sắp xếp các bản ghi từ mới nhất đến cũ nhất theo thời gian sao lưu
            history.Entries = history.Entries.OrderByDescending(e => e.BackupTime).ToList();

            if (history.Entries.Count > maxToKeep)
            {
                var excessEntries = history.Entries.Skip(maxToKeep).ToList();
                foreach (var entry in excessEntries)
                {
                    foreach (var upload in entry.CloudUploads)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(upload.FileId))
                            {
                                var provider = _cloudManager.GetProvider(upload.Provider);
                                if (provider != null)
                                {
                                    await provider.DeleteFileAsync(upload.FileId, ct);
                                    LoggingService.Info("Đã xóa bản sao lưu database cũ trên Cloud ({Provider}): {FileName}", upload.Provider, entry.FileName);
                                }
                            }
                        }
                        catch (Exception delEx)
                        {
                            LoggingService.Warn("Không thể xóa bản sao lưu database cũ trên {Provider} ({FileId}): {Message}", upload.Provider, upload.FileId, delEx.Message);
                        }
                    }
                }

                // Xóa hoàn toàn các entry vượt quá số lượng tối đa khỏi lịch sử
                history.Entries = history.Entries.Take(maxToKeep).ToList();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi áp dụng Retention Policy cho database cloud backup: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Thuật toán lựa chọn duy nhất 1 Cloud Source tốt nhất để tải file phục vụ khôi phục:
    /// 1. Nếu bản sao lưu chỉ lưu ở 1 drive: chọn drive đó.
    /// 2. Nếu lưu ở cả 2 drive: ưu tiên drive hiện đang đăng nhập (authenticated).
    ///    Nếu cả hai đều đăng nhập: ưu tiên Google Drive.
    /// </summary>
    public DatabaseCloudUploadItem? SelectBestCloudSource(DatabaseCloudBackupEntry entry)
    {
        if (entry.CloudUploads.Count == 0) return null;
        if (entry.CloudUploads.Count == 1) return entry.CloudUploads[0];

        var gdrive = entry.CloudUploads.FirstOrDefault(u => string.Equals(u.Provider, "GoogleDrive", StringComparison.OrdinalIgnoreCase));
        var onedrive = entry.CloudUploads.FirstOrDefault(u => string.Equals(u.Provider, "OneDrive", StringComparison.OrdinalIgnoreCase));

        bool gdriveAuth = _cloudManager.GoogleDrive.IsAuthenticated;
        bool onedriveAuth = _cloudManager.OneDrive.IsAuthenticated;

        if (gdriveAuth && gdrive != null) return gdrive;
        if (onedriveAuth && onedrive != null) return onedrive;

        return entry.CloudUploads[0];
    }

    /// <summary>
    /// Khôi phục database từ một mốc lịch sử (Entry).
    /// Hệ thống CHỈ TẢI TỪ 1 DRIVE DUY NHẤT. Nếu tải thất bại và có drive thứ 2, tự động fallback sang drive thứ 2.
    /// Luôn tạo bản sao an toàn (.bak) của save_backup.db hiện tại và tự động rollback nếu gặp sự cố.
    /// </summary>
    public async Task RestoreDatabaseFromHistoryEntryAsync(
        DatabaseCloudBackupEntry entry,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var trace = LoggingService.BeginTrace("Database_RestoreDatabaseFromHistoryEntryAsync", new { BackupDate = entry.BackupTime });
        if (entry.CloudUploads.Count == 0)
        {
            throw new InvalidOperationException("Bản sao lưu này không chứa thông tin file trên Cloud.");
        }

        var primarySource = SelectBestCloudSource(entry);
        if (primarySource == null)
        {
            throw new InvalidOperationException("Không tìm thấy nguồn tải đám mây hợp lệ.");
        }

        var secondarySource = entry.CloudUploads.FirstOrDefault(u => u != primarySource);

        var tempDir = GetTempDirectory();
        var downloadZipPath = Path.Combine(tempDir, $"restore_{Guid.NewGuid():N}.zip");
        var extractedDir = Path.Combine(tempDir, $"extracted_{Guid.NewGuid():N}");

        progress?.Report(new BackupProgress
        {
            Percent = 15,
            Message = $"Đang kết nối và tải file database từ {primarySource.Provider}..."
        });

        bool downloaded = false;
        string downloadError = string.Empty;

        // Thử tải từ nguồn ưu tiên
        try
        {
            var primaryProvider = _cloudManager.GetProvider(primarySource.Provider);
            if (primaryProvider == null || !primaryProvider.IsAuthenticated)
            {
                throw new InvalidOperationException($"Dịch vụ {primarySource.Provider} hiện chưa được kết nối.");
            }

            await primaryProvider.DownloadFileAsync(primarySource.FileId, downloadZipPath, progress, cancellationToken);
            downloaded = true;
        }
        catch (Exception ex)
        {
            downloadError = ex.Message;
            LoggingService.Warn("Tải database từ nguồn chính {Provider} thất bại: {Error}", primarySource.Provider, ex.Message);
        }

        // Tự động fallback sang nguồn thứ 2 nếu có
        if (!downloaded && secondarySource != null)
        {
            try
            {
                var secondaryProvider = _cloudManager.GetProvider(secondarySource.Provider);
                if (secondaryProvider != null && secondaryProvider.IsAuthenticated)
                {
                    progress?.Report(new BackupProgress
                    {
                        Percent = 30,
                        Message = $"Đang thử tải lại từ nguồn dự phòng {secondarySource.Provider}..."
                    });

                    await secondaryProvider.DownloadFileAsync(secondarySource.FileId, downloadZipPath, progress, cancellationToken);
                    downloaded = true;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Tải database từ nguồn dự phòng {Provider} thất bại: {Error}", secondarySource.Provider, ex.Message);
            }
        }

        if (!downloaded)
        {
            throw new InvalidOperationException($"Không thể tải file database từ Cloud: {downloadError}");
        }

        progress?.Report(new BackupProgress
        {
            Percent = 60,
            Message = "Đang giải nén và kiểm tra tính toàn vẹn của cơ sở dữ liệu..."
        });

        // Giải nén file zip kiểm tra
        if (Directory.Exists(extractedDir)) Directory.Delete(extractedDir, true);
        Directory.CreateDirectory(extractedDir);
        ZipFile.ExtractToDirectory(downloadZipPath, extractedDir);

        var restoredDbFile = Path.Combine(extractedDir, "save_backup.db");
        if (!File.Exists(restoredDbFile))
        {
            // Tìm file .db bất kỳ bên trong zip
            var anyDb = Directory.GetFiles(extractedDir, "*.db", SearchOption.AllDirectories).FirstOrDefault();
            if (anyDb != null)
            {
                restoredDbFile = anyDb;
            }
            else
            {
                throw new InvalidOperationException("Gói sao lưu không chứa tệp cơ sở dữ liệu SQLite (.db) hợp lệ.");
            }
        }

        progress?.Report(new BackupProgress
        {
            Percent = 80,
            Message = "Đang sao lưu khẩn cấp DB hiện tại và áp dụng dữ liệu mới..."
        });

        // 1. Tạo Safety Backup của database hiện tại
        var currentDbPath = _databaseService.DbPath;
        var backupBakPath = currentDbPath + ".bak";
        var walPath = currentDbPath + "-wal";
        var shmPath = currentDbPath + "-shm";

        try
        {
            if (File.Exists(currentDbPath))
            {
                // Thực hiện checkpoint để dồn toàn bộ dữ liệu từ WAL vào file chính trước khi backup
                try
                {
                    SqliteConnection.ClearAllPools();
                    using (var cpConn = new SqliteConnection($"Data Source={currentDbPath}"))
                    {
                        await cpConn.OpenAsync(cancellationToken);
                        using var cpCmd = cpConn.CreateCommand();
                        cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        await cpCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                catch { }

                SqliteConnection.ClearAllPools();
                File.Copy(currentDbPath, backupBakPath, true);
            }

            // 2. Giải phóng kết nối SQLite và xóa các file WAL/SHM cũ nếu có
            SqliteConnection.ClearAllPools();
            if (File.Exists(walPath)) try { File.Delete(walPath); } catch { }
            if (File.Exists(shmPath)) try { File.Delete(shmPath); } catch { }

            // 3. Ghi đè file database mới
            File.Copy(restoredDbFile, currentDbPath, true);

            // 4. Kiểm tra tính toàn vẹn (Integrity Check) của database mới
            using (var testConn = new SqliteConnection($"Data Source={currentDbPath}"))
            {
                await testConn.OpenAsync(cancellationToken);
                using (var cmd = testConn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA quick_check;";
                    var result = (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString();
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Kiểm tra toàn vẹn database thất bại: {result}");
                    }
                }
            }

            // Thành công: xóa file .bak
            if (File.Exists(backupBakPath))
            {
                try { File.Delete(backupBakPath); } catch { }
            }

            progress?.Report(new BackupProgress
            {
                Percent = 100,
                Message = "Khôi phục cơ sở dữ liệu thành công!"
            });

            LoggingService.LogAction("Database_Cloud_Restore_Success", new
            {
                entry.FileName,
                SourceProvider = downloaded ? primarySource.Provider : "Unknown",
                entry.GameCount,
                entry.SnapshotCount
            });
        }
        catch (Exception ex)
        {
            // TỰ ĐỘNG ROLLBACK NẾU GẶP SỰ CỐ
            LoggingService.Error(ex, "Khôi phục database gặp lỗi, đang hoàn tác an toàn: {Message}", ex.Message);
            try
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(walPath)) try { File.Delete(walPath); } catch { }
                if (File.Exists(shmPath)) try { File.Delete(shmPath); } catch { }

                if (File.Exists(backupBakPath))
                {
                    File.Copy(backupBakPath, currentDbPath, true);
                    File.Delete(backupBakPath);
                }
            }
            catch (Exception rollbackEx)
            {
                LoggingService.Error(rollbackEx, "Lỗi khi hoàn tác database từ .bak: {Message}", rollbackEx.Message);
            }

            throw new InvalidOperationException($"Khôi phục database thất bại: {ex.Message}. Hệ thống đã tự động hoàn tác dữ liệu về trạng thái an toàn ban đầu.");
        }
        finally
        {
            // Dọn dẹp thư mục tạm
            if (File.Exists(downloadZipPath)) try { File.Delete(downloadZipPath); } catch { }
            if (Directory.Exists(extractedDir)) try { Directory.Delete(extractedDir, true); } catch { }
        }
    }

    /// <summary>
    /// Quét tìm các bản sao lưu database trên Cloud (dành cho máy mới cài lại chưa có file db_cloud_backup_history.json).
    /// Tự động đồng bộ và tái lập file lịch sử được mã hóa AES-256.
    /// </summary>
    public async Task<DatabaseCloudHistoryFile> ScanCloudForDatabaseBackupsAsync(
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var history = await LoadHistoryAsync();
        var providers = new[] { "GoogleDrive", "OneDrive" };
        int scannedCount = 0;

        foreach (var provName in providers)
        {
            var provider = _cloudManager.GetProvider(provName);
            if (provider == null || !provider.IsAuthenticated) continue;

            progress?.Report(new BackupProgress
            {
                Percent = 30,
                Message = $"Đang quét bản lưu database trên {provider.DisplayName}..."
            });

            try
            {
                var files = await provider.ListBackupsAsync(RemoteDatabaseFolderName, cancellationToken);
                foreach (var file in files)
                {
                    if (string.IsNullOrWhiteSpace(file.Name) || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Tìm entry có tên file trùng
                    var existingEntry = history.Entries.FirstOrDefault(e =>
                        string.Equals(e.FileName, file.Name, StringComparison.OrdinalIgnoreCase));

                    if (existingEntry == null)
                    {
                        existingEntry = new DatabaseCloudBackupEntry
                        {
                            FileName = file.Name,
                            FileSizeBytes = file.SizeBytes,
                            BackupTime = file.ModifiedTime ?? DateTime.UtcNow,
                            GameCount = 0,
                            SnapshotCount = 0,
                            AppVersion = "Omnisave"
                        };
                        history.Entries.Add(existingEntry);
                        scannedCount++;
                    }

                    // Kiểm tra xem provider này đã có trong entry chưa
                    var existingUpload = existingEntry.CloudUploads.FirstOrDefault(u =>
                        string.Equals(u.Provider, provName, StringComparison.OrdinalIgnoreCase));

                    if (existingUpload == null)
                    {
                        var uploadItem = new DatabaseCloudUploadItem
                        {
                            Provider = provName,
                            UploadedAt = file.ModifiedTime ?? DateTime.UtcNow
                        };
                        uploadItem.FileId = file.Id;
                        uploadItem.AccountEmail = provider.CurrentAccountEmail ?? string.Empty;
                        uploadItem.WebUrl = file.WebUrl ?? string.Empty;
                        existingEntry.CloudUploads.Add(uploadItem);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi khi quét bản lưu database trên {Provider}: {Error}", provName, ex.Message);
            }
        }

        // Sắp xếp các bản sao lưu từ mới nhất đến cũ nhất
        history.Entries = history.Entries.OrderByDescending(e => e.BackupTime).ToList();
        await SaveHistoryAsync(history);

        progress?.Report(new BackupProgress
        {
            Percent = 100,
            Message = $"Đã hoàn tất quét Cloud. Tìm thấy {history.Entries.Count} bản sao lưu database."
        });

        return history;
    }
}

