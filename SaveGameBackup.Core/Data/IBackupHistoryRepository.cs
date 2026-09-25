using System.Data;
using System.Text.Json;
using Dapper;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Data;

public interface IBackupHistoryRepository
{
    Task<long> InsertOrUpdateBackupHistoryAsync(BackupHistoryDetail detail);
    Task<List<GameHistoryEntry>> GetGameHistoriesAsync();
    Task<List<BackupHistoryDetail>> GetHistoryDetailsByGameIdAsync(long gameHistoryId);
    Task UpdateCloudSyncDetailAsync(long detailId, bool isSynced, string provider, string fileId, string fileName, DateTime? syncDate, string? cloudSyncJson = null, string? backupPathJson = null);
    Task UpdateSnapshotLocationsAsync(long detailId, BackupHistoryDetail detail);
    Task<bool> DeleteHistoryDetailAsync(long detailId, long gameHistoryId);
    Task DeleteGameHistoryAsync(long gameHistoryId);
    Task<List<BackupRecord>> GetBackupHistoryAsync();
    Task DeleteBackupRecordAsync(long id);
    Task UpdateCoverUrlAsync(string gameName, string coverUrl);
    Task UpdateCoverPathAsync(string gameName, string coverPath);
}

public class BackupHistoryRepository : IBackupHistoryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public BackupHistoryRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private class MasterHistoryRow
    {
        public long Id { get; set; }
        public string GameName { get; set; } = string.Empty;
        public int BackupCount { get; set; }
        public string LatestBackupPath { get; set; } = string.Empty;
        public string LatestBackupDate { get; set; } = string.Empty;
        public long TotalSizeBytes { get; set; }
        public long LatestSizeBytes { get; set; }
        public int LatestFileCount { get; set; }
        public string? SavePaths { get; set; }
        public string Status { get; set; } = "Success";
        public string? Note { get; set; }
        public int HasCloudBackup { get; set; }
        public string? CoverUrl { get; set; }
        public string? CoverPath { get; set; }
    }

    private class DetailHistoryRow
    {
        public long Id { get; set; }
        public long GameHistoryId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string? SavePaths { get; set; }
        public string? ManifestJson { get; set; }
        public int FileCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public string BackupDate { get; set; } = string.Empty;
        public int IsCompressed { get; set; }
        public string Status { get; set; } = "Success";
        public string? Note { get; set; }
        public int IsCloudSynced { get; set; }
        public string? CloudProvider { get; set; }
        public string? CloudFileId { get; set; }
        public string? CloudFileName { get; set; }
        public string? CloudSyncDate { get; set; }
        public string? CloudSyncJson { get; set; }
        public string? CoverUrl { get; set; }
        public string? CoverPath { get; set; }
    }

    public async Task<long> InsertOrUpdateBackupHistoryAsync(BackupHistoryDetail detail)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (!detail.BackupPath.TrimStart().StartsWith("{"))
            {
                detail.SetLocalPath(detail.BackupPath);
            }

            var localPath = detail.LocalBackupPath;
            var folderPath = !string.IsNullOrEmpty(localPath)
                ? (Directory.Exists(localPath) ? localPath : Path.GetDirectoryName(localPath) ?? localPath)
                : string.Empty;

            if (string.IsNullOrEmpty(detail.CoverPath) && SaveGameBackup.Core.Services.GameCoverService.HasLocalCover(detail.GameName))
            {
                detail.CoverPath = SaveGameBackup.Core.Services.GameCoverService.GetCoverFilePath(detail.GameName);
            }

            const string checkSql = "SELECT Id, BackupCount, TotalSizeBytes, LatestBackupPath FROM backup_history WHERE LOWER(GameName) = LOWER(@Name) LIMIT 1;";
            var existing = await connection.QueryFirstOrDefaultAsync<MasterHistoryRow>(checkSql, new { Name = detail.GameName.Trim() }, transaction);

            long gameHistoryId;
            if (existing != null)
            {
                gameHistoryId = existing.Id;

                var folderList = new List<string>();
                if (!string.IsNullOrWhiteSpace(existing.LatestBackupPath))
                {
                    try
                    {
                        if (existing.LatestBackupPath.TrimStart().StartsWith("["))
                        {
                            folderList = JsonSerializer.Deserialize<List<string>>(existing.LatestBackupPath) ?? new();
                        }
                        else
                        {
                            folderList.Add(existing.LatestBackupPath.Trim());
                        }
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(folderPath) && !folderList.Any(f => f.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
                {
                    folderList.Add(folderPath);
                }
                var latestBackupPathJson = JsonSerializer.Serialize(folderList);

                const string updateMasterSql = @"
                    UPDATE backup_history 
                    SET BackupCount = @Count,
                        LatestBackupPath = @LatestBackupPath,
                        LatestBackupDate = @LatestBackupDate,
                        TotalSizeBytes = @TotalSizeBytes,
                        LatestSizeBytes = @LatestSizeBytes,
                        LatestFileCount = @LatestFileCount,
                        SavePaths = @SavePaths,
                        Status = @Status,
                        Note = @Note,
                        CoverUrl = COALESCE(@CoverUrl, CoverUrl),
                        CoverPath = COALESCE(@CoverPath, CoverPath)
                    WHERE Id = @Id;
                ";
                await connection.ExecuteAsync(updateMasterSql, new
                {
                    Count = existing.BackupCount + 1,
                    LatestBackupPath = latestBackupPathJson,
                    LatestBackupDate = detail.BackupDate.ToString("o"),
                    TotalSizeBytes = existing.TotalSizeBytes + detail.TotalSizeBytes,
                    LatestSizeBytes = detail.TotalSizeBytes,
                    LatestFileCount = detail.FileCount,
                    SavePaths = detail.SavePaths,
                    Status = detail.Status,
                    Note = detail.Note,
                    CoverUrl = detail.CoverUrl,
                    CoverPath = detail.CoverPath,
                    Id = gameHistoryId
                }, transaction);
            }
            else
            {
                var folderList = !string.IsNullOrEmpty(folderPath) ? new List<string> { folderPath } : new List<string>();
                var latestBackupPathJson = JsonSerializer.Serialize(folderList);

                const string insertMasterSql = @"
                    INSERT INTO backup_history (GameName, BackupCount, LatestBackupPath, LatestBackupDate, TotalSizeBytes, LatestSizeBytes, LatestFileCount, SavePaths, Status, Note, CoverUrl, CoverPath)
                    VALUES (@GameName, 1, @LatestBackupPath, @LatestBackupDate, @TotalSizeBytes, @LatestSizeBytes, @LatestFileCount, @SavePaths, @Status, @Note, @CoverUrl, @CoverPath);
                    SELECT last_insert_rowid();
                ";
                gameHistoryId = await connection.ExecuteScalarAsync<long>(insertMasterSql, new
                {
                    GameName = detail.GameName.Trim(),
                    LatestBackupPath = latestBackupPathJson,
                    LatestBackupDate = detail.BackupDate.ToString("o"),
                    TotalSizeBytes = detail.TotalSizeBytes,
                    LatestSizeBytes = detail.TotalSizeBytes,
                    LatestFileCount = detail.FileCount,
                    SavePaths = detail.SavePaths,
                    Status = detail.Status,
                    Note = detail.Note,
                    CoverUrl = detail.CoverUrl,
                    CoverPath = detail.CoverPath
                }, transaction);
            }

            detail.GameHistoryId = gameHistoryId;

            if (detail.CloudSyncList.Count > 0 && string.IsNullOrEmpty(detail.CloudSyncJson))
            {
                detail.CloudSyncJson = JsonSerializer.Serialize(detail.CloudSyncList);
            }

            const string insertDetailSql = @"
                INSERT INTO backup_history_details 
                (GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, IsCloudSynced, CloudProvider, CloudFileId, CloudFileName, CloudSyncDate, CloudSyncJson, CoverUrl, CoverPath)
                VALUES 
                (@GameHistoryId, @GameName, @BackupPath, @SourcePath, @SavePaths, @ManifestJson, @FileCount, @TotalSizeBytes, @BackupDate, @IsCompressed, @Status, @Note, @IsCloudSynced, @CloudProvider, @CloudFileId, @CloudFileName, @CloudSyncDate, @CloudSyncJson, @CoverUrl, @CoverPath);
                SELECT last_insert_rowid();
            ";

            var detailId = await connection.ExecuteScalarAsync<long>(insertDetailSql, new
            {
                GameHistoryId = detail.GameHistoryId,
                GameName = detail.GameName,
                BackupPath = detail.BackupPath,
                SourcePath = detail.SourcePath,
                SavePaths = detail.SavePaths,
                ManifestJson = detail.ManifestJson,
                FileCount = detail.FileCount,
                TotalSizeBytes = detail.TotalSizeBytes,
                BackupDate = detail.BackupDate.ToString("o"),
                IsCompressed = detail.IsCompressed ? 1 : 0,
                Status = detail.Status,
                Note = detail.Note,
                IsCloudSynced = detail.IsCloudSynced ? 1 : 0,
                CloudProvider = detail.CloudProvider,
                CloudFileId = detail.CloudFileId,
                CloudFileName = detail.CloudFileName,
                CloudSyncDate = detail.CloudSyncDate?.ToString("o"),
                CloudSyncJson = detail.CloudSyncJson,
                CoverUrl = detail.CoverUrl,
                CoverPath = detail.CoverPath
            }, transaction);

            detail.Id = detailId;
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
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string sql = @"
            SELECT h.Id, h.GameName, h.BackupCount, h.LatestBackupPath, h.LatestBackupDate, h.TotalSizeBytes, h.LatestSizeBytes, h.LatestFileCount, h.SavePaths, h.Status, h.Note, h.CoverUrl, h.CoverPath,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM backup_history_details d 
                       WHERE d.GameHistoryId = h.Id 
                         AND (d.IsCloudSynced = 1 OR (d.CloudFileId IS NOT NULL AND d.CloudFileId != '') OR (d.CloudSyncJson IS NOT NULL AND d.CloudSyncJson != '' AND d.CloudSyncJson != '[]') OR (d.BackupPath LIKE '%""CloudUrls"":{%' AND d.BackupPath NOT LIKE '%""CloudUrls"":{}%'))
                   ) THEN 1 ELSE 0 END AS HasCloudBackup
            FROM backup_history h
            ORDER BY h.LatestBackupDate DESC;
        ";

        var rows = await connection.QueryAsync<MasterHistoryRow>(sql);
        return rows.Select(r =>
        {
            var cover = r.CoverUrl;
            var coverPath = r.CoverPath;
            if (string.IsNullOrEmpty(coverPath) && SaveGameBackup.Core.Services.GameCoverService.HasLocalCover(r.GameName))
            {
                coverPath = SaveGameBackup.Core.Services.GameCoverService.GetCoverFilePath(r.GameName);
            }
            if (string.IsNullOrEmpty(cover))
            {
                cover = coverPath;
            }

            return new GameHistoryEntry
            {
                Id = r.Id,
                GameName = r.GameName,
                BackupCount = r.BackupCount,
                LatestBackupPath = r.LatestBackupPath,
                LatestBackupDate = DateTime.TryParse(r.LatestBackupDate, out var dt) ? dt : DateTime.MinValue,
                TotalSizeBytes = r.TotalSizeBytes,
                LatestSizeBytes = r.LatestSizeBytes,
                LatestFileCount = r.LatestFileCount,
                SavePaths = r.SavePaths ?? string.Empty,
                Status = r.Status,
                Note = r.Note,
                HasCloudBackup = r.HasCloudBackup == 1,
                CoverUrl = cover,
                CoverPath = coverPath
            };
        }).ToList();
    }

    public async Task<List<BackupHistoryDetail>> GetHistoryDetailsByGameIdAsync(long gameHistoryId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string sql = @"
            SELECT Id, GameHistoryId, GameName, BackupPath, SourcePath, SavePaths, ManifestJson, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, IsCloudSynced, CloudProvider, CloudFileId, CloudFileName, CloudSyncDate, CloudSyncJson, CoverUrl, CoverPath
            FROM backup_history_details
            WHERE GameHistoryId = @GameId
            ORDER BY Id DESC;
        ";

        var rows = await connection.QueryAsync<DetailHistoryRow>(sql, new { GameId = gameHistoryId });
        var list = new List<BackupHistoryDetail>();

        foreach (var r in rows)
        {
            var isCloudSynced = r.IsCloudSynced == 1;
            var cloudProvider = r.CloudProvider ?? string.Empty;
            var cloudFileId = r.CloudFileId ?? string.Empty;
            var cloudFileName = r.CloudFileName ?? string.Empty;
            var cloudSyncDate = DateTime.TryParse(r.CloudSyncDate, out var cdt) ? cdt : (DateTime?)null;
            var cloudSyncJson = r.CloudSyncJson ?? string.Empty;

            var syncList = new List<CloudSyncInfo>();
            if (!string.IsNullOrEmpty(cloudSyncJson))
            {
                try
                {
                    syncList = JsonSerializer.Deserialize<List<CloudSyncInfo>>(cloudSyncJson) ?? new();
                }
                catch { }
            }

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

            var coverPath = r.CoverPath;
            if (string.IsNullOrEmpty(coverPath) && SaveGameBackup.Core.Services.GameCoverService.HasLocalCover(r.GameName))
            {
                coverPath = SaveGameBackup.Core.Services.GameCoverService.GetCoverFilePath(r.GameName);
            }

            list.Add(new BackupHistoryDetail
            {
                Id = r.Id,
                GameHistoryId = r.GameHistoryId,
                GameName = r.GameName,
                BackupPath = r.BackupPath,
                SourcePath = r.SourcePath,
                SavePaths = r.SavePaths ?? string.Empty,
                ManifestJson = r.ManifestJson ?? string.Empty,
                FileCount = r.FileCount,
                TotalSizeBytes = r.TotalSizeBytes,
                BackupDate = DateTime.TryParse(r.BackupDate, out var dt) ? dt : DateTime.MinValue,
                IsCompressed = r.IsCompressed == 1,
                Status = r.Status,
                Note = r.Note,
                IsCloudSynced = isCloudSynced || syncList.Count > 0,
                CloudProvider = syncList.Count > 0 ? string.Join(", ", syncList.Select(c => c.Provider)) : cloudProvider,
                CloudFileId = cloudFileId,
                CloudFileName = cloudFileName,
                CloudSyncDate = cloudSyncDate,
                CloudSyncJson = cloudSyncJson,
                CloudSyncList = syncList,
                CoverUrl = r.CoverUrl ?? coverPath,
                CoverPath = coverPath
            });
        }

        return list;
    }

    public async Task UpdateCloudSyncDetailAsync(long detailId, bool isSynced, string provider, string fileId, string fileName, DateTime? syncDate, string? cloudSyncJson = null, string? backupPathJson = null)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        string sql;
        if (!string.IsNullOrEmpty(backupPathJson))
        {
            sql = @"
                UPDATE backup_history_details
                SET IsCloudSynced = @IsSynced,
                    CloudProvider = @Provider,
                    CloudFileId = @FileId,
                    CloudFileName = @FileName,
                    CloudSyncDate = @SyncDate,
                    CloudSyncJson = @CloudSyncJson,
                    BackupPath = @BackupPath
                WHERE Id = @Id;
            ";
        }
        else
        {
            sql = @"
                UPDATE backup_history_details
                SET IsCloudSynced = @IsSynced,
                    CloudProvider = @Provider,
                    CloudFileId = @FileId,
                    CloudFileName = @FileName,
                    CloudSyncDate = @SyncDate,
                    CloudSyncJson = @CloudSyncJson
                WHERE Id = @Id;
            ";
        }

        await connection.ExecuteAsync(sql, new
        {
            IsSynced = isSynced ? 1 : 0,
            Provider = provider,
            FileId = fileId,
            FileName = fileName,
            SyncDate = syncDate?.ToString("o"),
            CloudSyncJson = cloudSyncJson,
            BackupPath = backupPathJson,
            Id = detailId
        });
    }

    public async Task UpdateSnapshotLocationsAsync(long detailId, BackupHistoryDetail detail)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string sql = @"
            UPDATE backup_history_details
            SET BackupPath = @BackupPath,
                IsCloudSynced = @IsCloudSynced,
                CloudProvider = @CloudProvider,
                CloudFileId = @CloudFileId,
                CloudFileName = @CloudFileName,
                CloudSyncDate = @CloudSyncDate,
                CloudSyncJson = @CloudSyncJson
            WHERE Id = @Id;
        ";

        await connection.ExecuteAsync(sql, new
        {
            BackupPath = detail.BackupPath,
            IsCloudSynced = detail.IsCloudSynced ? 1 : 0,
            CloudProvider = detail.CloudProvider,
            CloudFileId = detail.CloudFileId,
            CloudFileName = detail.CloudFileName,
            CloudSyncDate = detail.CloudSyncDate?.ToString("o"),
            CloudSyncJson = detail.CloudSyncJson,
            Id = detailId
        });
    }

    public async Task<bool> DeleteHistoryDetailAsync(long detailId, long gameHistoryId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            const string delSql = "DELETE FROM backup_history_details WHERE Id = @Id;";
            await connection.ExecuteAsync(delSql, new { Id = detailId }, transaction);

            const string countSql = "SELECT COUNT(*) AS Count, COALESCE(SUM(TotalSizeBytes), 0) AS TotalSize FROM backup_history_details WHERE GameHistoryId = @GameId;";
            var stats = await connection.QueryFirstOrDefaultAsync<(int Count, long TotalSize)>(countSql, new { GameId = gameHistoryId }, transaction);

            if (stats.Count == 0)
            {
                const string delMasterSql = "DELETE FROM backup_history WHERE Id = @GameId;";
                await connection.ExecuteAsync(delMasterSql, new { GameId = gameHistoryId }, transaction);
            }
            else
            {
                const string latestSql = @"
                    SELECT BackupPath, BackupDate, TotalSizeBytes, FileCount, SavePaths
                    FROM backup_history_details
                    WHERE GameHistoryId = @GameId
                    ORDER BY Id DESC
                    LIMIT 1;
                ";
                var latest = await connection.QueryFirstOrDefaultAsync<DetailHistoryRow>(latestSql, new { GameId = gameHistoryId }, transaction);
                if (latest != null)
                {
                    // Recalculate unique folder paths from all remaining details
                    var allRemainingPaths = await connection.QueryAsync<string>(
                        "SELECT BackupPath FROM backup_history_details WHERE GameHistoryId = @GameId;",
                        new { GameId = gameHistoryId }, transaction);

                    var remainingFolders = new List<string>();
                    foreach (var bp in allRemainingPaths)
                    {
                        var loc = BackupPathLocations.FromJson(bp);
                        if (!string.IsNullOrEmpty(loc.LocalPath))
                        {
                            var f = Directory.Exists(loc.LocalPath) ? loc.LocalPath : Path.GetDirectoryName(loc.LocalPath) ?? loc.LocalPath;
                            if (!string.IsNullOrEmpty(f) && !remainingFolders.Any(rf => rf.Equals(f, StringComparison.OrdinalIgnoreCase)))
                            {
                                remainingFolders.Add(f);
                            }
                        }
                    }
                    var updatedLatestBackupPath = JsonSerializer.Serialize(remainingFolders);

                    const string updateMasterSql = @"
                        UPDATE backup_history
                        SET BackupCount = @Count,
                            TotalSizeBytes = @TotalSizeBytes,
                            LatestBackupPath = @LatestBackupPath,
                            LatestBackupDate = @LatestBackupDate,
                            LatestSizeBytes = @LatestSizeBytes,
                            LatestFileCount = @LatestFileCount,
                            SavePaths = @SavePaths
                        WHERE Id = @GameId;
                    ";
                    await connection.ExecuteAsync(updateMasterSql, new
                    {
                        Count = stats.Count,
                        TotalSizeBytes = stats.TotalSize,
                        LatestBackupPath = updatedLatestBackupPath,
                        LatestBackupDate = latest.BackupDate,
                        LatestSizeBytes = latest.TotalSizeBytes,
                        LatestFileCount = latest.FileCount,
                        SavePaths = latest.SavePaths,
                        GameId = gameHistoryId
                    }, transaction);
                }
            }

            transaction.Commit();
            return stats.Count == 0;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task DeleteGameHistoryAsync(long gameHistoryId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        try
        {
            await connection.ExecuteAsync("DELETE FROM backup_history_details WHERE GameHistoryId = @GameId;", new { GameId = gameHistoryId }, transaction);
            await connection.ExecuteAsync("DELETE FROM backup_history WHERE Id = @GameId;", new { GameId = gameHistoryId }, transaction);
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
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string sql = @"
            SELECT Id, GameName, BackupPath, SourcePath, FileCount, TotalSizeBytes, BackupDate, IsCompressed, Status, Note, SavePaths
            FROM backup_history_details
            ORDER BY Id DESC;
        ";

        try
        {
            var rows = await connection.QueryAsync<DetailHistoryRow>(sql);
            return rows.Select(r => new BackupRecord
            {
                Id = r.Id,
                GameName = r.GameName,
                BackupPath = r.BackupPath,
                SourcePath = r.SourcePath,
                FileCount = r.FileCount,
                TotalSizeBytes = r.TotalSizeBytes,
                BackupDate = DateTime.TryParse(r.BackupDate, out var dt) ? dt : DateTime.MinValue,
                IsCompressed = r.IsCompressed == 1,
                Status = r.Status,
                Note = r.Note,
                SavePaths = r.SavePaths ?? string.Empty
            }).ToList();
        }
        catch
        {
            return new List<BackupRecord>();
        }
    }

    public async Task DeleteBackupRecordAsync(long id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string checkSql = "SELECT GameHistoryId FROM backup_history_details WHERE Id = @Id LIMIT 1;";
        var gameId = await connection.ExecuteScalarAsync<long?>(checkSql, new { Id = id });

        if (gameId.HasValue)
        {
            await DeleteHistoryDetailAsync(id, gameId.Value);
        }
        else
        {
            await connection.ExecuteAsync("DELETE FROM backup_history WHERE Id = @Id;", new { Id = id });
        }
    }

    public async Task UpdateCoverUrlAsync(string gameName, string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(coverUrl)) return;
        try
        {
            using var connection = await _connectionFactory.CreateOpenConnectionAsync();
            const string sql = @"
                UPDATE backup_history SET CoverUrl = @CoverUrl WHERE LOWER(GameName) = LOWER(@GameName);
                UPDATE backup_history_details SET CoverUrl = @CoverUrl WHERE LOWER(GameName) = LOWER(@GameName);
            ";
            await connection.ExecuteAsync(sql, new { GameName = gameName.Trim(), CoverUrl = coverUrl });
        }
        catch { /* Best effort */ }
    }

    public async Task UpdateCoverPathAsync(string gameName, string coverPath)
    {
        if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(coverPath)) return;
        try
        {
            using var connection = await _connectionFactory.CreateOpenConnectionAsync();
            const string sql = @"
                UPDATE backup_history SET CoverPath = @CoverPath, CoverUrl = COALESCE(@CoverPath, CoverUrl) WHERE LOWER(GameName) = LOWER(@GameName);
                UPDATE backup_history_details SET CoverPath = @CoverPath, CoverUrl = COALESCE(@CoverPath, CoverUrl) WHERE LOWER(GameName) = LOWER(@GameName);
            ";
            await connection.ExecuteAsync(sql, new { GameName = gameName.Trim(), CoverPath = coverPath });
        }
        catch { /* Best effort */ }
    }
}
