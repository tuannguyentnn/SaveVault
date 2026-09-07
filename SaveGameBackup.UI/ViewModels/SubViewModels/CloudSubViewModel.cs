using System;
using System.ComponentModel;
using System.Diagnostics;
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

public class CloudSubViewModel : INotifyPropertyChanged
{
    private readonly CloudManagerService _cloudManager;
    private readonly BackupService _backupService;
    private readonly DatabaseService _databaseService;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;

    private int _selectedCloudProviderIndex; // 0: Google Drive, 1: OneDrive
    private bool _isCloudLoggedIn;
    private string? _cloudAccountEmail;
    private bool _isCloudSyncing;
    private int _cloudSyncProgressPercent;
    private string _cloudSyncProgressText = string.Empty;

    private string _oneDriveClientId = string.Empty;
    private string _googleDriveClientId = string.Empty;
    private string _googleDriveClientSecret = string.Empty;

    // Cloud Sync Provider Selection Modal
    private bool _isSyncCloudSelectModalOpen;
    private BackupHistoryDetail? _targetSyncDetail;
    private CancellationTokenSource? _syncCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CloudSubViewModel(
        CloudManagerService cloudManager,
        BackupService backupService,
        DatabaseService databaseService,
        IDialogService dialogService,
        IAppEventBus eventBus)
    {
        _cloudManager = cloudManager;
        _backupService = backupService;
        _databaseService = databaseService;
        _dialogService = dialogService;
        _eventBus = eventBus;

        var config = AppConfigService.GetConfig();
        _oneDriveClientId = config.OneDriveClientId ?? string.Empty;
        _googleDriveClientId = config.GoogleDriveClientId ?? string.Empty;
        _googleDriveClientSecret = config.GoogleDriveClientSecret ?? string.Empty;
        _selectedCloudProviderIndex = string.Equals(config.ActiveCloudProvider, "OneDrive", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        ConnectCloudAccountCommand = new RelayCommand(async _ => await ExecuteConnectCloudAccountAsync(), _ => !IsCloudSyncing);
        DisconnectCloudAccountCommand = new RelayCommand(async _ => await ExecuteDisconnectCloudAccountAsync(), _ => !IsCloudSyncing && IsCloudLoggedIn);
        SaveCloudApiSettingsCommand = new RelayCommand(async _ => await ExecuteSaveCloudApiSettingsAsync(), _ => !IsCloudSyncing);

        OpenSyncCloudSelectModalCommand = new RelayCommand(param => OpenSyncCloudSelectModal(param as BackupHistoryDetail), _ => !IsCloudSyncing);
        ConfirmSyncToProviderCommand = new RelayCommand(async param => await ConfirmSyncToProviderAsync(param?.ToString() ?? "GoogleDrive"), _ => !IsCloudSyncing);
        CloseSyncCloudSelectModalCommand = new RelayCommand(_ => ExecuteCloseSyncCloudSelectModal());
        CancelSyncCommand = new RelayCommand(_ => ExecuteCancelSync(), _ => IsCloudSyncing);

        OpenCloudWebViewCommand = new RelayCommand(param => ExecuteOpenCloudWebView(param));
        OpenAzurePortalGuideCommand = new RelayCommand(_ => ExecuteOpenAzurePortalGuide());
        OpenGoogleCloudConsoleGuideCommand = new RelayCommand(_ => ExecuteOpenGoogleCloudConsoleGuide());
    }

    public int SelectedCloudProviderIndex
    {
        get => _selectedCloudProviderIndex;
        set
        {
            if (SetField(ref _selectedCloudProviderIndex, value))
            {
                var providerName = value == 1 ? "OneDrive" : "GoogleDrive";
                _cloudManager.SetActiveProvider(providerName);
                UpdateCloudStatusDisplay();
                OnPropertyChanged(nameof(IsGoogleDriveSelected));
                OnPropertyChanged(nameof(IsOneDriveSelected));
                LoggingService.LogAction("Cloud_Tab_Switch_Provider", new { Provider = providerName });
            }
        }
    }

    public bool IsGoogleDriveSelected => SelectedCloudProviderIndex == 0;
    public bool IsOneDriveSelected => SelectedCloudProviderIndex == 1;

    public bool IsCloudLoggedIn
    {
        get => _isCloudLoggedIn;
        set
        {
            if (SetField(ref _isCloudLoggedIn, value))
            {
                (DisconnectCloudAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

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
                (ConnectCloudAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DisconnectCloudAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CancelSyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    public bool IsSyncCloudSelectModalOpen
    {
        get => _isSyncCloudSelectModalOpen;
        set => SetField(ref _isSyncCloudSelectModalOpen, value);
    }

    public BackupHistoryDetail? TargetSyncDetail
    {
        get => _targetSyncDetail;
        set => SetField(ref _targetSyncDetail, value);
    }

    public ICommand ConnectCloudAccountCommand { get; }
    public ICommand DisconnectCloudAccountCommand { get; }
    public ICommand SaveCloudApiSettingsCommand { get; }
    public ICommand OpenSyncCloudSelectModalCommand { get; }
    public ICommand ConfirmSyncToProviderCommand { get; }
    public ICommand CloseSyncCloudSelectModalCommand { get; }
    public ICommand CancelSyncCommand { get; }
    public ICommand OpenCloudWebViewCommand { get; }
    public ICommand OpenAzurePortalGuideCommand { get; }
    public ICommand OpenGoogleCloudConsoleGuideCommand { get; }

    public async Task InitializeCloudAsync()
    {
        try
        {
            await _cloudManager.InitializeAsync();
            UpdateCloudStatusDisplay();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khởi tạo Cloud lúc mở app: {Message}", ex.Message);
        }
    }

    public void UpdateCloudStatusDisplay()
    {
        IsCloudLoggedIn = _cloudManager.IsLoggedIn();
        CloudAccountEmail = _cloudManager.GetSavedUserEmail();
    }

    public async Task ExecuteConnectCloudAccountAsync()
    {
        var provider = _cloudManager.CurrentProvider;
        var providerName = provider.DisplayName;

        LoggingService.LogAction("Cloud_Connect_Requested", new { Provider = provider.ProviderName });

        try
        {
            var success = await provider.AuthenticateAsync();
            if (success)
            {
                UpdateCloudStatusDisplay();
                _dialogService.ShowMessage("Kết Nối Thành Công", $"Đã kết nối thành công với {providerName}!\n\nTài khoản: {CloudAccountEmail}", "Success");
                LoggingService.LogAction("Cloud_Connect_Success", new { Provider = provider.ProviderName, Email = CloudAccountEmail });
            }
            else
            {
                _dialogService.ShowMessage("Kết Nối Thất Bại", "Không nhận được phản hồi xác thực từ Cloud provider.", "Warning");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi kết nối Cloud {Provider}: {Message}", provider.ProviderName, ex.Message);
            _dialogService.ShowMessage("Lỗi Kết Nối Cloud", ex.Message, "Error", ex.StackTrace);
        }
    }

    public async Task ExecuteDisconnectCloudAccountAsync()
    {
        var provider = _cloudManager.CurrentProvider;
        LoggingService.LogAction("Cloud_Disconnect_Requested", new { Provider = provider.ProviderName });

        try
        {
            await provider.SignOutAsync();
            UpdateCloudStatusDisplay();
            _dialogService.ShowMessage("Đã Đăng Xuất", $"Đã ngắt kết nối tài khoản khỏi {provider.DisplayName}.", "Info");
            LoggingService.LogAction("Cloud_Disconnect_Success", new { Provider = provider.ProviderName });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi đăng xuất Cloud: {Message}", ex.Message);
            _dialogService.ShowMessage("Lỗi Đăng Xuất", ex.Message, "Error", ex.StackTrace);
        }
    }

    public async Task ExecuteSaveCloudApiSettingsAsync()
    {
        AppConfigService.UpdateConfig(cfg =>
        {
            cfg.OneDriveClientId = OneDriveClientId?.Trim() ?? string.Empty;
            cfg.GoogleDriveClientId = GoogleDriveClientId?.Trim() ?? string.Empty;
            cfg.GoogleDriveClientSecret = GoogleDriveClientSecret?.Trim() ?? string.Empty;
        });

        _dialogService.ShowMessage("Lưu Cấu Hình", "Đã lưu cài đặt Cloud API Credentials vào app_config.json thành công!", "Success");
        LoggingService.LogAction("Save_Cloud_Credentials", new
        {
            HasOneDriveId = !string.IsNullOrEmpty(OneDriveClientId),
            HasGDriveId = !string.IsNullOrEmpty(GoogleDriveClientId)
        });

        await Task.CompletedTask;
    }

    public void OpenSyncCloudSelectModal(BackupHistoryDetail? detail)
    {
        if (detail == null) return;
        TargetSyncDetail = detail;
        IsSyncCloudSelectModalOpen = true;
        LoggingService.LogAction("Open_Sync_Cloud_Modal", new { Game = detail.GameName, SnapshotId = detail.Id });
    }

    public void ExecuteCloseSyncCloudSelectModal()
    {
        IsSyncCloudSelectModalOpen = false;
        TargetSyncDetail = null;
        LoggingService.LogAction("Close_Sync_Cloud_Modal");
    }

    public async Task ConfirmSyncToProviderAsync(string providerName)
    {
        if (TargetSyncDetail == null) return;
        var detail = TargetSyncDetail;
        ExecuteCloseSyncCloudSelectModal();

        var provider = _cloudManager.GetProvider(providerName);
        if (provider == null || !provider.IsAuthenticated)
        {
            _dialogService.ShowMessage("Chưa Đăng Nhập", $"Bạn chưa kết nối tài khoản {providerName}! Vui lòng vào tab Cloud Sync để đăng nhập.", "Warning");
            return;
        }

        if (!File.Exists(detail.BackupPath))
        {
            _dialogService.ShowMessage("Không Tìm Thấy File", $"File sao lưu cục bộ không còn tồn tại tại:\n{detail.BackupPath}", "Error");
            return;
        }

        IsCloudSyncing = true;
        CloudSyncProgressPercent = 0;
        CloudSyncProgressText = $"Đang tải lên {provider.DisplayName}...";
        _syncCts = new CancellationTokenSource();

        var dateFormatted = detail.BackupDate.ToString("dd/MM/yyyy HH:mm");
        _dialogService.ShowProgress("Đồng Bộ Cloud", $"Đang đồng bộ snapshot ngày {dateFormatted} lên {provider.DisplayName}...", 0);
        LoggingService.LogAction("Cloud_Upload_Start", new { Game = detail.GameName, Provider = providerName, File = detail.BackupPath });

        var progress = new Progress<BackupProgress>(p =>
        {
            CloudSyncProgressPercent = p.Percent;
            CloudSyncProgressText = p.Message;
            _dialogService.UpdateProgress(p.Percent, p.Message);
        });

        try
        {
            var result = await _backupService.SyncSnapshotToCloudAsync(detail, provider, progress, _syncCts.Token);

            _dialogService.CloseProgress();
            if (result.Success)
            {
                _dialogService.ShowMessage("Đồng Bộ Thành Công", $"Đã tải snapshot của game '{detail.GameName}' lên {provider.DisplayName} thành công!", "Success");
                LoggingService.LogAction("Cloud_Upload_Success", new { Game = detail.GameName, Provider = providerName });
                _eventBus.Publish(new HistoryChangedEvent());
            }
            else
            {
                _dialogService.ShowMessage("Đồng Bộ Thất Bại", result.ErrorMessage ?? "Có lỗi khi tải lên Cloud.", "Error");
                LoggingService.LogAction("Cloud_Upload_Failed", new { Game = detail.GameName, Error = result.ErrorMessage }, level: "Error");
            }
        }
        catch (OperationCanceledException)
        {
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Đã Hủy", "Tác vụ đồng bộ lên Cloud đã bị hủy.", "Info");
            LoggingService.LogAction("Cloud_Upload_Cancelled", new { Game = detail.GameName });
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Lỗi Đồng Bộ", ex.Message, "Error", ex.StackTrace);
            LoggingService.Error(ex, "Lỗi đồng bộ snapshot {Game} lên {Provider}: {Message}", detail.GameName, providerName, ex.Message);
        }
        finally
        {
            IsCloudSyncing = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    public void ExecuteCancelSync()
    {
        if (_syncCts != null && !_syncCts.IsCancellationRequested)
        {
            _syncCts.Cancel();
            LoggingService.LogAction("Cloud_Upload_Cancel_Requested");
        }
    }

    private void ExecuteOpenCloudWebView(object? param)
    {
        string? url = null;

        if (param is CloudSyncInfo sync)
        {
            url = sync.WebViewUrl;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(sync.FileId))
            {
                url = sync.Provider.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                    ? $"https://onedrive.live.com/?id={Uri.EscapeDataString(sync.FileId)}"
                    : $"https://drive.google.com/file/d/{Uri.EscapeDataString(sync.FileId)}/view";
            }
        }
        else if (param is BackupHistoryDetail detail)
        {
            var syncInfo = detail.CloudSyncList.FirstOrDefault();
            url = syncInfo?.WebViewUrl;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(detail.CloudFileId))
            {
                url = detail.CloudProvider.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                    ? $"https://onedrive.live.com/?id={Uri.EscapeDataString(detail.CloudFileId)}"
                    : $"https://drive.google.com/file/d/{Uri.EscapeDataString(detail.CloudFileId)}/view";
            }
        }
        else if (param is string urlStr && (urlStr.StartsWith("http://") || urlStr.StartsWith("https://")))
        {
            url = urlStr;
        }

        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                LoggingService.LogAction("Open_Cloud_Web_View", new { Url = url });
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Không thể mở liên kết Cloud trên trình duyệt: {Message}", ex.Message);
            }
        }
    }

    private void ExecuteOpenAzurePortalGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationsListBlade", UseShellExecute = true });
        }
        catch { }
    }

    private void ExecuteOpenGoogleCloudConsoleGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://console.cloud.google.com/apis/credentials", UseShellExecute = true });
        }
        catch { }
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
