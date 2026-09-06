using System.Text.Json;
using Microsoft.Data.Sqlite;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dbPath;

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

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
    }

    public string DbPath => _dbPath;

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
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS games_cache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameName TEXT UNIQUE NOT NULL,
                NormalizedName TEXT NOT NULL,
                WikiPageTitle TEXT,
                SteamAppId TEXT,
                RawPatternsJson TEXT NOT NULL,
                Source TEXT,
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
                Note TEXT
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
                Note TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ";
        command.ExecuteNonQuery();
    }

    public async Task<GameSaveInfo?> GetCachedGameAsync(string gameName)
    {
        var normalized = NormalizeGameName(gameName);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT GameName, NormalizedName, WikiPageTitle, SteamAppId, RawPatternsJson, Source, LastUpdated 
            FROM games_cache 
            WHERE NormalizedName = $normalized OR LOWER(GameName) = LOWER($name)
            LIMIT 1;
        ";
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$name", gameName.Trim());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var rawJson = reader.GetString(4);
            var patterns = JsonSerializer.Deserialize<List<string>>(rawJson) ?? new List<string>();

            return new GameSaveInfo
            {
                GameName = reader.GetString(0),
                NormalizedName = reader.GetString(1),
                WikiPageTitle = reader.IsDBNull(2) ? null : reader.GetString(2),
                SteamAppId = reader.IsDBNull(3) ? null : reader.GetString(3),
                RawPatterns = patterns,
                Source = reader.IsDBNull(5) ? "Cache" : reader.GetString(5),
                LastScanned = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : null
            };
        }

        return null;
    }

    public async Task SaveGameCacheAsync(GameSaveInfo game)
    {
        if (string.IsNullOrWhiteSpace(game.GameName)) return;

        var normalized = NormalizeGameName(game.GameName);
        var rawJson = JsonSerializer.Serialize(game.RawPatterns);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO games_cache (GameName, NormalizedName, WikiPageTitle, SteamAppId, RawPatternsJson, Source, LastUpdated)
            VALUES ($name, $norm, $wiki, $steam, $json, $source, $updated)
            ON CONFLICT(GameName) DO UPDATE SET
                NormalizedName = excluded.NormalizedName,
                WikiPageTitle = excluded.WikiPageTitle,
                SteamAppId = excluded.SteamAppId,
                RawPatternsJson = excluded.RawPatternsJson,
                Source = excluded.Source,
                LastUpdated = excluded.LastUpdated;
        ";
        command.Parameters.AddWithValue("$name", game.GameName.Trim());
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$wiki", (object?)game.WikiPageTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$steam", (object?)game.SteamAppId ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", rawJson);
        command.Parameters.AddWithValue("$source", game.Source);
        command.Parameters.AddWithValue("$updated", DateTime.Now.ToString("o"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertOrUpdateBackupHistoryAsync(BackupHistoryDetail detail)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            long gameHistoryId;

            // Kiểm tra xem GameName đã có trong backup_history chưa
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT Id, BackupCount, TotalSizeBytes FROM backup_history WHERE LOWER(GameName) = LOWER($name) LIMIT 1;";
                checkCmd.Parameters.AddWithValue("$name", detail.GameName.Trim());

                using var reader = await checkCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    // Đã có -> UPDATE
                    gameHistoryId = reader.GetInt64(0);
                    var currentCount = reader.GetInt32(1);
                    var currentTotalSize = reader.GetInt64(2);
                    reader.Close();

                    using var updateCmd = connection.CreateCommand();
                    updateCmd.Transaction = transaction;
                    updateCmd.CommandText = @"
                        UPDATE backup_history 
                        SET BackupCount = $count,
                            LatestBackupPath = $latestPath,
                            LatestBackupDate = $latestDate,
                            TotalSizeBytes = $totalSize,
                            LatestSizeBytes = $latestSize,
                            LatestFileCount = $latestFiles,
                            SavePaths = $savePaths,
                            Status = $status,
                            Note = $note
                        WHERE Id = $id;
                    ";
                    updateCmd.Parameters.AddWithValue("$count", currentCount + 1);
                    updateCmd.Parameters.AddWithValue("$latestPath", detail.BackupPath);
                    updateCmd.Parameters.AddWithValue("$latestDate", detail.BackupDate.ToString("o"));
                    updateCmd.Parameters.AddWithValue("$totalSize", currentTotalSize + detail.TotalSizeBytes);
                    updateCmd.Parameters.AddWithValue("$latestSize", detail.TotalSizeBytes);
                    updateCmd.Parameters.AddWithValue("$latestFiles", detail.FileCount);
                    updateCmd.Parameters.AddWithValue("$savePaths", (object?)detail.SavePaths ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("$status", detail.Status);
                    updateCmd.Parameters.AddWithValue("$note", (object?)detail.Note ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("$id", gameHistoryId);
                    await updateCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    reader.Close();
                    // Chưa có -> INSERT
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
                        INSERT INTO backup_history (GameName, BackupCount, LatestBackupPath, LatestBackupDate, TotalSizeBytes, LatestSizeBytes, LatestFileCount, SavePaths, Status, Note)
                        VALUES ($name, 1, $latestPath, $latestDate, $totalSize, $latestSize, $latestFiles, $savePaths, $status, $note);
                        SELECT last_insert_rowid();
                    ";
                    insertCmd.Parameters.AddWithValue("$name", detail.GameName.Trim());
                    insertCmd.Parameters.AddWithValue("$latestPath", detail.BackupPath);
                    insertCmd.Parameters.AddWithValue("$latestDate", detail.BackupDate.ToString("o"));
                    insertCmd.Parameters.AddWithValue("$totalSize", detail.TotalSizeBytes);
                    insertCmd.Parameters.AddWithValue("$latestSize", detail.TotalSizeBytes);
                    insertCmd.Parameters.AddWithValue("$latestFiles", detail.FileCount);
                    insertCmd.Parameters.AddWithValue("$savePaths", (object?)detail.SavePaths ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$status", detail.Status);
                    insertCmd.Parameters.AddWithValue("$note", (object?)detail.Note ?? DBNull.Value);

                    var idObj = await insertCmd.ExecuteScalarAsync();
                    gameHistoryId = Convert.ToInt64(idObj);
                }
            }

            // Gán GameHistoryId cho detail
            detail.GameHistoryId = gameHistoryId;

            // INSERT vào backup_history_details
            using (var insertDetailCmd = connection.CreateCommand())
            {
                insertDetailCmd.Transaction = transaction;
                insertDetailCmd.CommandText = @"
                    INSERT INTO backup_history_details 
                    (GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note)
                    VALUES 
                    ($gameId, $game, $backup, $source, $savePaths, $manifest, $files, $size, $date, $comp, $status, $note);
                    SELECT last_insert_rowid();
                ";
                insertDetailCmd.Parameters.AddWithValue("$gameId", detail.GameHistoryId);
                insertDetailCmd.Parameters.AddWithValue("$game", detail.GameName);
                insertDetailCmd.Parameters.AddWithValue("$backup", detail.BackupPath);
                insertDetailCmd.Parameters.AddWithValue("$source", detail.SourcePath);
                insertDetailCmd.Parameters.AddWithValue("$savePaths", (object?)detail.SavePaths ?? DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$manifest", (object?)detail.ManifestJson ?? DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$files", detail.FileCount);
                insertDetailCmd.Parameters.AddWithValue("$size", detail.TotalSizeBytes);
                insertDetailCmd.Parameters.AddWithValue("$date", detail.BackupDate.ToString("o"));
                insertDetailCmd.Parameters.AddWithValue("$comp", detail.IsCompressed ? 1 : 0);
                insertDetailCmd.Parameters.AddWithValue("$status", detail.Status);
                insertDetailCmd.Parameters.AddWithValue("$note", (object?)detail.Note ?? DBNull.Value);

                var detailIdObj = await insertDetailCmd.ExecuteScalarAsync();
                detail.Id = Convert.ToInt64(detailIdObj);
            }

            transaction.Commit();
            return detail.Id;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<GameHistoryEntry>> GetGameHistoriesAsync()
    {
        var list = new List<GameHistoryEntry>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, GameName, BackupCount, LatestBackupPath, LatestBackupDate, TotalSizeBytes, LatestSizeBytes, LatestFileCount, SavePaths, Status, Note
            FROM backup_history
            ORDER BY LatestBackupDate DESC;
        ";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new GameHistoryEntry
            {
                Id = reader.GetInt64(0),
                GameName = reader.GetString(1),
                BackupCount = reader.GetInt32(2),
                LatestBackupPath = reader.GetString(3),
                LatestBackupDate = DateTime.TryParse(reader.GetString(4), out var dt) ? dt : DateTime.MinValue,
                TotalSizeBytes = reader.GetInt64(5),
                LatestSizeBytes = reader.GetInt64(6),
                LatestFileCount = reader.GetInt32(7),
                SavePaths = !reader.IsDBNull(8) ? reader.GetString(8) : string.Empty,
                Status = reader.GetString(9),
                Note = !reader.IsDBNull(10) ? reader.GetString(10) : null
            });
        }

        return list;
    }

    public async Task<List<BackupHistoryDetail>> GetHistoryDetailsByGameIdAsync(long gameHistoryId)
    {
        var list = new List<BackupHistoryDetail>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note
            FROM backup_history_details
            WHERE GameHistoryId = $gameId
            ORDER BY Id DESC;
        ";
        command.Parameters.AddWithValue("$gameId", gameHistoryId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new BackupHistoryDetail
            {
                Id = reader.GetInt64(0),
                GameHistoryId = reader.GetInt64(1),
                GameName = reader.GetString(2),
                BackupPath = reader.GetString(3),
                SourcePath = reader.GetString(4),
                SavePaths = !reader.IsDBNull(5) ? reader.GetString(5) : string.Empty,
                ManifestJson = !reader.IsDBNull(6) ? reader.GetString(6) : string.Empty,
                FileCount = reader.GetInt32(7),
                TotalSizeBytes = reader.GetInt64(8),
                BackupDate = DateTime.TryParse(reader.GetString(9), out var dt) ? dt : DateTime.MinValue,
                IsCompressed = reader.GetInt32(10) == 1,
                Status = reader.GetString(11),
                Note = !reader.IsDBNull(12) ? reader.GetString(12) : null
            });
        }

        return list;
    }

    public async Task DeleteHistoryDetailAsync(long detailId, long gameHistoryId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            // 1. Xóa bản ghi trong backup_history_details
            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM backup_history_details WHERE Id = $id;";
                deleteCmd.Parameters.AddWithValue("$id", detailId);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            // 2. Tính lại COUNT và SUM trong backup_history_details cho gameHistoryId
            int count = 0;
            long totalSize = 0;
            using (var sumCmd = connection.CreateCommand())
            {
                sumCmd.Transaction = transaction;
                sumCmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(TotalSizeBytes), 0) FROM backup_history_details WHERE GameHistoryId = $gameId;";
                sumCmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                using var reader = await sumCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    count = reader.GetInt32(0);
                    totalSize = reader.GetInt64(1);
                }
            }

            if (count == 0)
            {
                // Không còn snapshot nào -> Xóa luôn dòng game cha trong backup_history
                using var delParentCmd = connection.CreateCommand();
                delParentCmd.Transaction = transaction;
                delParentCmd.CommandText = "DELETE FROM backup_history WHERE Id = $gameId;";
                delParentCmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                await delParentCmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Vẫn còn snapshot -> Lấy snapshot mới nhất còn lại để cập nhật backup_history
                using (var latestCmd = connection.CreateCommand())
                {
                    latestCmd.Transaction = transaction;
                    latestCmd.CommandText = @"
                        SELECT BackupPath, BackupDate, TotalSizeBytes, FileCount, SavePaths
                        FROM backup_history_details
                        WHERE GameHistoryId = $gameId
                        ORDER BY Id DESC
                        LIMIT 1;
                    ";
                    latestCmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                    using var reader = await latestCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var latestPath = reader.GetString(0);
                        var latestDate = reader.GetString(1);
                        var latestSize = reader.GetInt64(2);
                        var latestFiles = reader.GetInt32(3);
                        var savePaths = !reader.IsDBNull(4) ? reader.GetString(4) : string.Empty;
                        reader.Close();

                        using var updateCmd = connection.CreateCommand();
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
                            UPDATE backup_history
                            SET BackupCount = $count,
                                TotalSizeBytes = $totalSize,
                                LatestBackupPath = $latestPath,
                                LatestBackupDate = $latestDate,
                                LatestSizeBytes = $latestSize,
                                LatestFileCount = $latestFiles,
                                SavePaths = $savePaths
                            WHERE Id = $gameId;
                        ";
                        updateCmd.Parameters.AddWithValue("$count", count);
                        updateCmd.Parameters.AddWithValue("$totalSize", totalSize);
                        updateCmd.Parameters.AddWithValue("$latestPath", latestPath);
                        updateCmd.Parameters.AddWithValue("$latestDate", latestDate);
                        updateCmd.Parameters.AddWithValue("$latestSize", latestSize);
                        updateCmd.Parameters.AddWithValue("$latestFiles", latestFiles);
                        updateCmd.Parameters.AddWithValue("$savePaths", (object?)savePaths ?? DBNull.Value);
                        updateCmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                        await updateCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task DeleteGameHistoryAsync(long gameHistoryId)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM backup_history_details WHERE GameHistoryId = $gameId;";
                cmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM backup_history WHERE Id = $gameId;";
                cmd.Parameters.AddWithValue("$gameId", gameHistoryId);
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<BackupRecord>> GetBackupHistoryAsync()
    {
        var list = new List<BackupRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        // Lấy từ backup_history_details nếu có, nếu không fallback
        command.CommandText = @"
            SELECT Id, GameName, BackupPath, SourcePath, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, SavePaths
            FROM backup_history_details
            ORDER BY Id DESC;
        ";

        try
        {
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new BackupRecord
                {
                    Id = reader.GetInt64(0),
                    GameName = reader.GetString(1),
                    BackupPath = reader.GetString(2),
                    SourcePath = reader.GetString(3),
                    FileCount = reader.GetInt32(4),
                    TotalSizeBytes = reader.GetInt64(5),
                    BackupDate = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.MinValue,
                    IsCompressed = reader.GetInt32(7) == 1,
                    Status = reader.GetString(8),
                    Note = reader.IsDBNull(9) ? null : reader.GetString(9),
                    SavePaths = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : string.Empty
                });
            }
        }
        catch
        {
            // Fallback nếu bảng details chưa có data
        }

        return list;
    }

    public async Task<long> InsertBackupRecordAsync(BackupRecord record)
    {
        // Wrapper chuyển tiếp sang InsertOrUpdateBackupHistoryAsync
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

        var detailId = await InsertOrUpdateBackupHistoryAsync(detail);
        record.Id = detailId;
        return detailId;
    }

    public async Task DeleteBackupRecordAsync(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GameHistoryId FROM backup_history_details WHERE Id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        var res = await cmd.ExecuteScalarAsync();
        if (res != null)
        {
            var gameId = Convert.ToInt64(res);
            await DeleteHistoryDetailAsync(id, gameId);
        }
        else
        {
            using var cmdDel = connection.CreateCommand();
            cmdDel.CommandText = "DELETE FROM backup_history WHERE Id = $id;";
            cmdDel.Parameters.AddWithValue("$id", id);
            await cmdDel.ExecuteNonQueryAsync();
        }
    }

    public async Task<string?> GetSettingAsync(string key, string? defaultValue = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM settings WHERE Key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);

        var val = await command.ExecuteScalarAsync();
        return val != null ? val.ToString() : defaultValue;
    }

    public async Task SaveSettingAsync(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO settings (Key, Value) VALUES ($key, $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
        ";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$val", value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<AppSettings> LoadSettingsAsync(string defaultBackupDir)
    {
        var backupDir = await GetSettingAsync("BackupRootDirectory", defaultBackupDir);
        var subfolder = await GetSettingAsync("CreateTimestampSubfolder", "true");
        var zip = await GetSettingAsync("AutoCompressZip", "false");
        var overwrite = await GetSettingAsync("OverwriteExisting", "true");

        return new AppSettings
        {
            BackupRootDirectory = string.IsNullOrWhiteSpace(backupDir) ? defaultBackupDir : backupDir,
            CreateTimestampSubfolder = bool.TryParse(subfolder, out var s) && s,
            AutoCompressZip = bool.TryParse(zip, out var z) && z,
            OverwriteExisting = !bool.TryParse(overwrite, out var o) || o
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await SaveSettingAsync("BackupRootDirectory", settings.BackupRootDirectory);
        await SaveSettingAsync("CreateTimestampSubfolder", settings.CreateTimestampSubfolder.ToString());
        await SaveSettingAsync("AutoCompressZip", settings.AutoCompressZip.ToString());
        await SaveSettingAsync("OverwriteExisting", settings.OverwriteExisting.ToString());
    }

    public static string NormalizeGameName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var lower = name.Trim().ToLowerInvariant();
        var chars = lower.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }
}

public class AppConfigFile
{
    public string? DatabasePath { get; set; }
    public string? BackupRootDirectory { get; set; }
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = false;
}

public static class AppConfigService
{
    private const string ConfigFileName = "app_config.json";
    private static AppConfigFile? _cachedConfig;
    private static readonly object _lock = new();

    public static string GetConfigFilePath()
    {
        return Path.Combine(DatabaseService.GetDefaultProjectRoot(), ConfigFileName);
    }

    /// <summary>
    /// Lấy cấu hình từ bộ nhớ đệm RAM. Nếu chưa nạp thì nạp từ app_config.json vào RAM.
    /// </summary>
    public static AppConfigFile GetConfig()
    {
        lock (_lock)
        {
            if (_cachedConfig != null) return _cachedConfig;
            _cachedConfig = LoadConfigFromFile();
            return _cachedConfig;
        }
    }

    /// <summary>
    /// Đọc trực tiếp từ file app_config.json và cập nhật cache trong RAM.
    /// </summary>
    public static AppConfigFile LoadConfig()
    {
        lock (_lock)
        {
            _cachedConfig = LoadConfigFromFile();
            return _cachedConfig;
        }
    }

    private static AppConfigFile LoadConfigFromFile()
    {
        try
        {
            var path = GetConfigFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfigFile>(json);
                if (config != null) return config;
            }
        }
        catch { }

        return new AppConfigFile();
    }

    public static string? GetConfiguredDatabasePath()
    {
        var config = GetConfig();
        return !string.IsNullOrWhiteSpace(config.DatabasePath) ? config.DatabasePath : null;
    }

    /// <summary>
    /// Ghi cấu hình ra app_config.json và cập nhật ngay vào RAM cache.
    /// </summary>
    public static void SaveConfig(AppConfigFile config)
    {
        lock (_lock)
        {
            _cachedConfig = config;
            try
            {
                var path = GetConfigFilePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }

    public static void SaveDatabasePath(string dbPath)
    {
        var config = GetConfig();
        config.DatabasePath = dbPath;
        SaveConfig(config);
    }

    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedConfig = null;
        }
    }
}

