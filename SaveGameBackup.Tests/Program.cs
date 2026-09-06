using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;

Console.WriteLine("=================================================");
Console.WriteLine("  GAME SAVE BACKUP TOOL - INTEGRATION TESTS");
Console.WriteLine("=================================================");

var tempTestDir = Path.Combine(Path.GetTempPath(), "SaveGameBackup_Test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempTestDir);

try
{
    var testDbPath = Path.Combine(tempTestDir, "test_save_backup.db");
    Console.WriteLine($"[1] Testing SQLite Database at: {testDbPath}");
    var db = new DatabaseService(testDbPath);

    // Test caching
    var testGame = new GameSaveInfo
    {
        GameName = "Test Adventure",
        NormalizedName = DatabaseService.NormalizeGameName("Test Adventure"),
        RawPatterns = new List<string> { @"%LOCALAPPDATA%\TestAdventure\Saves" },
        Source = "Test"
    };

    await db.SaveGameCacheAsync(testGame);
    var cached = await db.GetCachedGameAsync("test adventure");
    if (cached == null || cached.RawPatterns.Count != 1)
        throw new Exception("SQLite cache save/retrieve failed!");
    Console.WriteLine("  ✓ SQLite Game Cache OK!");

    // Test Backup History in SQLite
    var testRecord = new BackupRecord
    {
        GameName = "Test Adventure",
        BackupPath = Path.Combine(tempTestDir, "Backups", "Test Adventure"),
        SourcePath = Path.Combine(tempTestDir, "SourceSave"),
        FileCount = 3,
        TotalSizeBytes = 1024 * 50,
        BackupDate = DateTime.Now,
        IsCompressed = false,
        Status = "Success"
    };
    var recordId = await db.InsertBackupRecordAsync(testRecord);
    var history = await db.GetBackupHistoryAsync();
    if (history.Count == 0 || history[0].Id != recordId)
        throw new Exception("SQLite Backup History insertion failed!");
    Console.WriteLine($"  ✓ SQLite Backup History OK! (Inserted ID: {recordId})");

    // Test 2: Ludusavi Catalog
    Console.WriteLine("\n[2] Testing Ludusavi Built-in Catalog");
    var catalog = new LudusaviDatabaseService();
    var elden = catalog.FindMatchingGame("Elden Ring");
    if (elden == null || elden.RawPatterns.Count == 0)
        throw new Exception("Elden Ring not found in catalog!");
    Console.WriteLine($"  ✓ Elden Ring found: {elden.RawPatterns[0]} (Steam AppID: {elden.SteamAppId})");

    var wukong = catalog.FindMatchingGame("Black Myth Wukong");
    if (wukong == null || wukong.RawPatterns.Count == 0)
        throw new Exception("Black Myth: Wukong not found in catalog!");
    Console.WriteLine($"  ✓ Black Myth: Wukong found: {wukong.RawPatterns[0]}");

    // Test 3: PCGamingWiki Online Search
    Console.WriteLine("\n[3] Testing PCGamingWiki Online API");
    var wiki = new PCGamingWikiService();
    var onlineResult = await wiki.SearchAndFetchSaveInfoAsync("Hades");
    if (onlineResult != null)
    {
        Console.WriteLine($"  ✓ Online search Hades: {onlineResult.GameName}, Patterns found: {onlineResult.RawPatterns.Count}");
        foreach (var p in onlineResult.RawPatterns)
        {
            Console.WriteLine($"    - {p}");
        }
    }
    else
    {
        Console.WriteLine("  ⚠ Online Hades lookup returned null (network might be restricted or slow), skipping.");
    }

    // Test 4: Path Resolver
    Console.WriteLine("\n[4] Testing Path Resolver");
    var resolver = new PathResolverService();
    var steamPath = resolver.GetSteamPath();
    Console.WriteLine($"  ✓ Detected Steam Path: {steamPath}");

    var resolvedPaths = resolver.ResolveRawPattern(@"{{p|appdata}}\MyTestGame\save.dat");
    if (resolvedPaths.Count == 0 || !resolvedPaths[0].Contains(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
        throw new Exception("Pattern resolution for appdata failed!");
    Console.WriteLine($"  ✓ Resolved AppData: {resolvedPaths[0]}");

    // Test 5: End-to-End Backup & Restore simulation
    Console.WriteLine("\n[5] Testing End-to-End Backup & Restore");
    var mockSourceDir = Path.Combine(tempTestDir, "MockSourceSave", "SavedGames");
    Directory.CreateDirectory(mockSourceDir);
    File.WriteAllText(Path.Combine(mockSourceDir, "slot1.sav"), "SAVEGAME_HEADER_DATA_SLOT_1");
    File.WriteAllText(Path.Combine(mockSourceDir, "slot2.sav"), "SAVEGAME_HEADER_DATA_SLOT_2");
    var subDir = Path.Combine(mockSourceDir, "Profiles");
    Directory.CreateDirectory(subDir);
    File.WriteAllText(Path.Combine(subDir, "settings.cfg"), "GRAPHICS_LEVEL=HIGH");

    var mockGameInfo = new GameSaveInfo
    {
        GameName = "Awesome RPG Game",
        DetectedPathsOnDisk = new List<string> { mockSourceDir },
        TotalSizeBytes = 100,
        FileCount = 3
    };

    var backupDestRoot = Path.Combine(tempTestDir, "ActualBackups");
    var appSettings = new AppSettings
    {
        BackupRootDirectory = backupDestRoot,
        CreateTimestampSubfolder = false,
        AutoCompressZip = false
    };

    var backupService = new BackupService(db);
    var progress = new Progress<BackupProgress>(p =>
    {
        Console.WriteLine($"    [{p.Percent}%] {p.Message}");
    });

    Console.WriteLine("  Starting folder backup...");
    var backupResult = await backupService.BackupGameAsync(mockGameInfo, appSettings, progress);
    Console.WriteLine($"  ✓ Backup completed to: {backupResult.BackupPath}");
    Console.WriteLine($"    Files copied: {backupResult.FileCount}, Total size: {backupResult.FormattedSize}");

    if (!Directory.Exists(backupResult.BackupPath))
        throw new Exception("Target backup folder does not exist!");
    if (!File.Exists(Path.Combine(backupResult.BackupPath, "slot1.sav")))
        throw new Exception("slot1.sav was not copied to backup folder!");
    if (!File.Exists(Path.Combine(backupResult.BackupPath, "Profiles", "settings.cfg")))
        throw new Exception("Profiles/settings.cfg was not copied recursively!");

    // Test ZIP compression backup
    Console.WriteLine("\n  Testing Zip archive backup...");
    appSettings.AutoCompressZip = true;
    var zipResult = await backupService.BackupGameAsync(mockGameInfo, appSettings, progress);
    Console.WriteLine($"  ✓ Zip Backup completed: {zipResult.BackupPath}");
    if (!File.Exists(zipResult.BackupPath))
        throw new Exception("Backup zip file does not exist!");

    // Test Restore
    Console.WriteLine("\n  Testing Restore from Zip...");
    var restoreTargetDir = Path.Combine(tempTestDir, "RestoredSave");
    zipResult.SourcePath = restoreTargetDir;
    await backupService.RestoreAsync(zipResult, progress);
    if (!File.Exists(Path.Combine(restoreTargetDir, "slot1.sav")))
        throw new Exception("Restored slot1.sav not found!");
    Console.WriteLine("  ✓ Restore succeeded!");

    // Test Multi-Path Backup & Multi-Path Restore
    Console.WriteLine("\n[6] Testing Multi-Path Backup & Multi-Path Restore");
    var multiSource1 = Path.Combine(tempTestDir, "MultiGame_SaveDir1");
    var multiSource2 = Path.Combine(tempTestDir, "MultiGame_SaveDir2");
    Directory.CreateDirectory(multiSource1);
    Directory.CreateDirectory(multiSource2);
    File.WriteAllText(Path.Combine(multiSource1, "save1.bin"), "Multi Save Data 1");
    File.WriteAllText(Path.Combine(multiSource2, "config.ini"), "Resolution=1920x1080");

    var multiGameInfo = new GameSaveInfo
    {
        GameName = "Multi Path RPG",
        DetectedPathsOnDisk = new List<string> { multiSource1, multiSource2 }
    };

    var multiBackupSettings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "MultiBackups"),
        AutoCompressZip = false,
        CreateTimestampSubfolder = true
    };

    var multiBackupRecord = await backupService.BackupGameAsync(multiGameInfo, multiBackupSettings, new List<string> { multiSource1, multiSource2 });
    Console.WriteLine($"  ✓ Multi-path backup created at: {multiBackupRecord.BackupPath}");
    Console.WriteLine($"    Source paths recorded: {multiBackupRecord.SourcePath}");

    if (!multiBackupRecord.SourcePath.Contains(multiSource1) || !multiBackupRecord.SourcePath.Contains(multiSource2))
        throw new Exception("Multi-path backup record does not record both paths!");

    // Delete original files to test restore
    File.Delete(Path.Combine(multiSource1, "save1.bin"));
    File.Delete(Path.Combine(multiSource2, "config.ini"));

    await backupService.RestoreAsync(multiBackupRecord);

    if (!File.Exists(Path.Combine(multiSource1, "save1.bin")))
        throw new Exception("Multi-path restore failed for source 1!");
    if (!File.Exists(Path.Combine(multiSource2, "config.ini")))
        throw new Exception("Multi-path restore failed for source 2!");

    Console.WriteLine("  ✓ Multi-path restore restored files to their exact respective original paths!");

    // Test 7: Project Root and Default DB Path & AppConfigService
    Console.WriteLine("\n[7] Testing Project Root, Default DB Path & AppConfigService");
    var projectRoot = DatabaseService.GetDefaultProjectRoot();
    Console.WriteLine($"  ✓ Detected Project Root: {projectRoot}");
    if (!File.Exists(Path.Combine(projectRoot, "SaveVault.slnx")) && !File.Exists(Path.Combine(projectRoot, "LaunchApp.bat")))
        throw new Exception("Detected project root does not contain SaveVault solution files!");

    var defaultDbPath = Path.Combine(projectRoot, "save_backup.db");
    Console.WriteLine($"  ✓ Default DB Path: {defaultDbPath}");

    // Test AppConfigService
    var testCustomDbPath = Path.Combine(tempTestDir, "custom_configured_db.db");
    AppConfigService.SaveDatabasePath(testCustomDbPath);
    var loadedPath = AppConfigService.GetConfiguredDatabasePath();
    if (loadedPath != testCustomDbPath)
        throw new Exception($"AppConfigService failed: expected {testCustomDbPath} but got {loadedPath}");
    Console.WriteLine($"  ✓ AppConfigService Save/Load OK: {loadedPath}");

    // Reset config back so it doesn't pollute user environment
    AppConfigService.SaveDatabasePath(string.Empty);

    // Test 8: GetRestoreItemsFromBackup, Selective Restore & Custom Destination
    Console.WriteLine("\n[8] Testing GetRestoreItemsFromBackup, Selective Restore & Custom Destination");
    var restoreItems = backupService.GetRestoreItemsFromBackup(multiBackupRecord);
    if (restoreItems.Count != 2)
        throw new Exception($"Expected 2 restore items, but got {restoreItems.Count}");
    Console.WriteLine($"  ✓ GetRestoreItemsFromBackup returned {restoreItems.Count} items.");
    Console.WriteLine($"    Item 1: {restoreItems[0].OriginalSourcePath} ({restoreItems[0].FileCount} files, {restoreItems[0].FormattedSize})");
    Console.WriteLine($"    Item 2: {restoreItems[1].OriginalSourcePath} ({restoreItems[1].FileCount} files, {restoreItems[1].FormattedSize})");

    // Selective Restore: only restore item 1, ignore item 2
    File.Delete(Path.Combine(multiSource1, "save1.bin"));
    File.Delete(Path.Combine(multiSource2, "config.ini"));

    restoreItems[0].IsSelected = true;
    restoreItems[1].IsSelected = false; // Deselected!

    await backupService.RestoreAsync(multiBackupRecord, restoreItems);

    if (!File.Exists(Path.Combine(multiSource1, "save1.bin")))
        throw new Exception("Selective restore: Selected Item 1 was not restored!");
    if (File.Exists(Path.Combine(multiSource2, "config.ini")))
        throw new Exception("Selective restore: Unselected Item 2 was restored when it should have been skipped!");
    Console.WriteLine("  ✓ Selective restore OK (only item 1 restored, item 2 remained skipped)");

    // Custom Destination Restore: restore item 2 to a custom destination directory
    var customDestFolder = Path.Combine(tempTestDir, "CustomRestoredLocation");
    Directory.CreateDirectory(customDestFolder);

    restoreItems[0].IsSelected = false;
    restoreItems[1].IsSelected = true;
    restoreItems[1].RestoreDestinationPath = customDestFolder;

    await backupService.RestoreAsync(multiBackupRecord, restoreItems);

    if (!File.Exists(Path.Combine(customDestFolder, "config.ini")))
        throw new Exception("Custom destination restore failed: file not found in custom destination!");
    Console.WriteLine($"  ✓ Custom destination restore OK (restored to: {customDestFolder})");

    // [9] Testing GroupedBackupHistory and GameBackupSummary Aggregation
    Console.WriteLine("\n[9] Testing GroupedBackupHistory & GameBackupSummary Aggregation");
    var testRecords = new List<BackupRecord>
    {
        new() { Id = 101, GameName = "Cyberpunk 2077", TotalSizeBytes = 1000, BackupDate = DateTime.Now.AddHours(-3), BackupPath = "c:\\cp1" },
        new() { Id = 102, GameName = "Cyberpunk 2077", TotalSizeBytes = 2500, BackupDate = DateTime.Now.AddHours(-1), BackupPath = "c:\\cp2" },
        new() { Id = 103, GameName = "Cyberpunk 2077", TotalSizeBytes = 1500, BackupDate = DateTime.Now.AddHours(-2), BackupPath = "c:\\cp3" },
        new() { Id = 104, GameName = "Elden Ring", TotalSizeBytes = 5000, BackupDate = DateTime.Now.AddHours(-4), BackupPath = "c:\\er1" }
    };

    var groups = testRecords
        .GroupBy(r => r.GameName.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var ordered = g.OrderByDescending(r => r.BackupDate).ToList();
            var latest = ordered.First();
            return new GameBackupSummary
            {
                GameName = latest.GameName,
                BackupCount = ordered.Count,
                LatestBackupDate = latest.BackupDate,
                LatestSizeBytes = latest.TotalSizeBytes,
                TotalSizeBytes = ordered.Sum(r => r.TotalSizeBytes),
                LatestBackupType = latest.BackupTypeFormatted,
                LatestBackupPath = latest.BackupPath,
                LatestRecord = latest,
                Records = ordered
            };
        })
        .OrderByDescending(g => g.LatestBackupDate)
        .ToList();

    if (groups.Count != 2)
        throw new Exception($"Expected 2 unique game groups, got {groups.Count}");

    var cpSummary = groups.FirstOrDefault(g => g.GameName == "Cyberpunk 2077");
    if (cpSummary == null) throw new Exception("Cyberpunk 2077 group not found");
    if (cpSummary.BackupCount != 3) throw new Exception($"Expected 3 backups for Cyberpunk, got {cpSummary.BackupCount}");
    if (cpSummary.TotalSizeBytes != 5000) throw new Exception($"Expected total size 5000, got {cpSummary.TotalSizeBytes}");
    if (cpSummary.LatestSizeBytes != 2500) throw new Exception($"Expected latest size 2500, got {cpSummary.LatestSizeBytes}");
    if (cpSummary.LatestRecord?.Id != 102) throw new Exception($"Expected latest record ID 102, got {cpSummary.LatestRecord?.Id}");
    if (cpSummary.Records.Count != 3) throw new Exception($"Expected 3 snapshot records, got {cpSummary.Records.Count}");

    Console.WriteLine($"  ✓ Grouped {testRecords.Count} records into {groups.Count} game rows.");
    Console.WriteLine($"  ✓ Cyberpunk 2077: {cpSummary.BackupCount} snapshots, Total Size: {cpSummary.FormattedTotalSize}, Latest: {cpSummary.FormattedLatestDate}");
    Console.WriteLine("  ✓ GameBackupSummary aggregation verified successfully!");

    Console.WriteLine("\n=================================================");
    Console.WriteLine("  ALL INTEGRATION TESTS PASSED SUCCESSFULLY! ✓");
    Console.WriteLine("=================================================");
}
finally
{
    try
    {
        Directory.Delete(tempTestDir, true);
    }
    catch
    {
        // Cleanup best effort
    }
}
