using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Services;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

public class SettingsSubViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;
    private readonly LudusaviManifestService _ludusaviService;

    private string _databaseLocation = string.Empty;
    private string _backupDestinationRoot = string.Empty;
    private bool _createTimestampSubfolder = true;
    private bool _autoCompressZip = true;
    private int _pageSize = 10;
    private bool _isSyncingCatalog;
    private string _syncCatalogStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsSubViewModel(
        DatabaseService databaseService,
        IDialogService dialogService,
        IAppEventBus eventBus,
        INativeDialogService? nativeDialog = null,
        LudusaviManifestService? ludusaviService = null)
    {
        _databaseService = databaseService;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _nativeDialog = nativeDialog;
        _ludusaviService = ludusaviService ?? new LudusaviManifestService();

        var config = AppConfigService.GetConfig();
        _databaseLocation = !string.IsNullOrWhiteSpace(config.DatabasePath)
            ? config.DatabasePath
            : _databaseService.DbPath;

        _backupDestinationRoot = !string.IsNullOrWhiteSpace(config.BackupRootDirectory)
            ? config.BackupRootDirectory
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");

        _createTimestampSubfolder = config.CreateTimestampSubfolder;
        _autoCompressZip = config.AutoCompressZip;
        _pageSize = config.PageSize > 0 ? config.PageSize : 10;

        BrowseDatabaseFileCommand = new RelayCommand(async _ => await ExecuteBrowseDatabaseFileAsync());
        ApplyDatabaseLocationCommand = new RelayCommand(async _ => await ExecuteApplyDatabaseLocationAsync());
        ResetDatabaseLocationCommand = new RelayCommand(_ => ExecuteResetDatabaseLocation());
        BrowseBackupDirectoryCommand = new RelayCommand(async _ => await ExecuteBrowseBackupDirectoryAsync());
        SaveSettingsCommand = new RelayCommand(async _ => await ExecuteSaveSettingsAsync());
        SyncCatalogCommand = new RelayCommand(async _ => await ExecuteSyncCatalogAsync(), _ => !IsSyncingCatalog);
    }

    public string DatabaseLocation
    {
        get => _databaseLocation;
        set => SetField(ref _databaseLocation, value);
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

    public int PageSize
    {
        get => _pageSize;
        set => SetField(ref _pageSize, value);
    }

    public int CatalogGamesCount => _ludusaviService.TotalGamesCount;

    public bool IsSyncingCatalog
    {
        get => _isSyncingCatalog;
        set
        {
            if (SetField(ref _isSyncingCatalog, value))
            {
                (SyncCatalogCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SyncCatalogStatus
    {
        get => _syncCatalogStatus;
        set => SetField(ref _syncCatalogStatus, value);
    }

    public ICommand BrowseDatabaseFileCommand { get; }
    public ICommand ApplyDatabaseLocationCommand { get; }
    public ICommand ResetDatabaseLocationCommand { get; }
    public ICommand BrowseBackupDirectoryCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand SyncCatalogCommand { get; }

    public async Task ExecuteSyncCatalogAsync()
    {
        if (IsSyncingCatalog) return;
        IsSyncingCatalog = true;
        SyncCatalogStatus = "Đang kết nối tới GitHub để tải Ludusavi Manifest...";
        try
        {
            var progress = new Progress<string>(msg => SyncCatalogStatus = msg);
            int count = await _ludusaviService.SyncFromGithubAsync(progress);
            if (count > 0)
            {
                SyncCatalogStatus = $"Cập nhật thành công {count:N0} tựa game vào danh mục!";
                OnPropertyChanged(nameof(CatalogGamesCount));
                _dialogService.ShowMessage("Cập Nhật Danh Mục", $"Đã đồng bộ thành công {count:N0} tựa game từ Ludusavi Manifest (GitHub)!", "Success");
            }
            else
            {
                SyncCatalogStatus = "Không thể tải danh mục từ GitHub hoặc xảy ra lỗi mạng.";
                _dialogService.ShowMessage("Cập Nhật Danh Mục", "Không thể tải danh mục game. Vui lòng kiểm tra kết nối internet.", "Warning");
            }
        }
        catch (Exception ex)
        {
            SyncCatalogStatus = $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsSyncingCatalog = false;
        }
    }

    private async Task ExecuteBrowseDatabaseFileAsync()
    {
        var file = _nativeDialog != null
            ? await _nativeDialog.PickDatabaseFileAsync("Chọn file SQLite Database (.db)")
            : null;

        if (!string.IsNullOrEmpty(file))
        {
            DatabaseLocation = file;
            LoggingService.LogAction("Browse_Database_Location", new { Path = file });
        }
    }

    private async Task ExecuteApplyDatabaseLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(DatabaseLocation)) return;

        try
        {
            var oldPath = _databaseService.DbPath;
            var targetPath = DatabaseLocation.Trim();

            if (!string.Equals(oldPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(oldPath) && !File.Exists(targetPath))
                {
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                    File.Copy(oldPath, targetPath, overwrite: false);
                    LoggingService.LogAction("Migrate_Database_File", new { From = oldPath, To = targetPath });
                }

                AppConfigService.UpdateConfig(cfg => cfg.DatabasePath = targetPath);

                _dialogService.ShowMessage("Chuyển Đổi Database", $"Đã lưu đường dẫn cơ sở dữ liệu SQLite mới:\n{targetPath}\n\nĐường dẫn sẽ được áp dụng trong phiên làm việc tiếp theo.", "Success");
                LoggingService.LogAction("Apply_Database_Location_Success", new { NewLocation = targetPath });

                _eventBus.Publish(new HistoryChangedEvent());
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi chuyển vị trí database: {Message}", ex.Message);
            _dialogService.ShowMessage("Lỗi Database", ex.Message, "Error", ex.StackTrace);
        }

        await Task.CompletedTask;
    }

    private void ExecuteResetDatabaseLocation()
    {
        var defaultPath = Path.Combine(DatabaseService.GetDefaultProjectRoot(), "save_backup.db");
        DatabaseLocation = defaultPath;
        AppConfigService.UpdateConfig(cfg => cfg.DatabasePath = defaultPath);

        _dialogService.ShowMessage("Khôi Phục Mặc Định", $"Đã đặt lại database về đường dẫn mặc định:\n{defaultPath}", "Info");
        LoggingService.LogAction("Reset_Database_Location_Default", new { Path = defaultPath });

        _eventBus.Publish(new HistoryChangedEvent());
    }

    private async Task ExecuteBrowseBackupDirectoryAsync()
    {
        var folder = _nativeDialog != null
            ? await _nativeDialog.PickFolderAsync("Chọn thư mục gốc lưu trữ các bản sao lưu")
            : null;

        if (!string.IsNullOrEmpty(folder))
        {
            BackupDestinationRoot = folder;
            AppConfigService.UpdateConfig(cfg => cfg.BackupRootDirectory = folder);
            LoggingService.LogAction("Settings_Change_Backup_Dir", new { Directory = folder });
        }
    }

    public async Task ExecuteSaveSettingsAsync()
    {
        AppConfigService.UpdateConfig(cfg =>
        {
            cfg.BackupRootDirectory = BackupDestinationRoot;
            cfg.CreateTimestampSubfolder = CreateTimestampSubfolder;
            cfg.AutoCompressZip = AutoCompressZip;
            cfg.PageSize = PageSize;
            cfg.DatabasePath = DatabaseLocation;
        });

        _dialogService.ShowMessage("Lưu Cài Đặt", "Đã lưu toàn bộ cấu hình vào app_config.json thành công!", "Success");
        LoggingService.LogAction("Save_General_Settings", new
        {
            BackupRoot = BackupDestinationRoot,
            CreateTimestampSubfolder,
            AutoCompressZip,
            PageSize,
            DatabaseLocation
        });

        await Task.CompletedTask;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
