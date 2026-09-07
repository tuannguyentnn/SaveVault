using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.Core.Services.Cloud;

namespace SaveGameBackup.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private DatabaseService _databaseService;
    private GameSearchCoordinator _searchCoordinator;
    private BackupService _backupService;
    private CloudManagerService _cloudManager;

    private static readonly string[] DefaultPopularGames = new[]
    {
        "Elden Ring", "Cyberpunk 2077", "Black Myth: Wukong", "Baldur's Gate 3",
        "The Witcher 3", "Hades", "Dark Souls III", "Sekiro: Shadows Die Twice",
        "God of War", "Palworld", "Monster Hunter: World", "Grand Theft Auto V",
        "Red Dead Redemption 2", "Hogwarts Legacy", "Fallout 4"
    };

    // Database path property
    private string _databaseLocation = string.Empty;

    // Search properties
    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private string _statusMessage = "Sẵn sàng. Hãy nhập tên game để tìm kiếm vị trí save game.";
    private int _selectedTabIndex;

    // Detected Game Info
    private GameSaveInfo? _currentGame;
    private bool _hasGame;
    private string _detectedSizeFormatted = "0 B";
    private int _detectedFileCount;
    private bool _isGameFoundOnDisk;

    // Selected Paths Stats
    private int _selectedPathCount;
    private int _selectedFileCount;
    private string _selectedSizeFormatted = "0 B";
    private bool _hasSelectedPaths;

    // Backup Options & Progress
    private string _backupDestinationRoot = string.Empty;
    private bool _createTimestampSubfolder = true;
    private bool _autoCompressZip;
    private bool _isBackingUp;
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string? _lastBackupPath;

    // Collections
    public ObservableCollection<BackupRecord> BackupHistory { get; } = new();
    public ObservableCollection<GameBackupSummary> GroupedBackupHistory { get; } = new();
    public ObservableCollection<GameHistoryEntry> GameHistories { get; } = new();
    public ObservableCollection<BackupHistoryDetail> CurrentHistoryDetails { get; } = new();
    public ObservableCollection<string> PopularGameSuggestions { get; } = new();
    public ObservableCollection<string> DetectedPathsList { get; } = new();
    public ObservableCollection<DetectedPathItem> DetectedPathItems { get; } = new();
    public ObservableCollection<string> OnlinePatternsList { get; } = new();

    private BackupRecord? _selectedHistoryRecord;
    private GameBackupSummary? _selectedGameSummary;
    private GameHistoryEntry? _selectedGameHistory;
    private BackupHistoryDetail? _selectedHistoryDetail;

    // In-App Modal States
    private bool _isModalOpen;
    private bool _isHistoryDetailsModalOpen;
    private bool _isRestoreModalOpen;
    private BackupRecord? _activeRestoreRecord;
    private BackupHistoryDetail? _activeRestoreDetail;
    public ObservableCollection<RestoreItemTarget> ActiveRestoreItems { get; } = new();
    private bool _isModalRestoring;
    private int _modalRestoreProgressPercent;
    private string _modalRestoreProgressMessage = string.Empty;
    private string _modalRestoreSelectedCountText = string.Empty;
    private bool _canConfirmModalRestore;

    // In-App Confirmation Modal States (Theme-matched)
    private bool _isConfirmModalOpen;
    private string _confirmModalTitle = string.Empty;
    private string _confirmModalMessage = string.Empty;
    private string _confirmModalTargetPath = string.Empty;
    private Func<Task>? _pendingConfirmAction;
    private bool _showDeleteCloudOption;
    private bool _deleteAlsoFromCloud;
    private bool _isConfirmModalDeleting;
    private int _confirmModalProgressPercent;
    private string _confirmModalProgressText = string.Empty;

    // Universal In-App Message Popup States (Dark Theme)
    private bool _isMessageModalOpen;
    private string _messageModalTitle = string.Empty;
    private string _messageModalContent = string.Empty;
    private string _messageModalType = "Info"; // "Success", "Warning", "Error", "Info"
    private string? _messageModalDetails;

    // Cloud Manager Service & States
    private int _selectedCloudProviderIndex; // 0: Google Drive, 1: OneDrive
    private bool _isCloudLoggedIn;
    private string? _cloudAccountEmail;
    private bool _isCloudSyncing;
    private int _cloudSyncProgressPercent;
    private string _cloudSyncProgressText = string.Empty;
    private string _oneDriveClientId = string.Empty;
    private string _googleDriveClientId = string.Empty;
    private string _googleDriveClientSecret = string.Empty;

    // Restore Source Selection Modal (MODAL 5)
    private bool _isRestoreSourceModalOpen;
    private BackupHistoryDetail? _pendingRestoreDetail;
    private string _restoreSourceModalTitle = string.Empty;
    private string _restoreSourceModalMessage = string.Empty;
    private bool _activeRestoreFromCloud;
    private bool _isPendingDetailSyncedToGoogleDrive;
    private bool _isPendingDetailSyncedToOneDrive;

    // Cloud Sync Provider Selection Modal (MODAL 7)
    private bool _isSyncCloudSelectModalOpen;
    private BackupHistoryDetail? _targetSyncDetail;
    private CancellationTokenSource? _syncCts;

    // Dedicated Progress Modal States (Backup / Sync / Restore)
    private bool _isProgressModalOpen;
    private string _activeProgressTitle = "Đang xử lý...";
    private string _activeProgressMessage = string.Empty;
    private int _activeProgressPercent;
    private string _activeProgressSubText = string.Empty;

    public MainViewModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var defaultBackupsFolder = Path.Combine(baseDir, "Backups");
        Directory.CreateDirectory(defaultBackupsFolder);

        // Đọc cấu hình từ cache RAM
        var config = AppConfigService.GetConfig();

        _databaseService = new DatabaseService();
        _databaseLocation = _databaseService.DbPath;
        _searchCoordinator = new GameSearchCoordinator(_databaseService);
        _backupService = new BackupService(_databaseService);
        _cloudManager = new CloudManagerService(_databaseService);
        _isCloudLoggedIn = _cloudManager.IsLoggedIn();
        _cloudAccountEmail = _cloudManager.GetSavedUserEmail();
        _selectedCloudProviderIndex = string.Equals(_cloudManager.ActiveProviderName, "OneDrive", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        _backupDestinationRoot = !string.IsNullOrWhiteSpace(config.BackupRootDirectory)
            ? config.BackupRootDirectory
            : defaultBackupsFolder;
        _createTimestampSubfolder = config.CreateTimestampSubfolder;
        _autoCompressZip = config.AutoCompressZip;

        // Load suggestions
        foreach (var g in DefaultPopularGames)
        {
            PopularGameSuggestions.Add(g);
        }

        // Initialize Commands
        SearchCommand = new RelayCommand(async _ => await ExecuteSearchAsync(), _ => !IsSearching && !IsBackingUp && !string.IsNullOrWhiteSpace(SearchQuery));
        BackupCommand = new RelayCommand(async _ => await ExecuteBackupAsync(), _ => !IsBackingUp && HasGame && HasSelectedPaths);
        OpenBackupFolderCommand = new RelayCommand(_ => ExecuteOpenBackupFolder(), _ => !string.IsNullOrEmpty(LastBackupPath));
        OpenSpecificDetectedPathCommand = new RelayCommand(param => ExecuteOpenSpecificDetectedPath(param as string));
        RemoveDetectedPathCommand = new RelayCommand(param => ExecuteRemoveDetectedPath(param as DetectedPathItem));
        OpenRestoreModalCommand = new RelayCommand(param => ExecuteOpenRestoreModal(param), _ => !IsBackingUp);
        RestoreRecordCommand = OpenRestoreModalCommand;
        DeleteRecordCommand = new RelayCommand(param => ExecuteDeleteRecord(param as BackupRecord));
        OpenRecordFolderCommand = new RelayCommand(param => ExecuteOpenRecordFolder(param as BackupRecord));
        SaveSettingsCommand = new RelayCommand(async _ => await ExecuteSaveSettingsAsync());
        BrowseBackupDirectoryCommand = new RelayCommand(_ => ExecuteBrowseBackupDirectory());
        BrowseDatabaseFileCommand = new RelayCommand(_ => ExecuteBrowseDatabaseFile());
        ApplyDatabaseLocationCommand = new RelayCommand(async _ => await ExecuteApplyDatabaseLocationAsync());
        ResetDatabaseLocationCommand = new RelayCommand(_ => ExecuteResetDatabaseLocation());
        AddCustomPathCommand = new RelayCommand(_ => ExecuteAddCustomPath());
        SelectAllPathsCommand = new RelayCommand(_ => ExecuteSelectAllPaths(), _ => DetectedPathItems.Count > 0);
        DeselectAllPathsCommand = new RelayCommand(_ => ExecuteDeselectAllPaths(), _ => DetectedPathItems.Count > 0);

        // Modal Commands
        OpenGameDetailsCommand = new RelayCommand(async param => await ExecuteOpenGameDetailsAsync(param));
        CloseModalCommand = new RelayCommand(_ => ExecuteCloseModal(), _ => !IsModalRestoring && !IsCloudSyncing);
        CloseRestoreModalCommand = new RelayCommand(_ => ExecuteCloseRestoreModal(), _ => !IsModalRestoring && !IsCloudSyncing);
        ModalRestoreConfirmCommand = new RelayCommand(async _ => await ExecuteModalRestoreConfirmAsync(), _ => !IsModalRestoring && CanConfirmModalRestore);
        ModalSelectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalSelectAllRestoreItems(), _ => !IsModalRestoring);
        ModalDeselectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalDeselectAllRestoreItems(), _ => !IsModalRestoring);
        ModalResetRestorePathsCommand = new RelayCommand(_ => ExecuteModalResetRestorePaths(), _ => !IsModalRestoring);
        ModalToggleRestoreItemCommand = new RelayCommand(param => ExecuteModalToggleRestoreItem(param as RestoreItemTarget), _ => !IsModalRestoring);
        ModalBrowseRestoreDestCommand = new RelayCommand(param => ExecuteModalBrowseRestoreDest(param as RestoreItemTarget), _ => !IsModalRestoring);
        LoadBackupPathsFromHistoryCommand = new RelayCommand(param => ExecuteLoadBackupPathsFromHistory(param), _ => !IsBusy);
        LoadBackupPathsFromManifestCommand = new RelayCommand(param => ExecuteLoadBackupPathsFromManifest(param), _ => !IsBusy);
        LoadBackupPathsForGameCommand = LoadBackupPathsFromHistoryCommand;

        // In-App Confirm Modal & Delete Commands
        RequestDeleteHistoryDetailCommand = new RelayCommand(param => RequestDeleteHistoryDetail(param as BackupHistoryDetail));
        RequestDeleteGameHistoryCommand = new RelayCommand(param => RequestDeleteGameHistory(param as GameHistoryEntry));
        ConfirmModalExecuteCommand = new RelayCommand(async _ => await ExecuteConfirmModalActionAsync(), _ => !IsConfirmModalDeleting);
        CancelConfirmModalCommand = new RelayCommand(_ => ExecuteCancelConfirmModal(), _ => !IsConfirmModalDeleting);

        // Universal In-App Message Popup Command
        CloseMessageModalCommand = new RelayCommand(_ => ExecuteCloseMessageModal());

        // Cloud & Restore Source Modal Commands
        SyncSelectedDetailToCloudCommand = new RelayCommand(param => OpenSyncCloudSelectModal(param as BackupHistoryDetail), _ => !IsCloudSyncing);
        OpenSyncCloudSelectModalCommand = new RelayCommand(param => OpenSyncCloudSelectModal(param as BackupHistoryDetail), _ => !IsCloudSyncing);
        ConfirmSyncToProviderCommand = new RelayCommand(async param => await ConfirmSyncToProviderAsync(param?.ToString() ?? "GoogleDrive"), _ => !IsCloudSyncing);
        CloseSyncCloudSelectModalCommand = new RelayCommand(_ => ExecuteCloseSyncCloudSelectModal());
        CancelSyncCommand = new RelayCommand(_ => ExecuteCancelSync(), _ => IsCloudSyncing);
        OpenCloudWebViewCommand = new RelayCommand(param => ExecuteOpenCloudWebView(param));

        ConnectCloudAccountCommand = new RelayCommand(async _ => await ExecuteConnectCloudAccountAsync());
        DisconnectCloudAccountCommand = new RelayCommand(async _ => await ExecuteDisconnectCloudAccountAsync());
        SaveCloudApiSettingsCommand = new RelayCommand(async _ => await ExecuteSaveCloudApiSettingsAsync());
        OpenAzurePortalGuideCommand = new RelayCommand(_ => ExecuteOpenAzurePortalGuide());
        OpenGoogleCloudConsoleGuideCommand = new RelayCommand(_ => ExecuteOpenGoogleCloudConsoleGuide());
        ConfirmRestoreFromLocalCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromLocal());
        ConfirmRestoreFromCloudCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloud());
        ConfirmRestoreFromGoogleDriveCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloudProvider("GoogleDrive"));
        ConfirmRestoreFromOneDriveCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloudProvider("OneDrive"));
        CancelRestoreSourceModalCommand = new RelayCommand(_ => ExecuteCancelRestoreSourceModal());

        // Load initial data and saved cloud api keys
        _ = LoadInitialDataAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var odId = await _databaseService.GetSettingAsync("onedrive_client_id");
                if (!string.IsNullOrEmpty(odId))
                {
                    App.Current?.Dispatcher?.Invoke(() => OneDriveClientId = odId);
                }

                var gdId = await _databaseService.GetSettingAsync("gdrive_client_id");
                if (!string.IsNullOrEmpty(gdId))
                {
                    App.Current?.Dispatcher?.Invoke(() => GoogleDriveClientId = gdId);
                }

                var gdSec = await _databaseService.GetSettingAsync("gdrive_client_secret");
                if (!string.IsNullOrEmpty(gdSec))
                {
                    App.Current?.Dispatcher?.Invoke(() => GoogleDriveClientSecret = gdSec);
                }
            }
            catch { }
        });
    }

    public async Task LoadInitialDataAsync()
    {
        try
        {
            ReloadSettingsFromConfigCache();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tải dữ liệu ban đầu: {ex.Message}";
        }
    }

    /// <summary>
    /// Đọc cấu hình mới nhất từ bộ nhớ đệm RAM của AppConfigService khi chuyển tab hoặc khởi tạo
    /// </summary>
    public void ReloadSettingsFromConfigCache()
    {
        var config = AppConfigService.GetConfig();
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var defaultBackupsFolder = Path.Combine(baseDir, "Backups");
        var defaultDbPath = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "save_backup.db");

        BackupDestinationRoot = !string.IsNullOrWhiteSpace(config.BackupRootDirectory)
            ? config.BackupRootDirectory
            : defaultBackupsFolder;

        CreateTimestampSubfolder = config.CreateTimestampSubfolder;
        AutoCompressZip = config.AutoCompressZip;

        var targetDb = !string.IsNullOrWhiteSpace(config.DatabasePath)
            ? config.DatabasePath
            : defaultDbPath;

        if (DatabaseLocation != targetDb)
        {
            DatabaseLocation = targetDb;
        }

        // Tự động đồng bộ DatabaseService nếu config DB khác với service hiện tại
        if (_databaseService.DbPath != targetDb && !string.IsNullOrWhiteSpace(targetDb))
        {
            try
            {
                _databaseService = new DatabaseService(targetDb);
                _searchCoordinator = new GameSearchCoordinator(_databaseService);
                _backupService = new BackupService(_databaseService);
                _cloudManager = new CloudManagerService(_databaseService);
                _isCloudLoggedIn = _cloudManager.IsLoggedIn();
                _cloudAccountEmail = _cloudManager.GetSavedUserEmail();
                _ = RefreshHistoryAsync();
            }
            catch { }
        }
    }

    public async Task RefreshHistoryAsync()
    {
        try
        {
            // 1. Tải danh sách Master mới từ bảng SQLite backup_history
            var gameHistories = await _databaseService.GetGameHistoriesAsync();
            GameHistories.Clear();
            foreach (var gh in gameHistories)
            {
                GameHistories.Add(gh);
            }

            // Đồng bộ SelectedGameHistory
            if (SelectedGameHistory != null)
            {
                var updatedGh = GameHistories.FirstOrDefault(g => g.Id == SelectedGameHistory.Id || g.GameName.Equals(SelectedGameHistory.GameName, StringComparison.OrdinalIgnoreCase));
                if (updatedGh != null)
                {
                    SelectedGameHistory = updatedGh;
                }
                else
                {
                    SelectedGameHistory = null;
                    if (IsHistoryDetailsModalOpen)
                    {
                        ExecuteCloseModal();
                    }
                }
            }

            // 2. Duy trì BackupHistory cũ cho tính tương thích
            var records = await _databaseService.GetBackupHistoryAsync();
            BackupHistory.Clear();
            foreach (var r in records)
            {
                BackupHistory.Add(r);
            }

            var groups = records
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
                .OrderByDescending(s => s.LatestBackupDate)
                .ToList();

            GroupedBackupHistory.Clear();
            foreach (var grp in groups)
            {
                GroupedBackupHistory.Add(grp);
            }

            // Sync currently selected game in modal if open
            if (SelectedGameSummary != null)
            {
                var updated = GroupedBackupHistory.FirstOrDefault(g => g.GameName.Equals(SelectedGameSummary.GameName, StringComparison.OrdinalIgnoreCase));
                if (updated != null)
                {
                    SelectedGameSummary = updated;
                }
                else
                {
                    SelectedGameSummary = null;
                    if (IsHistoryDetailsModalOpen)
                    {
                        ExecuteCloseModal();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi cập nhật lịch sử: {ex.Message}";
        }
    }

    public async Task ExecuteSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        StatusMessage = $"Đang tìm kiếm thông tin cho '{SearchQuery}'...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await _searchCoordinator.SearchAndDetectGameAsync(SearchQuery, progress);

            CurrentGame = result;
            HasGame = true;
            IsGameFoundOnDisk = result.IsFoundOnDisk;
            DetectedFileCount = result.FileCount;
            DetectedSizeFormatted = FormatBytes(result.TotalSizeBytes);

            OnlinePatternsList.Clear();
            foreach (var p in result.RawPatterns)
            {
                OnlinePatternsList.Add(p);
            }

            foreach (var item in DetectedPathItems)
            {
                item.PropertyChanged -= OnDetectedItemPropertyChanged;
            }
            DetectedPathItems.Clear();
            DetectedPathsList.Clear();

            foreach (var item in result.DetectedPathItems)
            {
                item.PropertyChanged += OnDetectedItemPropertyChanged;
                DetectedPathItems.Add(item);
                DetectedPathsList.Add(item.Path);
            }

            UpdateSelectedStats();

            if (result.IsFoundOnDisk)
            {
                StatusMessage = $"✓ Đã tìm thấy {result.FileCount} file ({DetectedSizeFormatted}) tại {result.DetectedPathItems.Count} vị trí!";
            }
            else
            {
                StatusMessage = $"Đã lấy thông tin game nhưng chưa phát hiện thư mục save tương ứng trên máy tính.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tìm kiếm: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task ExecuteBackupAsync()
    {
        if (CurrentGame == null || !IsGameFoundOnDisk) return;

        var selectedPaths = DetectedPathItems.Where(p => p.IsSelected).Select(p => p.Path).ToList();
        if (selectedPaths.Count == 0)
        {
            ShowAppMessage("Chưa chọn thư mục", "Vui lòng tick chọn ít nhất 1 thư mục save để sao lưu!", "Warning");
            return;
        }

        IsBackingUp = true;
        BackupProgressPercent = 0;
        BackupProgressText = "Bắt đầu sao lưu...";

        ActiveProgressTitle = $"Đang sao lưu: {CurrentGame.GameName}";
        ActiveProgressMessage = "Bắt đầu sao lưu...";
        ActiveProgressPercent = 0;
        ActiveProgressSubText = string.Empty;
        IsProgressModalOpen = true;

        try
        {
            var settings = new AppSettings
            {
                BackupRootDirectory = BackupDestinationRoot,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                AutoCompressZip = AutoCompressZip
            };

            var progress = new Progress<BackupProgress>(p =>
            {
                BackupProgressPercent = p.Percent;
                BackupProgressText = p.Message;
                ActiveProgressPercent = p.Percent;
                ActiveProgressMessage = p.Message;
                ActiveProgressSubText = string.IsNullOrEmpty(p.SpeedText) ? string.Empty : $"Tốc độ: {p.SpeedText}";
            });

            var record = await _backupService.BackupGameAsync(CurrentGame, settings, selectedPaths, progress);
            LastBackupPath = record.BackupPath;

            StatusMessage = $"✓ Sao lưu thành công {record.FileCount} file ({record.FormattedSize}) từ {selectedPaths.Count} vị trí vào: {record.BackupPath}";
            await RefreshHistoryAsync();

            IsProgressModalOpen = false;
            ShowAppMessage("Sao lưu hoàn tất", $"Sao lưu game '{CurrentGame.GameName}' thành công!\n\nSố vị trí đã lưu: {selectedPaths.Count}\nVị trí sao lưu: {record.BackupPath}\nDung lượng: {record.FormattedSize}", "Success");
        }
        catch (Exception ex)
        {
            IsProgressModalOpen = false;
            StatusMessage = $"Lỗi sao lưu: {ex.Message}";
            ShowAppMessage("Lỗi sao lưu", "Đã xảy ra lỗi trong quá trình sao lưu.", "Error", ex.ToString());
        }
        finally
        {
            IsBackingUp = false;
            IsProgressModalOpen = false;
        }
    }

    public void ExecuteOpenBackupFolder()
    {
        var path = LastBackupPath ?? BackupDestinationRoot;
        if (File.Exists(path))
        {
            // Open parent directory with file selected
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        else if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
    }

    public async Task ExecuteOpenGameDetailsAsync(object? param)
    {
        GameHistoryEntry? gameHistory = null;
        if (param is GameHistoryEntry ghe)
        {
            gameHistory = ghe;
        }
        else if (param is GameBackupSummary gbs)
        {
            gameHistory = GameHistories.FirstOrDefault(g => g.GameName.Equals(gbs.GameName, StringComparison.OrdinalIgnoreCase));
            SelectedGameSummary = gbs;
        }
        else
        {
            gameHistory = SelectedGameHistory;
        }

        if (gameHistory == null && SelectedGameSummary != null)
        {
            gameHistory = GameHistories.FirstOrDefault(g => g.GameName.Equals(SelectedGameSummary.GameName, StringComparison.OrdinalIgnoreCase));
        }

        if (gameHistory == null) return;
        SelectedGameHistory = gameHistory;

        try
        {
            // Tải chi tiết snapshot 100% từ Database SQLite, KHÔNG quét đĩa hay file zip
            var details = await _databaseService.GetHistoryDetailsByGameIdAsync(gameHistory.Id);
            CurrentHistoryDetails.Clear();
            foreach (var d in details)
            {
                CurrentHistoryDetails.Add(d);
            }

            IsRestoreModalOpen = false;
            IsHistoryDetailsModalOpen = true;
            IsModalOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi mở chi tiết lịch sử: {ex.Message}";
            ShowAppMessage("Lỗi tải chi tiết", $"Không thể tải chi tiết bản sao lưu:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public void ExecuteOpenGameDetails(GameBackupSummary? summary)
    {
        summary ??= SelectedGameSummary;
        if (summary == null) return;

        SelectedGameSummary = summary;
        _ = ExecuteOpenGameDetailsAsync(summary);
    }

    public void ExecuteLoadBackupPathsFromHistory(object? param)
    {
        List<string> paths = new();
        string gameName = string.Empty;

        if (param is BackupHistoryDetail detail)
        {
            gameName = detail.GameName;
            paths = detail.SavePathsList;
        }
        else if (param is GameHistoryEntry ghe)
        {
            gameName = ghe.GameName;
            paths = ghe.SavePathsList;
        }
        else if (param is BackupRecord br)
        {
            gameName = br.GameName;
            paths = br.SavePathsList;
            if (paths.Count == 0 && br.SourcePathsList.Count > 0)
            {
                paths = br.SourcePathsList;
            }
        }
        else if (param is GameBackupSummary gbs)
        {
            var record = gbs.LatestRecord ?? gbs.Records.FirstOrDefault();
            if (record != null)
            {
                gameName = record.GameName;
                paths = record.SavePathsList.Count > 0 ? record.SavePathsList : record.SourcePathsList;
            }
        }
        else if (SelectedHistoryDetail != null)
        {
            gameName = SelectedHistoryDetail.GameName;
            paths = SelectedHistoryDetail.SavePathsList;
        }
        else if (SelectedGameHistory != null)
        {
            gameName = SelectedGameHistory.GameName;
            paths = SelectedGameHistory.SavePathsList;
        }

        if (string.IsNullOrWhiteSpace(gameName) || paths.Count == 0)
        {
            ShowAppMessage("Thông báo", "Không tìm thấy thông tin vị trí sao lưu (save_paths) nào!", "Warning");
            return;
        }

        ApplyPathsToTab1(gameName, paths, "Lịch sử sao lưu (save_paths)");
        StatusMessage = $"✓ Đã nạp {DetectedPathItems.Count} vị trí save của '{gameName}'. Sẵn sàng sao lưu ngay!";
    }

    public void ExecuteLoadBackupPathsFromManifest(object? param)
    {
        // ManifestJson được lưu thẳng vào database ở trường ManifestJson
        ExecuteLoadBackupPathsFromHistory(param);
    }

    private void ApplyPathsToTab1(string gameName, List<string> paths, string sourceLabel)
    {
        SearchQuery = gameName;
        DetectedPathsList.Clear();
        DetectedPathItems.Clear();

        int totalFoundFiles = 0;
        long totalFoundBytes = 0;

        foreach (var p in paths)
        {
            int fileCount = 0;
            long totalBytes = 0;

            if (Directory.Exists(p))
            {
                try
                {
                    var files = Directory.GetFiles(p, "*", SearchOption.AllDirectories);
                    fileCount = files.Length;
                    totalBytes = files.Sum(f => new FileInfo(f).Length);
                }
                catch { }
            }
            else if (File.Exists(p))
            {
                fileCount = 1;
                totalBytes = new FileInfo(p).Length;
            }

            totalFoundFiles += fileCount;
            totalFoundBytes += totalBytes;

            var item = new DetectedPathItem
            {
                Path = p,
                FileCount = fileCount,
                TotalSizeBytes = totalBytes,
                IsSelected = true
            };
            item.PropertyChanged += OnDetectedItemPropertyChanged;
            DetectedPathItems.Add(item);
            DetectedPathsList.Add(p);
        }

        CurrentGame = new GameSaveInfo
        {
            GameName = gameName,
            NormalizedName = gameName.ToLowerInvariant(),
            Source = sourceLabel,
            DetectedPathsOnDisk = paths,
            DetectedPathItems = DetectedPathItems.ToList(),
            TotalSizeBytes = totalFoundBytes,
            FileCount = totalFoundFiles,
            LastScanned = DateTime.Now
        };

        HasGame = true;
        IsGameFoundOnDisk = DetectedPathItems.Any(p => Directory.Exists(p.Path) || File.Exists(p.Path));
        UpdateSelectedStats();

        // Close modals if open
        IsModalOpen = false;
        IsHistoryDetailsModalOpen = false;
        IsRestoreModalOpen = false;

        // Switch to Tab 1 (Search & Backup)
        SelectedTabIndex = 0;
    }

    public void ExecuteCloseModal()
    {
        if (IsModalRestoring || IsConfirmModalDeleting || IsCloudSyncing) return;
        IsHistoryDetailsModalOpen = false;
        IsRestoreModalOpen = false;
        IsConfirmModalOpen = false;
        IsRestoreSourceModalOpen = false;
        IsSyncCloudSelectModalOpen = false;
        IsMessageModalOpen = false;
        IsModalOpen = false;
    }

    public void ExecuteCloseRestoreModal()
    {
        if (IsModalRestoring) return;

        IsRestoreModalOpen = false;
        if (SelectedGameHistory != null || SelectedGameSummary != null)
        {
            IsHistoryDetailsModalOpen = true;
        }
        else
        {
            UpdateMasterModalState();
        }
    }

    public void ExecuteOpenRestoreModal(object? param)
    {
        BackupHistoryDetail? detail = null;
        BackupRecord? record = null;

        if (param is BackupHistoryDetail bhd)
        {
            detail = bhd;
        }
        else if (param is BackupRecord br)
        {
            record = br;
        }
        else if (param is GameBackupSummary gbs)
        {
            record = gbs.LatestRecord ?? gbs.Records.FirstOrDefault();
        }
        else if (SelectedHistoryDetail != null)
        {
            detail = SelectedHistoryDetail;
        }
        else
        {
            record = SelectedHistoryRecord ?? SelectedGameSummary?.LatestRecord;
        }

        if (detail == null && record == null)
        {
            ShowAppMessage("Thông báo", "Vui lòng chọn một bản sao lưu để khôi phục!", "Info");
            return;
        }

        // Nếu bản sao lưu này đã được đồng bộ lên Cloud, hiển thị Modal chọn nguồn (Local / Google Drive / OneDrive)
        if (detail != null && detail.IsCloudSynced)
        {
            PendingRestoreDetail = detail;
            IsPendingDetailSyncedToGoogleDrive = detail.IsSyncedTo("GoogleDrive");
            IsPendingDetailSyncedToOneDrive = detail.IsSyncedTo("OneDrive");
            RestoreSourceModalTitle = $"Chọn nguồn khôi phục cho '{detail.GameName}'";
            RestoreSourceModalMessage = $"Bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss} ({detail.FormattedSize}) đã được đồng bộ lên {detail.CloudProvider ?? "Cloud"}.\n\nBạn muốn khôi phục dữ liệu từ nguồn nào?";
            IsRestoreSourceModalOpen = true;
            IsModalOpen = true;
            return;
        }

        OpenRestoreModalInternal(detail, record, restoreFromCloud: false);
    }

    public void ExecuteConfirmRestoreFromLocal()
    {
        IsRestoreSourceModalOpen = false;
        if (PendingRestoreDetail != null)
        {
            OpenRestoreModalInternal(PendingRestoreDetail, null, restoreFromCloud: false);
        }
    }

    public void ExecuteConfirmRestoreFromCloud()
    {
        var provider = IsPendingDetailSyncedToOneDrive && !IsPendingDetailSyncedToGoogleDrive ? "OneDrive" : "GoogleDrive";
        ExecuteConfirmRestoreFromCloudProvider(provider);
    }

    public void ExecuteConfirmRestoreFromCloudProvider(string providerName)
    {
        IsRestoreSourceModalOpen = false;
        if (PendingRestoreDetail != null)
        {
            _cloudManager.SetActiveProvider(providerName);
            OpenRestoreModalInternal(PendingRestoreDetail, null, restoreFromCloud: true);
        }
    }

    public void ExecuteCancelRestoreSourceModal()
    {
        IsRestoreSourceModalOpen = false;
        PendingRestoreDetail = null;
        UpdateMasterModalState();
    }

    private void OpenRestoreModalInternal(BackupHistoryDetail? detail, BackupRecord? record, bool restoreFromCloud)
    {
        try
        {
            _activeRestoreFromCloud = restoreFromCloud;
            OnPropertyChanged(nameof(ActiveRestoreFromCloud));

            List<RestoreItemTarget> items;
            if (detail != null)
            {
                // Đọc 100% từ SQLite ManifestJson, không mở tệp zip / ổ đĩa
                items = _backupService.GetRestoreItemsFromBackup(detail);
                ActiveRestoreDetail = detail;
                ActiveRestoreRecord = null;
            }
            else
            {
                items = _backupService.GetRestoreItemsFromBackup(record!);
                ActiveRestoreRecord = record;
                ActiveRestoreDetail = null;
            }

            if (items.Count == 0)
            {
                ShowAppMessage("Thông báo", "Không tìm thấy thông tin vị trí lưu nào trong bản sao lưu!", "Warning");
                return;
            }

            ActiveRestoreItems.Clear();
            foreach (var item in items)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RestoreItemTarget.IsSelected) || e.PropertyName == nameof(RestoreItemTarget.RestoreDestinationPath))
                    {
                        UpdateModalRestoreSelectedCount();
                    }
                };
                ActiveRestoreItems.Add(item);
            }

            UpdateModalRestoreSelectedCount();
            IsModalRestoring = false;
            ModalRestoreProgressPercent = 0;
            ModalRestoreProgressMessage = string.Empty;

            IsHistoryDetailsModalOpen = false;
            IsRestoreModalOpen = true;
            IsModalOpen = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi chuẩn bị khôi phục: {ex.Message}";
            ShowAppMessage("Lỗi chuẩn bị khôi phục", $"Không thể đọc thông tin vị trí lưu:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public async Task ExecuteModalRestoreConfirmAsync()
    {
        if (ActiveRestoreDetail == null && ActiveRestoreRecord == null) return;

        var activeItems = ActiveRestoreItems
            .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.RestoreDestinationPath))
            .ToList();

        if (activeItems.Count == 0)
        {
            ShowAppMessage("Chưa chọn", "Vui lòng chọn ít nhất 1 vị trí lưu để khôi phục!", "Warning");
            return;
        }

        string gameName = ActiveRestoreDetail?.GameName ?? ActiveRestoreRecord?.GameName ?? "game";

        IsModalRestoring = true;
        ModalRestoreProgressPercent = 0;
        ModalRestoreProgressMessage = "Đang bắt đầu khôi phục...";

        ActiveProgressTitle = $"Đang khôi phục save: {gameName}";
        ActiveProgressMessage = "Đang chuẩn bị khôi phục...";
        ActiveProgressPercent = 0;
        ActiveProgressSubText = string.Empty;
        IsProgressModalOpen = true;

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                ModalRestoreProgressPercent = p.Percent;
                ModalRestoreProgressMessage = string.IsNullOrEmpty(p.Message) ? "Đang khôi phục..." : p.Message;
                ActiveProgressPercent = p.Percent;
                ActiveProgressMessage = string.IsNullOrEmpty(p.Message) ? "Đang khôi phục..." : p.Message;
                ActiveProgressSubText = string.IsNullOrEmpty(p.SpeedText) ? string.Empty : $"Tốc độ: {p.SpeedText}";
            });

            if (ActiveRestoreDetail != null)
            {
                await _backupService.RestoreAsync(ActiveRestoreDetail, activeItems, _activeRestoreFromCloud, _cloudManager.CurrentProvider, progress);
            }
            else if (ActiveRestoreRecord != null)
            {
                await _backupService.RestoreAsync(ActiveRestoreRecord, activeItems, progress);
            }

            IsProgressModalOpen = false;
            StatusMessage = $"✓ Đã khôi phục thành công save game cho '{gameName}'{(_activeRestoreFromCloud ? " từ Cloud" : " từ local")}!";
            ShowAppMessage("Khôi phục thành công", $"Khôi phục thành công {activeItems.Count} vị trí lưu cho game '{gameName}'{(_activeRestoreFromCloud ? " trực tiếp từ Cloud!" : " từ bộ nhớ cục bộ.")}", "Success");

            IsRestoreModalOpen = false;
            if (SelectedGameHistory != null || SelectedGameSummary != null)
            {
                IsHistoryDetailsModalOpen = true;
            }
            else
            {
                IsModalOpen = false;
            }

            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            IsProgressModalOpen = false;
            ModalRestoreProgressMessage = $"Lỗi: {ex.Message}";
            ShowAppMessage("Lỗi khôi phục", $"Đã xảy ra lỗi trong quá trình khôi phục save:\n{ex.Message}", "Error", ex.ToString());
        }
        finally
        {
            IsModalRestoring = false;
            IsProgressModalOpen = false;
        }
    }

    public void RequestDeleteHistoryDetail(BackupHistoryDetail? detail)
    {
        detail ??= SelectedHistoryDetail;
        if (detail == null) return;

        ConfirmModalTitle = "Xác nhận xóa bản sao lưu";
        ConfirmModalMessage = $"Bạn có chắc chắn muốn xóa bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss} ({detail.FormattedSize}) không?\n\nTệp dữ liệu trên đĩa sẽ bị xóa vĩnh viễn!";
        ConfirmModalTargetPath = detail.BackupPath;

        ShowDeleteCloudOption = detail.IsCloudSynced;
        DeleteAlsoFromCloud = false;
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;

        _pendingConfirmAction = async () =>
        {
            try
            {
                IsConfirmModalDeleting = true;
                ConfirmModalProgressPercent = 0;
                ConfirmModalProgressText = "Đang xóa dữ liệu...";

                var progress = new Progress<BackupProgress>(p =>
                {
                    ConfirmModalProgressPercent = p.Percent;
                    ConfirmModalProgressText = p.Message;
                });

                await _backupService.DeleteSnapshotWithProgressAsync(detail, DeleteAlsoFromCloud, _cloudManager.CurrentProvider, progress);

                // Nạp lại danh sách CurrentHistoryDetails trực tiếp từ SQLite Database
                var freshDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(detail.GameHistoryId);
                CurrentHistoryDetails.Clear();
                foreach (var d in freshDetails)
                {
                    CurrentHistoryDetails.Add(d);
                }

                await RefreshHistoryAsync();

                if (CurrentHistoryDetails.Count == 0)
                {
                    ExecuteCloseModal();
                }

                StatusMessage = $"✓ Đã xóa vĩnh viễn bản sao lưu snapshot #{detail.Id}{(DeleteAlsoFromCloud ? " (kèm tệp trên Cloud)" : "")}.";
                ShowAppMessage("Đã xóa bản sao lưu", $"Bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss} đã được xóa thành công{(DeleteAlsoFromCloud ? " trên cả đĩa và Cloud." : ".")}", "Success");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi xóa bản sao lưu: {ex.Message}";
                ShowAppMessage("Lỗi xóa bản sao lưu", $"Không thể xóa bản sao lưu #{detail.Id}:\n{ex.Message}", "Error", ex.ToString());
            }
            finally
            {
                IsConfirmModalDeleting = false;
            }
        };

        IsConfirmModalOpen = true;
        IsModalOpen = true;
    }

    public void RequestDeleteGameHistory(GameHistoryEntry? entry)
    {
        entry ??= SelectedGameHistory;
        if (entry == null) return;

        ConfirmModalTitle = "Xác nhận xóa toàn bộ lịch sử game";
        ConfirmModalMessage = $"Bạn có chắc chắn muốn xóa toàn bộ lịch sử và TẤT CẢ các tệp sao lưu của game '{entry.GameName}' ({entry.BackupCount} bản sao lưu)?\n\nMọi tệp và thư mục sao lưu liên quan trên ổ đĩa sẽ bị xóa vĩnh viễn!";
        ConfirmModalTargetPath = entry.LatestBackupPath;

        ShowDeleteCloudOption = true;
        DeleteAlsoFromCloud = false;
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;

        _pendingConfirmAction = async () =>
        {
            try
            {
                IsConfirmModalDeleting = true;
                ConfirmModalProgressPercent = 0;
                ConfirmModalProgressText = "Đang xóa toàn bộ lịch sử game...";

                var progress = new Progress<BackupProgress>(p =>
                {
                    ConfirmModalProgressPercent = p.Percent;
                    ConfirmModalProgressText = p.Message;
                });

                await _backupService.DeleteGameHistoryWithProgressAsync(entry, DeleteAlsoFromCloud, _cloudManager.CurrentProvider, progress);

                if (SelectedGameHistory?.Id == entry.Id)
                {
                    ExecuteCloseModal();
                }

                await RefreshHistoryAsync();

                StatusMessage = $"✓ Đã xóa toàn bộ lịch sử và tệp sao lưu của game '{entry.GameName}'.";
                ShowAppMessage("Đã xóa lịch sử game", $"Đã xóa thành công toàn bộ lịch sử và tệp sao lưu của game '{entry.GameName}'{(DeleteAlsoFromCloud ? " (bao gồm trên Cloud)" : "")}.", "Success");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi xóa lịch sử game: {ex.Message}";
                ShowAppMessage("Lỗi xóa lịch sử", $"Không thể xóa lịch sử game '{entry.GameName}':\n{ex.Message}", "Error", ex.ToString());
            }
            finally
            {
                IsConfirmModalDeleting = false;
            }
        };

        IsConfirmModalOpen = true;
        IsModalOpen = true;
    }

    public async Task ExecuteConfirmModalActionAsync()
    {
        if (IsConfirmModalDeleting) return;
        var action = _pendingConfirmAction;
        if (action != null)
        {
            await action();
        }
        _pendingConfirmAction = null;
        IsConfirmModalOpen = false;
        UpdateMasterModalState();
    }

    public void ExecuteCancelConfirmModal()
    {
        if (IsConfirmModalDeleting) return;
        _pendingConfirmAction = null;
        IsConfirmModalOpen = false;
        UpdateMasterModalState();
    }

    public void ExecuteModalSelectAllRestoreItems()
    {
        foreach (var item in ActiveRestoreItems) item.IsSelected = true;
        UpdateModalRestoreSelectedCount();
    }

    public void ExecuteModalDeselectAllRestoreItems()
    {
        foreach (var item in ActiveRestoreItems) item.IsSelected = false;
        UpdateModalRestoreSelectedCount();
    }

    public void ExecuteModalResetRestorePaths()
    {
        foreach (var item in ActiveRestoreItems)
        {
            item.RestoreDestinationPath = item.OriginalSourcePath;
        }
    }

    public void ExecuteModalToggleRestoreItem(RestoreItemTarget? item)
    {
        if (item == null || IsModalRestoring) return;
        item.IsSelected = !item.IsSelected;
        UpdateModalRestoreSelectedCount();
    }

    public void ExecuteModalBrowseRestoreDest(RestoreItemTarget? item)
    {
        if (item == null || IsModalRestoring) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục đích để khôi phục save game vào",
            InitialDirectory = Directory.Exists(item.RestoreDestinationPath) ? item.RestoreDestinationPath : null
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            item.RestoreDestinationPath = dialog.FolderName;
        }
    }

    private void UpdateModalRestoreSelectedCount()
    {
        var selected = ActiveRestoreItems.Count(i => i.IsSelected);
        ModalRestoreSelectedCountText = $"Đã chọn {selected} / {ActiveRestoreItems.Count} vị trí lưu";
        CanConfirmModalRestore = selected > 0;
        OnPropertyChanged(nameof(CanConfirmModalRestore));
        OnPropertyChanged(nameof(ModalRestoreSelectedCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    public void ExecuteDeleteRecord(BackupRecord? record)
    {
        record ??= SelectedHistoryRecord;
        if (record == null) return;

        ConfirmModalTitle = "Xác nhận xóa bản ghi";
        ConfirmModalMessage = $"Xóa bản ghi lịch sử sao lưu #{record.Id} của game '{record.GameName}'?\n\nĐường dẫn: {record.BackupPath}";
        ConfirmModalTargetPath = record.BackupPath;
        ShowDeleteCloudOption = record.IsCloudSynced;
        DeleteAlsoFromCloud = false;
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;

        _pendingConfirmAction = async () =>
        {
            try
            {
                IsConfirmModalDeleting = true;
                ConfirmModalProgressPercent = 20;
                ConfirmModalProgressText = "Đang xóa dữ liệu...";

                if (DeleteAlsoFromCloud && record.IsCloudSynced && !string.IsNullOrEmpty(record.CloudFileId))
                {
                    ConfirmModalProgressPercent = 40;
                    ConfirmModalProgressText = "Đang xóa tệp trên Cloud...";
                    await _cloudManager.DeleteFileAsync(record.CloudFileId);
                }

                ConfirmModalProgressPercent = 80;
                ConfirmModalProgressText = "Đang cập nhật cơ sở dữ liệu...";
                await _databaseService.DeleteBackupRecordAsync(record.Id);
                await RefreshHistoryAsync();

                ConfirmModalProgressPercent = 100;
                StatusMessage = $"Đã xóa bản ghi #{record.Id}.";
                ShowAppMessage("Đã xóa", $"Đã xóa bản ghi lịch sử #{record.Id}.", "Success");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi xóa: {ex.Message}";
                ShowAppMessage("Lỗi xóa", ex.Message, "Error", ex.ToString());
            }
            finally
            {
                IsConfirmModalDeleting = false;
            }
        };

        IsConfirmModalOpen = true;
        IsModalOpen = true;
    }

    public void ExecuteOpenRecordFolder(BackupRecord? record)
    {
        record ??= SelectedHistoryRecord;
        if (record == null) return;

        if (File.Exists(record.BackupPath))
        {
            Process.Start("explorer.exe", $"/select,\"{record.BackupPath}\"");
        }
        else if (Directory.Exists(record.BackupPath))
        {
            Process.Start("explorer.exe", $"\"{record.BackupPath}\"");
        }
    }

    public void ExecuteBrowseBackupDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu Backup",
            InitialDirectory = Directory.Exists(BackupDestinationRoot) ? BackupDestinationRoot : AppDomain.CurrentDomain.BaseDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            BackupDestinationRoot = dialog.FolderName;
        }
    }

    public void ExecuteBrowseDatabaseFile()
    {
        var currentDir = !string.IsNullOrWhiteSpace(DatabaseLocation) && Directory.Exists(Path.GetDirectoryName(DatabaseLocation))
            ? Path.GetDirectoryName(DatabaseLocation)
            : DatabaseService.GetDefaultProjectRoot();

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn hoặc đặt tên file cơ sở dữ liệu SQLite (.db)",
            Filter = "SQLite Database (*.db)|*.db|Tất cả tệp (*.*)|*.*",
            DefaultExt = ".db",
            CheckFileExists = false,
            InitialDirectory = currentDir
        };

        if (dialog.ShowDialog() == true)
        {
            DatabaseLocation = dialog.FileName;
        }
    }

    public void ExecuteResetDatabaseLocation()
    {
        var defaultPath = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "save_backup.db");
        DatabaseLocation = defaultPath;
    }

    public async Task ExecuteApplyDatabaseLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(DatabaseLocation))
        {
            ShowAppMessage("Cảnh báo", "Vui lòng nhập đường dẫn file cơ sở dữ liệu SQLite hợp lệ!", "Warning");
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(DatabaseLocation);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            AppConfigService.SaveDatabasePath(DatabaseLocation);

            _databaseService = new DatabaseService(DatabaseLocation);
            _searchCoordinator = new GameSearchCoordinator(_databaseService);
            _backupService = new BackupService(_databaseService);
            _cloudManager = new CloudManagerService(_databaseService);
            _isCloudLoggedIn = _cloudManager.IsLoggedIn();
            _cloudAccountEmail = _cloudManager.GetSavedUserEmail();

            await RefreshHistoryAsync();

            StatusMessage = $"✓ Đã chuyển Database sang: {DatabaseLocation}";
            ShowAppMessage("Thành công", $"Đã áp dụng và chuyển cơ sở dữ liệu sang:\n{DatabaseLocation}", "Success");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi cập nhật Database: {ex.Message}";
            ShowAppMessage("Lỗi cơ sở dữ liệu", $"Không thể chuyển đường dẫn Database:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public void ExecuteAddCustomPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu save của game trên máy"
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            var folder = dialog.FolderName.Trim();
            var existing = DetectedPathItems.FirstOrDefault(p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                int fileCount = 0;
                long totalBytes = 0;
                if (Directory.Exists(folder))
                {
                    try
                    {
                        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                        fileCount = files.Length;
                        totalBytes = files.Sum(f => new FileInfo(f).Length);
                    }
                    catch { }
                }

                var newItem = new DetectedPathItem
                {
                    Path = folder,
                    FileCount = fileCount,
                    TotalSizeBytes = totalBytes,
                    IsSelected = true
                };
                newItem.PropertyChanged += OnDetectedItemPropertyChanged;
                DetectedPathItems.Add(newItem);
                if (!DetectedPathsList.Contains(folder))
                {
                    DetectedPathsList.Add(folder);
                }
            }
            else
            {
                existing.IsSelected = true;
            }

            if (CurrentGame != null)
            {
                if (!CurrentGame.DetectedPathsOnDisk.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    CurrentGame.DetectedPathsOnDisk.Add(folder);
                }
            }

            IsGameFoundOnDisk = true;
            HasGame = true;
            UpdateSelectedStats();
        }
    }

    public void ExecuteSelectAllPaths()
    {
        foreach (var item in DetectedPathItems)
        {
            item.IsSelected = true;
        }
        UpdateSelectedStats();
    }

    public void ExecuteDeselectAllPaths()
    {
        foreach (var item in DetectedPathItems)
        {
            item.IsSelected = false;
        }
        UpdateSelectedStats();
    }

    public void ExecuteOpenSpecificDetectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            else if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                ShowAppMessage("Không tìm thấy", $"Đường dẫn không tồn tại trên máy tính:\n{path}", "Warning");
            }
        }
        catch (Exception ex)
        {
            ShowAppMessage("Lỗi mở thư mục", ex.Message, "Error");
        }
    }

    public void ExecuteRemoveDetectedPath(DetectedPathItem? item)
    {
        if (item == null) return;

        item.PropertyChanged -= OnDetectedItemPropertyChanged;
        DetectedPathItems.Remove(item);
        DetectedPathsList.Remove(item.Path);

        if (CurrentGame != null)
        {
            CurrentGame.DetectedPathsOnDisk.RemoveAll(p => p.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
            CurrentGame.DetectedPathItems = DetectedPathItems.ToList();
            CurrentGame.TotalSizeBytes = DetectedPathItems.Sum(p => p.TotalSizeBytes);
            CurrentGame.FileCount = DetectedPathItems.Sum(p => p.FileCount);
            DetectedFileCount = CurrentGame.FileCount;
            DetectedSizeFormatted = FormatBytes(CurrentGame.TotalSizeBytes);
        }

        IsGameFoundOnDisk = DetectedPathItems.Any(p => Directory.Exists(p.Path) || File.Exists(p.Path));
        UpdateSelectedStats();
        StatusMessage = $"Đã xóa vị trí lưu: {item.Path}";
    }

    private void OnDetectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetectedPathItem.IsSelected))
        {
            UpdateSelectedStats();
        }
    }

    private void UpdateSelectedStats()
    {
        var selected = DetectedPathItems.Where(p => p.IsSelected).ToList();
        SelectedPathCount = selected.Count;
        SelectedFileCount = selected.Sum(p => p.FileCount);
        SelectedSizeFormatted = FormatBytes(selected.Sum(p => p.TotalSizeBytes));
        HasSelectedPaths = selected.Count > 0;
        OnPropertyChanged(nameof(HasDetectedPaths));
        OnPropertyChanged(nameof(HasNoDetectedPaths));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task ExecuteSaveSettingsAsync()
    {
        try
        {
            var config = AppConfigService.GetConfig();
            config.BackupRootDirectory = BackupDestinationRoot;
            config.CreateTimestampSubfolder = CreateTimestampSubfolder;
            config.AutoCompressZip = AutoCompressZip;

            AppConfigService.SaveConfig(config);

            var settings = new AppSettings
            {
                BackupRootDirectory = BackupDestinationRoot,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                AutoCompressZip = AutoCompressZip
            };

            await _databaseService.SaveSettingsAsync(settings);
            StatusMessage = "✓ Đã lưu cài đặt vào app_config.json và cập nhật bộ nhớ!";
            ShowAppMessage("Lưu cài đặt", "Cài đặt đã được lưu thành công vào app_config.json!", "Success");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi lưu cài đặt: {ex.Message}";
            ShowAppMessage("Lỗi lưu cài đặt", $"Không thể lưu cài đặt:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void UpdateMasterModalState()
    {
        if (!IsHistoryDetailsModalOpen &&
            !IsRestoreModalOpen &&
            !IsConfirmModalOpen &&
            !IsRestoreSourceModalOpen &&
            !IsSyncCloudSelectModalOpen &&
            !IsMessageModalOpen &&
            !IsProgressModalOpen)
        {
            IsModalOpen = false;
        }
    }

    private static string CleanErrorMessage(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "Lỗi không xác định.";
        var clean = msg.Trim();
        if (clean.Contains('{') && clean.Contains('}'))
        {
            try
            {
                var jsonStart = clean.IndexOf('{');
                var jsonEnd = clean.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var prefix = clean.Substring(0, jsonStart).Trim(' ', ':');
                    var jsonStr = clean.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    string? extracted = null;
                    if (root.TryGetProperty("error", out var errProp))
                    {
                        if (errProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            extracted = errProp.GetString();
                        else if (errProp.ValueKind == System.Text.Json.JsonValueKind.Object && errProp.TryGetProperty("message", out var mProp))
                            extracted = mProp.GetString();
                    }
                    else if (root.TryGetProperty("message", out var mProp2))
                    {
                        extracted = mProp2.GetString();
                    }

                    if (!string.IsNullOrEmpty(extracted))
                    {
                        return string.IsNullOrEmpty(prefix) ? extracted : $"{prefix}: {extracted}";
                    }
                }
            }
            catch
            {
                // Fallback to basic cleaning
            }
        }
        return clean.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
    }

    public void ShowAppMessage(string title, string content, string type = "Info", string? details = null)
    {
        MessageModalTitle = title;
        MessageModalContent = content;
        MessageModalType = type;
        MessageModalDetails = details;
        OnPropertyChanged(nameof(HasMessageModalDetails));
        IsMessageModalOpen = true;
        IsModalOpen = true;
    }

    public void ExecuteCloseMessageModal()
    {
        IsMessageModalOpen = false;
        UpdateMasterModalState();
    }

    public void OpenSyncCloudSelectModal(BackupHistoryDetail? detail)
    {
        detail ??= SelectedHistoryDetail;
        if (detail == null)
        {
            ShowAppMessage("Chưa chọn bản sao lưu", "Vui lòng chọn 1 bản sao lưu trong danh sách chi tiết để đồng bộ lên Cloud.", "Warning");
            return;
        }

        TargetSyncDetail = detail;
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
        OnPropertyChanged(nameof(GoogleDriveEmail));
        OnPropertyChanged(nameof(IsOneDriveConnected));
        OnPropertyChanged(nameof(OneDriveEmail));
        OnPropertyChanged(nameof(IsTargetSyncedToGoogleDrive));
        OnPropertyChanged(nameof(IsTargetSyncedToOneDrive));
        OnPropertyChanged(nameof(GoogleDriveSyncDateText));
        OnPropertyChanged(nameof(OneDriveSyncDateText));

        IsSyncCloudSelectModalOpen = true;
        IsModalOpen = true;
    }

    public async Task ConfirmSyncToProviderAsync(string providerName)
    {
        IsSyncCloudSelectModalOpen = false;
        if (TargetSyncDetail == null) return;
        await StartSyncToProviderAsync(TargetSyncDetail, providerName);
    }

    public void ExecuteCloseSyncCloudSelectModal()
    {
        IsSyncCloudSelectModalOpen = false;
        TargetSyncDetail = null;
        UpdateMasterModalState();
    }

    public async Task StartSyncToProviderAsync(BackupHistoryDetail detail, string providerName)
    {
        var provider = _cloudManager.GetProvider(providerName);
        if (provider == null || !provider.IsAuthenticated)
        {
            ShowAppMessage("Chưa kết nối Cloud", $"Bạn chưa kết nối tài khoản {providerName}.\n\nVui lòng vào tab Cài đặt để kết nối trước khi đồng bộ!", "Warning");
            return;
        }

        _cloudManager.SetActiveProvider(providerName);
        IsCloudSyncing = true;
        CloudSyncProgressPercent = 0;
        CloudSyncProgressText = "Đang chuẩn bị tải lên Cloud...";

        ActiveProgressTitle = $"Đồng bộ Cloud: {detail.GameName}";
        ActiveProgressMessage = $"Đang chuẩn bị tải lên {provider.DisplayName}...";
        ActiveProgressPercent = 0;
        ActiveProgressSubText = string.Empty;
        IsProgressModalOpen = true;

        _syncCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                CloudSyncProgressPercent = p.Percent;
                CloudSyncProgressText = string.IsNullOrEmpty(p.SpeedText) ? p.Message : $"{p.Message} ({p.SpeedText})";
                ActiveProgressPercent = p.Percent;
                ActiveProgressMessage = p.Message;
                ActiveProgressSubText = string.IsNullOrEmpty(p.SpeedText) ? string.Empty : $"Tốc độ: {p.SpeedText}";
            });

            var result = await _backupService.SyncSnapshotToCloudAsync(detail, provider, progress, _syncCts.Token);

            IsProgressModalOpen = false;

            if (result.Success)
            {
                StatusMessage = $"✓ Đồng bộ Cloud thành công bản sao lưu #{detail.Id} lên {provider.DisplayName}!";
                ShowAppMessage("Đồng bộ Cloud thành công", $"Bản sao lưu game '{detail.GameName}' ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss} đã được tải lên {provider.DisplayName} thành công!\n\nTên file Cloud: {result.FileName}\nProvider: {provider.DisplayName}", "Success");

                var freshDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(detail.GameHistoryId);
                CurrentHistoryDetails.Clear();
                foreach (var d in freshDetails)
                {
                    CurrentHistoryDetails.Add(d);
                }
                await RefreshHistoryAsync();
            }
            else
            {
                var cleanErr = CleanErrorMessage(result.ErrorMessage);
                StatusMessage = $"Đồng bộ Cloud thất bại: {cleanErr}";
                ShowAppMessage("Đồng bộ Cloud thất bại", $"Không thể tải bản sao lưu lên {provider.DisplayName}:\n{cleanErr}", "Error", result.ErrorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            IsProgressModalOpen = false;
            StatusMessage = "Đã hủy quá trình đồng bộ Cloud theo yêu cầu.";
            ShowAppMessage("Đã hủy đồng bộ", "Quá trình tải dữ liệu lên Cloud đã được hủy an toàn.", "Info");
        }
        catch (Exception ex)
        {
            IsProgressModalOpen = false;
            var cleanMsg = CleanErrorMessage(ex.Message);
            StatusMessage = $"Lỗi đồng bộ Cloud: {cleanMsg}";
            ShowAppMessage("Lỗi đồng bộ Cloud", $"Đã xảy ra lỗi trong quá trình tải lên Cloud:\n{cleanMsg}", "Error", ex.ToString());
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            IsCloudSyncing = false;
            IsProgressModalOpen = false;
        }
    }

    public void ExecuteCancelSync()
    {
        if (_syncCts != null && !_syncCts.IsCancellationRequested)
        {
            _syncCts.Cancel();
            ActiveProgressMessage = "Đang hủy quá trình tải lên...";
        }
    }

    public void ExecuteOpenCloudWebView(object? parameter)
    {
        string? url = null;
        if (parameter is CloudSyncInfo syncInfo)
        {
            url = syncInfo.WebViewUrl;
        }
        else if (parameter is BackupHistoryDetail detail)
        {
            url = detail.CloudSyncList.FirstOrDefault()?.WebViewUrl;
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowAppMessage("Không thể mở trình duyệt", ex.Message, "Error");
            }
        }
    }

    public async Task ExecuteConnectCloudAccountAsync()
    {
        try
        {
            if (string.Equals(_cloudManager.ActiveProviderName, "OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                var odId = await _databaseService.GetSettingAsync("onedrive_client_id");
                if (string.IsNullOrWhiteSpace(odId))
                {
                    ShowAppMessage(
                        "Cần OneDrive Application (Client) ID",
                        "Để kết nối Microsoft OneDrive, bạn cần nhập Client ID từ Azure Portal:\n\n" +
                        "1. Mở Azure Portal (bấm nút '🌐 Mở Azure Portal' bên dưới)\n" +
                        "2. Vào App registrations > New registration\n" +
                        "3. Loại tài khoản: Chọn 'Accounts in any organizational directory and personal Microsoft accounts'\n" +
                        "4. Redirect URI: Chọn platform 'Mobile and desktop applications' > tích/nhập: http://localhost\n" +
                        "5. Copy 'Application (client) ID' dán vào ô 'OneDrive Client ID' và bấm 'Lưu cấu hình API'.",
                        "Warning");
                    return;
                }
            }

            StatusMessage = $"Đang mở trình duyệt để xác thực {_cloudManager.ActiveProviderName}... Vui lòng duyệt đăng nhập.";
            var success = await _cloudManager.LoginAsync();
            IsCloudLoggedIn = _cloudManager.IsLoggedIn();
            CloudAccountEmail = _cloudManager.GetSavedUserEmail();

            if (success)
            {
                StatusMessage = $"✓ Đã kết nối {_cloudManager.ActiveProviderName}: {CloudAccountEmail}";
                ShowAppMessage("Kết nối thành công", $"Đã kết nối tài khoản {_cloudManager.ActiveProviderName} thành công!\n\nTài khoản: {CloudAccountEmail}", "Success");
            }
            else
            {
                StatusMessage = $"Kết nối {_cloudManager.ActiveProviderName} thất bại.";
                ShowAppMessage("Kết nối thất bại", $"Không thể kết nối {_cloudManager.ActiveProviderName}. Vui lòng thử lại.", "Error");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi xác thực: {ex.Message}";
            ShowAppMessage("Lỗi kết nối", $"Đã xảy ra lỗi khi kết nối cloud:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public void ExecuteOpenAzurePortalGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://portal.azure.com") { UseShellExecute = true });
            }
            catch { }
        }
    }

    public void ExecuteOpenGoogleCloudConsoleGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://console.cloud.google.com") { UseShellExecute = true });
            }
            catch { }
        }
    }

    public async Task ExecuteSaveCloudApiSettingsAsync()
    {
        try
        {
            await _databaseService.SaveSettingAsync("onedrive_client_id", OneDriveClientId?.Trim() ?? string.Empty);
            await _databaseService.SaveSettingAsync("gdrive_client_id", GoogleDriveClientId?.Trim() ?? string.Empty);
            await _databaseService.SaveSettingAsync("gdrive_client_secret", GoogleDriveClientSecret?.Trim() ?? string.Empty);

            StatusMessage = "✓ Đã lưu cấu hình Cloud API Credentials thành công!";
            ShowAppMessage("Đã lưu API Credentials", "Cấu hình API Credentials đã được lưu vào cơ sở dữ liệu!\n\nBây giờ bạn có thể bấm 'Kết nối tài khoản' để đăng nhập.", "Success");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi lưu Cloud API Credentials: {ex.Message}";
            ShowAppMessage("Lỗi lưu cấu hình", $"Không thể lưu thông tin API:\n{ex.Message}", "Error", ex.ToString());
        }
    }

    public async Task ExecuteDisconnectCloudAccountAsync()
    {
        try
        {
            var provider = _cloudManager.ActiveProviderName;
            _cloudManager.Logout();
            IsCloudLoggedIn = false;
            CloudAccountEmail = null;
            StatusMessage = $"✓ Đã đăng xuất {provider}.";
            ShowAppMessage("Đã đăng xuất", $"Đã ngắt kết nối tài khoản {provider}.", "Info");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi đăng xuất: {ex.Message}";
            ShowAppMessage("Lỗi", $"Không thể đăng xuất: {ex.Message}", "Error", ex.ToString());
        }
    }

    // Properties
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetField(ref _searchQuery, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetField(ref _isSearching, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            var sanitized = value;
            if (!string.IsNullOrEmpty(sanitized))
            {
                sanitized = sanitized.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                while (sanitized.Contains("  "))
                {
                    sanitized = sanitized.Replace("  ", " ");
                }
                sanitized = sanitized.Trim();
            }
            SetField(ref _statusMessage, sanitized ?? string.Empty);
        }
    }

    public GameSaveInfo? CurrentGame
    {
        get => _currentGame;
        set => SetField(ref _currentGame, value);
    }

    public bool HasGame
    {
        get => _hasGame;
        set => SetField(ref _hasGame, value);
    }

    public string DetectedSizeFormatted
    {
        get => _detectedSizeFormatted;
        set => SetField(ref _detectedSizeFormatted, value);
    }

    public int DetectedFileCount
    {
        get => _detectedFileCount;
        set => SetField(ref _detectedFileCount, value);
    }

    public bool IsGameFoundOnDisk
    {
        get => _isGameFoundOnDisk;
        set => SetField(ref _isGameFoundOnDisk, value);
    }

    public string BackupDestinationRoot
    {
        get => _backupDestinationRoot;
        set => SetField(ref _backupDestinationRoot, value);
    }

    public bool CreateTimestampSubfolder
    {
        get => _createTimestampSubfolder;
        set => SetField(ref _createTimestampSubfolder, value);
    }

    public bool AutoCompressZip
    {
        get => _autoCompressZip;
        set => SetField(ref _autoCompressZip, value);
    }

    public bool IsBackingUp
    {
        get => _isBackingUp;
        set
        {
            if (SetField(ref _isBackingUp, value))
            {
                OnPropertyChanged(nameof(IsNotBackingUp));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanCloseModal));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int BackupProgressPercent
    {
        get => _backupProgressPercent;
        set => SetField(ref _backupProgressPercent, value);
    }

    public string BackupProgressText
    {
        get => _backupProgressText;
        set => SetField(ref _backupProgressText, value);
    }

    public string? LastBackupPath
    {
        get => _lastBackupPath;
        set => SetField(ref _lastBackupPath, value);
    }

    public BackupRecord? SelectedHistoryRecord
    {
        get => _selectedHistoryRecord;
        set => SetField(ref _selectedHistoryRecord, value);
    }

    public GameBackupSummary? SelectedGameSummary
    {
        get => _selectedGameSummary;
        set => SetField(ref _selectedGameSummary, value);
    }

    public bool IsModalOpen
    {
        get => _isModalOpen || _isProgressModalOpen;
        set => SetField(ref _isModalOpen, value);
    }

    // Unified busy states for UI disabling and tab locking
    public bool IsBusy => IsBackingUp || IsCloudSyncing || IsModalRestoring;
    public bool IsNotBusy => !IsBusy;
    public bool IsNotBackingUp => !IsBackingUp;
    public bool CanCloseModal => !IsModalRestoring && !IsCloudSyncing;

    // Dedicated Progress Modal Properties
    public bool IsProgressModalOpen
    {
        get => _isProgressModalOpen;
        set
        {
            if (SetField(ref _isProgressModalOpen, value))
            {
                OnPropertyChanged(nameof(IsModalOpen));
            }
        }
    }

    public string ActiveProgressTitle
    {
        get => _activeProgressTitle;
        set => SetField(ref _activeProgressTitle, value);
    }

    public string ActiveProgressMessage
    {
        get => _activeProgressMessage;
        set => SetField(ref _activeProgressMessage, value);
    }

    public int ActiveProgressPercent
    {
        get => _activeProgressPercent;
        set => SetField(ref _activeProgressPercent, value);
    }

    public string ActiveProgressSubText
    {
        get => _activeProgressSubText;
        set => SetField(ref _activeProgressSubText, value);
    }

    public bool IsHistoryDetailsModalOpen
    {
        get => _isHistoryDetailsModalOpen;
        set => SetField(ref _isHistoryDetailsModalOpen, value);
    }

    public bool IsRestoreModalOpen
    {
        get => _isRestoreModalOpen;
        set => SetField(ref _isRestoreModalOpen, value);
    }

    public BackupRecord? ActiveRestoreRecord
    {
        get => _activeRestoreRecord;
        set => SetField(ref _activeRestoreRecord, value);
    }

    public bool IsModalRestoring
    {
        get => _isModalRestoring;
        set
        {
            if (SetField(ref _isModalRestoring, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanCloseModal));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int ModalRestoreProgressPercent
    {
        get => _modalRestoreProgressPercent;
        set => SetField(ref _modalRestoreProgressPercent, value);
    }

    public string ModalRestoreProgressMessage
    {
        get => _modalRestoreProgressMessage;
        set => SetField(ref _modalRestoreProgressMessage, value);
    }

    public string ModalRestoreSelectedCountText
    {
        get => _modalRestoreSelectedCountText;
        set => SetField(ref _modalRestoreSelectedCountText, value);
    }

    public bool CanConfirmModalRestore
    {
        get => _canConfirmModalRestore;
        set
        {
            if (SetField(ref _canConfirmModalRestore, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string DatabaseLocation
    {
        get => _databaseLocation;
        set => SetField(ref _databaseLocation, value);
    }

    public int SelectedPathCount
    {
        get => _selectedPathCount;
        set => SetField(ref _selectedPathCount, value);
    }

    public int SelectedFileCount
    {
        get => _selectedFileCount;
        set => SetField(ref _selectedFileCount, value);
    }

    public string SelectedSizeFormatted
    {
        get => _selectedSizeFormatted;
        set => SetField(ref _selectedSizeFormatted, value);
    }

    public bool HasSelectedPaths
    {
        get => _hasSelectedPaths;
        set => SetField(ref _hasSelectedPaths, value);
    }

    public bool HasDetectedPaths => DetectedPathItems.Count > 0;
    public bool HasNoDetectedPaths => DetectedPathItems.Count == 0;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetField(ref _selectedTabIndex, value))
            {
                // Khi chuyển tab thì đọc config cache trong RAM để luôn có config mới nhất
                ReloadSettingsFromConfigCache();
            }
        }
    }

    public GameHistoryEntry? SelectedGameHistory
    {
        get => _selectedGameHistory;
        set => SetField(ref _selectedGameHistory, value);
    }

    public BackupHistoryDetail? SelectedHistoryDetail
    {
        get => _selectedHistoryDetail;
        set => SetField(ref _selectedHistoryDetail, value);
    }

    public BackupHistoryDetail? ActiveRestoreDetail
    {
        get => _activeRestoreDetail;
        set => SetField(ref _activeRestoreDetail, value);
    }

    // In-App Confirm Modal Properties
    public bool IsConfirmModalOpen
    {
        get => _isConfirmModalOpen;
        set => SetField(ref _isConfirmModalOpen, value);
    }

    public string ConfirmModalTitle
    {
        get => _confirmModalTitle;
        set => SetField(ref _confirmModalTitle, value);
    }

    public string ConfirmModalMessage
    {
        get => _confirmModalMessage;
        set => SetField(ref _confirmModalMessage, value);
    }

    public string ConfirmModalTargetPath
    {
        get => _confirmModalTargetPath;
        set => SetField(ref _confirmModalTargetPath, value);
    }

    // Confirm Modal Delete Options & Progress
    public bool ShowDeleteCloudOption
    {
        get => _showDeleteCloudOption;
        set => SetField(ref _showDeleteCloudOption, value);
    }

    public bool DeleteAlsoFromCloud
    {
        get => _deleteAlsoFromCloud;
        set => SetField(ref _deleteAlsoFromCloud, value);
    }

    public bool IsConfirmModalDeleting
    {
        get => _isConfirmModalDeleting;
        set
        {
            if (SetField(ref _isConfirmModalDeleting, value))
            {
                OnPropertyChanged(nameof(IsNotConfirmModalDeleting));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotConfirmModalDeleting => !_isConfirmModalDeleting;

    public int ConfirmModalProgressPercent
    {
        get => _confirmModalProgressPercent;
        set => SetField(ref _confirmModalProgressPercent, value);
    }

    public string ConfirmModalProgressText
    {
        get => _confirmModalProgressText;
        set => SetField(ref _confirmModalProgressText, value);
    }

    // Universal In-App Message Popup Properties
    public bool IsMessageModalOpen
    {
        get => _isMessageModalOpen;
        set => SetField(ref _isMessageModalOpen, value);
    }

    public string MessageModalTitle
    {
        get => _messageModalTitle;
        set => SetField(ref _messageModalTitle, value);
    }

    public string MessageModalContent
    {
        get => _messageModalContent;
        set => SetField(ref _messageModalContent, value);
    }

    public string MessageModalType
    {
        get => _messageModalType;
        set => SetField(ref _messageModalType, value);
    }

    public string? MessageModalDetails
    {
        get => _messageModalDetails;
        set => SetField(ref _messageModalDetails, value);
    }

    public bool HasMessageModalDetails => !string.IsNullOrWhiteSpace(_messageModalDetails);

    // Cloud Manager Service Properties
    public int SelectedCloudProviderIndex
    {
        get => _selectedCloudProviderIndex;
        set
        {
            if (SetField(ref _selectedCloudProviderIndex, value))
            {
                var target = value == 1 ? "OneDrive" : "GoogleDrive";
                _cloudManager.SetActiveProvider(target);
                IsCloudLoggedIn = _cloudManager.IsLoggedIn();
                CloudAccountEmail = _cloudManager.GetSavedUserEmail();
                OnPropertyChanged(nameof(ActiveCloudProviderName));
                OnPropertyChanged(nameof(IsOneDriveSelected));
                OnPropertyChanged(nameof(IsGoogleDriveSelected));
            }
        }
    }

    public bool IsOneDriveSelected => SelectedCloudProviderIndex == 1;
    public bool IsGoogleDriveSelected => SelectedCloudProviderIndex == 0;

    public string OneDriveClientId
    {
        get => _oneDriveClientId;
        set => SetField(ref _oneDriveClientId, value);
    }

    public string GoogleDriveClientId
    {
        get => _googleDriveClientId;
        set => SetField(ref _googleDriveClientId, value);
    }

    public string GoogleDriveClientSecret
    {
        get => _googleDriveClientSecret;
        set => SetField(ref _googleDriveClientSecret, value);
    }

    public string ActiveCloudProviderName => _cloudManager?.ActiveProviderName ?? "Google Drive";

    public bool IsCloudLoggedIn
    {
        get => _isCloudLoggedIn;
        set
        {
            if (SetField(ref _isCloudLoggedIn, value))
            {
                OnPropertyChanged(nameof(IsCloudLoggedOut));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsCloudLoggedOut => !IsCloudLoggedIn;

    public string? CloudAccountEmail
    {
        get => _cloudAccountEmail;
        set => SetField(ref _cloudAccountEmail, value);
    }

    public bool IsCloudSyncing
    {
        get => _isCloudSyncing;
        set
        {
            if (SetField(ref _isCloudSyncing, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanCloseModal));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int CloudSyncProgressPercent
    {
        get => _cloudSyncProgressPercent;
        set => SetField(ref _cloudSyncProgressPercent, value);
    }

    public string CloudSyncProgressText
    {
        get => _cloudSyncProgressText;
        set => SetField(ref _cloudSyncProgressText, value);
    }

    // Modal 5 - Restore Source Selection
    public bool IsRestoreSourceModalOpen
    {
        get => _isRestoreSourceModalOpen;
        set => SetField(ref _isRestoreSourceModalOpen, value);
    }

    public BackupHistoryDetail? PendingRestoreDetail
    {
        get => _pendingRestoreDetail;
        set => SetField(ref _pendingRestoreDetail, value);
    }

    public string RestoreSourceModalTitle
    {
        get => _restoreSourceModalTitle;
        set => SetField(ref _restoreSourceModalTitle, value);
    }

    public string RestoreSourceModalMessage
    {
        get => _restoreSourceModalMessage;
        set => SetField(ref _restoreSourceModalMessage, value);
    }

    public bool ActiveRestoreFromCloud
    {
        get => _activeRestoreFromCloud;
        set => SetField(ref _activeRestoreFromCloud, value);
    }

    public bool IsPendingDetailSyncedToGoogleDrive
    {
        get => _isPendingDetailSyncedToGoogleDrive;
        set => SetField(ref _isPendingDetailSyncedToGoogleDrive, value);
    }

    public bool IsPendingDetailSyncedToOneDrive
    {
        get => _isPendingDetailSyncedToOneDrive;
        set => SetField(ref _isPendingDetailSyncedToOneDrive, value);
    }

    // Cloud Sync Provider Selection Modal (Modal 7) Properties
    public bool IsSyncCloudSelectModalOpen
    {
        get => _isSyncCloudSelectModalOpen;
        set
        {
            if (SetField(ref _isSyncCloudSelectModalOpen, value))
            {
                OnPropertyChanged(nameof(CanCloseModal));
            }
        }
    }

    public BackupHistoryDetail? TargetSyncDetail
    {
        get => _targetSyncDetail;
        set => SetField(ref _targetSyncDetail, value);
    }

    public bool IsGoogleDriveConnected
    {
        get
        {
            var p = _cloudManager?.GetProvider("GoogleDrive");
            return p != null && p.IsAuthenticated;
        }
    }

    public string GoogleDriveEmail
    {
        get
        {
            var p = _cloudManager?.GetProvider("GoogleDrive");
            return p != null && p.IsAuthenticated ? (p.CurrentAccountEmail ?? "Đã kết nối") : "Chưa kết nối";
        }
    }

    public bool IsOneDriveConnected
    {
        get
        {
            var p = _cloudManager?.GetProvider("OneDrive");
            return p != null && p.IsAuthenticated;
        }
    }

    public string OneDriveEmail
    {
        get
        {
            var p = _cloudManager?.GetProvider("OneDrive");
            return p != null && p.CurrentAccountEmail != null ? p.CurrentAccountEmail : (p != null && p.IsAuthenticated ? "Đã kết nối" : "Chưa kết nối");
        }
    }

    public bool IsTargetSyncedToGoogleDrive => TargetSyncDetail?.IsSyncedTo("GoogleDrive") ?? false;
    public bool IsTargetSyncedToOneDrive => TargetSyncDetail?.IsSyncedTo("OneDrive") ?? false;

    public string GoogleDriveSyncDateText
    {
        get
        {
            var item = TargetSyncDetail?.GetSyncInfo("GoogleDrive");
            return item != null ? $"Đã đồng bộ lúc: {item.SyncDate:dd/MM/yyyy HH:mm:ss}" : "Chưa đồng bộ lên Google Drive";
        }
    }

    public string OneDriveSyncDateText
    {
        get
        {
            var item = TargetSyncDetail?.GetSyncInfo("OneDrive");
            return item != null ? $"Đã đồng bộ lúc: {item.SyncDate:dd/MM/yyyy HH:mm:ss}" : "Chưa đồng bộ lên OneDrive";
        }
    }

    // Commands
    public ICommand SearchCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand OpenSpecificDetectedPathCommand { get; }
    public ICommand RemoveDetectedPathCommand { get; }
    public ICommand RestoreRecordCommand { get; }
    public ICommand DeleteRecordCommand { get; }
    public ICommand OpenRecordFolderCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand BrowseBackupDirectoryCommand { get; }
    public ICommand BrowseDatabaseFileCommand { get; }
    public ICommand ApplyDatabaseLocationCommand { get; }
    public ICommand ResetDatabaseLocationCommand { get; }
    public ICommand AddCustomPathCommand { get; }
    public ICommand SelectAllPathsCommand { get; }
    public ICommand DeselectAllPathsCommand { get; }

    // Modal Commands
    public ICommand OpenGameDetailsCommand { get; }
    public ICommand OpenRestoreModalCommand { get; }
    public ICommand CloseModalCommand { get; }
    public ICommand CloseRestoreModalCommand { get; }
    public ICommand ModalRestoreConfirmCommand { get; }
    public ICommand ModalSelectAllRestoreItemsCommand { get; }
    public ICommand ModalDeselectAllRestoreItemsCommand { get; }
    public ICommand ModalResetRestorePathsCommand { get; }
    public ICommand ModalToggleRestoreItemCommand { get; }
    public ICommand ModalBrowseRestoreDestCommand { get; }
    public ICommand LoadBackupPathsFromHistoryCommand { get; }
    public ICommand LoadBackupPathsFromManifestCommand { get; }
    public ICommand LoadBackupPathsForGameCommand { get; }

    // In-App Confirm & Delete Commands
    public ICommand RequestDeleteHistoryDetailCommand { get; }
    public ICommand RequestDeleteGameHistoryCommand { get; }
    public ICommand ConfirmModalExecuteCommand { get; }
    public ICommand CancelConfirmModalCommand { get; }

    // Universal In-App Message Popup Command
    public ICommand CloseMessageModalCommand { get; }

    // Cloud & Restore Source Modal Commands
    public ICommand SyncSelectedDetailToCloudCommand { get; }
    public ICommand OpenSyncCloudSelectModalCommand { get; }
    public ICommand ConfirmSyncToProviderCommand { get; }
    public ICommand CloseSyncCloudSelectModalCommand { get; }
    public ICommand CancelSyncCommand { get; }
    public ICommand OpenCloudWebViewCommand { get; }
    public ICommand ConnectCloudAccountCommand { get; }
    public ICommand DisconnectCloudAccountCommand { get; }
    public ICommand SaveCloudApiSettingsCommand { get; }
    public ICommand OpenAzurePortalGuideCommand { get; }
    public ICommand OpenGoogleCloudConsoleGuideCommand { get; }
    public ICommand ConfirmRestoreFromLocalCommand { get; }
    public ICommand ConfirmRestoreFromCloudCommand { get; }
    public ICommand ConfirmRestoreFromGoogleDriveCommand { get; }
    public ICommand ConfirmRestoreFromOneDriveCommand { get; }
    public ICommand CancelRestoreSourceModalCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
