using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.Core.Services.Cloud;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

public class SettingsSubViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;
    private readonly LudusaviManifestService _ludusaviService;
    private readonly DatabaseBackupService _databaseBackupService;
    private readonly CloudManagerService? _cloudManager;
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

    // Database Cloud Backup fields
    private string _databaseCloudBackupTarget = "GoogleDrive";
    private bool _isBackingUpDatabase;
    private string _databaseBackupProgressText = string.Empty;
    private bool _isDatabaseRestoreModalOpen;
    private bool _isScanningCloudDatabaseBackups;

    // Gemini AI Resolver fields
    private readonly GeminiExeResolverService _geminiService;
    private bool _enableGeminiExeSearch = true;
    private bool _useGeminiUnifiedWorkflow = true;
    private string _geminiApiKey = string.Empty;
    private string _geminiModel = "gemini-3.5-flash";
    private bool _isTestingGemini;
    private string _geminiTestResult = string.Empty;
    private bool? _geminiTestSuccess;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DatabaseCloudBackupEntry> DatabaseCloudBackups { get; } = new();

    public SettingsSubViewModel(
        DatabaseService databaseService,
        IDialogService dialogService,
        IAppEventBus eventBus,
        INativeDialogService? nativeDialog = null,
        LudusaviManifestService? ludusaviService = null,
        DatabaseBackupService? databaseBackupService = null,
        CloudManagerService? cloudManager = null,
        GeminiExeResolverService? geminiService = null)
    {
        _databaseService = databaseService;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _nativeDialog = nativeDialog;
        _ludusaviService = ludusaviService ?? new LudusaviManifestService();
        _cloudManager = cloudManager;
        _databaseBackupService = databaseBackupService ?? new DatabaseBackupService(databaseService, cloudManager ?? new CloudManagerService(databaseService));
        _geminiService = geminiService ?? new GeminiExeResolverService();

        var config = AppConfigService.GetConfig();

        _createTimestampSubfolder = config.CreateTimestampSubfolder;
        _autoCompressZip = config.AutoCompressZip;
        _pageSize = config.PageSize > 0 ? config.PageSize : 10;
        _useSqlPagination = config.UseSqlPagination;
        _autoUpdateLudusaviManifest = config.AutoUpdateLudusaviManifest;
        _ludusaviAutoUpdateDays = config.LudusaviAutoUpdateDays > 0 ? config.LudusaviAutoUpdateDays : 15;
        _databaseCloudBackupTarget = !string.IsNullOrWhiteSpace(config.DatabaseCloudBackupTarget) ? config.DatabaseCloudBackupTarget : "GoogleDrive";

        _enableGeminiExeSearch = config.EnableGeminiExeSearch;
        _useGeminiUnifiedWorkflow = config.UseGeminiUnifiedWorkflow;
        _geminiApiKey = config.GeminiApiKey ?? string.Empty;
        _geminiModel = !string.IsNullOrWhiteSpace(config.GeminiModel) && !config.GeminiModel.Contains("2.5") ? config.GeminiModel : "gemini-3.5-flash";

        SaveSettingsCommand = new RelayCommand(async _ => await ExecuteSaveSettingsAsync());
        SyncCatalogCommand = new RelayCommand(async _ => await ExecuteSyncCatalogAsync(), _ => !IsSyncingCatalog);

        BackupDatabaseToCloudCommand = new RelayCommand(async _ => await ExecuteBackupDatabaseToCloudAsync(), _ => !IsBackingUpDatabase);
        OpenDatabaseRestoreModalCommand = new RelayCommand(async _ => await OpenDatabaseRestoreModalAsync());
        CloseDatabaseRestoreModalCommand = new RelayCommand(_ => IsDatabaseRestoreModalOpen = false);
        RestoreDatabaseCommand = new RelayCommand(async param => await ExecuteRestoreDatabaseAsync(param as DatabaseCloudBackupEntry));
        ScanCloudDatabaseBackupsCommand = new RelayCommand(async _ => await ExecuteScanCloudDatabaseBackupsAsync(), _ => !_isScanningCloudDatabaseBackups);
        OpenCloudWebUrlCommand = new RelayCommand(param => OpenCloudWebUrl(param?.ToString()));

        TestGeminiConnectionCommand = new RelayCommand(async _ => await ExecuteTestGeminiConnectionAsync(), _ => !IsTestingGemini);
        OpenGoogleAiStudioCommand = new RelayCommand(_ => OpenCloudWebUrl("https://aistudio.google.com/app/apikey"));

        // Nạp lịch sử sao lưu database trong nền
        _ = LoadDatabaseCloudHistoryAsync();
    }


    public string BackupDestinationRoot => DatabaseService.DefaultBackupDir;

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

    public bool EnableGeminiExeSearch
    {
        get => _enableGeminiExeSearch;
        set => SetField(ref _enableGeminiExeSearch, value);
    }

    public bool UseGeminiUnifiedWorkflow
    {
        get => _useGeminiUnifiedWorkflow;
        set => SetField(ref _useGeminiUnifiedWorkflow, value);
    }

    public string GeminiApiKey
    {
        get => _geminiApiKey;
        set => SetField(ref _geminiApiKey, value);
    }

    public string GeminiModel
    {
        get => _geminiModel;
        set => SetField(ref _geminiModel, value);
    }

    public bool IsTestingGemini
    {
        get => _isTestingGemini;
        set
        {
            if (SetField(ref _isTestingGemini, value))
            {
                (TestGeminiConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string GeminiTestResult
    {
        get => _geminiTestResult;
        set => SetField(ref _geminiTestResult, value);
    }

    public bool? GeminiTestSuccess
    {
        get => _geminiTestSuccess;
        set => SetField(ref _geminiTestSuccess, value);
    }

    public async Task ExecuteTestGeminiConnectionAsync()
    {
        if (IsTestingGemini) return;
        if (string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            GeminiTestSuccess = false;
            GeminiTestResult = "Vui lòng nhập Gemini API Key trước khi kiểm tra.";
            return;
        }

        IsTestingGemini = true;
        GeminiTestResult = "Đang kết nối tới Google Gemini API...";
        GeminiTestSuccess = null;

        try
        {
            var (success, message) = await _geminiService.TestApiKeyAsync(GeminiApiKey, GeminiModel);
            GeminiTestSuccess = success;
            GeminiTestResult = message;
        }
        catch (Exception ex)
        {
            GeminiTestSuccess = false;
            GeminiTestResult = $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsTestingGemini = false;
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
                
                int days = LudusaviAutoUpdateDays > 0 ? LudusaviAutoUpdateDays : 15;
                _dialogService.ShowConfirm(
                    "Cập Nhật Danh Mục Game",
                    $"Đã đến thời hạn tự động cập nhật danh mục game mới nhất từ Ludusavi (chu kỳ {days} ngày). Bạn có muốn tải xuống và cập nhật bây giờ không?",
                    "Cập Nhật Ngay",
                    async () =>
                    {
                        await ExecuteSyncCatalogAsync();
                    });
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
    public ICommand TestGeminiConnectionCommand { get; }
    public ICommand OpenGoogleAiStudioCommand { get; }

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
            cfg.EnableGeminiExeSearch = EnableGeminiExeSearch;
            cfg.UseGeminiUnifiedWorkflow = UseGeminiUnifiedWorkflow;
            cfg.GeminiApiKey = GeminiApiKey;
            cfg.GeminiModel = GeminiModel;
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
            LudusaviAutoUpdateDays,
            EnableGeminiExeSearch,
            UseGeminiUnifiedWorkflow,
            GeminiModel
        });

        await Task.CompletedTask;
    }

    #region Database Cloud Backup & Restore

    public string DatabaseCloudBackupTarget
    {
        get => _databaseCloudBackupTarget;
        set
        {
            if (SetField(ref _databaseCloudBackupTarget, value))
            {
                AppConfigService.UpdateConfig(cfg => cfg.DatabaseCloudBackupTarget = value);
            }
        }
    }

    public bool IsBackingUpDatabase
    {
        get => _isBackingUpDatabase;
        set
        {
            if (SetField(ref _isBackingUpDatabase, value))
            {
                (BackupDatabaseToCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DatabaseBackupProgressText
    {
        get => _databaseBackupProgressText;
        set => SetField(ref _databaseBackupProgressText, value);
    }

    public bool IsDatabaseRestoreModalOpen
    {
        get => _isDatabaseRestoreModalOpen;
        set => SetField(ref _isDatabaseRestoreModalOpen, value);
    }

    public bool IsScanningCloudDatabaseBackups
    {
        get => _isScanningCloudDatabaseBackups;
        set
        {
            if (SetField(ref _isScanningCloudDatabaseBackups, value))
            {
                (ScanCloudDatabaseBackupsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasConnectedCloud => IsGoogleDriveConnected || IsOneDriveConnected;
    public bool IsGoogleDriveConnected => _cloudManager?.GoogleDrive?.IsAuthenticated == true;
    public bool IsOneDriveConnected => _cloudManager?.OneDrive?.IsAuthenticated == true;
    public string GoogleDriveEmail => _cloudManager?.GoogleDrive?.CurrentAccountEmail ?? string.Empty;
    public string OneDriveEmail => _cloudManager?.OneDrive?.CurrentAccountEmail ?? string.Empty;

    public void RefreshCloudStatus()
    {
        OnPropertyChanged(nameof(HasConnectedCloud));
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
        OnPropertyChanged(nameof(IsOneDriveConnected));
        OnPropertyChanged(nameof(GoogleDriveEmail));
        OnPropertyChanged(nameof(OneDriveEmail));
    }

    public ICommand BackupDatabaseToCloudCommand { get; }
    public ICommand OpenDatabaseRestoreModalCommand { get; }
    public ICommand CloseDatabaseRestoreModalCommand { get; }
    public ICommand RestoreDatabaseCommand { get; }
    public ICommand ScanCloudDatabaseBackupsCommand { get; }
    public ICommand OpenCloudWebUrlCommand { get; }

    public async Task LoadDatabaseCloudHistoryAsync()
    {
        try
        {
            var history = await _databaseBackupService.LoadHistoryAsync();
            DatabaseCloudBackups.Clear();
            foreach (var entry in history.Entries.OrderByDescending(e => e.BackupTime))
            {
                DatabaseCloudBackups.Add(entry);
            }
            OnPropertyChanged(nameof(DatabaseCloudBackups));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi tải lịch sử sao lưu database: {Message}", ex.Message);
        }
    }

    public async Task OpenDatabaseRestoreModalAsync()
    {
        await LoadDatabaseCloudHistoryAsync();
        RefreshCloudStatus();
        IsDatabaseRestoreModalOpen = true;
    }

    public async Task ExecuteBackupDatabaseToCloudAsync()
    {
        if (IsBackingUpDatabase) return;

        RefreshCloudStatus();

        var target = DatabaseCloudBackupTarget;
        bool gdriveOk = IsGoogleDriveConnected;
        bool onedriveOk = IsOneDriveConnected;

        if (string.Equals(target, "GoogleDrive", StringComparison.OrdinalIgnoreCase) && !gdriveOk)
        {
            _dialogService.ShowMessage("Chưa Kết Nối Google Drive",
                "Bạn đã chọn sao lưu lên Google Drive nhưng tài khoản chưa được kết nối. Vui lòng chuyển sang tab 'Đồng Bộ Đám Mây' để đăng nhập trước.",
                "Warning");
            return;
        }

        if (string.Equals(target, "OneDrive", StringComparison.OrdinalIgnoreCase) && !onedriveOk)
        {
            _dialogService.ShowMessage("Chưa Kết Nối OneDrive",
                "Bạn đã chọn sao lưu lên Microsoft OneDrive nhưng tài khoản chưa được kết nối. Vui lòng chuyển sang tab 'Đồng Bộ Đám Mây' để đăng nhập trước.",
                "Warning");
            return;
        }

        if (string.Equals(target, "Both", StringComparison.OrdinalIgnoreCase) && !gdriveOk && !onedriveOk)
        {
            _dialogService.ShowMessage("Chưa Kết Nối Cloud",
                "Bạn đã chọn sao lưu lên Cả hai Cloud nhưng chưa có tài khoản nào được kết nối. Vui lòng kết nối Google Drive hoặc OneDrive tại tab 'Đồng Bộ Đám Mây'.",
                "Warning");
            return;
        }

        string targetDesc = target switch
        {
            "GoogleDrive" => "Google Drive",
            "OneDrive" => "Microsoft OneDrive",
            _ => "cả Google Drive & Microsoft OneDrive"
        };

        _dialogService.ShowConfirm(
            "Xác Nhận Sao Lưu Database Lên Cloud",
            $"Bạn có muốn tạo bản snapshot cơ sở dữ liệu SQLite hiện tại và đồng bộ lên {targetDesc} không?\n\nỨng dụng sẽ nén dữ liệu an toàn và lưu tối đa 5 bản sao lưu gần nhất.",
            targetDesc,
            async () =>
            {
                IsBackingUpDatabase = true;
                DatabaseBackupProgressText = "Đang tạo bản snapshot SQLite (VACUUM INTO)...";
                _dialogService.ShowProgress("Sao Lưu Database Lên Cloud", DatabaseBackupProgressText, 0);

                try
                {
                    var progress = new Progress<BackupProgress>(p =>
                    {
                        DatabaseBackupProgressText = p.Message;
                        _dialogService.UpdateProgress(p.Percent, p.Message);
                    });

                    var result = await _databaseBackupService.BackupDatabaseToCloudAsync(target, progress);
                    await LoadDatabaseCloudHistoryAsync();

                    _dialogService.CloseProgress();

                    var cloudNames = string.Join(" & ", result.CloudUploads.Select(u => u.Provider));
                    _dialogService.ShowMessage(
                        "Sao Lưu Database Thành Công",
                        $"Đã sao lưu cơ sở dữ liệu lên {cloudNames} thành công!\n\n• Tên tệp: {result.FileName}\n• Dung lượng: {FormatBytes(result.FileSizeBytes)}\n• Tổng số game đã ghi nhận: {result.GameCount}\n• Tổng số bản lưu (snapshot): {result.SnapshotCount}",
                        "Success");
                }
                catch (Exception ex)
                {
                    _dialogService.CloseProgress();
                    LoggingService.Error(ex, "Lỗi khi sao lưu database lên Cloud: {Message}", ex.Message);
                    _dialogService.ShowMessage("Lỗi Sao Lưu Database", $"Đã xảy ra sự cố khi tải database lên Cloud:\n{ex.Message}", "Error");
                }
                finally
                {
                    IsBackingUpDatabase = false;
                    DatabaseBackupProgressText = string.Empty;
                }
            });

        await Task.CompletedTask;
    }

    public async Task ExecuteRestoreDatabaseAsync(DatabaseCloudBackupEntry? entry)
    {
        if (entry == null) return;

        RefreshCloudStatus();

        _dialogService.ShowConfirm(
            "Xác Nhận Khôi Phục Cơ Sở Dữ Liệu",
            $"Bạn có chắc chắn muốn khôi phục database từ bản lưu '{entry.FileName}' (sao lưu lúc {entry.BackupTime.ToLocalTime():dd/MM/yyyy HH:mm:ss})?\n\nLưu ý: Toàn bộ danh mục game và lịch sử bản lưu hiện tại trên máy tính sẽ được thay thế bằng dữ liệu trong bản sao lưu này. Ứng dụng sẽ tự động sao lưu khẩn cấp dữ liệu hiện tại trước khi khôi phục.",
            entry.FileName,
            async () =>
            {
                IsDatabaseRestoreModalOpen = false;
                _dialogService.ShowProgress("Khôi Phục Database Từ Cloud", "Đang kết nối Cloud...", 10);

                try
                {
                    var progress = new Progress<BackupProgress>(p =>
                    {
                        _dialogService.UpdateProgress(p.Percent, p.Message);
                    });

                    await _databaseBackupService.RestoreDatabaseFromHistoryEntryAsync(entry, progress);

                    _dialogService.CloseProgress();
                    _dialogService.ShowMessage(
                        "Khôi Phục Thành Công",
                        $"Đã khôi phục cơ sở dữ liệu thành công từ Cloud!\n• Game phục hồi: {entry.GameCount}\n• Bản lưu phục hồi: {entry.SnapshotCount}\n\nDữ liệu ứng dụng đã được đồng bộ lại toàn vẹn.",
                        "Success");

                    // Bắn tín hiệu để HistoryVM và các thành phần khác làm mới dữ liệu
                    _eventBus.Publish(new HistoryChangedEvent());
                }
                catch (Exception ex)
                {
                    _dialogService.CloseProgress();
                    LoggingService.Error(ex, "Lỗi khi khôi phục database từ Cloud: {Message}", ex.Message);
                    _dialogService.ShowMessage("Lỗi Khôi Phục Database", ex.Message, "Error");
                }
            });

        await Task.CompletedTask;
    }

    public async Task ExecuteScanCloudDatabaseBackupsAsync()
    {
        if (IsScanningCloudDatabaseBackups) return;

        RefreshCloudStatus();
        if (!HasConnectedCloud)
        {
            _dialogService.ShowMessage("Chưa Kết Nối Cloud", "Vui lòng kết nối Google Drive hoặc OneDrive trước khi quét tìm bản lưu.", "Warning");
            return;
        }

        IsScanningCloudDatabaseBackups = true;
        _dialogService.ShowProgress("Quét Tìm Bản Lưu Database", "Đang quét danh sách file trên Cloud...", 20);

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                _dialogService.UpdateProgress(p.Percent, p.Message);
            });

            await _databaseBackupService.ScanCloudForDatabaseBackupsAsync(progress);
            await LoadDatabaseCloudHistoryAsync();

            _dialogService.CloseProgress();
            _dialogService.ShowMessage(
                "Hoàn Tất Quét Cloud",
                $"Đã hoàn tất quét tìm và đồng bộ danh sách bản sao lưu database từ Cloud! Hiện có {DatabaseCloudBackups.Count} bản sao lưu sẵn sàng.",
                "Success");
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            LoggingService.Error(ex, "Lỗi khi quét Cloud database backups: {Message}", ex.Message);
            _dialogService.ShowMessage("Lỗi Quét Cloud", ex.Message, "Error");
        }
        finally
        {
            IsScanningCloudDatabaseBackups = false;
        }
    }

    public void OpenCloudWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            LoggingService.LogAction("Open_Database_Cloud_WebUrl", new { Url = url });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Không thể mở liên kết Cloud: {Message}", ex.Message);
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    #endregion

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
