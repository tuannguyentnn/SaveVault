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
                Note TEXT,
                IsCloudSynced INTEGER NOT NULL DEFAULT 0,
                CloudProvider TEXT,
                CloudFileId TEXT,
                CloudFileName TEXT,
                CloudSyncDate TEXT
            );

            CREATE TABLE IF NOT EXISTS settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
        ";
        command.ExecuteNonQuery();

        EnsureColumnExists(connection, "backup_history_details", "IsCloudSynced", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "backup_history_details", "CloudProvider", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudFileId", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudFileName", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudSyncDate", "TEXT");
        EnsureColumnExists(connection, "backup_history_details", "CloudSyncJson", "TEXT");
    }

    private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string columnDef)
    {
        try
        {
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            reader.Close();

            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef};";
            alterCmd.ExecuteNonQuery();
        }
        catch
        {
            // Bỏ qua nếu cột đã tồn tại hoặc bảng chưa sẵn sàng
        }
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

            if (detail.CloudSyncList.Count > 0 && string.IsNullOrEmpty(detail.CloudSyncJson))
            {
                detail.CloudSyncJson = JsonSerializer.Serialize(detail.CloudSyncList);
            }

            // INSERT vào backup_history_details
            using (var insertDetailCmd = connection.CreateCommand())
            {
                insertDetailCmd.Transaction = transaction;
                insertDetailCmd.CommandText = @"
                    INSERT INTO backup_history_details 
                    (GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, IsCloudSynced, CloudProvider, CloudFileId, CloudFileName, CloudSyncDate, CloudSyncJson)
                    VALUES 
                    ($gameId, $game, $backup, $source, $savePaths, $manifest, $files, $size, $date, $comp, $status, $note, $isCloudSynced, $cloudProvider, $cloudFileId, $cloudFileName, $cloudSyncDate, $cloudSyncJson);
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
                insertDetailCmd.Parameters.AddWithValue("$isCloudSynced", detail.IsCloudSynced ? 1 : 0);
                insertDetailCmd.Parameters.AddWithValue("$cloudProvider", (object?)detail.CloudProvider ?? DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$cloudFileId", (object?)detail.CloudFileId ?? DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$cloudFileName", (object?)detail.CloudFileName ?? DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$cloudSyncDate", detail.CloudSyncDate.HasValue ? detail.CloudSyncDate.Value.ToString("o") : (object)DBNull.Value);
                insertDetailCmd.Parameters.AddWithValue("$cloudSyncJson", (object?)detail.CloudSyncJson ?? DBNull.Value);

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
            SELECT Id, GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, IsCloudSynced, CloudProvider, CloudFileId, CloudFileName, CloudSyncDate, CloudSyncJson
            FROM backup_history_details
            WHERE GameHistoryId = $gameId
            ORDER BY Id DESC;
        ";
        command.Parameters.AddWithValue("$gameId", gameHistoryId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var isCloudSynced = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetInt32(13) == 1;
            var cloudProvider = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : string.Empty;
            var cloudFileId = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : string.Empty;
            var cloudFileName = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : string.Empty;
            var cloudSyncDate = reader.FieldCount > 17 && !reader.IsDBNull(17) && DateTime.TryParse(reader.GetString(17), out var cdt) ? cdt : (DateTime?)null;
            var cloudSyncJson = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : string.Empty;

            var syncList = new List<CloudSyncInfo>();
            if (!string.IsNullOrEmpty(cloudSyncJson))
            {
                try
                {
                    syncList = JsonSerializer.Deserialize<List<CloudSyncInfo>>(cloudSyncJson) ?? new();
                }
                catch { }
            }

            // Tương thích ngược: Nếu CloudSyncJson rỗng nhưng có IsCloudSynced từ trước
            if (syncList.Count == 0 && isCloudSynced && !string.IsNullOrEmpty(cloudFileId))
            {
                var provName = !string.IsNullOrEmpty(cloudProvider) ? cloudProvider : "Cloud";
                var webUrl = provName.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                    ? $"https://onedrive.live.com/?id={cloudFileId}"
                    : $"https://drive.google.com/file/d/{cloudFileId}/view";

                syncList.Add(new CloudSyncInfo
                {
                    Provider = provName,
                    FileId = cloudFileId,
                    FileName = cloudFileName,
                    SyncDate = cloudSyncDate ?? DateTime.Now,
                    WebViewUrl = webUrl
                });
            }

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
                Note = !reader.IsDBNull(12) ? reader.GetString(12) : null,
                IsCloudSynced = isCloudSynced || syncList.Count > 0,
                CloudProvider = syncList.Count > 0 ? string.Join(", ", syncList.Select(c => c.Provider)) : cloudProvider,
                CloudFileId = cloudFileId,
                CloudFileName = cloudFileName,
                CloudSyncDate = cloudSyncDate,
                CloudSyncJson = cloudSyncJson,
                CloudSyncList = syncList
            });
        }

        return list;
    }

    public async Task UpdateCloudSyncDetailAsync(long detailId, bool isSynced, string provider, string fileId, string fileName, DateTime? syncDate, string? cloudSyncJson = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE backup_history_details
            SET IsCloudSynced = $isSynced,
                CloudProvider = $provider,
                CloudFileId = $fileId,
                CloudFileName = $fileName,
                CloudSyncDate = $syncDate,
                CloudSyncJson = $cloudSyncJson
            WHERE Id = $id;
        ";
        command.Parameters.AddWithValue("$isSynced", isSynced ? 1 : 0);
        command.Parameters.AddWithValue("$provider", (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileId", (object?)fileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileName", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$syncDate", syncDate.HasValue ? syncDate.Value.ToString("o") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$cloudSyncJson", (object?)cloudSyncJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", detailId);

        await command.ExecuteNonQueryAsync();
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

