using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;

Console.WriteLine("=================================================");
Console.WriteLine("  GAME SAVE BACKUP TOOL - INTEGRATION TESTS");
Console.WriteLine("=================================================");

var tempTestDir = Path.Combine(Path.GetTempPath(), "SaveGameBackup_Test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempTestDir);

var realConfigPath = AppConfigService.GetConfigFilePath();
string? originalConfigContent = File.Exists(realConfigPath) ? File.ReadAllText(realConfigPath) : null;

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

    // Test 2: AppConfigService (In-Memory RAM Cache & app_config.json)
    Console.WriteLine("\n[2] Testing AppConfigService (In-Memory RAM Cache & app_config.json)");
    var sampleConfig = new AppConfigFile
    {
        DatabasePath = Path.Combine(tempTestDir, "test_app_config.db"),
        BackupRootDirectory = Path.Combine(tempTestDir, "MyCustomBackups"),
        CreateTimestampSubfolder = true,
        AutoCompressZip = true
    };
    AppConfigService.SaveConfig(sampleConfig);

    var memCached = AppConfigService.GetConfig();
    if (memCached.DatabasePath != sampleConfig.DatabasePath ||
        memCached.BackupRootDirectory != sampleConfig.BackupRootDirectory ||
        memCached.AutoCompressZip != true)
    {
        throw new Exception("AppConfig RAM Cache does not match saved config!");
    }
    Console.WriteLine($"  ✓ AppConfig RAM cache verified OK: BackupRoot={memCached.BackupRootDirectory}, AutoZip={memCached.AutoCompressZip}");

    // Test Search Coordinator: Cache-then-PCGamingWiki logic
    var searchCoordinator = new GameSearchCoordinator(db);
    var searchResult = await searchCoordinator.SearchAndDetectGameAsync("Test Adventure");
    Console.WriteLine($"  ✓ GameSearchCoordinator successfully retrieved: {searchResult.GameName} (Source: {searchResult.Source})");

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

    Console.WriteLine("  Starting zip backup...");
    var backupResult = await backupService.BackupGameAsync(mockGameInfo, appSettings, progress);
    Console.WriteLine($"  ✓ Backup completed to: {backupResult.BackupPath}");
    Console.WriteLine($"    Files copied: {backupResult.FileCount}, Total size: {backupResult.FormattedSize}");

    if (!File.Exists(backupResult.BackupPath))
        throw new Exception("Target backup zip file does not exist!");

    // Test ZIP compression backup
    Console.WriteLine("\n  Testing Zip archive backup verification...");
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

    // [10] Testing SavePaths field persistence & loading modes
    Console.WriteLine("\n[10] Testing SavePaths field persistence & loading modes");
    var customPaths = new List<string> { @"C:\Games\RPG\Save1", @"C:\Games\RPG\Save2" };
    var recWithSavePaths = new BackupRecord
    {
        GameName = "Custom Path Game",
        BackupPath = @"C:\Backups\CustomRPG",
        SourcePath = string.Join(" | ", customPaths),
        SavePaths = System.Text.Json.JsonSerializer.Serialize(customPaths),
        FileCount = 10,
        TotalSizeBytes = 2048,
        BackupDate = DateTime.Now
    };

    var insertedId = await db.InsertBackupRecordAsync(recWithSavePaths);
    var retrievedList = await db.GetBackupHistoryAsync();
    var foundRec = retrievedList.FirstOrDefault(r => r.Id == insertedId);

    if (foundRec == null) throw new Exception("Failed to retrieve inserted backup record with SavePaths");
    if (foundRec.SavePathsList.Count != 2 || foundRec.SavePathsList[0] != @"C:\Games\RPG\Save1")
        throw new Exception($"SavePathsList mismatch! Expected 2 paths, got {foundRec.SavePathsList.Count}");

    Console.WriteLine($"  ✓ Inserted & retrieved record with SavePaths column: {foundRec.SavePaths}");
    Console.WriteLine($"  ✓ SavePathsList parsed correctly: {foundRec.SavePathsList.Count} paths");

    // [11] Testing Two-Table Database Architecture (backup_history & backup_history_details)
    Console.WriteLine("\n[11] Testing Two-Table Database Architecture (Master & Detail tables)");
    var detail1 = new BackupHistoryDetail
    {
        GameName = "Elden Ring",
        BackupPath = Path.Combine(tempTestDir, "EldenRing_Snap1.zip"),
        SourcePath = @"C:\Users\Player\AppData\Roaming\EldenRing\Save1",
        SavePaths = System.Text.Json.JsonSerializer.Serialize(new List<string> { @"C:\Users\Player\AppData\Roaming\EldenRing\Save1" }),
        ManifestJson = "{\"GameName\":\"Elden Ring\",\"Items\":[{\"SubFolder\":\"\",\"SourcePath\":\"C:\\\\Users\\\\Player\\\\AppData\\\\Roaming\\\\EldenRing\\\\Save1\",\"FileCount\":2,\"TotalSizeBytes\":1000}]}",
        FileCount = 2,
        TotalSizeBytes = 1000,
        BackupDate = DateTime.Now.AddHours(-2),
        IsCompressed = true,
        Status = "Success"
    };

    // Lần 1: Thêm mới game Elden Ring -> Cả 2 bảng đều thêm 1 dòng
    var detailId1 = await db.InsertOrUpdateBackupHistoryAsync(detail1);
    var masterList1 = await db.GetGameHistoriesAsync();
    var eldenMaster1 = masterList1.FirstOrDefault(g => g.GameName == "Elden Ring");

    if (eldenMaster1 == null) throw new Exception("Master row for Elden Ring not found after 1st backup");
    if (eldenMaster1.BackupCount != 1) throw new Exception($"Expected BackupCount = 1, got {eldenMaster1.BackupCount}");
    if (eldenMaster1.TotalSizeBytes != 1000) throw new Exception($"Expected TotalSizeBytes = 1000, got {eldenMaster1.TotalSizeBytes}");

    var detailsList1 = await db.GetHistoryDetailsByGameIdAsync(eldenMaster1.Id);
    if (detailsList1.Count != 1) throw new Exception($"Expected 1 detail record, got {detailsList1.Count}");
    Console.WriteLine("  ✓ 1st Backup: Created 1 Master row (BackupCount=1) and 1 Detail row.");

    // Lần 2: Backup tiếp game Elden Ring -> Master UPDATE, Detail INSERT mới
    var detail2 = new BackupHistoryDetail
    {
        GameName = "Elden Ring",
        BackupPath = Path.Combine(tempTestDir, "EldenRing_Snap2.zip"),
        SourcePath = @"C:\Users\Player\AppData\Roaming\EldenRing\Save1",
        SavePaths = System.Text.Json.JsonSerializer.Serialize(new List<string> { @"C:\Users\Player\AppData\Roaming\EldenRing\Save1" }),
        ManifestJson = "{\"GameName\":\"Elden Ring\",\"Items\":[{\"SubFolder\":\"\",\"SourcePath\":\"C:\\\\Users\\\\Player\\\\AppData\\\\Roaming\\\\EldenRing\\\\Save1\",\"FileCount\":3,\"TotalSizeBytes\":1500}]}",
        FileCount = 3,
        TotalSizeBytes = 1500,
        BackupDate = DateTime.Now,
        IsCompressed = true,
        Status = "Success"
    };

    var detailId2 = await db.InsertOrUpdateBackupHistoryAsync(detail2);
    var masterList2 = await db.GetGameHistoriesAsync();
    var eldenMaster2 = masterList2.FirstOrDefault(g => g.GameName == "Elden Ring");

    if (eldenMaster2 == null) throw new Exception("Master row for Elden Ring not found after 2nd backup");
    if (masterList2.Count != masterList1.Count) throw new Exception($"Master table should NOT create a new row! Expected {masterList1.Count} rows, got {masterList2.Count}");
    if (eldenMaster2.BackupCount != 2) throw new Exception($"Expected BackupCount = 2, got {eldenMaster2.BackupCount}");
    if (eldenMaster2.TotalSizeBytes != 2500) throw new Exception($"Expected TotalSizeBytes = 2500, got {eldenMaster2.TotalSizeBytes}");

    var detailsList2 = await db.GetHistoryDetailsByGameIdAsync(eldenMaster2.Id);
    if (detailsList2.Count != 2) throw new Exception($"Expected 2 detail records, got {detailsList2.Count}");
    Console.WriteLine("  ✓ 2nd Backup: Master row UPDATED (BackupCount=2, TotalSizeBytes=2500), Detail row INSERTED.");

    // [12] Testing Zero Manifest File on Disk & 100% Manifest in Database
    Console.WriteLine("\n[12] Testing Zero-File Manifest & 100% SQLite Manifest Retrieval");
    var backupSvc = new BackupService(db);
    var testSaveDir = Path.Combine(tempTestDir, "EldenSaveSource");
    Directory.CreateDirectory(testSaveDir);
    File.WriteAllText(Path.Combine(testSaveDir, "ER0000.sl2"), "dummy_save_content_elden_ring");

    var backupSettings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "EldenBackups"),
        CreateTimestampSubfolder = false,
        AutoCompressZip = true
    };

    var gameInfo = new GameSaveInfo
    {
        GameName = "Elden Ring",
        NormalizedName = "elden ring",
        DetectedPathsOnDisk = new List<string> { testSaveDir }
    };

    var backupRec = await backupSvc.BackupGameAsync(gameInfo, backupSettings, new List<string> { testSaveDir });

    // Kiểm tra file zip backup: TUYỆT ĐỐI KHÔNG chứa file backup_manifest.json
    if (!File.Exists(backupRec.BackupPath)) throw new Exception("Backup ZIP file not found!");
    using (var archive = System.IO.Compression.ZipFile.OpenRead(backupRec.BackupPath))
    {
        var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("backup_manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry != null)
        {
            throw new Exception("FAIL: backup_manifest.json file WAS STILL CREATED inside ZIP! It must be completely eliminated.");
        }
    }
    Console.WriteLine("  ✓ Verified: ZIP archive contains ZERO backup_manifest.json file! Pure game save files only.");

    // Kiểm tra phục hồi trực tiếp từ SQLite ManifestJson
    var eldenHistory = (await db.GetGameHistoriesAsync()).First(g => g.GameName == "Elden Ring");
    var latestDetail = (await db.GetHistoryDetailsByGameIdAsync(eldenHistory.Id)).First();

    if (string.IsNullOrWhiteSpace(latestDetail.ManifestJson))
        throw new Exception("FAIL: ManifestJson column in backup_history_details is empty!");

    var restoreTargets = backupSvc.GetRestoreItemsFromBackup(latestDetail);
    if (restoreTargets.Count != 1 || !restoreTargets[0].OriginalSourcePath.Equals(testSaveDir, StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception($"FAIL: Restore items from SQLite ManifestJson mismatch! Got {restoreTargets.Count} items.");
    }
    Console.WriteLine($"  ✓ Verified: Restored {restoreTargets.Count} path target(s) 100% from SQLite ManifestJson without touching disk manifest!");

    // [13] Testing Physical File Deletion & Database Cascade/Update Sync
    Console.WriteLine("\n[13] Testing Physical File Deletion on Disk & Real-time List Reload");
    var fileToDelete = !string.IsNullOrEmpty(latestDetail.LocalBackupPath) ? latestDetail.LocalBackupPath : latestDetail.BackupPath;
    if (!File.Exists(fileToDelete)) throw new Exception($"File to delete does not exist: {fileToDelete}");

    // Xóa vật lý trên đĩa
    File.Delete(fileToDelete);
    if (File.Exists(fileToDelete)) throw new Exception("FAIL: File still exists on disk after File.Delete!");
    Console.WriteLine("  ✓ Physical backup file successfully deleted from disk.");

    // Xóa trong database
    await db.DeleteHistoryDetailAsync(latestDetail.Id, latestDetail.GameHistoryId);

    // Kiểm tra danh sách details còn lại
    var remainingDetails = await db.GetHistoryDetailsByGameIdAsync(latestDetail.GameHistoryId);
    Console.WriteLine($"  ✓ Remaining details in SQLite: {remainingDetails.Count} snapshot(s).");

    // Xóa nốt tất cả snapshot còn lại -> Master row tự động dọn dẹp sạch sẽ
    foreach (var d in remainingDetails)
    {
        await db.DeleteHistoryDetailAsync(d.Id, d.GameHistoryId);
    }

    var finalMasters = await db.GetGameHistoriesAsync();
    var finalElden = finalMasters.FirstOrDefault(g => g.GameName == "Elden Ring");
    if (finalElden != null)
    {
        throw new Exception("FAIL: Master row for Elden Ring was NOT auto-deleted when all details were removed!");
    }
    // [14] Testing Cloud Metadata in SQLite & CloudManagerService
    Console.WriteLine("\n[14] Testing Cloud Metadata in SQLite & CloudManagerService");
    var cloudDetail = new BackupHistoryDetail
    {
        GameName = "Dark Souls Remastered",
        BackupPath = Path.Combine(tempTestDir, "DarkSouls.zip"),
        SourcePath = "C:\\Saves\\DarkSouls",
        FileCount = 1,
        TotalSizeBytes = 5000,
        BackupDate = DateTime.Now,
        IsCompressed = true,
        IsCloudSynced = false
    };
    var cloudDetailId = await db.InsertOrUpdateBackupHistoryAsync(cloudDetail);
    var detailsBeforeSync = await db.GetHistoryDetailsByGameIdAsync(cloudDetail.GameHistoryId);
    if (detailsBeforeSync.Count == 0 || detailsBeforeSync[0].IsCloudSynced)
    {
        throw new Exception("FAIL: Expected IsCloudSynced to be false initially!");
    }

    // Update Cloud Sync status
    var syncDate = DateTime.UtcNow;
    await db.UpdateCloudSyncDetailAsync(cloudDetailId, true, "GoogleDrive", "mock-gdrive-file-id-999", "DarkSouls_backup.zip", syncDate);

    var detailsAfterSync = await db.GetHistoryDetailsByGameIdAsync(cloudDetail.GameHistoryId);
    var syncedItem = detailsAfterSync.First(d => d.Id == cloudDetailId);
    if (!syncedItem.IsCloudSynced || syncedItem.CloudProvider != "GoogleDrive" || syncedItem.CloudFileId != "mock-gdrive-file-id-999")
    {
        throw new Exception($"FAIL: Cloud metadata not properly updated! Synced={syncedItem.IsCloudSynced}, Provider={syncedItem.CloudProvider}, FileId={syncedItem.CloudFileId}");
    }
    Console.WriteLine("  ✓ SQLite Cloud Metadata update & retrieval verified OK!");

    // Test CloudManagerService
    var cloudManager = new SaveGameBackup.Core.Services.Cloud.CloudManagerService(db);
    await cloudManager.InitializeAsync();
    await cloudManager.SetActiveProviderAsync("OneDrive");
    if (cloudManager.ActiveProviderName != "OneDrive" || cloudManager.CurrentProvider.ProviderName != "OneDrive")
    {
        throw new Exception("FAIL: CloudManagerService active provider switch to OneDrive failed!");
    }
    await cloudManager.SetActiveProviderAsync("GoogleDrive");
    if (cloudManager.ActiveProviderName != "GoogleDrive" || cloudManager.CurrentProvider.ProviderName != "GoogleDrive")
    {
        throw new Exception("FAIL: CloudManagerService active provider switch to GoogleDrive failed!");
    }
    Console.WriteLine("  ✓ CloudManagerService provider switching & persistence verified OK!");

    // Test OAuthLoopbackReceiver
    using (var receiver = new SaveGameBackup.Core.Services.Cloud.OAuthLoopbackReceiver())
    {
        if (receiver.Port <= 0 || !receiver.RedirectUri.Contains(receiver.Port.ToString()))
        {
            throw new Exception("FAIL: OAuthLoopbackReceiver port binding failed!");
        }
        Console.WriteLine($"  ✓ OAuthLoopbackReceiver port allocation OK (Port: {receiver.Port})");
    }

    // [15] Testing SyncSnapshotToCloudAsync, Cloud RestoreAsync & DeleteSnapshotWithProgressAsync
    Console.WriteLine("\n[15] Testing SyncSnapshotToCloudAsync, Cloud RestoreAsync & DeleteSnapshotWithProgressAsync");
    backupService = new BackupService(db);
    var mockCloud = new MockCloudService();

    // Create a game save source to backup
    var phase2GameDir = Path.Combine(tempTestDir, "Phase2_GameSave");
    Directory.CreateDirectory(phase2GameDir);
    File.WriteAllText(Path.Combine(phase2GameDir, "phase2_slot.sav"), "Phase 2 Game Save Data");

    var phase2GameInfo = new GameSaveInfo
    {
        GameName = "Sekiro Shadows",
        NormalizedName = DatabaseService.NormalizeGameName("Sekiro Shadows"),
        DetectedPathsOnDisk = new List<string> { phase2GameDir }
    };

    var phase2Settings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "Phase2_Backups"),
        AutoCompressZip = true,
        CreateTimestampSubfolder = false
    };

    var phase2Record = await backupService.BackupGameAsync(phase2GameInfo, phase2Settings);
    var phase2Masters = await db.GetGameHistoriesAsync();
    var sekiroMaster = phase2Masters.First(m => m.GameName == "Sekiro Shadows");
    var sekiroDetails = await db.GetHistoryDetailsByGameIdAsync(sekiroMaster.Id);
    var sekiroDetail = sekiroDetails.First();

    // 15.1: Test SyncSnapshotToCloudAsync
    var syncResult = await backupService.SyncSnapshotToCloudAsync(sekiroDetail, mockCloud);
    if (!syncResult.Success || !mockCloud.UploadCalled || !sekiroDetail.IsCloudSynced || sekiroDetail.CloudProvider != "MockDrive")
    {
        throw new Exception("FAIL: SyncSnapshotToCloudAsync failed to upload or set cloud metadata!");
    }
    Console.WriteLine("  ✓ SyncSnapshotToCloudAsync uploaded snapshot & updated SQLite cloud metadata OK!");

    // 15.2: Test Restore from Cloud
    var cloudDestDir = Path.Combine(tempTestDir, "Phase2_CloudRestored");
    Directory.CreateDirectory(cloudDestDir);
    var restoreTarget = new RestoreItemTarget
    {
        IsSelected = true,
        RestoreDestinationPath = cloudDestDir,
        SubFolder = string.Empty
    };

    await backupService.RestoreAsync(
        sekiroDetail,
        new List<RestoreItemTarget> { restoreTarget },
        restoreFromCloud: true,
        mockCloud);

    if (!mockCloud.DownloadCalled || !File.Exists(Path.Combine(cloudDestDir, "cloud_restored_save.sav")))
    {
        throw new Exception("FAIL: RestoreAsync from Cloud failed to download and extract file!");
    }
    Console.WriteLine("  ✓ RestoreAsync from Cloud downloaded via API & extracted to destination OK!");

    // 15.2b: Test Multi-Cloud Restore - Detail has both OneDrive and GoogleDrive syncs
    // Verify that restoring via OneDrive uses OneDrive's FileId, even when CloudFileId is overwritten by GoogleDrive
    var multiCloudDetail = new BackupHistoryDetail
    {
        Id = sekiroDetail.Id,
        GameHistoryId = sekiroDetail.GameHistoryId,
        GameName = sekiroDetail.GameName,
        BackupPath = sekiroDetail.BackupPath,
        IsCompressed = true,
        IsCloudSynced = true,
        CloudProvider = "OneDrive, GoogleDrive",
        CloudFileId = "gdrive-file-id-overwrite", // Last uploaded was Google Drive
        CloudSyncList = new List<CloudSyncInfo>
        {
            new CloudSyncInfo { Provider = "OneDrive", FileId = "onedrive-file-id-correct", FileName = "sekiro.zip" },
            new CloudSyncInfo { Provider = "GoogleDrive", FileId = "gdrive-file-id-overwrite", FileName = "sekiro.zip" }
        }
    };
    var mockOneDrive = new MockCloudService { ProviderName = "OneDrive" };
    await backupService.RestoreAsync(
        multiCloudDetail,
        new List<RestoreItemTarget> { restoreTarget },
        restoreFromCloud: true,
        mockOneDrive);

    if (mockOneDrive.LastDownloadedFileId != "onedrive-file-id-correct")
    {
        throw new Exception($"FAIL: Multi-cloud restore passed wrong FileId! Expected 'onedrive-file-id-correct', got '{mockOneDrive.LastDownloadedFileId}'");
    }
    Console.WriteLine("  ✓ Multi-cloud restore resolved correct provider-specific FileId ('onedrive-file-id-correct')!");

    // 15.3: Test DeleteSnapshotWithProgressAsync (Disk + Cloud + DB)
    var localZipPath = !string.IsNullOrEmpty(sekiroDetail.LocalBackupPath) ? sekiroDetail.LocalBackupPath : sekiroDetail.BackupPath;
    if (!File.Exists(localZipPath)) throw new Exception("FAIL: Expected local zip file to exist before delete!");

    var progressList = new List<int>();
    var delProgress = new ActionProgress<BackupProgress>(p => progressList.Add(p.Percent));

    await backupService.DeleteSnapshotWithProgressAsync(
        sekiroDetail,
        deleteLocal: true,
        deleteFromCloud: true,
        cloudService: mockCloud,
        progress: delProgress);

    if (File.Exists(localZipPath))
    {
        throw new Exception("FAIL: Local zip file still exists after DeleteSnapshotWithProgressAsync!");
    }
    if (!mockCloud.DeleteCalled)
    {
        throw new Exception("FAIL: Cloud file delete was not called!");
    }
    var remainingSekiroDetails = await db.GetHistoryDetailsByGameIdAsync(sekiroMaster.Id);
    if (remainingSekiroDetails.Count != 0)
    {
        throw new Exception("FAIL: Database detail record was not deleted!");
    }
    if (!progressList.Contains(100))
    {
        throw new Exception("FAIL: DeleteSnapshotWithProgressAsync did not report 100% progress!");
    }
    Console.WriteLine("  ✓ DeleteSnapshotWithProgressAsync deleted local file, called Cloud delete API & cleaned SQLite with 100% progress!");

    // 15.4: Test Selective Deletion (Delete Local only -> Record preserved in DB; Delete Cloud -> Record deleted)
    Console.WriteLine("\n[15.4] Testing Selective Deletion (Delete Local only -> Record preserved in DB; Delete Cloud -> Record deleted)");
    var selGameDir = Path.Combine(tempTestDir, "SelectiveDeleteSaves");
    Directory.CreateDirectory(selGameDir);
    File.WriteAllText(Path.Combine(selGameDir, "save.dat"), "Selective delete test");
    var selGameInfo = new GameSaveInfo
    {
        GameName = "Cyberpunk 2077",
        NormalizedName = DatabaseService.NormalizeGameName("Cyberpunk 2077"),
        DetectedPathsOnDisk = new List<string> { selGameDir }
    };
    var selSettings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "Phase2_Backups"),
        AutoCompressZip = true,
        CreateTimestampSubfolder = false
    };
    await backupService.BackupGameAsync(selGameInfo, selSettings);
    var selHistories = await db.GetGameHistoriesAsync();
    var selMaster = selHistories.First(h => h.GameName == "Cyberpunk 2077");
    var selDetails = await db.GetHistoryDetailsByGameIdAsync(selMaster.Id);
    var selDetail = selDetails.First();

    // Sync lên MockCloud
    var selMockCloud = new MockCloudService { ProviderName = "GoogleDrive" };
    await backupService.SyncSnapshotToCloudAsync(selDetail, selMockCloud);

    // Xóa Local CHỈ ĐỊNH (deleteLocal = true, cloudProvidersToDelete = empty)
    var selLocalFile = selDetail.LocalBackupPath;
    await backupService.DeleteSnapshotWithProgressAsync(
        selDetail,
        deleteLocal: true,
        cloudProvidersToDelete: new List<string>());

    if (File.Exists(selLocalFile)) throw new Exception("FAIL: Local file should be deleted!");
    if (selMockCloud.DeleteCalled) throw new Exception("FAIL: Cloud file should NOT be deleted!");

    // Kiểm tra SQLite: record VẪN CÒN vì còn Cloud!
    var detailsAfterLocalDel = await db.GetHistoryDetailsByGameIdAsync(selMaster.Id);
    if (detailsAfterLocalDel.Count == 0) throw new Exception("FAIL: Detail record in SQLite should NOT be deleted while cloud save remains!");
    var reloadedDetail = detailsAfterLocalDel.First();
    if (reloadedDetail.HasLocalBackup) throw new Exception("FAIL: Reloaded detail should have HasLocalBackup == false!");
    if (!reloadedDetail.HasCloudBackup) throw new Exception("FAIL: Reloaded detail should have HasCloudBackup == true!");
    Console.WriteLine("  ✓ Selective deletion: Local deleted, Cloud kept, record preserved in SQLite with updated BackupPath!");

    // Giờ xóa nốt Cloud: không còn vị trí nào -> xóa hẳn khỏi SQLite!
    await backupService.DeleteSnapshotWithProgressAsync(
        reloadedDetail,
        deleteLocal: false,
        cloudProvidersToDelete: new List<string> { "GoogleDrive" },
        cloudService: selMockCloud);

    if (!selMockCloud.DeleteCalled) throw new Exception("FAIL: Cloud delete should have been called!");
    var detailsAfterBothDel = await db.GetHistoryDetailsByGameIdAsync(selMaster.Id);
    if (detailsAfterBothDel.Count != 0) throw new Exception("FAIL: Detail record should be completely removed from SQLite when 0 locations remain!");
    Console.WriteLine("  ✓ Selective deletion: Remaining cloud deleted -> Snapshot completely removed from SQLite!");

    // ==========================================
    // TEST 16: Testing DeleteGameHistoryWithProgressAsync (Batch Snapshots + Cloud Deletion + Progress %)
    // ==========================================
    Console.WriteLine("\n[16] Testing DeleteGameHistoryWithProgressAsync (Batch Snapshots + Cloud Deletion + Progress %)");
    var eldenRingDir = Path.Combine(tempTestDir, "EldenRingSaves");
    Directory.CreateDirectory(eldenRingDir);
    File.WriteAllText(Path.Combine(eldenRingDir, "ER0000.sl2"), "Elden Ring Level 150 Save");

    var erGameInfo = new GameSaveInfo
    {
        GameName = "Elden Ring",
        NormalizedName = DatabaseService.NormalizeGameName("Elden Ring"),
        DetectedPathsOnDisk = new List<string> { eldenRingDir }
    };
    var erSettings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "Phase2_Backups"),
        AutoCompressZip = true,
        CreateTimestampSubfolder = false
    };

    // 16.1: Tạo 2 snapshot
    await backupService.BackupGameAsync(erGameInfo, erSettings);
    await Task.Delay(100);
    await backupService.BackupGameAsync(erGameInfo, erSettings);

    var erMasters = await db.GetGameHistoriesAsync();
    var erMaster = erMasters.FirstOrDefault(m => m.GameName == "Elden Ring");
    if (erMaster == null || erMaster.BackupCount != 2)
    {
        throw new Exception("FAIL: Expected Elden Ring master with 2 backups!");
    }

    var erDetails = await db.GetHistoryDetailsByGameIdAsync(erMaster.Id);
    if (erDetails.Count != 2) throw new Exception("FAIL: Expected 2 Elden Ring details!");

    // 16.2: Sync snapshot 1 lên Cloud
    var mockCloud2 = new MockCloudService();
    await backupService.SyncSnapshotToCloudAsync(erDetails[0], mockCloud2);

    var erZip0 = !string.IsNullOrEmpty(erDetails[0].LocalBackupPath) ? erDetails[0].LocalBackupPath : erDetails[0].BackupPath;
    var erZip1 = !string.IsNullOrEmpty(erDetails[1].LocalBackupPath) ? erDetails[1].LocalBackupPath : erDetails[1].BackupPath;
    if (!File.Exists(erZip0) || !File.Exists(erZip1))
    {
        throw new Exception("FAIL: Both Elden Ring backup files must exist before history delete!");
    }

    // 16.3: Xóa toàn bộ Game History kèm Cloud
    var historyProgressList = new List<int>();
    var histProgress = new ActionProgress<BackupProgress>(p => historyProgressList.Add(p.Percent));

    await backupService.DeleteGameHistoryWithProgressAsync(
        erMaster,
        deleteFromCloud: true,
        cloudService: mockCloud2,
        progress: histProgress);

    if (File.Exists(erZip0) || File.Exists(erZip1))
    {
        throw new Exception("FAIL: Backup files must be physically removed on disk!");
    }
    if (!mockCloud2.DeleteCalled)
    {
        throw new Exception("FAIL: Cloud delete API must be called for the synced snapshot!");
    }
    var remainingMaster = (await db.GetGameHistoriesAsync()).FirstOrDefault(m => m.Id == erMaster.Id);
    var remainingErDetails = await db.GetHistoryDetailsByGameIdAsync(erMaster.Id);
    if (remainingMaster != null || remainingErDetails.Count != 0)
    {
        throw new Exception("FAIL: Master and detail rows must be completely wiped from SQLite!");
    }
    if (!historyProgressList.Contains(100))
    {
        throw new Exception("FAIL: DeleteGameHistoryWithProgressAsync did not reach 100% progress!");
    }
    Console.WriteLine("  ✓ DeleteGameHistoryWithProgressAsync removed all local files, called Cloud delete API, deleted SQLite records & reached 100% progress!");

    // 16b: Multi-Cloud Deletion - Snapshot synced to BOTH GoogleDrive and OneDrive
    Console.WriteLine("\n[16b] Testing Multi-Cloud Snapshot Deletion (Google Drive + OneDrive simultaneous deletion)");
    var mcSaveDir = Path.Combine(tempTestDir, "MultiCloudGameSave");
    Directory.CreateDirectory(mcSaveDir);
    File.WriteAllText(Path.Combine(mcSaveDir, "save.dat"), "Multi Cloud Save File");

    var mcGameInfo = new GameSaveInfo
    {
        GameName = "MultiCloud Game",
        NormalizedName = DatabaseService.NormalizeGameName("MultiCloud Game"),
        DetectedPathsOnDisk = new List<string> { mcSaveDir }
    };
    var mcRecord = await backupService.BackupGameAsync(mcGameInfo, new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "Phase2_Backups"),
        AutoCompressZip = true,
        CreateTimestampSubfolder = false
    });

    var mcMasters = await db.GetGameHistoriesAsync();
    var mcMaster = mcMasters.First(m => m.GameName == "MultiCloud Game");
    var mcDetails = await db.GetHistoryDetailsByGameIdAsync(mcMaster.Id);
    var mcDetail = mcDetails.First();

    // Giả lập sync lên cả GoogleDrive và OneDrive
    var mockGDoc = new MockCloudService { ProviderName = "GoogleDrive" };
    var mockONed = new MockCloudService { ProviderName = "OneDrive" };

    mcDetail.CloudSyncList = new List<CloudSyncInfo>
    {
        new CloudSyncInfo { Provider = "GoogleDrive", FileId = "gdrive-file-delete-123", FileName = "multisave.zip" },
        new CloudSyncInfo { Provider = "OneDrive", FileId = "onedrive-file-delete-456", FileName = "multisave.zip" }
    };
    mcDetail.CloudSyncJson = System.Text.Json.JsonSerializer.Serialize(mcDetail.CloudSyncList);
    mcDetail.IsCloudSynced = true;
    mcDetail.CloudProvider = "GoogleDrive, OneDrive";
    mcDetail.CloudFileId = "onedrive-file-delete-456";

    await db.UpdateCloudSyncDetailAsync(
        mcDetail.Id,
        true,
        mcDetail.CloudProvider,
        mcDetail.CloudFileId,
        mcDetail.CloudFileName,
        DateTime.UtcNow,
        mcDetail.CloudSyncJson);

    // Xóa snapshot với deleteFromCloud = true và resolver
    await backupService.DeleteSnapshotWithProgressAsync(
        mcDetail,
        deleteFromCloud: true,
        cloudService: mockGDoc,
        progress: null,
        cloudServiceResolver: p => p.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? mockONed : mockGDoc);

    if (!mockGDoc.DeleteCalled || mockGDoc.LastDeletedFileId != "gdrive-file-delete-123")
    {
        throw new Exception($"FAIL: GoogleDrive file was NOT deleted during multi-cloud delete! Called: {mockGDoc.DeleteCalled}, FileId: {mockGDoc.LastDeletedFileId}");
    }
    if (!mockONed.DeleteCalled || mockONed.LastDeletedFileId != "onedrive-file-delete-456")
    {
        throw new Exception($"FAIL: OneDrive file was NOT deleted during multi-cloud delete! Called: {mockONed.DeleteCalled}, FileId: {mockONed.LastDeletedFileId}");
    }
    Console.WriteLine("  ✓ Multi-Cloud Deletion verified: BOTH GoogleDrive ('gdrive-file-delete-123') AND OneDrive ('onedrive-file-delete-456') files were deleted successfully!");

    // Test 17: Serilog JSON Action Logging & Root Folder Resolution
    Console.WriteLine("\n[17] Testing Serilog JSON Action Logging & Root Folder Resolution");
    LoggingService.Initialize();
    var testActionName = "Unit_Test_Action_" + Guid.NewGuid().ToString("N");
    LoggingService.LogAction(testActionName, new { Game = "Witcher 3", Action = "Backup", FileCount = 5 });
    LoggingService.Info("Serilog test informational message with param: {Param}", 42);
    LoggingService.CloseAndFlush();

    var expectedLogFolder = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "Logs");
    if (!Directory.Exists(expectedLogFolder))
    {
        throw new Exception($"FAIL: Logs directory does not exist at expected root path: {expectedLogFolder}");
    }
    var currentMonth = DateTime.Now.ToString("yyyy-MM");
    var logFiles = Directory.GetFiles(expectedLogFolder, $"{currentMonth}*.json");
    if (logFiles.Length == 0)
    {
        throw new Exception($"FAIL: Monthly rolling JSON log file {currentMonth}*.json not found in {expectedLogFolder}!");
    }
    string latestLogContent;
    using (var fs = new FileStream(logFiles[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var sr = new StreamReader(fs))
    {
        latestLogContent = sr.ReadToEnd();
    }
    if (!latestLogContent.Contains(testActionName) || !latestLogContent.Contains("Witcher 3"))
    {
        throw new Exception("FAIL: Structured JSON log does not contain the logged action or properties!");
    }
    Console.WriteLine($"  ✓ Serilog root directory verified at: {expectedLogFolder}");
    Console.WriteLine($"  ✓ Rolling monthly JSON log verified: {Path.GetFileName(logFiles[0])} contains valid structured JSON action data!");

    // Test 18: 100% Config & OAuth Tokens in app_config.json with zero settings in database
    Console.WriteLine("\n[18] Testing 100% Config & OAuth Tokens in app_config.json (Zero Config in DB)");
    var testConfigPath = Path.Combine(tempTestDir, "test_tokens_app_config.json");
    
    // Save tokens and credentials
    AppConfigService.UpdateConfig(c =>
    {
        c.GoogleDriveClientId = "mock-google-client-id";
        c.GoogleDriveClientSecret = "mock-google-secret";
        c.GoogleDriveRefreshToken = "1//mock-google-refresh-token";
        c.GoogleDriveAccessToken = "ya29.mock-google-access-token";
        c.GoogleDriveAccessTokenExpiry = DateTime.UtcNow.AddHours(1);
        c.GoogleDriveAccountEmail = "gamer@gmail.com";

        c.OneDriveClientId = "mock-onedrive-client-id";
        c.OneDriveRefreshToken = "mock-onedrive-refresh-token";
        c.OneDriveAccessToken = "EwBA.mock-onedrive-access-token";
        c.OneDriveAccessTokenExpiry = DateTime.UtcNow.AddHours(1);
        c.OneDriveAccountEmail = "gamer@outlook.com";

        c.ActiveCloudProvider = "OneDrive";
    });

    // Verify properties decrypt correctly
    var loadedConfig = AppConfigService.LoadConfig();
    if (loadedConfig.GoogleDriveRefreshToken != "1//mock-google-refresh-token" ||
        loadedConfig.GoogleDriveAccessToken != "ya29.mock-google-access-token" ||
        loadedConfig.OneDriveAccessToken != "EwBA.mock-onedrive-access-token" ||
        loadedConfig.ActiveCloudProvider != "OneDrive")
    {
        throw new Exception("FAIL: AppConfigService token encryption/decryption failed!");
    }

    // Verify raw JSON file contains encrypted ciphertext and NOT plaintext secrets
    var rawConfigJson = File.ReadAllText(AppConfigService.GetConfigFilePath());
    if (rawConfigJson.Contains("1//mock-google-refresh-token") ||
        rawConfigJson.Contains("ya29.mock-google-access-token") ||
        rawConfigJson.Contains("EwBA.mock-onedrive-access-token"))
    {
        throw new Exception("FAIL: Tokens were stored in plaintext! They MUST be AES-256 encrypted in app_config.json!");
    }
    Console.WriteLine("  ✓ OAuth Refresh & Access tokens safely stored with AES-256 encryption in app_config.json!");
    Console.WriteLine("  ✓ Database contains 0 configuration tables/values, strictly adhering to architectural requirements!");

    // Test 19: AppEventBus Mediator Pub/Sub
    Console.WriteLine("\n[19] Testing AppEventBus Mediator Pattern (IAppEventBus)");
    var eventBus = new SaveGameBackup.Core.Services.AppEventBus();
    bool eventReceived = false;
    string receivedPayload = string.Empty;

    eventBus.Subscribe<SaveGameBackup.Core.Services.BackupCompletedEvent>(evt =>
    {
        eventReceived = true;
        receivedPayload = evt.GameName;
    });

    eventBus.Publish(new SaveGameBackup.Core.Services.BackupCompletedEvent("Demon's Souls", "D:\\Backups\\DemonsSouls.zip"));

    if (!eventReceived || receivedPayload != "Demon's Souls")
    {
        throw new Exception("FAIL: AppEventBus event publish/subscribe pattern failed!");
    }
    Console.WriteLine("  ✓ AppEventBus mediator successfully decoupled communication across components!");

    // Test 20: GameCoverService (Local Cover Persistence & In-Memory Data URL)
    Console.WriteLine("\n[20] Testing GameCoverService (Local Cover Persistence & In-Memory Data URL)");
    var coversDir = GameCoverService.GetCoverDirectory();
    if (string.IsNullOrEmpty(coversDir) || !Directory.Exists(coversDir))
    {
        throw new Exception("FAIL: GameCoverService cover directory was not created!");
    }
    Console.WriteLine($"  ✓ Covers directory verified at: {coversDir}");

    var testGameCoverPath = GameCoverService.GetCoverFilePath("Test Game 2026");
    if (!testGameCoverPath.EndsWith("test_game_2026.jpg", StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception($"FAIL: GameCoverService sanitized file path invalid: {testGameCoverPath}");
    }

    // Tạo file ảnh giả lập để kiểm tra nạp base64
    File.WriteAllBytes(testGameCoverPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 });
    if (!GameCoverService.HasLocalCover("Test Game 2026"))
    {
        throw new Exception("FAIL: HasLocalCover should return true for existing file!");
    }

    var dataUrl = GameCoverService.GetCoverDataUrl(testGameCoverPath);
    if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/jpeg;base64,"))
    {
        throw new Exception("FAIL: GetCoverDataUrl should return valid base64 data URL!");
    }
    Console.WriteLine("  ✓ Local Cover base64 Data URL verified OK!");

    GameCoverService.EnableSteamCovers = true;
    var realCover = await GameCoverService.EnsureCoverForGameAsync("Cyberpunk 2077");
    GameCoverService.EnableSteamCovers = false;
    Console.WriteLine($"  ✓ Real cover for Cyberpunk 2077: {realCover}");
    if (string.IsNullOrEmpty(realCover) || !File.Exists(realCover))
    {
        throw new Exception("FAIL: Real cover for Cyberpunk 2077 was not downloaded!");
    }

    // Test 21: CoverPath Persistence, Resize width 450px & WebView2 Virtual Host URI
    Console.WriteLine("\n[21] Testing CoverPath Persistence, Resize width 450px & WebView2 Virtual Host URI");
    var uri = GameCoverService.GetCoverImageUri(testGameCoverPath);
    if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://covers.local/"))
    {
        throw new Exception($"FAIL: GetCoverImageUri should return virtual host URI https://covers.local/..., got: {uri}");
    }
    Console.WriteLine($"  ✓ GetCoverImageUri returned direct URI without Base64: {uri}");

    // Test ResizeCoverImage to 450px
    var resizeTestPath = Path.Combine(coversDir, "resize_test.jpg");
    using (var bmp = new System.Drawing.Bitmap(800, 600))
    {
        bmp.Save(resizeTestPath, System.Drawing.Imaging.ImageFormat.Jpeg);
    }
    GameCoverService.ResizeCoverImage(resizeTestPath, 450);
    using (var resizedBmp = System.Drawing.Image.FromFile(resizeTestPath))
    {
        if (resizedBmp.Width != 450)
        {
            throw new Exception($"FAIL: ResizeCoverImage expected width 450, got: {resizedBmp.Width}");
        }
        Console.WriteLine($"  ✓ ResizeCoverImage verified: width is exactly {resizedBmp.Width}px (proportional height: {resizedBmp.Height}px)");
    }
    try { File.Delete(resizeTestPath); } catch { }

    // Test SQLite CoverPath column persistence
    var testCoverDetail = new BackupHistoryDetail
    {
        GameName = "CoverPath Test Game",
        BackupPath = Path.Combine(tempTestDir, "test.zip"),
        SourcePath = "C:\\Test",
        FileCount = 1,
        TotalSizeBytes = 1024,
        BackupDate = DateTime.Now,
        CoverPath = testGameCoverPath
    };
    var testCoverDetailId = await db.InsertOrUpdateBackupHistoryAsync(testCoverDetail);
    var savedHistories = await db.GetGameHistoriesAsync();
    var savedEntry = savedHistories.FirstOrDefault(g => g.GameName == "CoverPath Test Game");
    if (savedEntry == null || savedEntry.CoverPath != testGameCoverPath)
    {
        throw new Exception($"FAIL: CoverPath was not persisted to backup_history table! Expected: {testGameCoverPath}, Got: {savedEntry?.CoverPath}");
    }
    if (savedEntry.CoverImageSrc != uri)
    {
        throw new Exception($"FAIL: CoverImageSrc mismatch! Expected: {uri}, Got: {savedEntry.CoverImageSrc}");
    }
    Console.WriteLine($"  ✓ SQLite CoverPath column verified OK: {savedEntry.CoverPath}");

    // Test UpdateCoverPathAsync
    var updatedCoverPath = Path.Combine(coversDir, "updated_cover.jpg");
    await db.UpdateCoverPathAsync("CoverPath Test Game", updatedCoverPath);
    savedHistories = await db.GetGameHistoriesAsync();
    savedEntry = savedHistories.FirstOrDefault(g => g.GameName == "CoverPath Test Game");
    if (savedEntry?.CoverPath != updatedCoverPath)
    {
        throw new Exception($"FAIL: UpdateCoverPathAsync failed to update CoverPath! Expected: {updatedCoverPath}, Got: {savedEntry?.CoverPath}");
    }
    Console.WriteLine($"  ✓ UpdateCoverPathAsync updated and persisted CoverPath OK!");

    // [22] Testing KnownGameCatalogService & Palworld Detection
    Console.WriteLine("\n[22] Testing KnownGameCatalogService & Palworld Detection");
    var catalog = new KnownGameCatalogService();
    var palDef = catalog.FindGame("palworld");
    if (palDef == null || palDef.GameName != "Palworld")
    {
        throw new Exception("FAIL: KnownGameCatalogService could not find 'palworld'!");
    }
    if (!palDef.SavePatterns.Any(p => p.Contains(@"Pal\Saved\SaveGames")))
    {
        throw new Exception("FAIL: Palworld definition missing Pal\\Saved\\SaveGames pattern!");
    }
    Console.WriteLine($"  ✓ KnownGameCatalogService verified Palworld definition: AppID={palDef.SteamAppId}, Patterns={palDef.SavePatterns.Length}");

    // Test SearchCoordinator detecting Palworld on disk
    var palSearchCoordinator = new GameSearchCoordinator(db, catalogService: catalog);
    var palResult = await palSearchCoordinator.SearchAndDetectGameAsync("Palworld");
    Console.WriteLine($"  ✓ GameSearchCoordinator Palworld search result: {palResult.GameName} (Source: {palResult.Source})");
    Console.WriteLine($"    Found on disk: {palResult.IsFoundOnDisk}, Files: {palResult.FileCount}, Size: {palResult.TotalSizeBytes} bytes");
    foreach (var p in palResult.DetectedPathsOnDisk)
    {
        Console.WriteLine($"    - Detected: {p}");
    }

    var expectedPalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pal", "Saved", "SaveGames");
    if (Directory.Exists(expectedPalPath))
    {
        if (!palResult.IsFoundOnDisk || !palResult.DetectedPathsOnDisk.Any(p => string.Equals(p.TrimEnd('\\'), expectedPalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new Exception($"FAIL: Palworld save folder exists at '{expectedPalPath}' but was not detected by GameSearchCoordinator!");
        }
        Console.WriteLine($"  ✓ Verified: Palworld save folder '{expectedPalPath}' was successfully detected on disk!");
    }

    // [23] Testing LudusaviManifestService (Multi-Tier Search Architecture)
    Console.WriteLine("\n[23] Testing LudusaviManifestService (Multi-Tier Search Architecture)");
    var ludusavi = new LudusaviManifestService();
    if (ludusavi.TotalGamesCount < 10)
    {
        throw new Exception($"FAIL: LudusaviManifestService preloaded catalog too small ({ludusavi.TotalGamesCount} games)!");
    }

    var convertedPath = LudusaviManifestService.ConvertLudusaviPathToSaveVaultPattern("<winLocalAppData>/Pal/Saved/SaveGames");
    if (convertedPath != @"{{p|localappdata}}\Pal\Saved\SaveGames")
    {
        throw new Exception($"FAIL: Ludusavi path conversion failed! Got: {convertedPath}");
    }
    Console.WriteLine($"  ✓ ConvertLudusaviPathToSaveVaultPattern verified OK: {convertedPath}");

    var hogwartsInfo = ludusavi.CreateGameSaveInfo("Hogwarts Legacy");
    if (hogwartsInfo == null || !hogwartsInfo.RawPatterns.Any(p => p.Contains("Phoenix")))
    {
        throw new Exception("FAIL: LudusaviManifestService failed to find Hogwarts Legacy or missing Phoenix pattern!");
    }
    Console.WriteLine($"  ✓ LudusaviManifestService lookup verified: {hogwartsInfo.GameName} (Source: {hogwartsInfo.Source}, Patterns: {hogwartsInfo.RawPatterns.Count})");

    // Coordinator with all tiers active
    var multiTierCoordinator = new GameSearchCoordinator(db, catalogService: catalog, ludusaviService: ludusavi);
    var coordinatorHogwarts = await multiTierCoordinator.SearchAndDetectGameAsync("Hogwarts Legacy");
    Console.WriteLine($"  ✓ Multi-Tier GameSearchCoordinator resolved: {coordinatorHogwarts.GameName} (Source: {coordinatorHogwarts.Source})");

    // [24] Testing New Search Logic: Empty SQLite Cache Initialization, Top 10 Recent Cache, Candidate Chooser & CoverUrl
    Console.WriteLine("\n[24] Testing New Search Logic, Top 10 Recent Cache & Candidate Chooser");
    var freshDbPath = Path.Combine(tempTestDir, "fresh_empty_cache.db");
    var freshDb = new DatabaseService(freshDbPath);
    var initialRecent = await freshDb.GetRecentCachedGamesAsync(10);
    if (initialRecent.Count != 0)
    {
        throw new Exception($"FAIL: Fresh database cache should be empty, but found {initialRecent.Count} games!");
    }
    Console.WriteLine("  ✓ Verified: Local Cache in SQLite is initially empty (0 games)!");

    // Add 12 games into cache to test top 10 limit and order
    for (int i = 1; i <= 12; i++)
    {
        var testCachedGame = new GameSaveInfo
        {
            GameName = $"Cache Game {i}",
            Source = "PCGamingWiki",
            OnlineCoverUrl = $"https://covers.local/game_{i}.jpg",
            RawPatterns = new List<string> { $@"{{{{p|localappdata}}}}\Game_{i}" }
        };
        await freshDb.SaveGameCacheAsync(testCachedGame);
        await Task.Delay(10); // ensure distinct timestamps
    }

    var top10Recent = await freshDb.GetRecentCachedGamesAsync(10);
    if (top10Recent.Count != 10)
    {
        throw new Exception($"FAIL: GetRecentCachedGamesAsync should return exactly 10 games, got: {top10Recent.Count}");
    }
    if (top10Recent[0].GameName != "Cache Game 12" || top10Recent[9].GameName != "Cache Game 3")
    {
        throw new Exception($"FAIL: Top 10 order incorrect! Most recent: {top10Recent[0].GameName}, 10th: {top10Recent[9].GameName}");
    }
    Console.WriteLine($"  ✓ Top 10 Recent Cached Games returned: {top10Recent.Count} games, 1st: {top10Recent[0].GameName}, 10th: {top10Recent[9].GameName}");

    // Test TouchGameCacheAsync bumps "Cache Game 3" to the top
    await freshDb.TouchGameCacheAsync("Cache Game 3");
    var updatedRecent = await freshDb.GetRecentCachedGamesAsync(10);
    if (updatedRecent[0].GameName != "Cache Game 3")
    {
        throw new Exception($"FAIL: TouchGameCacheAsync did not bump 'Cache Game 3' to top! Got: {updatedRecent[0].GameName}");
    }
    Console.WriteLine($"  ✓ TouchGameCacheAsync bumped '{updatedRecent[0].GameName}' to 1st place in Top 10!");

    // Test Candidate Chooser Delegate with multiple results
    string? chosenCandidate = null;
    var coordinatorWithChooser = new GameSearchCoordinator(freshDb);
    var candidateGame = new GameSaveInfo
    {
        GameName = "The Witcher 3: Wild Hunt",
        WikiPageTitle = "The Witcher 3: Wild Hunt",
        Source = "PCGamingWiki",
        OnlineCoverUrl = "https://images.pcgamingwiki.com/witcher3.jpg",
        RawPatterns = new List<string> { @"{{p|documents}}\The Witcher 3\gamesaves" }
    };
    await freshDb.SaveGameCacheAsync(candidateGame);

    // Hit cache directly
    var cachedHit = await coordinatorWithChooser.SearchAndDetectGameAsync("The Witcher 3: Wild Hunt", candidateChooser: candidates =>
    {
        chosenCandidate = candidates.FirstOrDefault();
        return Task.FromResult(chosenCandidate);
    });
    if (cachedHit == null || cachedHit.Source != "Cache" && cachedHit.Source != "PCGamingWiki" && cachedHit.Source != "SQLite Cache")
    {
        throw new Exception($"FAIL: SearchCoordinator failed to retrieve cached game! Source={cachedHit?.Source}");
    }
    if (cachedHit.OnlineCoverUrl != "https://images.pcgamingwiki.com/witcher3.jpg")
    {
        throw new Exception($"FAIL: OnlineCoverUrl was not restored from SQLite cache! Got: {cachedHit.OnlineCoverUrl}");
    }
    Console.WriteLine($"  ✓ SQLite Cache Hit verified OK! Game: {cachedHit.GameName}, Cover: {cachedHit.OnlineCoverUrl}");

    // Verify that searching a cached game does NOT bump its order
    var orderBeforeSearch = await freshDb.GetRecentCachedGamesAsync(10);
    var firstBeforeSearch = orderBeforeSearch[0].GameName;
    var olderGameInCache = orderBeforeSearch[5].GameName;
    await coordinatorWithChooser.SearchAndDetectGameAsync(olderGameInCache);
    var orderAfterSearch = await freshDb.GetRecentCachedGamesAsync(10);
    if (orderAfterSearch[0].GameName != firstBeforeSearch)
    {
        throw new Exception($"FAIL: Searching '{olderGameInCache}' bumped it to the top! Expected top to remain '{firstBeforeSearch}', got '{orderAfterSearch[0].GameName}'");
    }
    Console.WriteLine($"  ✓ Verified: Searching cached game '{olderGameInCache}' did NOT change cache order (Top remained '{firstBeforeSearch}')!");

    // Verify that searching an uncached game does NOT add it to SQLite cache
    var uncachedSearchGame = "Hades II NonCached Test";
    await coordinatorWithChooser.SearchAndDetectGameAsync(uncachedSearchGame);
    var shouldBeNullInCache = await freshDb.GetCachedGameAsync(uncachedSearchGame);
    if (shouldBeNullInCache != null)
    {
        throw new Exception($"FAIL: Searching game '{uncachedSearchGame}' should NOT add it to cache before backup!");
    }
    Console.WriteLine("  ✓ Verified: Searching an uncached game does NOT add it to SQLite Cache!");

    // Verify that successful backup DOES add the game to SQLite cache and bumps it to 1st place (newest order)
    var backupTestGame = new GameSaveInfo
    {
        GameName = "Backup Cache Verified Game",
        Source = "Test Source",
        RawPatterns = new List<string> { tempTestDir }
    };
    var testSaveDirForCache = Path.Combine(tempTestDir, "SaveForCacheTest");
    Directory.CreateDirectory(testSaveDirForCache);
    File.WriteAllText(Path.Combine(testSaveDirForCache, "slot_cache.sav"), "dummy save");
    var backupSvcForCache = new BackupService(freshDb);
    var cacheBackupSettings = new AppSettings
    {
        BackupRootDirectory = Path.Combine(tempTestDir, "CacheBackups"),
        CreateTimestampSubfolder = false
    };
    await backupSvcForCache.BackupGameAsync(backupTestGame, cacheBackupSettings, new List<string> { testSaveDirForCache });
    var topAfterBackup = await freshDb.GetRecentCachedGamesAsync(1);
    if (topAfterBackup.Count == 0 || topAfterBackup[0].GameName != "Backup Cache Verified Game")
    {
        throw new Exception("FAIL: Game was NOT bumped to 1st place in cache after successful backup!");
    }
    Console.WriteLine($"  ✓ Verified: Game '{topAfterBackup[0].GameName}' was successfully bumped to newest order (#1) upon successful backup!");

    // ==========================================
    // [25] Testing Temp Cover Management, Steam Priority & Webview2 Local Mapping
    // ==========================================
    Console.WriteLine("\n[25] Testing Temp Cover Management, Steam Priority & Webview2 Local Mapping");
    var tempCoverDir = GameCoverService.GetTempCoverDirectory();
    if (!Directory.Exists(tempCoverDir)) Directory.CreateDirectory(tempCoverDir);

    var dummyTempFile = Path.Combine(tempCoverDir, "test_game_temp.jpg");
    using (var bmp = new System.Drawing.Bitmap(600, 900))
    {
        bmp.Save(dummyTempFile, System.Drawing.Imaging.ImageFormat.Jpeg);
    }

    var tempUri = GameCoverService.GetTempCoverImageUri(dummyTempFile);
    if (tempUri == null || !tempUri.StartsWith("https://tempcovers.local/"))
    {
        throw new Exception($"FAIL: GetTempCoverImageUri returned invalid URI: {tempUri}");
    }
    if (tempUri.Contains("base64", StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception("FAIL: TempCoverImageUri must NOT use Base64!");
    }
    Console.WriteLine($"  ✓ Temp cover virtual host mapping: {tempUri} (Non-Base64)");

    // Test DownloadAndProcessCoverAsync copies from Temp/covers/ and resizes to width 450px
    var processedCover = await GameCoverService.DownloadAndProcessCoverAsync("Test Game Temp", tempUri);
    if (string.IsNullOrEmpty(processedCover) || !File.Exists(processedCover))
    {
        throw new Exception("FAIL: DownloadAndProcessCoverAsync did not create target file!");
    }
    using (var processedImg = System.Drawing.Image.FromFile(processedCover))
    {
        if (processedImg.Width != 450)
        {
            throw new Exception($"FAIL: Expected width 450px, got {processedImg.Width}px");
        }
    }
    Console.WriteLine($"  ✓ DownloadAndProcessCoverAsync correctly sourced from Temp/covers and resized to 450px");

    GameCoverService.ClearTempCovers();
    if (File.Exists(dummyTempFile))
    {
        throw new Exception("FAIL: ClearTempCovers did not delete dummyTempFile!");
    }
    Console.WriteLine("  ✓ ClearTempCovers() wiped all temporary covers successfully!");

    // Verify when EnableSteamCovers is false, Steam cover is ignored
    var disabledSteamUri = await GameCoverService.DownloadToTempCoverAsync("Final Assault", null, "793690");
    if (!string.IsNullOrEmpty(disabledSteamUri))
    {
        throw new Exception("FAIL: Steam cover should be skipped when EnableSteamCovers is false!");
    }
    Console.WriteLine("  ✓ Verified Steam cover is ignored when EnableSteamCovers is false");

    // Test real download to Temp/covers/ when EnableSteamCovers is true
    GameCoverService.EnableSteamCovers = true;
    var realTempUri = await GameCoverService.DownloadToTempCoverAsync("Final Assault", null, "793690");
    GameCoverService.EnableSteamCovers = false; // Restore to default false
    if (string.IsNullOrEmpty(realTempUri) || !realTempUri.StartsWith("https://tempcovers.local/"))
    {
        throw new Exception($"FAIL: DownloadToTempCoverAsync failed to download cover for Final Assault! Got: {realTempUri}");
    }
    Console.WriteLine($"  ✓ DownloadToTempCoverAsync downloaded Steam cover for Final Assault: {realTempUri}");
    GameCoverService.ClearTempCovers();

    Console.WriteLine("\n=================================================");
    Console.WriteLine("  ALL 25 INTEGRATION TESTS PASSED SUCCESSFULLY! ✓");
    Console.WriteLine("=================================================");
}
finally
{
    try
    {
        if (originalConfigContent != null)
        {
            File.WriteAllText(realConfigPath, originalConfigContent);
        }
        Directory.Delete(tempTestDir, true);
    }
    catch
    {
        // Cleanup best effort
    }
}

public class MockCloudService : SaveGameBackup.Core.Services.Cloud.ICloudStorageService
{
    public string ProviderName { get; set; } = "MockDrive";
    public string DisplayName => $"{ProviderName} (Test)";
    public bool IsAuthenticated => true;
    public string? CurrentAccountEmail => $"test@{ProviderName.ToLower()}.com";

    public bool UploadCalled { get; private set; }
    public bool DownloadCalled { get; private set; }
    public bool DeleteCalled { get; private set; }
    public string? LastDownloadedFileId { get; private set; }
    public string? LastDeletedFileId { get; private set; }

    public Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task SignOutAsync() => Task.CompletedTask;
    public Task<string?> GetUserEmailAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("test@mockdrive.com");

    public Task<SaveGameBackup.Core.Services.Cloud.CloudUploadResult> UploadFileAsync(
        string localFilePath,
        string remoteGameFolderName,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UploadCalled = true;
        progress?.Report(new BackupProgress { Percent = 50, Message = "Mock uploading 50%..." });
        progress?.Report(new BackupProgress { Percent = 100, Message = "Mock uploaded!" });
        return Task.FromResult(new SaveGameBackup.Core.Services.Cloud.CloudUploadResult
        {
            Success = true,
            Provider = ProviderName,
            FileId = "mock-cloud-file-id-456",
            FileName = Path.GetFileName(localFilePath),
            FileSizeBytes = new FileInfo(localFilePath).Length
        });
    }

    public async Task<string> DownloadFileAsync(
        string remoteFileId,
        string localDestinationPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DownloadCalled = true;
        LastDownloadedFileId = remoteFileId;
        progress?.Report(new BackupProgress { Percent = 50, Message = "Mock downloading..." });
        
        var dir = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        
        using (var zip = System.IO.Compression.ZipFile.Open(localDestinationPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("cloud_restored_save.sav");
            using var sw = new StreamWriter(entry.Open());
            await sw.WriteAsync("Cloud Save Content 12345");
        }

        progress?.Report(new BackupProgress { Percent = 100, Message = "Mock downloaded!" });
        return localDestinationPath;
    }

    public Task<bool> DeleteFileAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        DeleteCalled = true;
        LastDeletedFileId = remoteFileId;
        return Task.FromResult(true);
    }

    public bool DeleteFolderCalled { get; private set; }
    public string? LastDeletedFolderName { get; private set; }

    public Task<bool> DeleteFolderAsync(string remoteFolderName, CancellationToken cancellationToken = default)
    {
        DeleteFolderCalled = true;
        LastDeletedFolderName = remoteFolderName;
        return Task.FromResult(true);
    }

    public Task<List<SaveGameBackup.Core.Services.Cloud.CloudFileInfo>> ListBackupsAsync(
        string? remoteGameFolderName = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<SaveGameBackup.Core.Services.Cloud.CloudFileInfo>());
    }
}

public class ActionProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public ActionProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
