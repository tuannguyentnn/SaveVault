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
    private readonly DatabaseService _databaseService;
    private readonly GameSearchCoordinator _searchCoordinator;
    private readonly BackupService _backupService;
    private readonly LudusaviDatabaseService _ludusaviService;

    // Search properties
    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private bool _forceOnlineSearch;
    private string _statusMessage = "Sẵn sàng. Hãy nhập tên game để tìm kiếm vị trí save game.";

    // Detected Game Info
    private GameSaveInfo? _currentGame;
    private bool _hasGame;
    private string _detectedSizeFormatted = "0 B";
    private int _detectedFileCount;
    private bool _isGameFoundOnDisk;

    // Backup Options & Progress
    private string _backupDestinationRoot = string.Empty;
    private bool _createTimestampSubfolder = true;
    private bool _autoCompressZip = false;
    private bool _isBackingUp;
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string? _lastBackupPath;

    // History
    public ObservableCollection<BackupRecord> BackupHistory { get; } = new();
    public ObservableCollection<string> PopularGameSuggestions { get; } = new();
    public ObservableCollection<string> DetectedPathsList { get; } = new();
    public ObservableCollection<string> OnlinePatternsList { get; } = new();

    private BackupRecord? _selectedHistoryRecord;

    public MainViewModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var defaultBackupsFolder = Path.Combine(baseDir, "Backups");
        Directory.CreateDirectory(defaultBackupsFolder);

        _databaseService = new DatabaseService();
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
        BackupCommand = new RelayCommand(async _ => await ExecuteBackupAsync(), _ => !IsBackingUp && HasGame && IsGameFoundOnDisk);
        OpenBackupFolderCommand = new RelayCommand(_ => ExecuteOpenBackupFolder(), _ => !string.IsNullOrEmpty(LastBackupPath));
        OpenSourceFolderCommand = new RelayCommand(_ => ExecuteOpenSourceFolder(), _ => HasGame && DetectedPathsList.Count > 0);
        RestoreRecordCommand = new RelayCommand(async param => await ExecuteRestoreAsync(param as BackupRecord), _ => !IsBackingUp);
        DeleteRecordCommand = new RelayCommand(async param => await ExecuteDeleteRecordAsync(param as BackupRecord));
        OpenRecordFolderCommand = new RelayCommand(param => ExecuteOpenRecordFolder(param as BackupRecord));
        SaveSettingsCommand = new RelayCommand(async _ => await ExecuteSaveSettingsAsync());
        BrowseBackupDirectoryCommand = new RelayCommand(_ => ExecuteBrowseBackupDirectory());
        AddCustomPathCommand = new RelayCommand(_ => ExecuteAddCustomPath());

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

            DetectedPathsList.Clear();
            foreach (var p in result.DetectedPathsOnDisk)
            {
                DetectedPathsList.Add(p);
            }

            if (result.IsFoundOnDisk)
            {
                StatusMessage = $"✓ Đã tìm thấy {result.FileCount} file ({DetectedSizeFormatted}) tại {result.DetectedPathsOnDisk.Count} vị trí!";
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

            var record = await _backupService.BackupGameAsync(CurrentGame, settings, progress);
            LastBackupPath = record.BackupPath;

            StatusMessage = $"✓ Sao lưu thành công {record.FileCount} file ({record.FormattedSize}) vào: {record.BackupPath}";
            await RefreshHistoryAsync();

            MessageBox.Show($"Sao lưu game '{CurrentGame.GameName}' thành công!\n\nVị trí sao lưu: {record.BackupPath}\nDung lượng: {record.FormattedSize}",
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
        if (DetectedPathsList.Count > 0)
        {
            var path = DetectedPathsList[0];
            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            else if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
        }
    }

    public async Task ExecuteRestoreAsync(BackupRecord? record)
    {
        record ??= SelectedHistoryRecord;
        if (record == null) return;

        var msg = $"Bạn có chắc chắn muốn khôi phục bản sao lưu của '{record.GameName}'?\n\nNguồn sao lưu: {record.BackupPath}\nVị trí khôi phục: {record.SourcePath}\n\nLưu ý: Các file hiện tại trong thư mục game có thể bị ghi đè.";
        var confirm = MessageBox.Show(msg, "Xác nhận khôi phục", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        IsBackingUp = true;
        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                BackupProgressPercent = p.Percent;
                BackupProgressText = p.Message;
            });

            await _backupService.RestoreAsync(record, progress);
            StatusMessage = $"✓ Đã khôi phục thành công save game cho '{record.GameName}'!";
            MessageBox.Show($"Khôi phục save game '{record.GameName}' thành công!", "Khôi phục thành tất", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khôi phục: {ex.Message}";
            MessageBox.Show($"Lỗi khôi phục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackingUp = false;
        }
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

    public void ExecuteAddCustomPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu save của game trên máy"
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            if (!DetectedPathsList.Contains(dialog.FolderName))
            {
                DetectedPathsList.Add(dialog.FolderName);
            }

            if (CurrentGame != null)
            {
                if (!CurrentGame.DetectedPathsOnDisk.Contains(dialog.FolderName))
                {
                    CurrentGame.DetectedPathsOnDisk.Add(dialog.FolderName);
                }

                var files = Directory.GetFiles(dialog.FolderName, "*", SearchOption.AllDirectories);
                CurrentGame.FileCount += files.Length;
                CurrentGame.TotalSizeBytes += files.Sum(f => new FileInfo(f).Length);

                DetectedFileCount = CurrentGame.FileCount;
                DetectedSizeFormatted = FormatBytes(CurrentGame.TotalSizeBytes);
                IsGameFoundOnDisk = true;
                HasGame = true;
            }
        }
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

    public string DatabaseLocation => _databaseService.DbPath;

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
    public ICommand AddCustomPathCommand { get; }

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
