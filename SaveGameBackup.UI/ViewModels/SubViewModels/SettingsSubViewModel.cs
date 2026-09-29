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

    private string _backupDestinationRoot = string.Empty;
    private bool _createTimestampSubfolder = true;
    private bool _autoCompressZip = true;
    private int _pageSize = 10;
    private bool _useSqlPagination = true;
    private bool _autoUpdateLudusaviManifest = true;
    private int _ludusaviAutoUpdateDays = 15;
    private bool _isSyncingCatalog;
    private string _syncCatalogStatus = string.Empty;
    private int _syncProgressPercent;
    private string _syncProgressSubText = string.Empty;
    private CancellationTokenSource? _syncCts;

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
        _backupDestinationRoot = DatabaseService.DefaultBackupDir;

        _createTimestampSubfolder = config.CreateTimestampSubfolder;
        _autoCompressZip = config.AutoCompressZip;
        _pageSize = config.PageSize > 0 ? config.PageSize : 10;
        _useSqlPagination = config.UseSqlPagination;
        _autoUpdateLudusaviManifest = config.AutoUpdateLudusaviManifest;
        _ludusaviAutoUpdateDays = config.LudusaviAutoUpdateDays > 0 ? config.LudusaviAutoUpdateDays : 15;

        SaveSettingsCommand = new RelayCommand(async _ => await ExecuteSaveSettingsAsync());
        SyncCatalogCommand = new RelayCommand(async _ => await ExecuteSyncCatalogAsync(), _ => !IsSyncingCatalog);
    }

    public string BackupDestinationRoot
    {
        get => DatabaseService.DefaultBackupDir;
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

    public bool UseSqlPagination
    {
        get => _useSqlPagination;
        set => SetField(ref _useSqlPagination, value);
    }

    public int CatalogGamesCount => _ludusaviService.TotalGamesCount;
    public DateTime? LastLudusaviSyncDate => AppConfigService.GetConfig().LastLudusaviManifestSync;

    public bool AutoUpdateLudusaviManifest
    {
        get => _autoUpdateLudusaviManifest;
        set
        {
            if (SetField(ref _autoUpdateLudusaviManifest, value))
            {
                AppConfigService.UpdateConfig(cfg => cfg.AutoUpdateLudusaviManifest = value);
            }
        }
    }

    public int LudusaviAutoUpdateDays
    {
        get => _ludusaviAutoUpdateDays;
        set
        {
            if (SetField(ref _ludusaviAutoUpdateDays, value))
            {
                AppConfigService.UpdateConfig(cfg => cfg.LudusaviAutoUpdateDays = value);
            }
        }
    }

    /// <summary>
    /// Kiểm tra và tự động cập nhật danh mục game Ludusavi nếu đã quá chu kỳ.
    /// </summary>
    public async Task CheckAutoSyncCatalogAsync()
    {
        try
        {
            if (LudusaviManifestService.IsUpdateDue())
            {
                await Task.Delay(3000);
                int count = await _ludusaviService.CheckAndAutoSyncIfDueAsync();
                if (count > 0)
                {
                    OnPropertyChanged(nameof(CatalogGamesCount));
                    OnPropertyChanged(nameof(LastLudusaviSyncDate));
                    int days = LudusaviAutoUpdateDays > 0 ? LudusaviAutoUpdateDays : 15;
                    SyncCatalogStatus = $"Tự động cập nhật thành công {count:N0} game từ GitHub (chu kỳ {days} ngày).";
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi kiểm tra tự động cập nhật Ludusavi: {Message}", ex.Message);
        }
    }

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

    public int SyncProgressPercent
    {
        get => _syncProgressPercent;
        set => SetField(ref _syncProgressPercent, value);
    }

    public string SyncProgressSubText
    {
        get => _syncProgressSubText;
        set => SetField(ref _syncProgressSubText, value);
    }

    public void CancelSync()
    {
        _syncCts?.Cancel();
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand SyncCatalogCommand { get; }

    public async Task ExecuteSyncCatalogAsync()
    {
        if (IsSyncingCatalog) return;
        IsSyncingCatalog = true;
        SyncProgressPercent = 0;
        SyncProgressSubText = "0%";
        SyncCatalogStatus = "Đang kết nối tới GitHub để tải Ludusavi Manifest...";

        _syncCts = new CancellationTokenSource();

        _dialogService.ShowProgress(
            "Cập Nhật Danh Mục Game Từ GitHub",
            "Đang kết nối tới máy chủ GitHub...",
            0,
            subText: "0%",
            onCancel: () => CancelSync());

        try
        {
            var progress = new Progress<ManifestSyncProgress>(p =>
            {
                SyncProgressPercent = p.Percent;
                SyncProgressSubText = p.SubText;
                SyncCatalogStatus = p.Status;
                _dialogService.UpdateProgress(p.Percent, p.Status, p.SubText);
            });

            int count = await _ludusaviService.SyncFromGithubAsync(progress, _syncCts.Token);
            _dialogService.CloseProgress();

            if (count > 0)
            {
                SyncProgressPercent = 100;
                SyncProgressSubText = $"{count:N0} game";
                SyncCatalogStatus = $"Cập nhật thành công {count:N0} tựa game vào danh mục!";
                OnPropertyChanged(nameof(CatalogGamesCount));
                OnPropertyChanged(nameof(LastLudusaviSyncDate));
                _dialogService.ShowMessage("Cập Nhật Danh Mục", $"Đã đồng bộ thành công {count:N0} tựa game từ Ludusavi Manifest (GitHub)!", "Success");
            }
            else
            {
                if (_syncCts.IsCancellationRequested)
                {
                    SyncCatalogStatus = "Đã hủy thao tác đồng bộ từ GitHub.";
                    _dialogService.ShowMessage("Cập Nhật Danh Mục", "Thao tác đồng bộ đã được hủy.", "Information");
                }
                else
                {
                    SyncCatalogStatus = "Không thể tải danh mục từ GitHub hoặc xảy ra lỗi mạng.";
                    _dialogService.ShowMessage("Cập Nhật Danh Mục", "Không thể tải danh mục game. Vui lòng kiểm tra kết nối internet.", "Warning");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _dialogService.CloseProgress();
            SyncCatalogStatus = "Đã hủy thao tác đồng bộ từ GitHub.";
            _dialogService.ShowMessage("Cập Nhật Danh Mục", "Thao tác đồng bộ đã được hủy.", "Information");
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            SyncCatalogStatus = $"Lỗi: {ex.Message}";
            _dialogService.ShowMessage("Lỗi Cập Nhật", $"Đã xảy ra lỗi trong quá trình cập nhật: {ex.Message}", "Error");
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            IsSyncingCatalog = false;
        }
    }



    public async Task ExecuteSaveSettingsAsync()
    {
        AppConfigService.UpdateConfig(cfg =>
        {
            cfg.CreateTimestampSubfolder = CreateTimestampSubfolder;
            cfg.AutoCompressZip = AutoCompressZip;
            cfg.PageSize = PageSize;
            cfg.UseSqlPagination = UseSqlPagination;
            cfg.AutoUpdateLudusaviManifest = AutoUpdateLudusaviManifest;
            cfg.LudusaviAutoUpdateDays = LudusaviAutoUpdateDays;
        });

        _dialogService.ShowMessage("Lưu Cài Đặt", "Đã lưu toàn bộ cấu hình vào app_config.json thành công!", "Success");
        LoggingService.LogAction("Save_General_Settings", new
        {
            BackupRoot = DatabaseService.DefaultBackupDir,
            CreateTimestampSubfolder,
            AutoCompressZip,
            PageSize,
            UseSqlPagination,
            AutoUpdateLudusaviManifest,
            LudusaviAutoUpdateDays
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
