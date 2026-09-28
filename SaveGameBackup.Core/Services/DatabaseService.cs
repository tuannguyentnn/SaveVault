using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SaveGameBackup.Core.Data;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Quản lý dữ liệu SQLite kết hợp Dapper Micro-ORM và Repository Pattern.
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IGameCacheRepository _cacheRepository;
    private readonly IBackupHistoryRepository _historyRepository;

    public static string DefaultDbPath => Path.Combine(GetDefaultProjectRoot(), "data", "database", "save_backup.db");
    public static string DefaultBackupDir => Path.Combine(GetDefaultProjectRoot(), "data", "backups");

    public DatabaseService(string? customDbPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customDbPath))
        {
            _dbPath = customDbPath;
        }
        else
        {
            _dbPath = DefaultDbPath;
        }

        var dbDir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dbDir) && !Directory.Exists(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _connectionString = _connectionFactory.ConnectionString;
        _cacheRepository = new GameCacheRepository(_connectionFactory);
        _historyRepository = new BackupHistoryRepository(_connectionFactory);

        InitializeDatabase();
    }

    public string DbPath => _dbPath;
    public ISqliteConnectionFactory ConnectionFactory => _connectionFactory;
    public IGameCacheRepository CacheRepository => _cacheRepository;
    public IBackupHistoryRepository HistoryRepository => _historyRepository;

    public static string GetDefaultProjectRoot()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirInfo = new DirectoryInfo(baseDir);

        // Trường hợp chạy từ thư mục app/ của gói xuất bản phân cấp (SaveVault/app/)
        if (dirInfo.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && dirInfo.Parent != null)
        {
            var parent = dirInfo.Parent;
            if (File.Exists(Path.Combine(parent.FullName, "SaveVault.exe")) ||
                Directory.Exists(Path.Combine(parent.FullName, "data")))
            {
                return parent.FullName;
            }
        }

        var current = dirInfo;
        while (current != null)
        {
            var isInsideBuildOutput = current.FullName.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.EndsWith(@"\obj", StringComparison.OrdinalIgnoreCase);

            if (current.GetFiles("*.slnx").Length > 0 ||
                current.GetFiles("*.sln").Length > 0 ||
                current.GetFiles("LaunchApp.bat").Length > 0 ||
                (!isInsideBuildOutput && !current.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && current.GetFiles("*.exe").Length > 0) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return baseDir;
    }

    private void InitializeDatabase()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        const string schemaSql = @"
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS games_cache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT UNIQUE NOT NULL,
                NormalizedName TEXT NOT NULL,
                WikiPageTitle TEXT,
                SteamAppId TEXT,
                RawPatternsJson TEXT NOT NULL,
                Source TEXT,
                CoverUrl TEXT,
                LastUpdated TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS backup_history (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT UNIQUE NOT NULL,
                BackupCount INTEGER NOT NULL DEFAULT 1,
                LatestBackupPath TEXT NOT NULL,
                LatestBackupDate TEXT NOT NULL,
                TotalSizeBytes INTEGER NOT NULL DEFAULT 0,
                LatestSizeBytes INTEGER NOT NULL DEFAULT 0,
                LatestFileCount INTEGER NOT NULL DEFAULT 0,
                SavePaths TEXT,
                Status TEXT NOT NULL DEFAULT 'Success',
                Note TEXT,
                CoverUrl TEXT,
                CoverPath TEXT
            );

            CREATE TABLE IF NOT EXISTS backup_history_details (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameHistoryId INTEGER NOT NULL,
                GameName TEXT NOT NULL,
                BackupPath TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                SavePaths TEXT,
                ManifestJson TEXT,
                FileCount INTEGER NOT NULL,
                TotalSizeBytes INTEGER NOT NULL,
                BackupDate TEXT NOT NULL,
                IsCompressed INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Success',
                Note TEXT,
                IsCloudSynced INTEGER NOT NULL DEFAULT 0,
                CloudProvider TEXT,
                CloudFileId TEXT,
                CloudFileName TEXT,
                CloudSyncDate TEXT,
                CloudSyncJson TEXT,
                CoverUrl TEXT,
                CoverPath TEXT
            );

            CREATE TABLE IF NOT EXISTS restore_history (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT NOT NULL,
                BackupHistoryDetailId INTEGER,
                SourcePath TEXT NOT NULL,
                RestoreDate TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Success',
                RevertZipPath TEXT,
                RestoredPathsJson TEXT,
                FileCount INTEGER NOT NULL DEFAULT 0,
                TotalSizeBytes INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT,
                IsCloud INTEGER NOT NULL DEFAULT 0,
                CloudProvider TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_restore_history_gamename ON restore_history(GameName);
            CREATE INDEX IF NOT EXISTS idx_restore_history_date ON restore_history(RestoreDate);

            CREATE TABLE IF NOT EXISTS settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ";

        connection.Execute(schemaSql);

        EnsureColumnExists(connection, "games_cache", "CoverUrl", "TEXT");
        EnsureColumnExists(connection, "backup_history", "CoverUrl", "TEXT");
        EnsureColumnExists(connection, "backup_history", "CoverPath", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CoverUrl", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CoverPath", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "IsCloudSynced", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "backup_history_details", "CloudProvider", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudFileId", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudFileName", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudSyncDate", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudSyncJson", "TEXT");
    }

    private static void EnsureColumnExists(System.Data.IDbConnection connection, string table, string column, string columnDef)
    {
        try
        {
            var columns = connection.Query<string>($"PRAGMA table_info({table});");
            // Check with table_info columns
            var exists = connection.Query<dynamic>($"PRAGMA table_info({table});")
                                   .Any(row => string.Equals((string)row.name, column, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {columnDef};");
            }
        }
        catch
        {
            // Bỏ qua nếu cột đã tồn tại hoặc bảng chưa sẵn sàng
        }
    }

    // --- Game Cache Delegation to IGameCacheRepository (Dapper) ---

    public Task<GameSaveInfo?> GetCachedGameAsync(string gameName)
    {
        return _cacheRepository.GetCachedGameAsync(gameName);
    }

    public Task SaveGameCacheAsync(GameSaveInfo game)
    {
        return _cacheRepository.SaveGameCacheAsync(game);
    }

    public Task<List<GameSaveInfo>> GetRecentCachedGamesAsync(int limit = 10)
    {
        return _cacheRepository.GetRecentCachedGamesAsync(limit);
    }

    public Task TouchGameCacheAsync(string gameName)
    {
        return _cacheRepository.TouchGameCacheAsync(gameName);
    }

    // --- History Delegation to IBackupHistoryRepository (Dapper) ---

    public Task<long> InsertOrUpdateBackupHistoryAsync(BackupHistoryDetail detail)
    {
        return _historyRepository.InsertOrUpdateBackupHistoryAsync(detail);
    }

    public Task<List<GameHistoryEntry>> GetGameHistoriesAsync()
    {
        return _historyRepository.GetGameHistoriesAsync();
    }

    public Task<PagedResult<GameHistoryEntry>> GetGameHistoriesPagedAsync(int pageNumber, int pageSize, string? filterText = null, string sortColumn = "Date", bool sortAscending = false)
    {
        return _historyRepository.GetGameHistoriesPagedAsync(pageNumber, pageSize, filterText, sortColumn, sortAscending);
    }

    public Task<List<BackupHistoryDetail>> GetHistoryDetailsByGameIdAsync(long gameHistoryId)
    {
        return _historyRepository.GetHistoryDetailsByGameIdAsync(gameHistoryId);
    }

    public Task<PagedResult<BackupHistoryDetail>> GetHistoryDetailsPagedAsync(long gameHistoryId, int pageNumber, int pageSize, bool sortAscending = false)
    {
        return _historyRepository.GetHistoryDetailsPagedAsync(gameHistoryId, pageNumber, pageSize, sortAscending);
    }

    public Task UpdateCloudSyncDetailAsync(long detailId, bool isSynced, string provider, string fileId, string fileName, DateTime? syncDate, string? cloudSyncJson = null, string? backupPathJson = null)
    {
        return _historyRepository.UpdateCloudSyncDetailAsync(detailId, isSynced, provider, fileId, fileName, syncDate, cloudSyncJson, backupPathJson);
    }

    public Task UpdateSnapshotLocationsAsync(long detailId, BackupHistoryDetail detail)
    {
        return _historyRepository.UpdateSnapshotLocationsAsync(detailId, detail);
    }

    public Task UpdateCoverUrlAsync(string gameName, string coverUrl)
    {
        return _historyRepository.UpdateCoverUrlAsync(gameName, coverUrl);
    }

    public Task UpdateCoverPathAsync(string gameName, string coverPath)
    {
        return _historyRepository.UpdateCoverPathAsync(gameName, coverPath);
    }

    public Task<bool> DeleteHistoryDetailAsync(long detailId, long gameHistoryId)
    {
        return _historyRepository.DeleteHistoryDetailAsync(detailId, gameHistoryId);
    }

    public Task DeleteGameHistoryAsync(long gameHistoryId)
    {
        return _historyRepository.DeleteGameHistoryAsync(gameHistoryId);
    }

    public Task<List<BackupRecord>> GetBackupHistoryAsync()
    {
        return _historyRepository.GetBackupHistoryAsync();
    }

    public async Task<long> InsertBackupRecordAsync(BackupRecord record)
    {
        var detail = new BackupHistoryDetail
        {
            GameName = record.GameName,
            BackupPath = record.BackupPath,
            SourcePath = record.SourcePath,
            SavePaths = record.SavePaths,
            FileCount = record.FileCount,
            TotalSizeBytes = record.TotalSizeBytes,
            BackupDate = record.BackupDate,
            IsCompressed = record.IsCompressed,
            Status = record.Status,
            Note = record.Note
        };

        var detailId = await _historyRepository.InsertOrUpdateBackupHistoryAsync(detail);
        record.Id = detailId;
        return detailId;
    }

    public Task DeleteBackupRecordAsync(long id)
    {
        return _historyRepository.DeleteBackupRecordAsync(id);
    }

    // --- Settings Delegation to AppConfigService ---

    public Task<string?> GetSettingAsync(string key, string? defaultValue = null)
    {
        return Task.FromResult(AppConfigService.GetSetting(key, defaultValue));
    }

    public Task SaveSettingAsync(string key, string value)
    {
        AppConfigService.SaveSetting(key, value);
        return Task.CompletedTask;
    }

    public Task<AppSettings> LoadSettingsAsync(string defaultBackupDir)
    {
        var config = AppConfigService.GetConfig();
        return Task.FromResult(new AppSettings
        {
            BackupRootDirectory = DefaultBackupDir,
            CreateTimestampSubfolder = config.CreateTimestampSubfolder,
            AutoCompressZip = true, // Mặc định và bắt buộc nén zip 100%
            OverwriteExisting = true
        });
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        var config = AppConfigService.GetConfig();
        config.CreateTimestampSubfolder = settings.CreateTimestampSubfolder;
        config.AutoCompressZip = true;
        AppConfigService.SaveConfig(config);
        return Task.CompletedTask;
    }

    public static string NormalizeGameName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var lower = name.Trim().ToLowerInvariant();
        var chars = lower.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    // --- Restore History Management ---

    public async Task<long> InsertRestoreHistoryAsync(RestoreHistoryRecord record)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO restore_history (
                GameName, BackupHistoryDetailId, SourcePath, RestoreDate, Status,
                RevertZipPath, RestoredPathsJson, FileCount, TotalSizeBytes,
                ErrorMessage, IsCloud, CloudProvider
            ) VALUES (
                @GameName, @BackupHistoryDetailId, @SourcePath, @RestoreDate, @Status,
                @RevertZipPath, @RestoredPathsJson, @FileCount, @TotalSizeBytes,
                @ErrorMessage, @IsCloud, @CloudProvider
            );
            SELECT last_insert_rowid();";

        var id = await connection.ExecuteScalarAsync<long>(sql, new
        {
            record.GameName,
            record.BackupHistoryDetailId,
            record.SourcePath,
            RestoreDate = record.RestoreDate.ToString("o"),
            record.Status,
            record.RevertZipPath,
            record.RestoredPathsJson,
            record.FileCount,
            record.TotalSizeBytes,
            record.ErrorMessage,
            IsCloud = record.IsCloud ? 1 : 0,
            record.CloudProvider
        });

        record.Id = id;
        return id;
    }

    public async Task<List<RestoreHistoryRecord>> GetRestoreHistoryByGameAsync(string gameName, int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT 
                Id, GameName, BackupHistoryDetailId, SourcePath, RestoreDate, Status,
                RevertZipPath, RestoredPathsJson, FileCount, TotalSizeBytes,
                ErrorMessage, IsCloud, CloudProvider
            FROM restore_history
            WHERE GameName = @GameName
            ORDER BY RestoreDate DESC
            LIMIT @Limit;";

        var rows = await connection.QueryAsync<dynamic>(sql, new { GameName = gameName, Limit = limit });
        return rows.Select(MapRestoreHistoryRecord).ToList();
    }

    public async Task<List<RestoreHistoryRecord>> GetAllRestoreHistoryAsync(int limit = 100)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT 
                Id, GameName, BackupHistoryDetailId, SourcePath, RestoreDate, Status,
                RevertZipPath, RestoredPathsJson, FileCount, TotalSizeBytes,
                ErrorMessage, IsCloud, CloudProvider
            FROM restore_history
            ORDER BY RestoreDate DESC
            LIMIT @Limit;";

        var rows = await connection.QueryAsync<dynamic>(sql, new { Limit = limit });
        return rows.Select(MapRestoreHistoryRecord).ToList();
    }

    public async Task<bool> DeleteRestoreHistoryAsync(long id)
    {
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync("DELETE FROM restore_history WHERE Id = @Id;", new { Id = id });
        return affected > 0;
    }

    private static RestoreHistoryRecord MapRestoreHistoryRecord(dynamic row)
    {
        var r = new RestoreHistoryRecord
        {
            Id = (long)row.Id,
            GameName = (string)row.GameName,
            BackupHistoryDetailId = row.BackupHistoryDetailId != null ? (long?)row.BackupHistoryDetailId : null,
            SourcePath = (string)(row.SourcePath ?? ""),
            Status = (string)(row.Status ?? "Success"),
            RevertZipPath = (string?)row.RevertZipPath,
            RestoredPathsJson = (string?)row.RestoredPathsJson,
            FileCount = row.FileCount != null ? (int)(long)row.FileCount : 0,
            TotalSizeBytes = row.TotalSizeBytes != null ? (long)row.TotalSizeBytes : 0,
            ErrorMessage = (string?)row.ErrorMessage,
            IsCloud = row.IsCloud != null && (long)row.IsCloud == 1,
            CloudProvider = (string?)row.CloudProvider
        };
        if (DateTime.TryParse((string)row.RestoreDate, out var dt))
        {
            r.RestoreDate = dt;
        }
        return r;
    }
}

