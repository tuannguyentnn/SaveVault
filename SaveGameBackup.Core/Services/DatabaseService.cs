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
                GameName TEXT NOT NULL,
                BackupPath TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
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

    public async Task<List<BackupRecord>> GetBackupHistoryAsync()
    {
        var list = new List<BackupRecord>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, GameName, BackupPath, SourcePath, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note
            FROM backup_history
            ORDER BY Id DESC;
        ";

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
                Note = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return list;
    }

    public async Task<long> InsertBackupRecordAsync(BackupRecord record)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO backup_history (GameName, BackupPath, SourcePath, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note)
            VALUES ($game, $backup, $source, $files, $size, $date, $comp, $status, $note);
            SELECT last_insert_rowid();
        ";
        command.Parameters.AddWithValue("$game", record.GameName);
        command.Parameters.AddWithValue("$backup", record.BackupPath);
        command.Parameters.AddWithValue("$source", record.SourcePath);
        command.Parameters.AddWithValue("$files", record.FileCount);
        command.Parameters.AddWithValue("$size", record.TotalSizeBytes);
        command.Parameters.AddWithValue("$date", record.BackupDate.ToString("o"));
        command.Parameters.AddWithValue("$comp", record.IsCompressed ? 1 : 0);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task DeleteBackupRecordAsync(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM backup_history WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
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
}

public static class AppConfigService
{
    private const string ConfigFileName = "app_config.json";

    public static string GetConfigFilePath()
    {
        return Path.Combine(DatabaseService.GetDefaultProjectRoot(), ConfigFileName);
    }

    public static AppConfigFile LoadConfig()
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
        var config = LoadConfig();
        return !string.IsNullOrWhiteSpace(config.DatabasePath) ? config.DatabasePath : null;
    }

    public static void SaveConfig(AppConfigFile config)
    {
        try
        {
            var path = GetConfigFilePath();
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static void SaveDatabasePath(string dbPath)
    {
        var config = LoadConfig();
        config.DatabasePath = dbPath;
        SaveConfig(config);
    }
}

