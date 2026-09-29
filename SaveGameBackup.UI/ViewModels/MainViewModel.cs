using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.Core.Services.Cloud;
using SaveGameBackup.UI.Services;
using SaveGameBackup.UI.ViewModels.SubViewModels;

namespace SaveGameBackup.UI.ViewModels;

/// <summary>
/// Root ViewModel đóng vai trò Facade và Coordinator theo MVVM Pattern.
/// Phối hợp các Sub-ViewModels chuyên biệt và bảo đảm tính tương thích ngược hoàn toàn
/// với toàn bộ các binding và command của giao diện.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly GameSearchCoordinator _searchCoordinator;
    private readonly BackupService _backupService;
    private readonly CloudManagerService _cloudManager;
    private readonly IAppEventBus _eventBus;

    private int _selectedTabIndex;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Sub-ViewModels (Composite MVVM Pattern)
    public SearchSubViewModel SearchVM { get; }
    public BackupSubViewModel BackupVM { get; }
    public HistorySubViewModel HistoryVM { get; }
    public RestoreSubViewModel RestoreVM { get; }
    public CloudSubViewModel CloudVM { get; }
    public SettingsSubViewModel SettingsVM { get; }
    public UpdateSubViewModel UpdateVM { get; }
    public IDialogService DialogService { get; }
    public INativeDialogService NativeDialog { get; }
    public DatabaseService DatabaseService => _databaseService;

    public MainViewModel(
        DatabaseService? databaseService = null,
        GameSearchCoordinator? searchCoordinator = null,
        BackupService? backupService = null,
        CloudManagerService? cloudManager = null,
        IAppEventBus? eventBus = null,
        IDialogService? dialogService = null,
        INativeDialogService? nativeDialog = null)
    {
        _databaseService = databaseService ?? new DatabaseService();
        _searchCoordinator = searchCoordinator ?? new GameSearchCoordinator(_databaseService);
        _backupService = backupService ?? new BackupService(_databaseService);
        _cloudManager = cloudManager ?? new CloudManagerService(_databaseService);
        _eventBus = eventBus ?? new AppEventBus();
        DialogService = dialogService ?? new DialogService();
        NativeDialog = nativeDialog ?? new NativeDialogService();

        // Khởi tạo các Sub-ViewModels độc lập
        SearchVM = new SearchSubViewModel(_databaseService, _searchCoordinator, DialogService, _eventBus, NativeDialog);
        BackupVM = new BackupSubViewModel(_backupService, SearchVM, DialogService, _eventBus, NativeDialog);
        HistoryVM = new HistorySubViewModel(_databaseService, _backupService, _cloudManager, DialogService, _eventBus);
        RestoreVM = new RestoreSubViewModel(_backupService, _cloudManager, _databaseService, DialogService, _eventBus, NativeDialog);
        CloudVM = new CloudSubViewModel(_cloudManager, _backupService, _databaseService, DialogService, _eventBus);
        SettingsVM = new SettingsSubViewModel(_databaseService, DialogService, _eventBus, NativeDialog, cloudManager: _cloudManager);
        UpdateVM = new UpdateSubViewModel(new UpdateService(), CheckIfBusyBeforeUpdate, DialogService, _eventBus);

        // Lắng nghe sự kiện chuyển tab từ Mediator
        _eventBus.Subscribe<RequestNavigateTabEvent>(e => SelectedTabIndex = e.TabIndex);

        // Đăng ký theo dõi thay đổi trạng thái modal để cập nhật IsModalOpen
        DialogService.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        SearchVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        HistoryVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        RestoreVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        CloudVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        SettingsVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));
        UpdateVM.PropertyChanged += (s, e) => OnPropertyChanged(nameof(IsModalOpen));

        // Đăng ký chuyển tiếp PropertyChanged từ các Sub-VMs để binding cũ tiếp tục hoạt động
        ForwardPropertyChanged(SearchVM);
        ForwardPropertyChanged(BackupVM);
        ForwardPropertyChanged(HistoryVM);
        ForwardPropertyChanged(RestoreVM);
        ForwardPropertyChanged(CloudVM);
        ForwardPropertyChanged(SettingsVM);
        ForwardPropertyChanged(UpdateVM);
        ForwardPropertyChanged(DialogService);

        CloseModalCommand = new RelayCommand(_ => ExecuteCloseModal());

        // Khởi tạo dữ liệu và khôi phục phiên Cloud tự động từ app_config.json
        _ = Task.Run(async () =>
        {
            await HistoryVM.RefreshHistoryAsync();
            await CloudVM.InitializeCloudAsync();
        });

        LoggingService.LogAction("MainViewModel_Initialized");
    }

    private void ForwardPropertyChanged(INotifyPropertyChanged subVm)
    {
        subVm.PropertyChanged += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };
    }

    // --- Tab Navigation ---
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetField(ref _selectedTabIndex, value))
            {
                var tabName = value switch
                {
                    0 => "Tìm kiếm & Sao lưu",
                    1 => "Lịch sử sao lưu",
                    2 => "Cài đặt",
                    3 => "Thông tin & Hỗ trợ",
                    _ => $"Tab {value}"
                };
                LoggingService.LogAction("Tab_Navigated", new { TabIndex = value, TabName = tabName });
            }
        }
    }

    // --- Trạng thái Modal chung (Lớp phủ mờ nền) ---
    public bool IsModalOpen =>
        DialogService.IsMessageModalOpen ||
        DialogService.IsConfirmModalOpen ||
        DialogService.IsProgressModalOpen ||
        HistoryVM.IsHistoryDetailsModalOpen ||
        RestoreVM.IsRestoreModalOpen ||
        RestoreVM.IsRestoreSourceModalOpen ||
        CloudVM.IsSyncCloudSelectModalOpen ||
        SearchVM.IsGameSelectModalOpen ||
        (SettingsVM?.IsDatabaseRestoreModalOpen == true) ||
        (UpdateVM?.IsUpdateModalOpen == true);

    /// <summary>
    /// Thẩm định xem hệ thống có đang bận thực hiện tác vụ quan trọng nào không (sao lưu, khôi phục, đồng bộ đám mây, v.v.).
    /// </summary>
    public (bool isBusy, string reason) CheckIfBusyBeforeUpdate()
    {
        if (BackupVM.IsBackingUp)
        {
            return (true, "Đang diễn ra tiến trình sao lưu save game. Hãy đợi sao lưu hoàn tất trước khi cập nhật.");
        }
        if (SettingsVM.IsBackingUpDatabase)
        {
            return (true, "Đang diễn ra tiến trình sao lưu cơ sở dữ liệu lên Cloud. Hãy đợi sao lưu hoàn tất trước khi cập nhật.");
        }
        if (RestoreVM.IsModalRestoring)
        {
            return (true, "Đang diễn ra tiến trình khôi phục bản lưu game. Hãy đợi khôi phục hoàn tất trước khi cập nhật.");
        }
        if (CloudVM.IsCloudSyncing)
        {
            var p = !string.IsNullOrWhiteSpace(CloudVM.CloudSyncProgressText) ? CloudVM.CloudSyncProgressText : $"{CloudVM.CloudSyncProgressPercent}%";
            return (true, $"Đang đồng bộ dữ liệu lên đám mây ({p}). Hãy đợi đồng bộ hoàn tất trước khi cập nhật.");
        }
        if (SettingsVM.IsSyncingCatalog)
        {
            return (true, $"Đang cập nhật danh bạ Ludusavi Manifest từ GitHub ({SettingsVM.SyncProgressPercent}%). Hãy đợi tải xong trước khi cập nhật.");
        }
        if (HistoryVM.IsSyncingCovers)
        {
            return (true, "Đang tải ảnh bìa game về máy. Hãy đợi tác vụ hoàn tất trước khi cập nhật.");
        }
        if (SearchVM.IsSearching)
        {
            return (true, "Đang tìm kiếm thông tin game. Hãy đợi tìm kiếm xong trước khi cập nhật.");
        }

        return (false, string.Empty);
    }

    // --- CHUYỂN TIẾP CÁC THUỘC TÍNH TỪ SUB-VIEWMODELS (Bảo toàn 100% XAML Bindings) ---

    // Search & Detection
    public string SearchQuery { get => SearchVM.SearchQuery; set => SearchVM.SearchQuery = value; }
    public bool IsSearching { get => SearchVM.IsSearching; set => SearchVM.IsSearching = value; }
    public string StatusMessage { get => SearchVM.StatusMessage; set => SearchVM.StatusMessage = value; }
    public GameSaveInfo? CurrentGame { get => SearchVM.CurrentGame; set => SearchVM.CurrentGame = value; }
    public bool HasGame => SearchVM.HasGame;
    public string DetectedSizeFormatted => SearchVM.DetectedSizeFormatted;
    public int DetectedFileCount => SearchVM.DetectedFileCount;
    public bool IsGameFoundOnDisk => SearchVM.IsGameFoundOnDisk;
    public int SelectedPathCount => SearchVM.SelectedPathCount;
    public int SelectedFileCount => SearchVM.SelectedFileCount;
    public string SelectedSizeFormatted => SearchVM.SelectedSizeFormatted;
    public bool HasSelectedPaths => SearchVM.HasSelectedPaths;
    public ObservableCollection<string> PopularGameSuggestions => SearchVM.PopularGameSuggestions;
    public ObservableCollection<DetectedPathItem> DetectedPathItems => SearchVM.DetectedPathItems;
    public ObservableCollection<string> DetectedPathsList => SearchVM.DetectedPathsList;
    public ObservableCollection<string> OnlinePatternsList => SearchVM.OnlinePatternsList;

    // Backup
    public string BackupDestinationRoot { get => BackupVM.BackupDestinationRoot; set => BackupVM.BackupDestinationRoot = value; }
    public bool CreateTimestampSubfolder { get => BackupVM.CreateTimestampSubfolder; set => BackupVM.CreateTimestampSubfolder = value; }
    public bool AutoCompressZip { get => BackupVM.AutoCompressZip; set => BackupVM.AutoCompressZip = value; }
    public bool IsBackingUp { get => BackupVM.IsBackingUp; set => BackupVM.IsBackingUp = value; }
    public bool IsNotBackingUp => BackupVM.IsNotBackingUp;
    public int BackupProgressPercent { get => BackupVM.BackupProgressPercent; set { } }
    public string BackupProgressText => BackupVM.BackupProgressText;
    public string? LastBackupPath { get => BackupVM.LastBackupPath; set => BackupVM.LastBackupPath = value; }
    public bool HasLastBackupPath => BackupVM.HasLastBackupPath;

    // History
    public ObservableCollection<GameHistoryEntry> GameHistories => HistoryVM.GameHistories;
    public ObservableCollection<GameBackupSummary> GroupedBackupHistory => HistoryVM.GroupedBackupHistory;
    public ObservableCollection<BackupRecord> BackupHistory => HistoryVM.BackupHistory;
    public ObservableCollection<BackupHistoryDetail> CurrentHistoryDetails => HistoryVM.CurrentHistoryDetails;
    public GameHistoryEntry? SelectedGameHistory { get => HistoryVM.SelectedGameHistory; set => HistoryVM.SelectedGameHistory = value; }
    public GameBackupSummary? SelectedGameSummary { get => HistoryVM.SelectedGameSummary; set => HistoryVM.SelectedGameSummary = value; }
    public BackupRecord? SelectedHistoryRecord { get => HistoryVM.SelectedHistoryRecord; set => HistoryVM.SelectedHistoryRecord = value; }
    public BackupHistoryDetail? SelectedHistoryDetail { get => HistoryVM.SelectedHistoryDetail; set => HistoryVM.SelectedHistoryDetail = value; }
    public bool IsHistoryDetailsModalOpen { get => HistoryVM.IsHistoryDetailsModalOpen; set => HistoryVM.IsHistoryDetailsModalOpen = value; }
    public string SelectedHistoryGameTitle => HistoryVM.SelectedHistoryGameTitle;

    // Restore
    public bool IsRestoreModalOpen { get => RestoreVM.IsRestoreModalOpen; set => RestoreVM.IsRestoreModalOpen = value; }
    public BackupRecord? ActiveRestoreRecord { get => RestoreVM.ActiveRestoreRecord; set => RestoreVM.ActiveRestoreRecord = value; }
    public BackupHistoryDetail? ActiveRestoreDetail { get => RestoreVM.ActiveRestoreDetail; set => RestoreVM.ActiveRestoreDetail = value; }
    public ObservableCollection<RestoreItemTarget> ActiveRestoreItems => RestoreVM.ActiveRestoreItems;
    public bool IsModalRestoring => RestoreVM.IsModalRestoring;
    public int ModalRestoreProgressPercent { get => RestoreVM.ModalRestoreProgressPercent; set { } }
    public string ModalRestoreProgressMessage => RestoreVM.ModalRestoreProgressMessage;
    public string ModalRestoreSelectedCountText => RestoreVM.ModalRestoreSelectedCountText;
    public bool CanConfirmModalRestore => RestoreVM.CanConfirmModalRestore;
    public bool IsRestoreSourceModalOpen { get => RestoreVM.IsRestoreSourceModalOpen; set => RestoreVM.IsRestoreSourceModalOpen = value; }
    public string RestoreSourceModalTitle => RestoreVM.RestoreSourceModalTitle;
    public string RestoreSourceModalMessage => RestoreVM.RestoreSourceModalMessage;
    public bool IsPendingDetailSyncedToGoogleDrive => RestoreVM.IsPendingDetailSyncedToGoogleDrive;
    public bool IsPendingDetailSyncedToOneDrive => RestoreVM.IsPendingDetailSyncedToOneDrive;

    // Cloud
    public int SelectedCloudProviderIndex { get => CloudVM.SelectedCloudProviderIndex; set => CloudVM.SelectedCloudProviderIndex = value; }
    public bool IsGoogleDriveSelected => CloudVM.IsGoogleDriveSelected;
    public bool IsOneDriveSelected => CloudVM.IsOneDriveSelected;
    public bool IsCloudLoggedIn => CloudVM.IsCloudLoggedIn;
    public string? CloudAccountEmail => CloudVM.CloudAccountEmail;
    public bool IsCloudSyncing => CloudVM.IsCloudSyncing;
    public int CloudSyncProgressPercent { get => CloudVM.CloudSyncProgressPercent; set { } }
    public string CloudSyncProgressText => CloudVM.CloudSyncProgressText;
    public string OneDriveClientId { get => CloudVM.OneDriveClientId; set => CloudVM.OneDriveClientId = value; }
    public string GoogleDriveClientId { get => CloudVM.GoogleDriveClientId; set => CloudVM.GoogleDriveClientId = value; }
    public string GoogleDriveClientSecret { get => CloudVM.GoogleDriveClientSecret; set => CloudVM.GoogleDriveClientSecret = value; }
    public bool IsSyncCloudSelectModalOpen { get => CloudVM.IsSyncCloudSelectModalOpen; set => CloudVM.IsSyncCloudSelectModalOpen = value; }
    public BackupHistoryDetail? TargetSyncDetail => CloudVM.TargetSyncDetail;


    // Dialogs / Modals
    public bool IsMessageModalOpen => DialogService.IsMessageModalOpen;
    public string MessageModalTitle => DialogService.MessageModalTitle;
    public string MessageModalContent => DialogService.MessageModalContent;
    public string MessageModalType => DialogService.MessageModalType;
    public string? MessageModalDetails => DialogService.MessageModalDetails;
    public bool HasMessageModalDetails => DialogService.HasMessageModalDetails;

    public bool IsConfirmModalOpen => DialogService.IsConfirmModalOpen;
    public string ConfirmModalTitle => DialogService.ConfirmModalTitle;
    public string ConfirmModalMessage => DialogService.ConfirmModalMessage;
    public string ConfirmModalTargetPath => DialogService.ConfirmModalTargetPath;
    public bool ShowDeleteCloudOption { get => DialogService.ShowDeleteCloudOption; set => DialogService.ShowDeleteCloudOption = value; }
    public bool DeleteAlsoFromCloud { get => DialogService.DeleteAlsoFromCloud; set => DialogService.DeleteAlsoFromCloud = value; }
    public bool IsConfirmModalDeleting => DialogService.IsConfirmModalDeleting;
    public int ConfirmModalProgressPercent { get => DialogService.ConfirmModalProgressPercent; set { } }
    public string ConfirmModalProgressText => DialogService.ConfirmModalProgressText;

    public bool IsProgressModalOpen => DialogService.IsProgressModalOpen;
    public string ActiveProgressTitle => DialogService.ActiveProgressTitle;
    public string ActiveProgressMessage => DialogService.ActiveProgressMessage;
    public int ActiveProgressPercent { get => DialogService.ActiveProgressPercent; set { } }
    public string ActiveProgressSubText => DialogService.ActiveProgressSubText;

    // --- CHUYỂN TIẾP TOÀN BỘ COMMANDS (Bảo toàn 100% XAML Bindings) ---

    // Search Commands
    public ICommand SearchCommand => SearchVM.SearchCommand;
    public ICommand AddCustomPathCommand => SearchVM.AddCustomPathCommand;
    public ICommand SelectAllPathsCommand => SearchVM.SelectAllPathsCommand;
    public ICommand DeselectAllPathsCommand => SearchVM.DeselectAllPathsCommand;
    public ICommand OpenSpecificDetectedPathCommand => SearchVM.OpenSpecificDetectedPathCommand;
    public ICommand RemoveDetectedPathCommand => SearchVM.RemoveDetectedPathCommand;

    // Backup Commands
    public ICommand BackupCommand => BackupVM.BackupCommand;
    public ICommand OpenBackupFolderCommand => BackupVM.OpenBackupFolderCommand;

    // History Commands
    public bool CanCloseModal => !DialogService.IsConfirmModalDeleting && !DialogService.IsProgressModalOpen && !RestoreVM.IsModalRestoring;
    public ICommand OpenGameDetailsCommand => HistoryVM.OpenGameDetailsCommand;
    public ICommand CloseModalCommand { get; }
    public ICommand CloseGameDetailsModalCommand => HistoryVM.CloseModalCommand;
    public ICommand RequestDeleteHistoryDetailCommand => HistoryVM.RequestDeleteHistoryDetailCommand;
    public ICommand RequestDeleteGameHistoryCommand => HistoryVM.RequestDeleteGameHistoryCommand;
    public ICommand DeleteRecordCommand => HistoryVM.DeleteRecordCommand;
    public ICommand OpenRecordFolderCommand => HistoryVM.OpenRecordFolderCommand;
    public ICommand RefreshHistoryCommand => HistoryVM.RefreshHistoryCommand;

    // Restore Commands
    public ICommand OpenRestoreModalCommand => RestoreVM.OpenRestoreModalCommand;
    public ICommand RestoreRecordCommand => RestoreVM.OpenRestoreModalCommand;
    public ICommand CloseRestoreModalCommand => RestoreVM.CloseRestoreModalCommand;
    public ICommand ModalRestoreConfirmCommand => RestoreVM.ModalRestoreConfirmCommand;
    public ICommand ModalSelectAllRestoreItemsCommand => RestoreVM.ModalSelectAllRestoreItemsCommand;
    public ICommand ModalDeselectAllRestoreItemsCommand => RestoreVM.ModalDeselectAllRestoreItemsCommand;
    public ICommand ModalResetRestorePathsCommand => RestoreVM.ModalResetRestorePathsCommand;
    public ICommand ModalToggleRestoreItemCommand => RestoreVM.ModalToggleRestoreItemCommand;
    public ICommand ModalBrowseRestoreDestCommand => RestoreVM.ModalBrowseRestoreDestCommand;
    public ICommand ConfirmRestoreFromLocalCommand => RestoreVM.ConfirmRestoreFromLocalCommand;
    public ICommand ConfirmRestoreFromCloudCommand => RestoreVM.ConfirmRestoreFromCloudCommand;
    public ICommand ConfirmRestoreFromGoogleDriveCommand => RestoreVM.ConfirmRestoreFromGoogleDriveCommand;
    public ICommand ConfirmRestoreFromOneDriveCommand => RestoreVM.ConfirmRestoreFromOneDriveCommand;
    public ICommand CancelRestoreSourceModalCommand => RestoreVM.CancelRestoreSourceModalCommand;

    // Cloud Commands
    public ICommand ConnectCloudAccountCommand => CloudVM.ConnectCloudAccountCommand;
    public ICommand DisconnectCloudAccountCommand => CloudVM.DisconnectCloudAccountCommand;
    public ICommand SaveCloudApiSettingsCommand => CloudVM.SaveCloudApiSettingsCommand;
    public ICommand OpenSyncCloudSelectModalCommand => CloudVM.OpenSyncCloudSelectModalCommand;
    public ICommand SyncSelectedDetailToCloudCommand => CloudVM.OpenSyncCloudSelectModalCommand;
    public ICommand ConfirmSyncToProviderCommand => CloudVM.ConfirmSyncToProviderCommand;
    public ICommand CloseSyncCloudSelectModalCommand => CloudVM.CloseSyncCloudSelectModalCommand;
    public ICommand CancelSyncCommand => CloudVM.CancelSyncCommand;
    public ICommand OpenCloudWebViewCommand => CloudVM.OpenCloudWebViewCommand;
    public ICommand OpenAzurePortalGuideCommand => CloudVM.OpenAzurePortalGuideCommand;
    public ICommand OpenGoogleCloudConsoleGuideCommand => CloudVM.OpenGoogleCloudConsoleGuideCommand;

    // Settings Commands
    public ICommand SaveSettingsCommand => SettingsVM.SaveSettingsCommand;

    // Dialog Commands
    public ICommand CloseMessageModalCommand => DialogService.CloseMessageModalCommand;
    public ICommand ConfirmModalExecuteCommand => DialogService.ConfirmModalExecuteCommand;
    public ICommand CancelConfirmModalCommand => DialogService.CancelConfirmModalCommand;

    private void ExecuteCloseModal()
    {
        if (DialogService.IsProgressModalOpen) return;
        if (DialogService.IsConfirmModalDeleting) return;
        if (RestoreVM.IsModalRestoring) return;

        if (DialogService.IsMessageModalOpen)
        {
            DialogService.CloseMessage();
            return;
        }

        if (DialogService.IsConfirmModalOpen)
        {
            DialogService.CancelConfirm();
            return;
        }

        if (SearchVM.IsGameSelectModalOpen)
        {
            SearchVM.CancelCandidateModalCommand.Execute(null);
            return;
        }

        if (RestoreVM.IsRestoreModalOpen)
        {
            RestoreVM.CloseRestoreModalCommand.Execute(null);
            return;
        }

        if (RestoreVM.IsRestoreSourceModalOpen)
        {
            RestoreVM.CancelRestoreSourceModalCommand.Execute(null);
            return;
        }

        if (CloudVM.IsSyncCloudSelectModalOpen)
        {
            CloudVM.CloseSyncCloudSelectModalCommand.Execute(null);
            return;
        }

        if (HistoryVM.IsHistoryDetailsModalOpen)
        {
            HistoryVM.ExecuteCloseDetailsModal();
            return;
        }

        if (UpdateVM.IsUpdateModalOpen && !UpdateVM.IsDownloading)
        {
            UpdateVM.RemindLaterCommand.Execute(null);
            return;
        }
    }

    // Update
    public bool IsUpdateModalOpen { get => UpdateVM.IsUpdateModalOpen; set => UpdateVM.IsUpdateModalOpen = value; }
    public string? DownloadErrorMessage => UpdateVM.DownloadErrorMessage;
    public bool HasDownloadError => UpdateVM.HasDownloadError;
    public ICommand RetryDownloadCommand => UpdateVM.RetryDownloadCommand;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
