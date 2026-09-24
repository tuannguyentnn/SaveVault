using System;
using System.Collections.Generic;
using System.Linq;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class KnownGameDefinition
{
    public string GameName { get; set; } = string.Empty;
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public string? SteamAppId { get; set; }
    public string[] SavePatterns { get; set; } = Array.Empty<string>();
    public string? CoverUrl { get; set; }
}

/// <summary>
/// Cơ sở dữ liệu danh mục game đã biết (Known Games Catalog).
/// Cung cấp thông tin đường dẫn save game chính xác 100% cho các game bom tấn và phổ biến
/// mà không cần phụ thuộc vào kết nối mạng hay PCGamingWiki (tránh lỗi Cloudflare/offline).
/// </summary>
public class KnownGameCatalogService
{
    private readonly List<KnownGameDefinition> _catalog = new();

    public KnownGameCatalogService()
    {
        InitializeCatalog();
    }

    public IReadOnlyList<KnownGameDefinition> GetAllKnownGames() => _catalog;

    public KnownGameDefinition? FindGame(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var cleanQuery = query.Trim();
        var normalizedQuery = DatabaseService.NormalizeGameName(cleanQuery);

        // 1. Tìm kiếm chính xác tên game
        var exact = _catalog.FirstOrDefault(g =>
            string.Equals(g.GameName, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(DatabaseService.NormalizeGameName(g.GameName), normalizedQuery, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // 2. Tìm kiếm trong danh sách Aliases
        var aliasMatch = _catalog.FirstOrDefault(g =>
            g.Aliases.Any(a =>
                string.Equals(a, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(DatabaseService.NormalizeGameName(a), normalizedQuery, StringComparison.OrdinalIgnoreCase)));
        if (aliasMatch != null) return aliasMatch;

        // 3. Tìm kiếm tương đối: Query chứa tên game hoặc Alias
        var partial = _catalog.FirstOrDefault(g =>
        {
            var normName = DatabaseService.NormalizeGameName(g.GameName);
            if (!string.IsNullOrEmpty(normName) && (normalizedQuery.Contains(normName) || normName.Contains(normalizedQuery)))
            {
                return true;
            }

            return g.Aliases.Any(a =>
            {
                var normAlias = DatabaseService.NormalizeGameName(a);
                return !string.IsNullOrEmpty(normAlias) && normAlias.Length >= 3 &&
                       (normalizedQuery.Contains(normAlias) || normAlias.Contains(normalizedQuery));
            });
        });

        return partial;
    }

    public GameSaveInfo? CreateGameSaveInfoFromCatalog(string query)
    {
        var match = FindGame(query);
        if (match == null) return null;

        var info = new GameSaveInfo
        {
            GameName = match.GameName,
            NormalizedName = DatabaseService.NormalizeGameName(match.GameName),
            SteamAppId = match.SteamAppId,
            Source = "Known Game Catalog",
            OnlineCoverUrl = match.CoverUrl,
            RawPatterns = new List<string>(match.SavePatterns)
        };

        if (!string.IsNullOrEmpty(match.SteamAppId))
        {
            var steamCloudPath = $@"{{{{p|steam}}}}\userdata\{{{{p|uid}}}}\{match.SteamAppId}\remote";
            if (!info.RawPatterns.Contains(steamCloudPath, StringComparer.OrdinalIgnoreCase))
            {
                info.RawPatterns.Add(steamCloudPath);
            }
        }

        return info;
    }

    private void InitializeCatalog()
    {
        // 1. Palworld
        // Unreal Engine project name nội bộ là 'Pal', thư mục save nằm ở: AppData\Local\Pal\Saved\SaveGames
        // Bản Xbox / Game Pass nằm ở: AppData\Local\Packages\PocketpairInc.Palworld_*\SystemAppData\wgs
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Palworld",
            Aliases = new[] { "pal", "pal world", "pocketpair", "pocketpairinc" },
            SteamAppId = "1623730",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1623730/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\Pal\Saved\SaveGames",
                @"{{p|localappdata}}\Packages\PocketpairInc.Palworld_*\SystemAppData\wgs",
                @"{{p|localappdata}}\Palworld\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\1623730\remote"
            }
        });

        // 2. Hogwarts Legacy
        // Unreal Engine project name nội bộ là 'Phoenix'
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Hogwarts Legacy",
            Aliases = new[] { "phoenix", "hogwarts", "hogwartslegacy" },
            SteamAppId = "990080",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/990080/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\Phoenix\Saved\SaveGames",
                @"{{p|localappdata}}\HogwartsLegacy\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\990080\remote"
            }
        });

        // 3. Black Myth: Wukong
        // Unreal Engine project name nội bộ là 'b1'
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Black Myth: Wukong",
            Aliases = new[] { "b1", "black myth wukong", "blackmythwukong", "wukong", "blackmyth" },
            SteamAppId = "2358720",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/2358720/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\b1\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\2358720\remote"
            }
        });

        // 4. Cyberpunk 2077
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Cyberpunk 2077",
            Aliases = new[] { "cyberpunk", "cp2077", "cd projekt red cyberpunk 2077" },
            SteamAppId = "1091500",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1091500/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|savedgames}}\CD Projekt Red\Cyberpunk 2077",
                @"{{p|userprofile}}\Saved Games\CD Projekt Red\Cyberpunk 2077",
                @"{{p|localappdata}}\CD Projekt Red\Cyberpunk 2077"
            }
        });

        // 5. Elden Ring
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Elden Ring",
            Aliases = new[] { "eldenring", "er" },
            SteamAppId = "1245620",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1245620/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|appdata}}\EldenRing\{{p|uid}}",
                @"{{p|appdata}}\EldenRing"
            }
        });

        // 6. Baldur's Gate 3
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Baldur's Gate 3",
            Aliases = new[] { "baldurs gate 3", "baldursgate3", "bg3", "larian studios" },
            SteamAppId = "1086940",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1086940/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames\Story",
                @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames",
                @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3"
            }
        });

        // 7. The Witcher 3: Wild Hunt
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "The Witcher 3: Wild Hunt",
            Aliases = new[] { "the witcher 3", "witcher 3", "witcher3", "witcher" },
            SteamAppId = "292030",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/292030/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|documents}}\The Witcher 3\gamesaves",
                @"{{p|documents}}\The Witcher 3"
            }
        });

        // 8. Hades
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Hades",
            Aliases = new[] { "hades 1", "supergiant hades" },
            SteamAppId = "1145360",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|savedgames}}\Hades",
                @"{{p|documents}}\Saved Games\Hades"
            }
        });

        // 9. Hades II
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Hades II",
            Aliases = new[] { "hades 2", "hadesii" },
            SteamAppId = "1145350",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1145350/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|savedgames}}\Hades II",
                @"{{p|documents}}\Saved Games\Hades II"
            }
        });

        // 10. Dark Souls III
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Dark Souls III",
            Aliases = new[] { "dark souls 3", "darksouls3", "ds3" },
            SteamAppId = "374320",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/374320/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|appdata}}\DarkSoulsIII\{{p|uid}}",
                @"{{p|appdata}}\DarkSoulsIII"
            }
        });

        // 11. Sekiro: Shadows Die Twice
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Sekiro: Shadows Die Twice",
            Aliases = new[] { "sekiro", "sekiro shadows die twice" },
            SteamAppId = "814380",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/814380/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|appdata}}\Sekiro\{{p|uid}}",
                @"{{p|appdata}}\Sekiro"
            }
        });

        // 12. God of War
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "God of War",
            Aliases = new[] { "god of war 2018", "gow" },
            SteamAppId = "1593500",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1593500/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|savedgames}}\God of War\{{p|uid}}",
                @"{{p|savedgames}}\God of War"
            }
        });

        // 13. Monster Hunter: World
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Monster Hunter: World",
            Aliases = new[] { "monster hunter world", "mhw", "monster hunter" },
            SteamAppId = "582010",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/582010/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|steam}}\userdata\{{p|uid}}\582010\remote"
            }
        });

        // 14. Grand Theft Auto V
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Grand Theft Auto V",
            Aliases = new[] { "gta v", "gtav", "gta 5", "grand theft auto 5" },
            SteamAppId = "271590",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/271590/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|documents}}\Rockstar Games\GTA V\Profiles\{{p|uid}}",
                @"{{p|documents}}\Rockstar Games\GTA V\Profiles"
            }
        });

        // 15. Red Dead Redemption 2
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Red Dead Redemption 2",
            Aliases = new[] { "rdr 2", "rdr2", "red dead 2" },
            SteamAppId = "1174180",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1174180/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|documents}}\Rockstar Games\Red Dead Redemption 2\Profiles\{{p|uid}}",
                @"{{p|documents}}\Rockstar Games\Red Dead Redemption 2\Profiles"
            }
        });

        // 16. Fallout 4
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Fallout 4",
            Aliases = new[] { "fo4", "fallout4" },
            SteamAppId = "377160",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/377160/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|documents}}\My Games\Fallout4\Saves",
                @"{{p|documents}}\My Games\Fallout4"
            }
        });

        // 17. Manor Lords
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Manor Lords",
            Aliases = new[] { "manorlords" },
            SteamAppId = "1363080",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1363080/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\ManorLords\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\1363080\remote"
            }
        });

        // 18. Stray
        // Unreal Engine internal name là 'Hk_project'
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Stray",
            Aliases = new[] { "hk_project", "hkproject" },
            SteamAppId = "1332010",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1332010/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\Hk_project\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\1332010\remote"
            }
        });

        // 19. Lies of P
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Lies of P",
            Aliases = new[] { "liesofp" },
            SteamAppId = "1627720",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1627720/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|localappdata}}\LiesofP\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\1627720\remote"
            }
        });

        // 20. Remnant II
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Remnant II",
            Aliases = new[] { "remnant 2", "remnant2", "remnant" },
            SteamAppId = "1282100",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/1282100/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|savedgames}}\Remnant2\Saved\SaveGames",
                @"{{p|steam}}\userdata\{{p|uid}}\1282100\remote"
            }
        });

        // 21. Stardew Valley
        _catalog.Add(new KnownGameDefinition
        {
            GameName = "Stardew Valley",
            Aliases = new[] { "stardew" },
            SteamAppId = "413150",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/413150/header.jpg",
            SavePatterns = new[]
            {
                @"{{p|appdata}}\StardewValley\Saves"
            }
        });
    }
}
