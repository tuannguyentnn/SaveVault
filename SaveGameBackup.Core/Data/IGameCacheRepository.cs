using System.Text.Json;
using Dapper;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Data;

public interface IGameCacheRepository
{
    Task<GameSaveInfo?> GetCachedGameAsync(string gameName);
    Task SaveGameCacheAsync(GameSaveInfo game);
    Task<List<GameSaveInfo>> GetRecentCachedGamesAsync(int limit = 10);
    Task TouchGameCacheAsync(string gameName);
}

public class GameCacheRepository : IGameCacheRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public GameCacheRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private class GameCacheRecord
    {
        public string GameName { get; set; } = string.Empty;
        public string NormalizedName { get; set; } = string.Empty;
        public string? WikiPageTitle { get; set; }
        public string? SteamAppId { get; set; }
        public string RawPatternsJson { get; set; } = string.Empty;
        public string? Source { get; set; }
        public string? CoverUrl { get; set; }
        public string LastUpdated { get; set; } = string.Empty;
    }

    public async Task<GameSaveInfo?> GetCachedGameAsync(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        var normalized = NormalizeGameName(gameName);
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        const string sql = @"
            SELECT GameName, NormalizedName, WikiPageTitle, SteamAppId, RawPatternsJson, Source, CoverUrl, LastUpdated 
            FROM games_cache 
            WHERE NormalizedName = @Normalized OR LOWER(GameName) = LOWER(@Name)
            LIMIT 1;
        ";

        var row = await connection.QueryFirstOrDefaultAsync<GameCacheRecord>(sql, new
        {
            Normalized = normalized,
            Name = gameName.Trim()
        });

        if (row == null) return null;

        var patterns = !string.IsNullOrEmpty(row.RawPatternsJson)
            ? JsonSerializer.Deserialize<List<string>>(row.RawPatternsJson) ?? new List<string>()
            : new List<string>();

        return new GameSaveInfo
        {
            GameName = row.GameName,
            NormalizedName = row.NormalizedName,
            WikiPageTitle = row.WikiPageTitle,
            SteamAppId = row.SteamAppId,
            RawPatterns = patterns,
            Source = row.Source ?? "Cache",
            OnlineCoverUrl = !string.IsNullOrEmpty(row.CoverUrl) && !row.CoverUrl.Contains(".local", StringComparison.OrdinalIgnoreCase)
                ? row.CoverUrl
                : null,
            LastScanned = DateTime.TryParse(row.LastUpdated, out var dt) ? dt : null
        };
    }

    public async Task SaveGameCacheAsync(GameSaveInfo game)
    {
        if (string.IsNullOrWhiteSpace(game.GameName)) return;

        var normalized = NormalizeGameName(game.GameName);
        var rawJson = JsonSerializer.Serialize(game.RawPatterns);

        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        var coverUrlToSave = !string.IsNullOrEmpty(game.OnlineCoverUrl) && !game.OnlineCoverUrl.Contains(".local", StringComparison.OrdinalIgnoreCase)
            ? game.OnlineCoverUrl
            : null;

        const string sql = @"
            INSERT INTO games_cache (GameName, NormalizedName, WikiPageTitle, SteamAppId, RawPatternsJson, Source, CoverUrl, LastUpdated)
            VALUES (@GameName, @NormalizedName, @WikiPageTitle, @SteamAppId, @RawPatternsJson, @Source, @CoverUrl, @LastUpdated)
            ON CONFLICT(GameName) DO UPDATE SET
                NormalizedName = excluded.NormalizedName,
                WikiPageTitle = excluded.WikiPageTitle,
                SteamAppId = excluded.SteamAppId,
                RawPatternsJson = excluded.RawPatternsJson,
                Source = excluded.Source,
                CoverUrl = COALESCE(@CoverUrl, games_cache.CoverUrl),
                LastUpdated = excluded.LastUpdated;
        ";

        await connection.ExecuteAsync(sql, new
        {
            GameName = game.GameName.Trim(),
            NormalizedName = normalized,
            WikiPageTitle = game.WikiPageTitle,
            SteamAppId = game.SteamAppId,
            RawPatternsJson = rawJson,
            Source = game.Source,
            CoverUrl = coverUrlToSave,
            LastUpdated = DateTime.Now.ToString("o")
        });
    }

    public async Task<List<GameSaveInfo>> GetRecentCachedGamesAsync(int limit = 10)
    {
        if (limit <= 0) limit = 10;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        const string sql = @"
            SELECT GameName, NormalizedName, WikiPageTitle, SteamAppId, RawPatternsJson, Source, CoverUrl, LastUpdated
            FROM games_cache
            ORDER BY LastUpdated DESC
            LIMIT @Limit;
        ";

        var rows = await connection.QueryAsync<GameCacheRecord>(sql, new { Limit = limit });
        var results = new List<GameSaveInfo>();

        foreach (var row in rows)
        {
            var patterns = !string.IsNullOrEmpty(row.RawPatternsJson)
                ? JsonSerializer.Deserialize<List<string>>(row.RawPatternsJson) ?? new List<string>()
                : new List<string>();

            results.Add(new GameSaveInfo
            {
                GameName = row.GameName,
                NormalizedName = row.NormalizedName,
                WikiPageTitle = row.WikiPageTitle,
                SteamAppId = row.SteamAppId,
                RawPatterns = patterns,
                Source = row.Source ?? "Cache",
                OnlineCoverUrl = !string.IsNullOrEmpty(row.CoverUrl) && !row.CoverUrl.Contains(".local", StringComparison.OrdinalIgnoreCase)
                    ? row.CoverUrl
                    : null,
                LastScanned = DateTime.TryParse(row.LastUpdated, out var dt) ? dt : null
            });
        }

        return results;
    }

    public async Task TouchGameCacheAsync(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return;

        var normalized = NormalizeGameName(gameName);
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        const string sql = @"
            UPDATE games_cache
            SET LastUpdated = @LastUpdated
            WHERE NormalizedName = @Normalized OR LOWER(GameName) = LOWER(@Name);
        ";

        await connection.ExecuteAsync(sql, new
        {
            LastUpdated = DateTime.Now.ToString("o"),
            Normalized = normalized,
            Name = gameName.Trim()
        });
    }

    public static string NormalizeGameName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var lower = name.Trim().ToLowerInvariant();
        var chars = lower.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }
}
