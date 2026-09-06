using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private DatabaseService _databaseService;
    private GameSearchCoordinator _searchCoordinator;
    private BackupService _backupService;
    private readonly LudusaviDatabaseService _ludusaviService;

    // Database path property
    private string _databaseLocation = string.Empty;

    // Search properties
    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private bool _forceOnlineSearch;
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
    private bool _autoCompressZip = false;
    private bool _isBackingUp;
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string? _lastBackupPath;

    // Collections
    public ObservableCollection<BackupRecord> BackupHistory { get; } = new();
    public ObservableCollection<GameBackupSummary> GroupedBackupHistory { get; } = new();
    public ObservableCollection<string> PopularGameSuggestions { get; } = new();
    public ObservableCollection<string> DetectedPathsList { get; } = new();
    public ObservableCollection<DetectedPathItem> DetectedPathItems { get; } = new();
    public ObservableCollection<string> OnlinePatternsList { get; } = new();

    private BackupRecord? _selectedHistoryRecord;
    private GameBackupSummary? _selectedGameSummary;

    // In-App Modal States
    private bool _isModalOpen;
    private bool _isHistoryDetailsModalOpen;
    private bool _isRestoreModalOpen;
    private BackupRecord? _activeRestoreRecord;
    public ObservableCollection<RestoreItemTarget> ActiveRestoreItems { get; } = new();
    private bool _isModalRestoring;
    private int _modalRestoreProgressPercent;
    private string _modalRestoreProgressMessage = string.Empty;
    private string _modalRestoreSelectedCountText = string.Empty;
    private bool _canConfirmModalRestore;

    public MainViewModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var defaultBackupsFolder = Path.Combine(baseDir, "Backups");
        Directory.CreateDirectory(defaultBackupsFolder);

        _databaseService = new DatabaseService();
        _databaseLocation = _databaseService.DbPath;
        _searchCoordinator = new GameSearchCoordinator(_databaseService);
        _backupService = new BackupService(_databaseService);
        _ludusaviService = new LudusaviDatabaseService();

        _backupDestinationRoot = defaultBackupsFolder;

        // Load suggestions
        foreach (var g in _ludusaviService.GetAllKnownGameNames().Take(20))
        {
            PopularGameSuggestions.Add(g);
        }

        // Initialize Commands
        SearchCommand = new RelayCommand(async _ => await ExecuteSearchAsync(), _ => !IsSearching && !IsBackingUp && !string.IsNullOrWhiteSpace(SearchQuery));
        BackupCommand = new RelayCommand(async _ => await ExecuteBackupAsync(), _ => !IsBackingUp && HasGame && HasSelectedPaths);
        OpenBackupFolderCommand = new RelayCommand(_ => ExecuteOpenBackupFolder(), _ => !string.IsNullOrEmpty(LastBackupPath));
        OpenSourceFolderCommand = new RelayCommand(_ => ExecuteOpenSourceFolder(), _ => HasGame && DetectedPathItems.Count > 0);
        OpenRestoreModalCommand = new RelayCommand(param => ExecuteOpenRestoreModal(param), _ => !IsBackingUp);
        RestoreRecordCommand = OpenRestoreModalCommand;
        DeleteRecordCommand = new RelayCommand(async param => await ExecuteDeleteRecordAsync(param as BackupRecord));
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
        OpenGameDetailsCommand = new RelayCommand(param => ExecuteOpenGameDetails(param as GameBackupSummary));
        CloseModalCommand = new RelayCommand(_ => ExecuteCloseModal(), _ => !IsModalRestoring);
        CloseRestoreModalCommand = new RelayCommand(_ => ExecuteCloseRestoreModal(), _ => !IsModalRestoring);
        ModalRestoreConfirmCommand = new RelayCommand(async _ => await ExecuteModalRestoreConfirmAsync(), _ => !IsModalRestoring && CanConfirmModalRestore);
        ModalSelectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalSelectAllRestoreItems(), _ => !IsModalRestoring);
        ModalDeselectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalDeselectAllRestoreItems(), _ => !IsModalRestoring);
        ModalResetRestorePathsCommand = new RelayCommand(_ => ExecuteModalResetRestorePaths(), _ => !IsModalRestoring);
        ModalToggleRestoreItemCommand = new RelayCommand(param => ExecuteModalToggleRestoreItem(param as RestoreItemTarget), _ => !IsModalRestoring);
        ModalBrowseRestoreDestCommand = new RelayCommand(param => ExecuteModalBrowseRestoreDest(param as RestoreItemTarget), _ => !IsModalRestoring);
        LoadBackupPathsFromHistoryCommand = new RelayCommand(param => ExecuteLoadBackupPathsFromHistory(param), _ => !IsBackingUp);
        LoadBackupPathsFromManifestCommand = new RelayCommand(param => ExecuteLoadBackupPathsFromManifest(param), _ => !IsBackingUp);
        LoadBackupPathsForGameCommand = LoadBackupPathsFromHistoryCommand;

        // Load initial data
        _ = LoadInitialDataAsync();
    }

    public async Task LoadInitialDataAsync()
    {
        try
        {
            var settings = await _databaseService.LoadSettingsAsync(_backupDestinationRoot);
            BackupDestinationRoot = settings.BackupRootDirectory;
            CreateTimestampSubfolder = settings.CreateTimestampSubfolder;
            AutoCompressZip = settings.AutoCompressZip;

            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tải dữ liệu ban đầu: {ex.Message}";
        }
    }

    public async Task RefreshHistoryAsync()
    {
        try
        {
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
            var result = await _searchCoordinator.SearchAndDetectGameAsync(SearchQuery, ForceOnlineSearch, progress);

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
            MessageBox.Show("Vui lòng tick chọn ít nhất 1 thư mục save để sao lưu!", "Chưa chọn thư mục", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBackingUp = true;
        BackupProgressPercent = 0;
        BackupProgressText = "Bắt đầu sao lưu...";

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
            });

            var record = await _backupService.BackupGameAsync(CurrentGame, settings, selectedPaths, progress);
            LastBackupPath = record.BackupPath;

            StatusMessage = $"✓ Sao lưu thành công {record.FileCount} file ({record.FormattedSize}) từ {selectedPaths.Count} vị trí vào: {record.BackupPath}";
            await RefreshHistoryAsync();

            MessageBox.Show($"Sao lưu game '{CurrentGame.GameName}' thành công!\n\nSố vị trí đã lưu: {selectedPaths.Count}\nVị trí sao lưu: {record.BackupPath}\nDung lượng: {record.FormattedSize}",
                "Sao lưu hoàn tất", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi sao lưu: {ex.Message}";
            MessageBox.Show($"Đã xảy ra lỗi trong quá trình sao lưu:\n{ex.Message}", "Lỗi sao lưu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackingUp = false;
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

    public void ExecuteOpenSourceFolder()
    {
        var target = DetectedPathItems.FirstOrDefault(p => p.IsSelected)?.Path 
            ?? (DetectedPathItems.Count > 0 ? DetectedPathItems[0].Path : null)
            ?? (DetectedPathsList.Count > 0 ? DetectedPathsList[0] : null);

        if (!string.IsNullOrEmpty(target))
        {
            if (Directory.Exists(target))
            {
                Process.Start("explorer.exe", $"\"{target}\"");
            }
            else if (File.Exists(target))
            {
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            }
        }
    }

    public void ExecuteOpenGameDetails(GameBackupSummary? summary)
    {
        summary ??= SelectedGameSummary;
        if (summary == null) return;

        SelectedGameSummary = summary;
        IsRestoreModalOpen = false;
        IsHistoryDetailsModalOpen = true;
        IsModalOpen = true;
    }

    public void ExecuteLoadBackupPathsFromHistory(object? param)
    {
        BackupRecord? record = null;
        if (param is BackupRecord br)
        {
            record = br;
        }
        else if (param is GameBackupSummary gbs)
        {
            record = gbs.LatestRecord ?? gbs.Records.FirstOrDefault();
        }
        else
        {
            record = SelectedHistoryRecord ?? SelectedGameSummary?.LatestRecord ?? SelectedGameSummary?.Records.FirstOrDefault();
        }

        if (record == null)
        {
            MessageBox.Show("Vui lòng chọn một game trong danh sách lịch sử sao lưu!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Mode 1: Use save_paths directly without reading manifest
            var paths = record.SavePathsList;
            if (paths.Count == 0 && record.SourcePathsList.Count > 0)
            {
                paths = record.SourcePathsList;
            }

            if (paths.Count == 0)
            {
                // Fallback to manifest if legacy record has no save_paths
                var restoreItems = _backupService.GetRestoreItemsFromBackup(record);
                paths = restoreItems
                    .Select(i => !string.IsNullOrWhiteSpace(i.OriginalSourcePath) ? i.OriginalSourcePath : i.RestoreDestinationPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (paths.Count == 0)
            {
                MessageBox.Show($"Không tìm thấy thông tin trường save_paths trong bản sao lưu của game '{record.GameName}'!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ApplyPathsToTab1(record.GameName, paths, "Lịch sử sao lưu (save_paths)");
            StatusMessage = $"✓ Đã nạp {DetectedPathItems.Count} vị trí save từ trường save_paths của '{record.GameName}'. Sẵn sàng sao lưu ngay!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi nạp vị trí sao lưu: {ex.Message}";
            MessageBox.Show($"Lỗi nạp vị trí sao lưu: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ExecuteLoadBackupPathsFromManifest(object? param)
    {
        BackupRecord? record = null;
        if (param is BackupRecord br)
        {
            record = br;
        }
        else if (param is GameBackupSummary gbs)
        {
            record = gbs.LatestRecord ?? gbs.Records.FirstOrDefault();
        }
        else
        {
            record = SelectedHistoryRecord ?? SelectedGameSummary?.LatestRecord ?? SelectedGameSummary?.Records.FirstOrDefault();
        }

        if (record == null)
        {
            MessageBox.Show("Vui lòng chọn một bản ghi sao lưu trong chi tiết lịch sử!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Mode 2: Read manifest from the specific snapshot backup (ZIP or directory)
            var restoreItems = _backupService.GetRestoreItemsFromBackup(record);
            var paths = restoreItems
                .Select(i => !string.IsNullOrWhiteSpace(i.OriginalSourcePath) ? i.OriginalSourcePath : i.RestoreDestinationPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0 && record.SourcePathsList.Count > 0)
            {
                paths = record.SourcePathsList;
            }

            if (paths.Count == 0)
            {
                MessageBox.Show($"Không tìm thấy thông tin manifest vị trí save trong bản sao lưu của game '{record.GameName}'!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ApplyPathsToTab1(record.GameName, paths, "Lịch sử sao lưu (Manifest)");
            StatusMessage = $"✓ Đã đọc manifest và nạp {DetectedPathItems.Count} vị trí save của bản snapshot '{record.GameName}'. Sẵn sàng sao lưu ngay!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi đọc manifest sao lưu: {ex.Message}";
            MessageBox.Show($"Lỗi đọc manifest sao lưu: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        if (IsModalRestoring) return;
        IsModalOpen = false;
        IsHistoryDetailsModalOpen = false;
        IsRestoreModalOpen = false;
    }

    public void ExecuteCloseRestoreModal()
    {
        if (IsModalRestoring) return;

        IsRestoreModalOpen = false;
        if (SelectedGameSummary != null)
        {
            IsHistoryDetailsModalOpen = true;
        }
        else
        {
            IsModalOpen = false;
        }
    }

    public void ExecuteOpenRestoreModal(object? param)
    {
        BackupRecord? record = null;
        if (param is BackupRecord br)
        {
            record = br;
        }
        else if (param is GameBackupSummary gbs)
        {
            record = gbs.LatestRecord ?? gbs.Records.FirstOrDefault();
        }
        else
        {
            record = SelectedHistoryRecord ?? SelectedGameSummary?.LatestRecord;
        }

        if (record == null)
        {
            MessageBox.Show("Vui lòng chọn một game hoặc bản sao lưu để khôi phục!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var items = _backupService.GetRestoreItemsFromBackup(record);
            if (items.Count == 0)
            {
                MessageBox.Show("Không tìm thấy thông tin vị trí lưu nào trong bản sao lưu!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ActiveRestoreRecord = record;
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
            MessageBox.Show($"Lỗi chuẩn bị khôi phục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ExecuteModalRestoreConfirmAsync()
    {
        if (ActiveRestoreRecord == null) return;

        var activeItems = ActiveRestoreItems
            .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.RestoreDestinationPath))
            .ToList();

        if (activeItems.Count == 0)
        {
            MessageBox.Show("Vui lòng chọn ít nhất 1 vị trí lưu để khôi phục!", "Chưa chọn", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsModalRestoring = true;
        ModalRestoreProgressPercent = 0;
        ModalRestoreProgressMessage = "Đang bắt đầu khôi phục...";

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                ModalRestoreProgressPercent = p.Percent;
                ModalRestoreProgressMessage = string.IsNullOrEmpty(p.Message) ? "Đang khôi phục..." : p.Message;
            });

            await _backupService.RestoreAsync(ActiveRestoreRecord, activeItems, progress);

            StatusMessage = $"✓ Đã khôi phục thành công save game cho '{ActiveRestoreRecord.GameName}'!";
            MessageBox.Show($"Khôi phục thành công {activeItems.Count} vị trí lưu cho game '{ActiveRestoreRecord.GameName}'!", "Khôi phục thành công", MessageBoxButton.OK, MessageBoxImage.Information);

            IsRestoreModalOpen = false;
            if (SelectedGameSummary != null)
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
            ModalRestoreProgressMessage = $"Lỗi: {ex.Message}";
            MessageBox.Show($"Lỗi khôi phục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsModalRestoring = false;
        }
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

    public async Task ExecuteDeleteRecordAsync(BackupRecord? record)
    {
        record ??= SelectedHistoryRecord;
        if (record == null) return;

        var confirm = MessageBox.Show($"Xóa bản ghi lịch sử cho '{record.GameName}'?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _databaseService.DeleteBackupRecordAsync(record.Id);
            await RefreshHistoryAsync();
            StatusMessage = $"Đã xóa bản ghi #{record.Id}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi xóa: {ex.Message}";
        }
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
            MessageBox.Show("Vui lòng nhập đường dẫn file cơ sở dữ liệu SQLite hợp lệ!", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            await RefreshHistoryAsync();

            StatusMessage = $"✓ Đã chuyển Database sang: {DatabaseLocation}";
            MessageBox.Show($"Đã áp dụng và chuyển cơ sở dữ liệu sang:\n{DatabaseLocation}", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi cập nhật Database: {ex.Message}";
            MessageBox.Show($"Không thể chuyển đường dẫn Database:\n{ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var settings = new AppSettings
            {
                BackupRootDirectory = BackupDestinationRoot,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                AutoCompressZip = AutoCompressZip
            };

            await _databaseService.SaveSettingsAsync(settings);
            StatusMessage = "✓ Đã lưu cài đặt thành công!";
            MessageBox.Show("Cài đặt đã được lưu thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi lưu cài đặt: {ex.Message}";
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
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

    public bool ForceOnlineSearch
    {
        get => _forceOnlineSearch;
        set => SetField(ref _forceOnlineSearch, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
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
        set => SetField(ref _isBackingUp, value);
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
        get => _isModalOpen;
        set => SetField(ref _isModalOpen, value);
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
        set => SetField(ref _selectedTabIndex, value);
    }

    // Commands
    public ICommand SearchCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand OpenSourceFolderCommand { get; }
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
