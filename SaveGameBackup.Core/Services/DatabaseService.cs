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

    public DatabaseService(string? customDbPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customDbPath))
        {
            _dbPath = customDbPath;
        }
        else
        {
            var configuredPath = AppConfigService.GetConfiguredDatabasePath();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                _dbPath = configuredPath;
            }
            else
            {
                _dbPath = Path.Combine(GetDefaultProjectRoot(), "save_backup.db");
            }
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
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            var isInsideBuildOutput = current.FullName.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                                      current.FullName.EndsWith(@"\obj", StringComparison.OrdinalIgnoreCase);

            if (current.GetFiles("*.slnx").Length > 0 ||
                current.GetFiles("*.sln").Length > 0 ||
                current.GetFiles("LaunchApp.bat").Length > 0 ||
                (!isInsideBuildOutput && current.GetFiles("*.exe").Length > 0) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
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

    public Task<List<BackupHistoryDetail>> GetHistoryDetailsByGameIdAsync(long gameHistoryId)
    {
        return _historyRepository.GetHistoryDetailsByGameIdAsync(gameHistoryId);
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

    public Task DeleteHistoryDetailAsync(long detailId, long gameHistoryId)
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
            BackupRootDirectory = string.IsNullOrWhiteSpace(config.BackupRootDirectory) ? defaultBackupDir : config.BackupRootDirectory,
            CreateTimestampSubfolder = config.CreateTimestampSubfolder,
            AutoCompressZip = true, // Mặc định và bắt buộc nén zip 100%
            OverwriteExisting = true
        });
    }

    public Task SaveSettingsAsync(AppSettings settings)
    {
        var config = AppConfigService.GetConfig();
        config.BackupRootDirectory = settings.BackupRootDirectory;
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
}
