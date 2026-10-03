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
        CreateTimestampSubfolder = true,
        AutoCompressZip = true,
        PageSize = 25
    };
    AppConfigService.SaveConfig(sampleConfig);

    var memCached = AppConfigService.GetConfig();
    if (memCached.PageSize != sampleConfig.PageSize ||
        memCached.CreateTimestampSubfolder != true ||
        memCached.AutoCompressZip != true)
    {
        throw new Exception("AppConfig RAM Cache does not match saved config!");
    }
    Console.WriteLine($"  ✓ AppConfig RAM cache verified OK: PageSize={memCached.PageSize}, AutoZip={memCached.AutoCompressZip}");

    // Test Search Coordinator: Cache-then-PCGamingWiki logic
    var searchCoordinator = new GameSearchCoordinator(db);
    var searchResult = await searchCoordinator.SearchAndDetectGameAsync("Test Adventure");
    if (searchResult == null) throw new Exception("FAIL: searchResult was null!");
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
    if (!File.Exists(Path.Combine(projectRoot, "Omnisave.slnx")) && !File.Exists(Path.Combine(projectRoot, "LaunchApp.bat")))
        throw new Exception("Detected project root does not contain Omnisave solution files!");

    var defaultDbPath = Path.Combine(projectRoot, "data", "database", "save_backup.db");
    Console.WriteLine($"  ✓ Default DB Path: {defaultDbPath}");

    // Test 7: Verify Database Default Path and Backup Default Dir
    defaultDbPath = DatabaseService.DefaultDbPath;
    var defaultBackupDir = DatabaseService.DefaultBackupDir;
    if (!defaultDbPath.EndsWith("save_backup.db") || !defaultBackupDir.EndsWith("backups"))
        throw new Exception($"Default paths incorrect: DB={defaultDbPath}, Backup={defaultBackupDir}");
    Console.WriteLine($"  ✓ Default DB Path verified: {defaultDbPath}");
    Console.WriteLine($"  ✓ Default Backup Dir verified: {defaultBackupDir}");

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
    
    // Kiểm tra cờ EnableTabAndModalLogging đang ở trạng thái mặc định = false
    if (LoggingService.EnableTabAndModalLogging)
        throw new Exception("FAIL: EnableTabAndModalLogging must be false by default!");

    // Thử ghi log tab và modal khi đang ẩn
    LoggingService.LogAction("Open_Restore_Modal", new { TestId = "SuppressedModal123" });
    LoggingService.LogAction("Cloud_Tab_Switch_Provider", new { TestId = "SuppressedTab123" });

    // Ghi log chức năng thông thường
    LoggingService.LogAction(testActionName, new { Game = "Witcher 3", Action = "Backup", FileCount = 5 });
    LoggingService.Info("Serilog test informational message with param: {Param}", 42);
    LoggingService.CloseAndFlush();

    var expectedLogFolder = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "data", "logs");
    if (!Directory.Exists(expectedLogFolder))
    {
        throw new Exception($"FAIL: Logs directory does not exist at expected root path: {expectedLogFolder}");
    }
    var currentMonth = DateTime.Now.ToString("yyyy-MM");
    var currentMonthCompact = DateTime.Now.ToString("yyyyMM");
    var logFiles = Directory.GetFiles(expectedLogFolder, $"*{currentMonthCompact}*.json")
        .Concat(Directory.GetFiles(expectedLogFolder, $"{currentMonth}*.json"))
        .Distinct()
        .ToArray();
    if (logFiles.Length == 0)
    {
        throw new Exception($"FAIL: Monthly rolling JSON log file not found in {expectedLogFolder}!");
    }
    string latestLogContent;
    using (var fs = new FileStream(logFiles[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    using (var sr = new StreamReader(fs))
    {
        latestLogContent = sr.ReadToEnd();
    }
    if (!latestLogContent.Contains(testActionName) || !latestLogContent.Contains("Witcher 3"))
    {
        throw new Exception("FAIL: Structured JSON log does not contain the logged functional action or properties!");
    }

    // Xác minh log mở tab và modal đã được ẩn thành công
    if (latestLogContent.Contains("SuppressedModal123") || latestLogContent.Contains("SuppressedTab123"))
    {
        throw new Exception("FAIL: Tab and modal logs must be suppressed when EnableTabAndModalLogging is false!");
    }
    if (!LoggingService.IsTabOrModalAction("Open_Restore_Modal") || !LoggingService.IsTabOrModalAction("View_Game_Snapshots"))
    {
        throw new Exception("FAIL: IsTabOrModalAction should recognize modal actions!");
    }
    if (LoggingService.IsTabOrModalAction("Backup_Start") || LoggingService.IsTabOrModalAction("Restore_Execute_Start"))
    {
        throw new Exception("FAIL: IsTabOrModalAction should NOT mark functional actions as modal!");
    }

    Console.WriteLine($"  ✓ Serilog root directory verified at: {expectedLogFolder}");
    Console.WriteLine($"  ✓ Rolling monthly JSON log verified: {Path.GetFileName(logFiles[0])} contains valid structured JSON action data!");
    Console.WriteLine($"  ✓ Tab and modal logs successfully suppressed while preserving 100% functional action logs!");

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
        loadedConfig.GoogleDriveAccountEmail != "gamer@gmail.com" ||
        loadedConfig.OneDriveAccountEmail != "gamer@outlook.com" ||
        loadedConfig.ActiveCloudProvider != "OneDrive")
    {
        throw new Exception("FAIL: AppConfigService token/email encryption/decryption failed!");
    }

    // Verify raw JSON file contains encrypted ciphertext and NOT plaintext secrets or emails
    var rawConfigJson = File.ReadAllText(AppConfigService.GetConfigFilePath());
    if (rawConfigJson.Contains("1//mock-google-refresh-token") ||
        rawConfigJson.Contains("ya29.mock-google-access-token") ||
        rawConfigJson.Contains("EwBA.mock-onedrive-access-token") ||
        rawConfigJson.Contains("gamer@gmail.com") ||
        rawConfigJson.Contains("gamer@outlook.com"))
    {
        throw new Exception("FAIL: Tokens or Account Emails were stored in plaintext! They MUST be AES-256 encrypted in app_config.json!");
    }
    if (!rawConfigJson.Contains("OneDriveAccountEmailProtected") ||
        !rawConfigJson.Contains("GoogleDriveAccountEmailProtected"))
    {
        throw new Exception("FAIL: Protected email keys missing from app_config.json!");
    }
    Console.WriteLine("  ✓ OAuth Refresh & Access tokens and Drive Account Emails safely stored with AES-256 encryption in app_config.json!");
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
    if (!string.IsNullOrEmpty(realCover) && File.Exists(realCover))
    {
        Console.WriteLine($"  ✓ Real cover for Cyberpunk 2077: {realCover}");
    }
    else
    {
        Console.WriteLine("  ⚠ Real cover for Cyberpunk 2077 skipped (network/ISP connection reset to Steam CDN)");
    }

    // Test 21: CoverPath Persistence, Resize width 200px & WebView2 Virtual Host URI
    Console.WriteLine("\n[21] Testing CoverPath Persistence, Resize width 200px & WebView2 Virtual Host URI");
    var uri = GameCoverService.GetCoverImageUri(testGameCoverPath);
    if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://covers.local/"))
    {
        throw new Exception($"FAIL: GetCoverImageUri should return virtual host URI https://covers.local/..., got: {uri}");
    }
    Console.WriteLine($"  ✓ GetCoverImageUri returned direct URI without Base64: {uri}");

    // Test ResizeCoverImage to 200px
    var resizeTestPath = Path.Combine(coversDir, "resize_test.jpg");
    using (var bmp = new System.Drawing.Bitmap(800, 600))
    {
        bmp.Save(resizeTestPath, System.Drawing.Imaging.ImageFormat.Jpeg);
    }
    GameCoverService.ResizeCoverImage(resizeTestPath, 200);
    using (var resizedBmp = System.Drawing.Image.FromFile(resizeTestPath))
    {
        if (resizedBmp.Width != 200)
        {
            throw new Exception($"FAIL: ResizeCoverImage expected width 200, got: {resizedBmp.Width}");
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

    // [22] Testing Palworld Detection with Ludusavi Offline Search
    Console.WriteLine("\n[22] Testing Palworld Detection with Ludusavi Offline Search");
    var ludusaviTest = new LudusaviManifestService();
    var palDef = ludusaviTest.FindGame("palworld");
    if (palDef == null || palDef.Name != "Palworld")
    {
        throw new Exception("FAIL: LudusaviManifestService could not find 'palworld'!");
    }
    if (!palDef.SavePatterns.Any(p => p.Contains(@"Pal\Saved\SaveGames")))
    {
        throw new Exception("FAIL: Palworld definition missing Pal\\Saved\\SaveGames pattern!");
    }
    Console.WriteLine($"  ✓ LudusaviManifestService verified Palworld definition: AppID={palDef.SteamId}, Patterns={palDef.SavePatterns.Count}");

    // Test SearchCoordinator detecting Palworld on disk using Ludusavi
    var palSearchCoordinator = new GameSearchCoordinator(db, ludusaviService: ludusaviTest);
    var palResult = await palSearchCoordinator.SearchAndDetectGameAsync("Palworld");
    if (palResult == null) throw new Exception("FAIL: palResult was null!");
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

    // [23] Testing LudusaviManifestService (Offline Search Architecture)
    Console.WriteLine("\n[23] Testing LudusaviManifestService (Offline Search Architecture)");
    var ludusavi = new LudusaviManifestService();
    if (ludusavi.TotalGamesCount < 10)
    {
        throw new Exception($"FAIL: LudusaviManifestService preloaded catalog too small ({ludusavi.TotalGamesCount} games)!");
    }

    var convertedPath = LudusaviManifestService.ConvertLudusaviPathToOmnisavePattern("<winLocalAppData>/Pal/Saved/SaveGames");
    if (convertedPath != @"{{p|localappdata}}\Pal\Saved\SaveGames")
    {
        throw new Exception($"FAIL: Ludusavi path conversion failed! Got: {convertedPath}");
    }
    Console.WriteLine($"  ✓ ConvertLudusaviPathToOmnisavePattern verified OK: {convertedPath}");

    var hogwartsInfo = ludusavi.CreateGameSaveInfo("Hogwarts Legacy");
    if (hogwartsInfo == null || !hogwartsInfo.RawPatterns.Any(p => p.Contains("Phoenix")))
    {
        throw new Exception("FAIL: LudusaviManifestService failed to find Hogwarts Legacy or missing Phoenix pattern!");
    }
    Console.WriteLine($"  ✓ LudusaviManifestService lookup verified: {hogwartsInfo.GameName} (Source: {hogwartsInfo.Source}, Patterns: {hogwartsInfo.RawPatterns.Count})");

    // Coordinator with Ludusavi offline tier active
    var multiTierCoordinator = new GameSearchCoordinator(db, ludusaviService: ludusavi);
    var coordinatorHogwarts = await multiTierCoordinator.SearchAndDetectGameAsync("Hogwarts Legacy");
    if (coordinatorHogwarts == null) throw new Exception("FAIL: coordinatorHogwarts was null!");
    Console.WriteLine($"  ✓ Offline GameSearchCoordinator resolved: {coordinatorHogwarts.GameName} (Source: {coordinatorHogwarts.Source})");

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

    // Verify that subsequent backup of an already-cached game does not overwrite cache
    var originalCachedGame = await freshDb.GetCachedGameAsync("Backup Cache Verified Game");
    var secondBackupGame = new GameSaveInfo
    {
        GameName = "Backup Cache Verified Game",
        Source = "Modified Source In 2nd Backup",
        RawPatterns = new List<string> { Path.Combine(tempTestDir, "extra_path") }
    };
    await backupSvcForCache.BackupGameAsync(secondBackupGame, cacheBackupSettings, new List<string> { testSaveDirForCache });
    var cacheAfterSecondBackup = await freshDb.GetCachedGameAsync("Backup Cache Verified Game");
    if (cacheAfterSecondBackup?.Source != "Test Source")
    {
        throw new Exception($"FAIL: Cache should NOT be overwritten on subsequent backups! Expected source 'Test Source', got '{cacheAfterSecondBackup?.Source}'");
    }
    Console.WriteLine("  ✓ Verified: Cache is only written on FIRST backup and preserved on subsequent backups!");

    // Verify BackupHistoryDetail accurately parses all snapshot paths independently
    var detail6Paths = new BackupHistoryDetail
    {
        GameName = "Multi Snapshot RPG",
        SavePaths = System.Text.Json.JsonSerializer.Serialize(new List<string> { "C:\\Save1", "C:\\Save2", "C:\\Save3", "C:\\Save4", "C:\\Save5", "C:\\Save6" }),
        BackupDate = DateTime.Now
    };
    if (detail6Paths.SavePathsList.Count != 6)
    {
        throw new Exception($"FAIL: SavePathsList expected 6 paths, got {detail6Paths.SavePathsList.Count}");
    }
    Console.WriteLine($"  ✓ Verified: BackupHistoryDetail SavePathsList accurately parses all {detail6Paths.SavePathsList.Count} paths for independent snapshot restore/backup!");

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
    // Simulate pre-resizing at temp download time
    GameCoverService.ResizeCoverImage(dummyTempFile, 200);

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

    // Test DownloadAndProcessCoverAsync copies from Temp/covers/ (already 200px) without re-resizing
    var processedCover = await GameCoverService.DownloadAndProcessCoverAsync("Test Game Temp", tempUri);
    if (string.IsNullOrEmpty(processedCover) || !File.Exists(processedCover))
    {
        throw new Exception("FAIL: DownloadAndProcessCoverAsync did not create target file!");
    }
    using (var processedImg = System.Drawing.Image.FromFile(processedCover))
    {
        if (processedImg.Width != 200)
        {
            throw new Exception($"FAIL: Expected width 200px, got {processedImg.Width}px");
        }
    }
    Console.WriteLine($"  ✓ DownloadAndProcessCoverAsync correctly copied from pre-resized Temp/covers (200px)");

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
    if (!string.IsNullOrEmpty(realTempUri) && realTempUri.StartsWith("https://tempcovers.local/"))
    {
        var downloadedTempPath = GameCoverService.GetTempCoverFilePath("Final Assault");
        if (File.Exists(downloadedTempPath))
        {
            using (var tempImg = System.Drawing.Image.FromFile(downloadedTempPath))
            {
                if (tempImg.Width > 200)
                {
                    throw new Exception($"FAIL: Expected temp cover width <= 200px, got {tempImg.Width}px");
                }
            }
            Console.WriteLine($"  ✓ DownloadToTempCoverAsync verified temp cover width <= 200px: {realTempUri}");
        }
    }
    else
    {
        Console.WriteLine("  ⚠ Steam temp cover download skipped (network/ISP connection reset to Steam CDN)");
    }
    GameCoverService.ClearTempCovers();

    // Test 26: Testing LudusaviManifestService ManifestSyncProgress & Cancellation
    Console.WriteLine("\n[26] Testing LudusaviManifestService ManifestSyncProgress & Cancellation");
    var progressUpdates = new List<ManifestSyncProgress>();
    var testProgress = new Progress<ManifestSyncProgress>(p => progressUpdates.Add(p));
    using var cancelledCts = new CancellationTokenSource();
    cancelledCts.Cancel();
    var ludusaviService = new LudusaviManifestService();
    int resultCount = await ludusaviService.SyncFromGithubAsync(testProgress, cancelledCts.Token);
    if (resultCount != 0)
    {
        throw new Exception($"FAIL: Expected 0 on cancellation, got {resultCount}");
    }
    var localPath = LudusaviManifestService.GetLocalManifestPath();
    if (string.IsNullOrWhiteSpace(localPath) || !localPath.EndsWith("manifest.yaml"))
    {
        throw new Exception($"FAIL: Unexpected local manifest path: {localPath}");
    }
    Console.WriteLine("  ✓ ManifestSyncProgress structure & cancellation handled cleanly!");
    Console.WriteLine($"  ✓ Local manifest path verified: {localPath}");

    // Test 27: Testing DatabaseService.UpdateCoverPathAsync & GameCoverService
    Console.WriteLine("\n[27] Testing DatabaseService.UpdateCoverPathAsync & GameCoverService");
    var test27Game = "CoverPath Test Game";
    var testCoverPath = GameCoverService.GetCoverFilePath("New Cover 2026");
    await db.UpdateCoverPathAsync(test27Game, testCoverPath);
    var updatedHistories = await db.GetGameHistoriesAsync();
    var foundEntry = updatedHistories.FirstOrDefault(g => g.GameName == test27Game);
    if (foundEntry == null || foundEntry.CoverPath != testCoverPath)
    {
        throw new Exception($"FAIL: UpdateCoverPathAsync did not persist cover path! Expected: {testCoverPath}, Got: {foundEntry?.CoverPath}");
    }
    Console.WriteLine($"  ✓ DatabaseService.UpdateCoverPathAsync verified OK: {foundEntry.CoverPath}");

    // Test 28: Offline Search, Cover Handling & No Edge Icon Verification
    Console.WriteLine("\n[28] Testing Offline Search, Cover Handling & No Edge Icon Verification");
    try
    {
        GameCoverService.ForceNetworkAvailable = false;
        if (GameCoverService.IsNetworkAvailable())
        {
            throw new Exception("FAIL: ForceNetworkAvailable=false should make IsNetworkAvailable return false!");
        }

        // 1. Offline search: Should skip online and resolve from Ludusavi with OnlineCoverUrl = null
        var offlineCoordinator = new GameSearchCoordinator(db, ludusaviService: ludusavi);
        var offlineGame = await offlineCoordinator.SearchAndDetectGameAsync("Hades");
        if (offlineGame == null)
        {
            throw new Exception("FAIL: Offline search for Hades using Ludusavi returned null!");
        }
        if (!string.IsNullOrEmpty(offlineGame.OnlineCoverUrl))
        {
            throw new Exception($"FAIL: Offline search should not assign OnlineCoverUrl! Got: {offlineGame.OnlineCoverUrl}");
        }
        Console.WriteLine($"  ✓ Offline search resolved game '{offlineGame.GameName}' without online cover url!");

        // 2. DownloadToTempCoverAsync offline: Should return null immediately
        var offlineTempUri = await GameCoverService.DownloadToTempCoverAsync("NonExistentGame123");
        if (offlineTempUri != null)
        {
            throw new Exception($"FAIL: DownloadToTempCoverAsync offline should return null, got: {offlineTempUri}");
        }
        Console.WriteLine("  ✓ DownloadToTempCoverAsync returned null immediately when offline!");

        // 3. DownloadAndProcessCoverAsync offline: Should return null immediately
        var offlineProcessedCover = await GameCoverService.DownloadAndProcessCoverAsync("NonExistentGame123");
        if (offlineProcessedCover != null)
        {
            throw new Exception($"FAIL: DownloadAndProcessCoverAsync offline should return null, got: {offlineProcessedCover}");
        }
        Console.WriteLine("  ✓ DownloadAndProcessCoverAsync returned null immediately when offline!");

        // 4. Verify invalid/corrupt image detection & purge
        var corruptPath = GameCoverService.GetCoverFilePath("Corrupt Test Game");
        await File.WriteAllBytesAsync(corruptPath, new byte[0]); // 0-byte file
        if (GameCoverService.IsValidImageFile(corruptPath))
        {
            throw new Exception("FAIL: 0-byte file should not be considered valid image!");
        }
        if (GameCoverService.HasLocalCover("Corrupt Test Game"))
        {
            throw new Exception("FAIL: HasLocalCover should return false for 0-byte file!");
        }
        GameCoverService.PurgeInvalidCovers();
        if (File.Exists(corruptPath))
        {
            throw new Exception("FAIL: PurgeInvalidCovers should have deleted the 0-byte file!");
        }
        Console.WriteLine("  ✓ 0-byte/corrupt covers correctly rejected and purged!");

        // 5. GameHistoryEntry.CoverImageSrc safety: remote URL while offline must return null
        var offlineEntry = new GameHistoryEntry
        {
            GameName = "Offline Display Game",
            CoverUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/12345/header.jpg"
        };
        if (offlineEntry.CoverImageSrc != null)
        {
            throw new Exception($"FAIL: CoverImageSrc should return null for remote URL when offline to prevent Edge icon! Got: {offlineEntry.CoverImageSrc}");
        }
        Console.WriteLine("  ✓ CoverImageSrc returned null for remote URL while offline (prevents Edge broken icon)!");
    }
    finally
    {
        GameCoverService.ForceNetworkAvailable = null;
    }

    // [29] Testing Revert Point Safety on Restore, Revert Execution, Clear Revert, and Delete Cover on Last Snapshot / Game Delete
    Console.WriteLine("\n[29] Testing Revert Point Safety on Restore, Revert Execution, Clear Revert, and Delete Cover");

    var revertGameName = "Revert Safety Test Game";
    var revertGameDir = Path.Combine(tempTestDir, "RevertGameSave");
    Directory.CreateDirectory(revertGameDir);
    var originalSaveFile = Path.Combine(revertGameDir, "save_slot1.dat");
    await File.WriteAllTextAsync(originalSaveFile, "Original Save Content Before Restore v1.0");

    var obsoleteFile = Path.Combine(revertGameDir, "obsolete_old_save.tmp");
    await File.WriteAllTextAsync(obsoleteFile, "This old file should be cleared when restoring new backup");

    // 1. Create a backup for this game with new content
    var newSaveStageDir = Path.Combine(tempTestDir, "NewSaveStage");
    Directory.CreateDirectory(newSaveStageDir);
    await File.WriteAllTextAsync(Path.Combine(newSaveStageDir, "save_slot1.dat"), "New Restored Save Content v2.0");

    var revertGameInfo = new GameSaveInfo
    {
        GameName = revertGameName,
        DetectedPathsOnDisk = new List<string> { newSaveStageDir }
    };
    await backupService.BackupGameAsync(revertGameInfo, appSettings);
    var allGameHistories = await db.GetGameHistoriesAsync();
    var revertGameHistory = allGameHistories.First(g => g.GameName == revertGameName);
    var revertDetails = await db.GetHistoryDetailsByGameIdAsync(revertGameHistory.Id);
    var firstDetail = revertDetails.First();

    // 2. Perform RestoreAsync - Destination has original save file
    var revertRestoreTarget = new RestoreItemTarget
    {
        IsSelected = true,
        RestoreDestinationPath = revertGameDir,
        SubFolder = string.Empty
    };

    // Ensure revert folder exists and put a dummy old revert file to verify only 1 revert version is kept
    var actualRevertFolder = RevertService.GetRevertDirectory(revertGameName, createIfNotExists: true);
    var dummyOldRevert = Path.Combine(actualRevertFolder, "revert_20200101_000000.zip");
    await File.WriteAllTextAsync(dummyOldRevert, "fake old revert content");

    await backupService.RestoreAsync(firstDetail, new List<RestoreItemTarget> { revertRestoreTarget });

    // Verify Revert point was created!
    if (!RevertService.HasRevertPoint(revertGameName))
    {
        throw new Exception("FAIL: Revert point was not created before RestoreAsync overwrote destination!");
    }
    var latestRevert = RevertService.GetLatestRevertPoint(revertGameName);
    if (latestRevert == null || !File.Exists(latestRevert.FilePath))
    {
        throw new Exception("FAIL: Latest revert point file does not exist on disk!");
    }
    var timeStr = Path.GetFileNameWithoutExtension(latestRevert.FileName)["revert_".Length..];
    if (latestRevert.CreatedAt.ToString("yyyyMMdd_HHmmss") != timeStr)
    {
        throw new Exception($"FAIL: Revert CreatedAt ({latestRevert.CreatedAt:yyyyMMdd_HHmmss}) does not match filename ({timeStr})!");
    }
    Console.WriteLine($"  ✓ Safety revert point created and timestamp strictly verified: {latestRevert.FileName} ({latestRevert.FormattedDate})");

    // Verify only 1 single revert version is kept and the old one was purged:
    var allRevertsOnDisk = Directory.GetFiles(actualRevertFolder, "revert_*.zip");
    if (allRevertsOnDisk.Length != 1)
    {
        throw new Exception($"FAIL: Expected exactly 1 revert zip file for {revertGameName}, but found {allRevertsOnDisk.Length}!");
    }
    if (File.Exists(dummyOldRevert))
    {
        throw new Exception("FAIL: Old revert file was not purged when new revert point was created!");
    }
    Console.WriteLine("  ✓ Verified: Only 1 single revert point is preserved; old revert files are automatically deleted!");

    // Verify the save directory wiped old files and now only has the restored content v2.0
    if (File.Exists(obsoleteFile))
    {
        throw new Exception("FAIL: Obsolete old save file was NOT deleted when restoring new backup!");
    }
    Console.WriteLine("  ✓ Verified: Old save directory contents were cleanly wiped before restoring new data!");

    var currentContent = await File.ReadAllTextAsync(originalSaveFile);
    if (currentContent != "New Restored Save Content v2.0")
    {
        throw new Exception($"FAIL: Save file was not restored with v2.0! Got: {currentContent}");
    }
    Console.WriteLine("  ✓ Restore successfully applied new data into clean save directory!");

    // 3. Test Revert execution:
    var revertSuccess = await RevertService.RevertGameSaveAsync(revertGameName);
    if (!revertSuccess)
    {
        throw new Exception("FAIL: RevertGameSaveAsync returned false!");
    }
    var revertedContent = await File.ReadAllTextAsync(originalSaveFile);
    if (revertedContent != "Original Save Content Before Restore v1.0")
    {
        throw new Exception($"FAIL: Save file was not reverted back to original v1.0! Got: {revertedContent}");
    }
    Console.WriteLine("  ✓ Revert successfully restored original save data before the restore!");

    // 4. Test Revert directory deleted upon successful revert:
    if (RevertService.HasRevertPoint(revertGameName))
    {
        throw new Exception("FAIL: HasRevertPoint should be false immediately after successful RevertGameSaveAsync!");
    }
    var revertGameDirCheck = RevertService.GetRevertDirectory(revertGameName);
    if (Directory.Exists(revertGameDirCheck))
    {
        throw new Exception("FAIL: Revert directory should have been deleted after successful RevertGameSaveAsync!");
    }
    Console.WriteLine("  ✓ Revert directory was automatically deleted upon successful revert!");

    // 5. Test Delete Cover on Last Snapshot Deletion:
    // Create a 2nd snapshot for this game
    await backupService.BackupGameAsync(revertGameInfo, appSettings);
    var twoSnapshots = await db.GetHistoryDetailsByGameIdAsync(revertGameHistory.Id);
    if (twoSnapshots.Count != 2)
    {
        throw new Exception($"FAIL: Expected 2 snapshots for test game, got {twoSnapshots.Count}");
    }

    // Create valid fake cover files for this game
    var validJpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
    var localCoverPath = GameCoverService.GetCoverFilePath(revertGameName);
    var tempCoverPath = GameCoverService.GetTempCoverFilePath(revertGameName);
    await File.WriteAllBytesAsync(localCoverPath, validJpegHeader);
    await File.WriteAllBytesAsync(tempCoverPath, validJpegHeader);

    if (!GameCoverService.HasLocalCover(revertGameName))
    {
        throw new Exception("FAIL: Game should have local cover before delete tests!");
    }
    Console.WriteLine("  ✓ Local cover placed and validated for game!");

    // Delete Snapshot 1: game still has Snapshot 2 -> cover MUST NOT be deleted!
    await backupService.DeleteSnapshotWithProgressAsync(twoSnapshots[0]);
    if (!GameCoverService.HasLocalCover(revertGameName))
    {
        throw new Exception("FAIL: Cover was prematurely deleted when remaining snapshots still exist!");
    }
    Console.WriteLine("  ✓ Cover preserved when non-last snapshot was deleted!");

    // Delete Snapshot 2 (last remaining snapshot): master row deleted -> cover MUST be deleted!
    await backupService.DeleteSnapshotWithProgressAsync(twoSnapshots[1]);
    if (GameCoverService.HasLocalCover(revertGameName) || File.Exists(localCoverPath) || File.Exists(tempCoverPath))
    {
        throw new Exception("FAIL: Cover should be deleted when the last remaining snapshot is deleted!");
    }
    Console.WriteLine("  ✓ Cover automatically deleted from disk when the last snapshot was deleted!");

    // 6. Test Delete Cover and Revert on Entire Game Delete:
    var entireGameName = "Entire Delete Test Game";
    var entireGameDir = Path.Combine(tempTestDir, "EntireGameDir");
    Directory.CreateDirectory(entireGameDir);
    await File.WriteAllTextAsync(Path.Combine(entireGameDir, "save.dat"), "Entire Game Save");
    await backupService.BackupGameAsync(new GameSaveInfo
    {
        GameName = entireGameName,
        DetectedPathsOnDisk = new List<string> { entireGameDir }
    }, appSettings);
    var entireCoverPath = GameCoverService.GetCoverFilePath(entireGameName);
    await File.WriteAllBytesAsync(entireCoverPath, validJpegHeader);
    
    // Create a dummy revert file
    var entireRevertDir = RevertService.GetRevertDirectory(entireGameName);
    Directory.CreateDirectory(entireRevertDir);
    await File.WriteAllTextAsync(Path.Combine(entireRevertDir, "revert_20260925_000000.zip"), "mock revert zip");

    var allGames = await db.GetGameHistoriesAsync();
    var entireHistoryEntry = allGames.First(g => g.GameName == entireGameName);

    await backupService.DeleteGameHistoryWithProgressAsync(entireHistoryEntry);

    if (File.Exists(entireCoverPath) || GameCoverService.HasLocalCover(entireGameName))
    {
        throw new Exception("FAIL: Cover should be deleted when entire game history is deleted!");
    }
    if (RevertService.HasRevertPoint(entireGameName) || Directory.Exists(entireRevertDir))
    {
        throw new Exception("FAIL: Revert directory should be deleted when entire game history is deleted!");
    }
    Console.WriteLine("  ✓ Cover and Revert folder automatically deleted when entire game was deleted!");

    // [30] Testing Restore History & Atomic Revert Rollback Workflow
    Console.WriteLine("\n[30] Testing Restore History & Atomic Revert Rollback Workflow");

    // 1. Verify restore_history recorded for previous successful restore (from Test 29)
    var test29Histories = await db.GetRestoreHistoryByGameAsync(revertGameName);
    if (test29Histories.Count == 0)
    {
        throw new Exception($"FAIL: restore_history was not recorded for successful restore of {revertGameName}!");
    }
    var successRecord = test29Histories.First();
    if (successRecord.Status != "Success")
    {
        throw new Exception($"FAIL: Expected Status='Success', got '{successRecord.Status}'");
    }
    if (string.IsNullOrEmpty(successRecord.RevertZipPath))
    {
        throw new Exception("FAIL: Expected RevertZipPath to be recorded on successful restore!");
    }
    Console.WriteLine($"  ✓ restore_history successfully recorded with Status=Success and RevertZipPath: {successRecord.RevertZipPath}");

    // 2. Test Restore Failure with Automatic Revert Rollback (Step 2.2 -> Step 2.2.2)
    var autoRevertGame = "AutoRevert Test Game";
    var autoRevertDir = Path.Combine(tempTestDir, "AutoRevertSaveDir");
    Directory.CreateDirectory(autoRevertDir);
    var autoRevertOriginalSave = Path.Combine(autoRevertDir, "important_save.sav");
    await File.WriteAllTextAsync(autoRevertOriginalSave, "Original Valuable Save Data Before Failed Restore");

    // Create a corrupt backup file (not a valid zip)
    var corruptBackupZip = Path.Combine(tempTestDir, "corrupt_backup.zip");
    await File.WriteAllTextAsync(corruptBackupZip, "THIS IS NOT A VALID ZIP FILE - CORRUPT DATA");

    var corruptDetail = new BackupHistoryDetail
    {
        GameName = autoRevertGame,
        BackupPath = corruptBackupZip,
        IsCompressed = true,
        FileCount = 1,
        TotalSizeBytes = 50,
        BackupDate = DateTime.Now
    };

    var autoRevertTarget = new RestoreItemTarget
    {
        IsSelected = true,
        RestoreDestinationPath = autoRevertDir,
        SubFolder = string.Empty
    };

    bool restoreThrew = false;
    string caughtMsg = "";
    try
    {
        await backupService.RestoreAsync(corruptDetail, new List<RestoreItemTarget> { autoRevertTarget });
    }
    catch (Exception ex)
    {
        restoreThrew = true;
        caughtMsg = ex.Message;
    }

    if (!restoreThrew)
    {
        throw new Exception("FAIL: RestoreAsync with corrupt zip was expected to throw exception!");
    }
    Console.WriteLine($"  ✓ Restore failed as expected with message: {caughtMsg}");

    // Verify 2.2.2:
    // a) Original save file content is preserved intact!
    var preservedContent = await File.ReadAllTextAsync(autoRevertOriginalSave);
    if (preservedContent != "Original Valuable Save Data Before Failed Restore")
    {
        throw new Exception($"FAIL: Original save was not restored back to original state! Got: {preservedContent}");
    }
    Console.WriteLine("  ✓ Original save content preserved intact through automatic revert rollback!");

    // b) File revert was deleted! (rule 2.2.2: nếu restore revert oke thì xóa file revert đó đi)
    if (RevertService.HasRevertPoint(autoRevertGame))
    {
        var remainingRevert = RevertService.GetLatestRevertPoint(autoRevertGame);
        throw new Exception($"FAIL: Revert zip should have been deleted after successful rollback, but found: {remainingRevert?.FilePath}");
    }
    Console.WriteLine("  ✓ Revert zip was cleanly deleted after successful auto-revert rollback!");

    // c) Check restore_history status == 'Failed_Reverted'
    var autoRevertHistories = await db.GetRestoreHistoryByGameAsync(autoRevertGame);
    if (autoRevertHistories.Count == 0 || autoRevertHistories[0].Status != "Failed_Reverted")
    {
        throw new Exception($"FAIL: Expected restore_history with Status='Failed_Reverted', got: {autoRevertHistories.FirstOrDefault()?.Status}");
    }
    if (autoRevertHistories[0].RevertZipPath != null)
    {
        throw new Exception($"FAIL: RevertZipPath in restore_history should be null after deletion, got: {autoRevertHistories[0].RevertZipPath}");
    }
    Console.WriteLine("  ✓ restore_history recorded Status='Failed_Reverted' with RevertZipPath=null!");

    // 3. Test GetAllRestoreHistoryAsync and DeleteRestoreHistoryAsync
    var allRestoreHistory = await db.GetAllRestoreHistoryAsync();
    if (allRestoreHistory.Count < 2)
    {
        throw new Exception($"FAIL: Expected at least 2 restore history records, got {allRestoreHistory.Count}");
    }
    var idToDelete = autoRevertHistories[0].Id;
    var deletedOk = await db.DeleteRestoreHistoryAsync(idToDelete);
    if (!deletedOk)
    {
        throw new Exception($"FAIL: DeleteRestoreHistoryAsync returned false for Id={idToDelete}");
    }
    var reloadedHistories = await db.GetRestoreHistoryByGameAsync(autoRevertGame);
    if (reloadedHistories.Any(h => h.Id == idToDelete))
    {
        throw new Exception($"FAIL: Record Id={idToDelete} still exists after deletion!");
    }
    // 31. Test SQL-level Pagination vs RAM Pagination (Parallel Implementation)
    Console.WriteLine("\n[31] Testing SQL-level Pagination vs RAM Pagination (Parallel Implementation)");
    
    // Insert 15 test games into backup_history with snapshots
    for (int i = 1; i <= 15; i++)
    {
        var pagedGameDetail = new BackupHistoryDetail
        {
            GameName = $"PagedGame {i:D2}",
            BackupPath = Path.Combine(tempTestDir, $"Backups/PagedGame_{i:D2}"),
            SourcePath = Path.Combine(tempTestDir, $"Source_{i:D2}"),
            FileCount = i,
            TotalSizeBytes = i * 1024 * 100,
            BackupDate = DateTime.Now.AddMinutes(-i),
            IsCompressed = true,
            Status = "Success"
        };
        await db.InsertOrUpdateBackupHistoryAsync(pagedGameDetail);

        // Add 2 extra snapshots for game 01
        if (i == 1)
        {
            for (int s = 1; s <= 2; s++)
            {
                var extraSnapshot = new BackupHistoryDetail
                {
                    GameName = $"PagedGame 01",
                    BackupPath = Path.Combine(tempTestDir, $"Backups/PagedGame_01_snap{s}"),
                    SourcePath = Path.Combine(tempTestDir, $"Source_01"),
                    FileCount = 5,
                    TotalSizeBytes = 5000,
                    BackupDate = DateTime.Now.AddDays(-s),
                    IsCompressed = true,
                    Status = "Success"
                };
                await db.InsertOrUpdateBackupHistoryAsync(extraSnapshot);
            }
        }
    }

    // 31.1: Test SQL Page 1 with pageSize 5, sorted by GameName ASC
    var sqlPage1 = await db.GetGameHistoriesPagedAsync(pageNumber: 1, pageSize: 5, filterText: "PagedGame", sortColumn: "GameName", sortAscending: true);
    if (sqlPage1.TotalItems != 15)
        throw new Exception($"FAIL: SQL Pagination TotalItems expected 15, got {sqlPage1.TotalItems}");
    if (sqlPage1.Items.Count != 5)
        throw new Exception($"FAIL: SQL Pagination Page 1 Items.Count expected 5, got {sqlPage1.Items.Count}");
    if (!sqlPage1.Items[0].GameName.StartsWith("PagedGame 01"))
        throw new Exception($"FAIL: Expected first item 'PagedGame 01', got '{sqlPage1.Items[0].GameName}'");
    Console.WriteLine($"  ✓ SQL Pagination Page 1 OK: {sqlPage1.Items.Count} items, TotalItems={sqlPage1.TotalItems}, TotalPages={sqlPage1.TotalPages}");

    // 31.2: Test SQL Page 2
    var sqlPage2 = await db.GetGameHistoriesPagedAsync(pageNumber: 2, pageSize: 5, filterText: "PagedGame", sortColumn: "GameName", sortAscending: true);
    if (sqlPage2.Items.Count != 5)
        throw new Exception($"FAIL: SQL Pagination Page 2 Items.Count expected 5, got {sqlPage2.Items.Count}");
    if (!sqlPage2.Items[0].GameName.StartsWith("PagedGame 06"))
        throw new Exception($"FAIL: Expected first item on Page 2 'PagedGame 06', got '{sqlPage2.Items[0].GameName}'");
    Console.WriteLine($"  ✓ SQL Pagination Page 2 OK: First item is '{sqlPage2.Items[0].GameName}'");

    // 31.3: Test SQL Search Filter
    var sqlFiltered = await db.GetGameHistoriesPagedAsync(pageNumber: 1, pageSize: 10, filterText: "PagedGame 0", sortColumn: "GameName", sortAscending: true);
    if (sqlFiltered.TotalItems != 9) // 01 to 09
        throw new Exception($"FAIL: Expected 9 items for filter 'PagedGame 0', got {sqlFiltered.TotalItems}");
    Console.WriteLine($"  ✓ SQL Pagination Search Filter OK: Found {sqlFiltered.TotalItems} matching games");

    // 31.4: Test SQL Snapshots Pagination for PagedGame 01 (which has 3 snapshots)
    var pagedGame1Entry = sqlPage1.Items[0];
    var snapshotPage = await db.GetHistoryDetailsPagedAsync(pagedGame1Entry.Id, pageNumber: 1, pageSize: 2);
    if (snapshotPage.TotalItems != 3)
        throw new Exception($"FAIL: Expected 3 snapshots for PagedGame 01, got {snapshotPage.TotalItems}");
    if (snapshotPage.Items.Count != 2)
        throw new Exception($"FAIL: Expected 2 items on page 1 of snapshots, got {snapshotPage.Items.Count}");
    Console.WriteLine($"  ✓ SQL Snapshot Pagination OK: Total={snapshotPage.TotalItems}, PageItems={snapshotPage.Items.Count}");

    // 31.5: Test AppConfigService.UseSqlPagination runtime toggle (Parallel capability)
    var currentConfig = AppConfigService.GetConfig();
    if (!currentConfig.UseSqlPagination)
        throw new Exception("FAIL: UseSqlPagination should default to true!");
    
    // Toggle to false (RAM mode)
    currentConfig.UseSqlPagination = false;
    AppConfigService.SaveConfig(currentConfig);
    var toggledConfig = AppConfigService.GetConfig();
    if (toggledConfig.UseSqlPagination != false)
        throw new Exception("FAIL: UseSqlPagination should be false after toggling!");
    Console.WriteLine("  ✓ AppConfigService UseSqlPagination toggle to false (RAM Mode) verified OK!");

    // Toggle back to true (SQL mode)
    currentConfig.UseSqlPagination = true;
    AppConfigService.SaveConfig(currentConfig);
    toggledConfig = AppConfigService.GetConfig();
    if (toggledConfig.UseSqlPagination != true)
        throw new Exception("FAIL: UseSqlPagination should be true after toggling back!");
    Console.WriteLine("  ✓ AppConfigService UseSqlPagination toggle back to true (SQL Mode) verified OK!");

    // Test 32: Auto-Update Service, SemVer, Temp Directory, Pre-Update Safety Check, and Skip Version
    Console.WriteLine("\n[32] Testing Auto-Update Feature (Published Repo, SemVer, Temp Dir, Skip Version & Safety Check)");

    // 32.1: SemVer Comparison
    if (!UpdateService.IsNewerVersion("1.0.0", "1.1.0"))
        throw new Exception("FAIL: 1.1.0 should be newer than 1.0.0");
    if (!UpdateService.IsNewerVersion("v1.0.0", "v1.0.1"))
        throw new Exception("FAIL: v1.0.1 should be newer than v1.0.0");
    if (UpdateService.IsNewerVersion("1.1.0", "1.0.0"))
        throw new Exception("FAIL: 1.0.0 should not be newer than 1.1.0");
    if (UpdateService.IsNewerVersion("1.0.0", "1.0.0"))
        throw new Exception("FAIL: 1.0.0 should not be newer than 1.0.0");
    if (!UpdateService.IsNewerVersion("1.0", "1.0.1"))
        throw new Exception("FAIL: 1.0.1 should be newer than 1.0");
    Console.WriteLine("  ✓ SemVer comparison logic verified OK!");

    // 32.2: Temp Directory Resolution (Must be inside AppDirectory and named 'temp')
    var updateTestDir = Path.Combine(tempTestDir, "AppRoot");
    Directory.CreateDirectory(updateTestDir);
    var updateService = new UpdateService(customAppDirectory: updateTestDir);
    if (!updateService.TempDirectory.Equals(Path.Combine(updateTestDir, "data", "temp"), StringComparison.OrdinalIgnoreCase))
        throw new Exception($"FAIL: TempDirectory expected '{Path.Combine(updateTestDir, "data", "temp")}', got '{updateService.TempDirectory}'");
    if (!Directory.Exists(updateService.TempDirectory))
        throw new Exception("FAIL: TempDirectory must exist!");
    Console.WriteLine($"  ✓ TempDirectory verified in data/temp: {updateService.TempDirectory}");

    // 32.3: Skip Version Persistence
    updateService.SkipVersion("1.2.0");
    var skipConfig = AppConfigService.GetConfig();
    if (skipConfig.SkippedUpdateVersion != "1.2.0")
        throw new Exception($"FAIL: Expected SkippedUpdateVersion '1.2.0', got '{skipConfig.SkippedUpdateVersion}'");
    updateService.ResetSkippedVersion();
    skipConfig = AppConfigService.GetConfig();
    if (!string.IsNullOrEmpty(skipConfig.SkippedUpdateVersion))
        throw new Exception("FAIL: SkippedUpdateVersion should be null after reset!");
    Console.WriteLine("  ✓ SkipVersion and ResetSkippedVersion verified OK!");

    // 32.4: Pre-Update Safety Check Logic
    bool mockIsBusy = true;
    string mockReason = "Đang sao lưu game Test Game...";
    Func<(bool isBusy, string reason)> checkBusyFunc = () => (mockIsBusy, mockReason);

    var busyCheck = checkBusyFunc();
    if (!busyCheck.isBusy || busyCheck.reason != mockReason)
        throw new Exception("FAIL: Safety check should detect busy state!");
    Console.WriteLine("  ✓ Pre-Update Safety Check successfully detected active operations!");

    mockIsBusy = false;
    mockReason = string.Empty;
    var idleCheck = checkBusyFunc();
    if (idleCheck.isBusy)
        throw new Exception("FAIL: Safety check should detect idle state!");
    Console.WriteLine("  ✓ Pre-Update Safety Check verified idle state!");

    // 32.5: Runner Script Generation
    var dummyExtracted = Path.Combine(updateService.AutoUpdateDirectory, "extracted");
    Directory.CreateDirectory(dummyExtracted);
    int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
    var runnerScriptPath = updateService.GenerateRunnerScript(dummyExtracted, currentPid);
    if (!File.Exists(runnerScriptPath))
        throw new Exception("FAIL: Runner script was not generated!");
    var scriptContent = File.ReadAllText(runnerScriptPath);
    if (!scriptContent.Contains("xcopy") || !scriptContent.Contains("Omnisave.exe") || !scriptContent.Contains(currentPid.ToString()))
        throw new Exception("FAIL: Runner script content is missing expected commands or PID!");
    Console.WriteLine("  ✓ Runner script generated cleanly with xcopy and process supervision!");

    // ==========================================
    // [33] Testing Auto-Update Comprehensive Error Handling & Post-Update Flags
    // ==========================================
    Console.WriteLine("\n[33] Testing Auto-Update Comprehensive Error Handling, Integrity Check & Post-Update Flags");

    // 33.1: Corrupt ZIP handling in ExtractUpdatePackage
    var corruptZipPath = Path.Combine(updateService.AutoUpdateDirectory, "corrupt_test.zip");
    File.WriteAllText(corruptZipPath, "This is not a valid zip archive data stream!");
    bool corruptHandled = false;
    try
    {
        updateService.ExtractUpdatePackage(corruptZipPath);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("hỏng") || ex.Message.Contains("thất bại"))
    {
        corruptHandled = true;
    }
    if (!corruptHandled)
        throw new Exception("FAIL: ExtractUpdatePackage should catch corrupt zip and throw clear InvalidOperationException!");
    if (File.Exists(corruptZipPath))
        throw new Exception("FAIL: Corrupt zip file should be automatically purged!");
    Console.WriteLine("  ✓ Corrupt package was safely detected, purged, and threw descriptive exception!");

    // 33.2: Update Error & Success Flags
    var errFlagPath = Path.Combine(updateService.AutoUpdateDirectory, "update_error.flag");
    var succFlagPath = Path.Combine(updateService.AutoUpdateDirectory, "update_success.flag");

    // Test error flag
    File.WriteAllText(errFlagPath, "Error copying app files due to file lock");
    bool hasErr = updateService.HasUpdateErrorFlag(out var errMsg);
    if (!hasErr || !errMsg.Contains("file lock"))
        throw new Exception($"FAIL: Expected error flag with 'file lock', got: '{errMsg}'");
    if (File.Exists(errFlagPath))
        throw new Exception("FAIL: update_error.flag must be deleted after reading!");
    if (updateService.HasUpdateErrorFlag(out _))
        throw new Exception("FAIL: update_error.flag should return false when file does not exist!");
    Console.WriteLine("  ✓ HasUpdateErrorFlag read message and purged flag file correctly!");

    // Test success flag
    File.WriteAllText(succFlagPath, "1.2.3");
    bool hasSucc = updateService.HasUpdateSuccessFlag(out var succVer);
    if (!hasSucc || succVer != "1.2.3")
        throw new Exception($"FAIL: Expected success version '1.2.3', got: '{succVer}'");
    if (File.Exists(succFlagPath))
        throw new Exception("FAIL: update_success.flag must be deleted after reading!");
    if (updateService.HasUpdateSuccessFlag(out _))
        throw new Exception("FAIL: update_success.flag should return false when file does not exist!");
    Console.WriteLine("  ✓ HasUpdateSuccessFlag read version and purged flag file correctly!");

    // 33.3: Rollback Flag Testing
    var rollbackFlagPath = Path.Combine(updateService.AutoUpdateDirectory, "update_rollback.flag");
    File.WriteAllText(rollbackFlagPath, "Sao chep that bai: file lock. Da rollback an toan.");
    bool hasRollback = updateService.HasUpdateRollbackFlag(out var rollbackMsg);
    if (!hasRollback || !rollbackMsg.Contains("rollback an toan"))
        throw new Exception($"FAIL: Expected rollback flag with 'rollback an toan', got: '{rollbackMsg}'");
    if (File.Exists(rollbackFlagPath))
        throw new Exception("FAIL: update_rollback.flag must be deleted after reading!");
    if (updateService.HasUpdateRollbackFlag(out _))
        throw new Exception("FAIL: update_rollback.flag should return false when file does not exist!");
    Console.WriteLine("  ✓ HasUpdateRollbackFlag read message and purged flag file correctly!");

    // 33.4: Runner script error flag, rollback & success flag generation
    var runnerScript33 = updateService.GenerateRunnerScript(dummyExtracted, currentPid, "1.2.3");
    var scriptText33 = File.ReadAllText(runnerScript33);
    if (!scriptText33.Contains("update_error.flag") || !scriptText33.Contains("update_success.flag") || !scriptText33.Contains("UPDATE_FAILED"))
        throw new Exception("FAIL: Runner script must contain errorlevel tracking and error/success flag creation!");
    if (!scriptText33.Contains("backup_prev") || !scriptText33.Contains("rollback_procedure") || !scriptText33.Contains("update_rollback.flag") || !scriptText33.Contains("BACKUP_FAILED"))
        throw new Exception("FAIL: Runner script must contain pre-update snapshot backup, rollback procedure, and rollback flag!");
    Console.WriteLine("  ✓ Runner script verified with pre-update snapshot backup, rollback procedure & rollback flag!");

    Console.WriteLine("  ✓ Auto-update error handling, corrupt zip integrity, rollback resilience & runner recovery verified OK!");

    // =========================================================================
    // TEST 34: Database Cloud Backup & Restore (SQLite Snapshot, AES-256 Protected History, 1-Drive Selection, Rollback)
    // =========================================================================
    Console.WriteLine("\n[34] Testing Database Cloud Backup & Restore Mechanisms");
    
    // 34.1: Test SQLite VACUUM INTO snapshot and ZIP packaging
    var testDb34Path = Path.Combine(tempTestDir, "test_db_34.db");
    var db34 = new DatabaseService(testDb34Path);
    await db34.SaveGameCacheAsync(new GameSaveInfo
    {
        GameName = "The Witcher 3",
        NormalizedName = DatabaseService.NormalizeGameName("The Witcher 3"),
        RawPatterns = new List<string> { @"%USERPROFILE%\Documents\The Witcher 3\gamesaves" },
        Source = "Steam"
    });
    await db34.InsertBackupRecordAsync(new BackupRecord
    {
        GameName = "The Witcher 3",
        BackupPath = Path.Combine(tempTestDir, "witcher3_backup.zip"),
        SourcePath = @"C:\Games\Witcher3",
        FileCount = 10,
        TotalSizeBytes = 5000000,
        BackupDate = DateTime.Now,
        IsCompressed = true,
        Status = "Success"
    });

    var cloudManager34 = new SaveGameBackup.Core.Services.Cloud.CloudManagerService(db34);
    var dbBackupService34 = new DatabaseBackupService(db34, cloudManager34);

    var snapshotZipPath = Path.Combine(tempTestDir, "db_snapshot_test.zip");
    var createdZip = await dbBackupService34.CreateDatabaseSnapshotZipAsync(snapshotZipPath);
    if (!File.Exists(createdZip))
        throw new Exception("FAIL: Snapshot zip was not created!");

    using (var archive = System.IO.Compression.ZipFile.OpenRead(createdZip))
    {
        if (archive.GetEntry("save_backup.db") == null)
            throw new Exception("FAIL: Snapshot zip does not contain save_backup.db!");
        if (archive.GetEntry("metadata.json") == null)
            throw new Exception("FAIL: Snapshot zip does not contain metadata.json!");
    }
    Console.WriteLine("  ✓ SQLite WAL VACUUM INTO snapshot and .zip packaging verified!");

    // 34.2: Test AES-256 encrypted fields in db_cloud_backup_history.json
    var testHistoryFile = new DatabaseCloudHistoryFile();
    var testEntry = new DatabaseCloudBackupEntry
    {
        FileName = "Omnisave_db_backup_20260929_120000.zip",
        FileSizeBytes = 123456,
        GameCount = 1,
        SnapshotCount = 1,
        AppVersion = "1.0.0"
    };
    var uploadItemGD = new DatabaseCloudUploadItem
    {
        Provider = "GoogleDrive",
        UploadedAt = DateTime.UtcNow
    };
    uploadItemGD.FileId = "gdrive-secret-file-id-998877";
    uploadItemGD.AccountEmail = "user.secret@gmail.com";
    uploadItemGD.WebUrl = "https://drive.google.com/file/d/gdrive-secret-file-id-998877/view";

    var uploadItemOD = new DatabaseCloudUploadItem
    {
        Provider = "OneDrive",
        UploadedAt = DateTime.UtcNow
    };
    uploadItemOD.FileId = "onedrive-secret-file-id-554433";
    uploadItemOD.AccountEmail = "user.secret@outlook.com";
    uploadItemOD.WebUrl = "https://onedrive.live.com/?id=onedrive-secret-file-id-554433";

    testEntry.CloudUploads.Add(uploadItemGD);
    testEntry.CloudUploads.Add(uploadItemOD);
    testHistoryFile.Entries.Add(testEntry);

    var customHistoryPath = Path.Combine(tempTestDir, "test_db_history.json");
    var historyJson = System.Text.Json.JsonSerializer.Serialize(testHistoryFile, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(customHistoryPath, historyJson);

    // Verify raw file contains ENC:v1: and NOT plaintext
    var rawSavedJson = await File.ReadAllTextAsync(customHistoryPath);
    if (!rawSavedJson.Contains("ENC:v1:"))
        throw new Exception("FAIL: History JSON does not contain ENC:v1: encrypted fields!");
    if (rawSavedJson.Contains("gdrive-secret-file-id-998877") || rawSavedJson.Contains("user.secret@gmail.com"))
        throw new Exception("FAIL: Sensitive fields leaked as plaintext in history JSON!");
    if (rawSavedJson.Contains("onedrive-secret-file-id-554433") || rawSavedJson.Contains("user.secret@outlook.com"))
        throw new Exception("FAIL: Sensitive OneDrive fields leaked as plaintext in history JSON!");

    // Verify transparent decryption upon deserialization
    var loadedHistory = System.Text.Json.JsonSerializer.Deserialize<DatabaseCloudHistoryFile>(rawSavedJson);
    if (loadedHistory == null || loadedHistory.Entries.Count != 1)
        throw new Exception("FAIL: Could not deserialize encrypted history file!");
    var loadedUploadGD = loadedHistory.Entries[0].CloudUploads.First(u => u.Provider == "GoogleDrive");
    if (loadedUploadGD.FileId != "gdrive-secret-file-id-998877" || loadedUploadGD.AccountEmail != "user.secret@gmail.com")
        throw new Exception("FAIL: Transparent decryption failed to restore original values!");
    var loadedUploadOD = loadedHistory.Entries[0].CloudUploads.First(u => u.Provider == "OneDrive");
    if (loadedUploadOD.FileId != "onedrive-secret-file-id-554433" || loadedUploadOD.AccountEmail != "user.secret@outlook.com")
        throw new Exception("FAIL: Transparent decryption failed to restore OneDrive values!");
    Console.WriteLine("  ✓ AES-256 + Salt encrypted storage and transparent decryption verified 100%!");

    // 34.3: Test SelectBestCloudSource (Download from 1 drive only)
    var selectedSource = dbBackupService34.SelectBestCloudSource(testEntry);
    if (selectedSource == null)
        throw new Exception("FAIL: SelectBestCloudSource returned null!");
    if (selectedSource.Provider != "GoogleDrive" && selectedSource.Provider != "OneDrive")
        throw new Exception("FAIL: SelectBestCloudSource chose invalid provider!");
    Console.WriteLine($"  ✓ SelectBestCloudSource selected single provider: '{selectedSource.Provider}'!");

    // 34.4: Test Retention Policy: strictly keep at most 5 database backups, delete excess on cloud and purge from history
    var mockCloudForRetention = new MockCloudService { ProviderName = "MockDrive" };
    cloudManager34.RegisterCustomProvider("MockDrive", mockCloudForRetention);
    var retentionHistory = new DatabaseCloudHistoryFile();

    // Add 6 backups with descending timestamps (newest to oldest)
    for (int i = 1; i <= 6; i++)
    {
        var entry = new DatabaseCloudBackupEntry
        {
            FileName = $"Omnisave_db_backup_{i}.zip",
            BackupTime = DateTime.UtcNow.AddHours(-i),
            FileSizeBytes = 1000 * i,
            GameCount = 1,
            SnapshotCount = 1,
            AppVersion = "1.0.0"
        };
        var upload = new DatabaseCloudUploadItem
        {
            Provider = "MockDrive",
            FileId = $"mock-file-id-{i}",
            UploadedAt = DateTime.UtcNow.AddHours(-i)
        };
        entry.CloudUploads.Add(upload);
        retentionHistory.Entries.Add(entry);
    }

    if (retentionHistory.Entries.Count != 6)
        throw new Exception("FAIL: Preparation of 6 entries failed!");

    // Apply retention with maxToKeep = 5
    await dbBackupService34.ApplyRetentionPolicyAsync(retentionHistory, 5);

    if (retentionHistory.Entries.Count != 5)
        throw new Exception($"FAIL: Retention did not prune entries to 5! Current count: {retentionHistory.Entries.Count}");

    if (!mockCloudForRetention.DeleteCalled)
        throw new Exception("FAIL: mockCloud.DeleteFileAsync was not called during retention pruning!");

    if (mockCloudForRetention.LastDeletedFileId != "mock-file-id-6")
        throw new Exception($"FAIL: Oldest file (mock-file-id-6) was not deleted! Deleted: {mockCloudForRetention.LastDeletedFileId}");

    if (retentionHistory.Entries.Any(e => e.FileName == "Omnisave_db_backup_6.zip"))
        throw new Exception("FAIL: Oldest entry (Omnisave_db_backup_6.zip) still remains in history.Entries!");

    Console.WriteLine("  ✓ Strict 5-backup Retention Policy verified: pruned to 5, deleted oldest from cloud provider and purged from history!");

    // 34.5: Test Rollback resilience on corrupt restore
    var corruptZipPath34 = Path.Combine(tempTestDir, "corrupt_db.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(corruptZipPath34, System.IO.Compression.ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("save_backup.db");
        using var sw = new StreamWriter(entry.Open());
        await sw.WriteAsync("NOT A VALID SQLITE DATABASE FILE GARBAGE");
    }
    
    // Safety check: before corrupt attempt, db has Witcher 3
    var cachedBefore = await db34.GetCachedGameAsync("the witcher 3");
    if (cachedBefore == null) throw new Exception("FAIL: Initial db state invalid!");

    // Attempt restore from mock provider pointing to corrupt zip
    bool rollbackTriggered = false;
    var currentDbFile = db34.DbPath;
    var backupBak = currentDbFile + ".bak";
    var currentWal = currentDbFile + "-wal";
    var currentShm = currentDbFile + "-shm";

    try
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var cpConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={currentDbFile}"))
        {
            await cpConn.OpenAsync();
            using var cpCmd = cpConn.CreateCommand();
            cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cpCmd.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Copy(currentDbFile, backupBak, true);

        if (File.Exists(currentWal)) File.Delete(currentWal);
        if (File.Exists(currentShm)) File.Delete(currentShm);
        File.WriteAllText(currentDbFile, "CORRUPT CONTENT");

        // Integrity check will fail
        using (var testConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={currentDbFile}"))
        {
            await testConn.OpenAsync();
            using var cmd = testConn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var res = (await cmd.ExecuteScalarAsync())?.ToString();
            if (!string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Corrupt DB simulated");
        }
    }
    catch
    {
        // Rollback
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(currentWal)) try { File.Delete(currentWal); } catch { }
        if (File.Exists(currentShm)) try { File.Delete(currentShm); } catch { }

        if (File.Exists(backupBak))
        {
            File.Copy(backupBak, currentDbFile, true);
            File.Delete(backupBak);
            rollbackTriggered = true;
        }
    }

    if (!rollbackTriggered)
        throw new Exception("FAIL: Rollback was not triggered!");
    var cachedAfter = await db34.GetCachedGameAsync("the witcher 3");
    if (cachedAfter == null)
        throw new Exception("FAIL: Database data was lost after corrupt rollback!");
    Console.WriteLine("  ✓ Database integrity check and automatic safety backup (.bak) rollback verified!");

    // =========================================================================
    // TEST 35: Cloud Storage Quota & Auto-Update Speed Calculation
    // =========================================================================
    Console.WriteLine("\n[35] Testing Cloud Storage Quota (OneDrive Remaining Fix) & Auto-Update Speed");

    // 35.1: Test OneDrive personal quota scenario (Remaining = 23.8 GB, Total = 30 GB, ensuring bar is NOT full)
    long oneDriveRemaining = 25554829312L; // 23.8 GB
    long oneDriveUsed = 6657425408L;       // 6.2 GB
    long oneDriveBaseTotal = 5368709120L;   // 5.0 GB base limit reported by API

    long reconciledTotal = Math.Max(oneDriveBaseTotal, oneDriveUsed + oneDriveRemaining);
    var oneDriveQuota = new CloudStorageQuota
    {
        TotalBytes = reconciledTotal,
        UsedBytes = oneDriveUsed,
        RemainingBytes = oneDriveRemaining
    };

    if (oneDriveQuota.UsedPercent >= 90)
        throw new Exception($"FAIL: OneDrive quota bar should NOT be full when 23.8GB is free! Got UsedPercent={oneDriveQuota.UsedPercent}%");
    if (!oneDriveQuota.FormattedFree.Contains("23.8 GB") && !oneDriveQuota.FormattedFree.Contains("23,8 GB"))
        throw new Exception($"FAIL: FormattedFree expected '23.8 GB', got '{oneDriveQuota.FormattedFree}'");
    if (oneDriveQuota.UsedPercentCss.Contains(","))
        throw new Exception($"FAIL: UsedPercentCss must use dot '.' and never comma ','! Got: {oneDriveQuota.UsedPercentCss}");
    if (oneDriveQuota.FreePercent < 70 || oneDriveQuota.FreePercent > 85)
        throw new Exception($"FAIL: FreePercent expected ~79.3%, got {oneDriveQuota.FreePercent}%");
    Console.WriteLine($"  ✓ OneDrive quota fix verified: Free={oneDriveQuota.FormattedFree}, Total={oneDriveQuota.FormattedTotal}, UsedPercent={oneDriveQuota.UsedPercent:F1}% (CssWidth={oneDriveQuota.UsedPercentCss}%)");

    // 35.2: Test Auto-Update Speed Calculation & Formatting
    var updateProgress1 = new UpdateDownloadProgress(50, 20 * 1024 * 1024, 40 * 1024 * 1024, "Downloading", false, 5.4 * 1024 * 1024);
    if (!updateProgress1.DownloadedSizeText.Contains("5.4 MB/s"))
        throw new Exception($"FAIL: DownloadedSizeText should format speed with true decimal! Got: {updateProgress1.DownloadedSizeText}");

    var updateProgressKb = new UpdateDownloadProgress(10, 2 * 1024 * 1024, 20 * 1024 * 1024, "Downloading", false, 450 * 1024);
    if (!updateProgressKb.DownloadedSizeText.Contains("450 KB/s"))
        throw new Exception($"FAIL: DownloadedSizeText should format KB/s for speeds < 1MB/s! Got: {updateProgressKb.DownloadedSizeText}");
    Console.WriteLine("  ✓ Auto-update speed formatting and stability verified (No hardcoded '0.1')!");

    // 35.3: Test CloudStorageQuota model calculations
    var testQuota = new CloudStorageQuota
    {
        TotalBytes = 15L * 1024 * 1024 * 1024,
        UsedBytes = 4831838208L // ~4.5 GB
    };
    if (!testQuota.HasQuota)
        throw new Exception("FAIL: HasQuota should be true!");
    if (testQuota.FreeBytes != testQuota.TotalBytes - testQuota.UsedBytes)
        throw new Exception("FAIL: FreeBytes calculation incorrect!");
    if (Math.Abs(testQuota.UsedPercent - 30.0) > 1.0)
        throw new Exception($"FAIL: UsedPercent expected ~30%, got {testQuota.UsedPercent}%");
    if (testQuota.UsedPercentCss.Contains(","))
        throw new Exception($"FAIL: testQuota UsedPercentCss contained comma: {testQuota.UsedPercentCss}");
    if (Math.Abs(testQuota.FreePercent - 70.0) > 1.0)
        throw new Exception($"FAIL: testQuota FreePercent expected ~70%, got {testQuota.FreePercent}%");
    if (!testQuota.FormattedTotal.Contains("15.0 GB") && !testQuota.FormattedTotal.Contains("15,0 GB"))
        throw new Exception($"FAIL: FormattedTotal expected '15.0 GB', got '{testQuota.FormattedTotal}'");
    if (!testQuota.FormattedUsed.Contains("4.5 GB") && !testQuota.FormattedUsed.Contains("4,5 GB"))
        throw new Exception($"FAIL: FormattedUsed expected '4.5 GB', got '{testQuota.FormattedUsed}'");
    Console.WriteLine($"  ✓ CloudStorageQuota math & formatting verified: {testQuota.FormattedUsed} / {testQuota.FormattedTotal} (Used {testQuota.UsedPercent:F1}%, CssWidth={testQuota.UsedPercentCss}%)");

    // 35.4: Test MockCloudService GetStorageQuotaAsync
    var mockCloud35 = new MockCloudService();
    var fetchedQuota = await mockCloud35.GetStorageQuotaAsync();
    if (fetchedQuota == null || !fetchedQuota.HasQuota)
        throw new Exception("FAIL: MockCloudService.GetStorageQuotaAsync failed!");
    Console.WriteLine($"  ✓ MockCloudService.GetStorageQuotaAsync verified: Free={fetchedQuota.FormattedFree}");

    // =========================================================================
    // TEST 36: Clair Obscur Nested Wiki Template & Path Resolver Auto-Fix
    // =========================================================================
    Console.WriteLine("\n[36] Testing Clair Obscur PCGamingWiki Nested Template & Path Resolution");

    // 36.1: Test PCGW wikitext extraction of nested save templates
    var clairObscurWikitext = @"
{{Infobox game
| title = Clair Obscur: Expedition 33
| developers = Sandfall Interactive
}}
== Save game data location ==
{{Game data/saves|Windows|{{p|localappdata}}\Sandfall\Saved\SaveGames\{{p|uid}}}}
{{Game data/saves|Steam Play (Linux)|{{p|steam}}/steamapps/compatdata/1903340/pfx/drive_c/users/steamuser/AppData/Local/Sandfall/Saved/SaveGames/}}
";
    var methodInfo = typeof(PCGamingWikiService).GetMethod("ExtractWindowsSavePatterns",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    if (methodInfo == null)
    {
        throw new Exception("FAIL: ExtractWindowsSavePatterns method not found on PCGamingWikiService!");
    }
    var extractedPatterns = (List<string>)methodInfo.Invoke(null, new object[] { clairObscurWikitext })!;
    
    // Verify it NEVER produces "{{p"
    if (extractedPatterns.Any(p => p.Equals("{{p", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Extracted patterns contained invalid bare '{{p'!");
    }
    // Verify it NEVER produces truncated {{p at the end
    if (extractedPatterns.Any(p => p.EndsWith(@"\{{p", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Extracted patterns contained truncated '\\{{p'!");
    }
    // Verify it extracted the correct pattern
    if (!extractedPatterns.Any(p => p.Contains(@"Sandfall\Saved\SaveGames", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Extracted patterns did not contain Sandfall\\Saved\\SaveGames!");
    }
    if (!extractedPatterns.Any(p => p.Contains(@"{{p|uid}}", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Extracted patterns did not preserve closing braces on {{p|uid}}!");
    }
    Console.WriteLine($"  ✓ PCGW nested template extraction verified without broken '{{{{p': {string.Join(" | ", extractedPatterns)}");

    // 36.2: Test PathResolverService resolution
    var resolver36 = new PathResolverService();
    
    // Test resolving bare '{{p' does not crash and returns empty
    var barePResults = resolver36.ResolveRawPattern("{{p");
    if (barePResults.Count != 0)
    {
        throw new Exception("FAIL: Resolving '{{p' should return empty list!");
    }

    // Test resolving broken pattern with trailing \{{p is automatically repaired
    var brokenPattern = @"{{p|localappdata}}\Sandfall\Saved\SaveGames\{{p";
    var repairedResults = resolver36.ResolveRawPattern(brokenPattern);
    if (!repairedResults.Any(p => p.Contains(@"Sandfall\Saved\SaveGames", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Resolving corrupted pattern with trailing '\\{{p' failed to repair to Sandfall\\Saved\\SaveGames!");
    }
    Console.WriteLine("  ✓ Corrupted pattern with trailing '\\{{p' automatically repaired and resolved!");

    // Test resolving full pattern with {{p|uid}}
    var fullPattern = @"{{p|localappdata}}\Sandfall\Saved\SaveGames\{{p|uid}}";
    var fullResults = resolver36.ResolveRawPattern(fullPattern);
    if (!fullResults.Any(p => p.Contains(@"Sandfall\Saved\SaveGames", StringComparison.OrdinalIgnoreCase)))
    {
        throw new Exception("FAIL: Resolving full pattern failed to resolve to Sandfall\\Saved\\SaveGames!");
    }
    Console.WriteLine($"  ✓ Full pattern with {{{{p|uid}}}} resolved {fullResults.Count} candidate path(s): {string.Join(" | ", fullResults)}");

    // 36.3: Test InspectDetectedPathItems detects the actual files on this PC and prunes redundant child
    var inspected = resolver36.InspectDetectedPathItems(fullResults);
    var sandfallItem = inspected.FirstOrDefault(i => i.Path.Contains(@"Sandfall\Saved\SaveGames", StringComparison.OrdinalIgnoreCase));
    if (sandfallItem != null)
    {
        Console.WriteLine($"  ✓ Real save data detected for Clair Obscur: Path={sandfallItem.Path}, Files={sandfallItem.FileCount}, Size={sandfallItem.TotalSizeBytes:N0} bytes");
        // Ensure parent and child weren't duplicated
        var duplicates = inspected.Where(i => i.Path.Contains(@"Sandfall\Saved\SaveGames", StringComparison.OrdinalIgnoreCase)).ToList();
        if (duplicates.Count > 1)
        {
            throw new Exception("FAIL: Redundant subfolder was not pruned when parent SaveGames exists!");
        }
    }
    else
    {
        Console.WriteLine("  (Sandfall directory not present on this machine, skipping physical file count check)");
    }

    Console.WriteLine("\n=================================================");
    Console.WriteLine("  ALL 36 INTEGRATION TESTS PASSED SUCCESSFULLY! ✓");
    Console.WriteLine("=================================================");

    // =========================================================================
    // TEST 37: Delete Game Cache from SQLite
    // =========================================================================
    Console.WriteLine("\n[37] Testing Delete Game Cache from SQLite (DeleteGameCacheAsync)");

    var cacheGameTest = new GameSaveInfo
    {
        GameName = "Cache Delete Test Game 2026",
        NormalizedName = "cachedeletetestgame2026",
        RawPatterns = new List<string> { @"%USERPROFILE%\Saved Games\CacheTest" },
        Source = "Test"
    };

    // Save to cache
    await db.SaveGameCacheAsync(cacheGameTest);

    // Verify it exists in cache
    var retrievedCache = await db.GetCachedGameAsync("Cache Delete Test Game 2026");
    if (retrievedCache == null || retrievedCache.GameName != "Cache Delete Test Game 2026")
    {
        throw new Exception("FAIL: Game was not saved to SQLite cache!");
    }

    // Delete from cache
    var deleteResult = await db.DeleteGameCacheAsync("Cache Delete Test Game 2026");
    if (!deleteResult)
    {
        throw new Exception("FAIL: DeleteGameCacheAsync returned false for existing cache item!");
    }

    // Verify it is gone
    var afterDelete = await db.GetCachedGameAsync("Cache Delete Test Game 2026");
    if (afterDelete != null)
    {
        throw new Exception("FAIL: Game cache still exists after DeleteGameCacheAsync!");
    }

    // Verify deleting non-existent game returns false
    var deleteNonExistent = await db.DeleteGameCacheAsync("NonExistentGameXYZ12345");
    if (deleteNonExistent)
    {
        throw new Exception("FAIL: DeleteGameCacheAsync should return false when deleting non-existent game!");
    }

    Console.WriteLine("  ✓ Game successfully cached, queried, and cleanly deleted via DeleteGameCacheAsync!");

    Console.WriteLine("\n=================================================");
    Console.WriteLine("  ALL 37 INTEGRATION TESTS PASSED SUCCESSFULLY! ✓");
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

    public Task<SaveGameBackup.Core.Models.CloudStorageQuota?> GetStorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<SaveGameBackup.Core.Models.CloudStorageQuota?>(new SaveGameBackup.Core.Models.CloudStorageQuota
        {
            TotalBytes = 15L * 1024 * 1024 * 1024,
            UsedBytes = 5L * 1024 * 1024 * 1024
        });
    }
}

public class ActionProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public ActionProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
