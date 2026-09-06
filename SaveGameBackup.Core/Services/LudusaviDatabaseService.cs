using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services;

public class LudusaviDatabaseService
{
    private static readonly List<GameSaveEntry> BuiltInGames = new()
    {
        new GameSaveEntry(
            "Elden Ring",
            new[] { @"{{p|appdata}}\EldenRing\{{p|uid}}" },
            "1245620"
        ),
        new GameSaveEntry(
            "Elden Ring Nightreign",
            new[] { @"{{p|appdata}}\EldenRingNightreign\{{p|uid}}" },
            null
        ),
        new GameSaveEntry(
            "Black Myth: Wukong",
            new[] {
                @"{{p|steam}}\userdata\{{p|uid}}\2358720\remote",
                @"{{p|localappdata}}\b1\Saved\SaveGames"
            },
            "2358720"
        ),
        new GameSaveEntry(
            "Cyberpunk 2077",
            new[] { @"{{p|savedgames}}\CD Projekt Red\Cyberpunk 2077" },
            "1091500"
        ),
        new GameSaveEntry(
            "Baldur's Gate 3",
            new[] { @"{{p|localappdata}}\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames" },
            "1086940"
        ),
        new GameSaveEntry(
            "The Witcher 3: Wild Hunt",
            new[] { @"{{p|documents}}\The Witcher 3\gamesaves" },
            "292030"
        ),
        new GameSaveEntry(
            "Sekiro: Shadows Die Twice",
            new[] { @"{{p|appdata}}\Sekiro\{{p|uid}}" },
            "814380"
        ),
        new GameSaveEntry(
            "Dark Souls III",
            new[] { @"{{p|appdata}}\DarkSoulsIII\{{p|uid}}" },
            "374320"
        ),
        new GameSaveEntry(
            "Dark Souls Remastered",
            new[] { @"{{p|documents}}\NBGI\DARK SOULS REMASTERED\{{p|uid}}" },
            "570940"
        ),
        new GameSaveEntry(
            "Dark Souls II: Scholar of the First Sin",
            new[] { @"{{p|appdata}}\DarkSoulsII\{{p|uid}}" },
            "335300"
        ),
        new GameSaveEntry(
            "Hades",
            new[] { @"{{p|documents}}\Saved Games\Hades" },
            "1145360"
        ),
        new GameSaveEntry(
            "Hades II",
            new[] { @"{{p|savedgames}}\Hades II" },
            "1145350"
        ),
        new GameSaveEntry(
            "Hollow Knight",
            new[] { @"{{p|appdata}}\..\LocalLow\Team Cherry\Hollow Knight" },
            "367520"
        ),
        new GameSaveEntry(
            "Stardew Valley",
            new[] { @"{{p|appdata}}\StardewValley\Saves" },
            "413150"
        ),
        new GameSaveEntry(
            "Grand Theft Auto V",
            new[] {
                @"{{p|documents}}\Rockstar Games\GTA V\Profiles",
                @"{{p|localappdata}}\Rockstar Games\GTA V"
            },
            "271590"
        ),
        new GameSaveEntry(
            "Red Dead Redemption 2",
            new[] { @"{{p|documents}}\Rockstar Games\Red Dead Redemption 2\Profiles" },
            "1174180"
        ),
        new GameSaveEntry(
            "Palworld",
            new[] { @"{{p|localappdata}}\Pal\Saved\SaveGames" },
            "1623730"
        ),
        new GameSaveEntry(
            "God of War",
            new[] { @"{{p|savedgames}}\God of War\{{p|uid}}" },
            "1593500"
        ),
        new GameSaveEntry(
            "God of War Ragnarok",
            new[] { @"{{p|savedgames}}\God of War Ragnarok\{{p|uid}}" },
            "2322010"
        ),
        new GameSaveEntry(
            "Monster Hunter: World",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\582010\remote" },
            "582010"
        ),
        new GameSaveEntry(
            "Monster Hunter Rise",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\1446780\remote" },
            "1446780"
        ),
        new GameSaveEntry(
            "Monster Hunter Wilds",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\2246340\remote" },
            "2246340"
        ),
        new GameSaveEntry(
            "The Elder Scrolls V: Skyrim Special Edition",
            new[] { @"{{p|documents}}\My Games\Skyrim Special Edition\Saves" },
            "489830"
        ),
        new GameSaveEntry(
            "Fallout 4",
            new[] { @"{{p|documents}}\My Games\Fallout4\Saves" },
            "377160"
        ),
        new GameSaveEntry(
            "Lies of P",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\1627720\remote" },
            "1627720"
        ),
        new GameSaveEntry(
            "Resident Evil 4",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\2050650\remote" },
            "2050650"
        ),
        new GameSaveEntry(
            "Resident Evil Village",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\1196590\remote" },
            "1196590"
        ),
        new GameSaveEntry(
            "Resident Evil 2",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\883710\remote" },
            "883710"
        ),
        new GameSaveEntry(
            "Resident Evil 3",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\952060\remote" },
            "952060"
        ),
        new GameSaveEntry(
            "Resident Evil 7 Biohazard",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\418370\remote" },
            "418370"
        ),
        new GameSaveEntry(
            "Slay the Spire",
            new[] { @"{{p|steam}}\steamapps\common\SlayTheSpire\preferences" },
            "646570"
        ),
        new GameSaveEntry(
            "Persona 5 Royal",
            new[] { @"{{p|appdata}}\SEGA\P5R\Steam\{{p|uid}}" },
            "1687950"
        ),
        new GameSaveEntry(
            "Persona 3 Reload",
            new[] { @"{{p|appdata}}\SEGA\P3R\Steam\{{p|uid}}" },
            "2161700"
        ),
        new GameSaveEntry(
            "Persona 4 Golden",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\1113000\remote" },
            "1113000"
        ),
        new GameSaveEntry(
            "Marvel's Spider-Man Remastered",
            new[] { @"{{p|documents}}\Marvel's Spider-Man Remastered\{{p|uid}}" },
            "1817070"
        ),
        new GameSaveEntry(
            "Ghost of Tsushima DIRECTOR'S CUT",
            new[] { @"{{p|documents}}\Ghost of Tsushima DIRECTOR'S CUT\{{p|uid}}" },
            "2215430"
        ),
        new GameSaveEntry(
            "Horizon Zero Dawn",
            new[] { @"{{p|documents}}\Horizon Zero Dawn\Saved Games" },
            "1151640"
        ),
        new GameSaveEntry(
            "Horizon Forbidden West",
            new[] { @"{{p|documents}}\Horizon Forbidden West Complete Edition\{{p|uid}}" },
            "2420110"
        ),
        new GameSaveEntry(
            "Dead Cells",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\588650\remote" },
            "588650"
        ),
        new GameSaveEntry(
            "Celeste",
            new[] { @"{{p|localappdata}}\Celeste\Saves" },
            "504230"
        ),
        new GameSaveEntry(
            "Subnautica",
            new[] { @"{{p|appdata}}\..\LocalLow\Unknown Worlds\Subnautica\Subnautica\SavedGames" },
            "264710"
        ),
        new GameSaveEntry(
            "Terraria",
            new[] { @"{{p|documents}}\My Games\Terraria" },
            "105600"
        ),
        new GameSaveEntry(
            "Hogwarts Legacy",
            new[] { @"{{p|localappdata}}\Hogwarts Legacy\Saved\SaveGames\{{p|uid}}" },
            "990080"
        ),
        new GameSaveEntry(
            "Silent Hill 2",
            new[] { @"{{p|localappdata}}\SilentHill2\Saved\SaveGames\{{p|uid}}" },
            "2124490"
        ),
        new GameSaveEntry(
            "Armored Core VI Fires of Rubicon",
            new[] { @"{{p|appdata}}\ArmoredCore6\{{p|uid}}" },
            "1888160"
        ),
        new GameSaveEntry(
            "Dragon's Dogma 2",
            new[] { @"{{p|steam}}\userdata\{{p|uid}}\2054970\remote\win64_save" },
            "2054970"
        ),
        new GameSaveEntry(
            "Nier: Automata",
            new[] { @"{{p|documents}}\My Games\NieR_Automata" },
            "524220"
        ),
        new GameSaveEntry(
            "Death Stranding",
            new[] { @"{{p|localappdata}}\KojimaProductions\DeathStranding" },
            "1190460"
        ),
        new GameSaveEntry(
            "Dave the Diver",
            new[] { @"{{p|appdata}}\..\LocalLow\nexon\DAVE THE DIVER\SteamSaves" },
            "1868140"
        ),
        new GameSaveEntry(
            "Forza Horizon 5",
            new[] {
                @"{{p|steam}}\userdata\{{p|uid}}\1551360\remote",
                @"{{p|localappdata}}\Packages\Microsoft.624F8B84B80_8wekyb3d8bbwe\SystemAppData\wgs"
            },
            "1551360"
        ),
        new GameSaveEntry(
            "Like a Dragon: Infinite Wealth",
            new[] { @"{{p|appdata}}\SEGA\LikeADragonInfiniteWealth\Steam\{{p|uid}}" },
            "2072450"
        ),
        new GameSaveEntry(
            "Yakuza: Like a Dragon",
            new[] { @"{{p|appdata}}\SEGA\YakuzaLikeADragon\{{p|uid}}" },
            "1235140"
        ),
        new GameSaveEntry(
            "Helldivers 2",
            new[] { @"{{p|appdata}}\Arrowhead\Helldivers2\saves" },
            "553850"
        )
    };

    public GameSaveInfo? FindMatchingGame(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var normalized = DatabaseService.NormalizeGameName(query);

        // Exact normalized match
        var match = BuiltInGames.FirstOrDefault(g => DatabaseService.NormalizeGameName(g.Name) == normalized);
        if (match != null) return ToGameSaveInfo(match);

        // Substring / contains match
        match = BuiltInGames.FirstOrDefault(g => 
            DatabaseService.NormalizeGameName(g.Name).Contains(normalized) ||
            normalized.Contains(DatabaseService.NormalizeGameName(g.Name)));

        if (match != null) return ToGameSaveInfo(match);

        return null;
    }

    public List<string> GetAllKnownGameNames()
    {
        return BuiltInGames.Select(g => g.Name).ToList();
    }

    private static GameSaveInfo ToGameSaveInfo(GameSaveEntry entry)
    {
        return new GameSaveInfo
        {
            GameName = entry.Name,
            NormalizedName = DatabaseService.NormalizeGameName(entry.Name),
            RawPatterns = entry.Patterns.ToList(),
            SteamAppId = entry.SteamAppId,
            Source = "Ludusavi Catalog"
        };
    }

    private record GameSaveEntry(string Name, string[] Patterns, string? SteamAppId);
}
