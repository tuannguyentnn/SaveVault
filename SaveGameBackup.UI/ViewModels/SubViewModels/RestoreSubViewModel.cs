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

public class RestoreSubViewModel : INotifyPropertyChanged
{
    private readonly BackupService _backupService;
    private readonly CloudManagerService _cloudManager;
    private readonly DatabaseService _databaseService;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;

    private bool _isRestoreModalOpen;
    private BackupRecord? _activeRestoreRecord;
    private BackupHistoryDetail? _activeRestoreDetail;
    public ObservableCollection<RestoreItemTarget> ActiveRestoreItems { get; } = new();

    private bool _isModalRestoring;
    private int _modalRestoreProgressPercent;
    private string _modalRestoreProgressMessage = string.Empty;
    private string _modalRestoreSelectedCountText = string.Empty;
    private bool _canConfirmModalRestore;

    private bool _isRestoreSourceModalOpen;
    private BackupHistoryDetail? _pendingRestoreDetail;
    private string _restoreSourceModalTitle = string.Empty;
    private string _restoreSourceModalMessage = string.Empty;
    private bool _activeRestoreFromCloud;
    private string _activeCloudProvider = "GoogleDrive";
    private bool _isPendingDetailSyncedToGoogleDrive;
    private bool _isPendingDetailSyncedToOneDrive;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RestoreSubViewModel(
        BackupService backupService,
        CloudManagerService cloudManager,
        DatabaseService databaseService,
        IDialogService dialogService,
        IAppEventBus eventBus,
        INativeDialogService? nativeDialog = null)
    {
        _backupService = backupService;
        _cloudManager = cloudManager;
        _databaseService = databaseService;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _nativeDialog = nativeDialog;

        OpenRestoreModalCommand = new RelayCommand(param => ExecuteOpenRestoreModal(param), _ => !IsModalRestoring);
        CloseRestoreModalCommand = new RelayCommand(_ => ExecuteCloseRestoreModal(), _ => !IsModalRestoring);
        ModalRestoreConfirmCommand = new RelayCommand(async _ => await ExecuteModalRestoreConfirmAsync(), _ => !IsModalRestoring && CanConfirmModalRestore);
        ModalSelectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalSelectAllRestoreItems(), _ => !IsModalRestoring);
        ModalDeselectAllRestoreItemsCommand = new RelayCommand(_ => ExecuteModalDeselectAllRestoreItems(), _ => !IsModalRestoring);
        ModalResetRestorePathsCommand = new RelayCommand(_ => ExecuteModalResetRestorePaths(), _ => !IsModalRestoring);
        ModalToggleRestoreItemCommand = new RelayCommand(param => ExecuteModalToggleRestoreItem(param as RestoreItemTarget), _ => !IsModalRestoring);
        ModalBrowseRestoreDestCommand = new RelayCommand(async param => await ExecuteModalBrowseRestoreDestAsync(param as RestoreItemTarget), _ => !IsModalRestoring);

        ConfirmRestoreFromLocalCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromLocal());
        ConfirmRestoreFromCloudCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloud());
        ConfirmRestoreFromGoogleDriveCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloudProvider("GoogleDrive"));
        ConfirmRestoreFromOneDriveCommand = new RelayCommand(_ => ExecuteConfirmRestoreFromCloudProvider("OneDrive"));
        CancelRestoreSourceModalCommand = new RelayCommand(_ => ExecuteCancelRestoreSourceModal());
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

    public BackupHistoryDetail? ActiveRestoreDetail
    {
        get => _activeRestoreDetail;
        set => SetField(ref _activeRestoreDetail, value);
    }

    public bool IsModalRestoring
    {
        get => _isModalRestoring;
        set
        {
            if (SetField(ref _isModalRestoring, value))
            {
                (ModalRestoreConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CloseRestoreModalCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
                (ModalRestoreConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

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

    public bool ActiveRestoreFromCloud
    {
        get => _activeRestoreFromCloud;
        set => SetField(ref _activeRestoreFromCloud, value);
    }

    public string ActiveCloudProvider
    {
        get => _activeCloudProvider;
        set => SetField(ref _activeCloudProvider, value);
    }

    public ICommand OpenRestoreModalCommand { get; }
    public ICommand CloseRestoreModalCommand { get; }
    public ICommand ModalRestoreConfirmCommand { get; }
    public ICommand ModalSelectAllRestoreItemsCommand { get; }
    public ICommand ModalDeselectAllRestoreItemsCommand { get; }
    public ICommand ModalResetRestorePathsCommand { get; }
    public ICommand ModalToggleRestoreItemCommand { get; }
    public ICommand ModalBrowseRestoreDestCommand { get; }

    public ICommand ConfirmRestoreFromLocalCommand { get; }
    public ICommand ConfirmRestoreFromCloudCommand { get; }
    public ICommand ConfirmRestoreFromGoogleDriveCommand { get; }
    public ICommand ConfirmRestoreFromOneDriveCommand { get; }
    public ICommand CancelRestoreSourceModalCommand { get; }

    public void ExecuteOpenRestoreModal(object? param)
    {
        if (param is BackupHistoryDetail detail)
        {
            var isLocalAvailable = detail.HasLocalBackup;
            var hasGdrive = detail.IsSyncedTo("GoogleDrive");
            var hasOnedrive = detail.IsSyncedTo("OneDrive");

            if (isLocalAvailable && (hasGdrive || hasOnedrive))
            {
                PendingRestoreDetail = detail;
                RestoreSourceModalTitle = $"Chọn nguồn khôi phục cho '{detail.GameName}'";
                var dateStr = detail.BackupDate.ToString("dd/MM/yyyy HH:mm");
                RestoreSourceModalMessage = $"Bản sao lưu ngày {dateStr} có sẵn cả trên máy tính và trên Cloud.\n\nBạn muốn khôi phục từ đâu?";
                IsPendingDetailSyncedToGoogleDrive = hasGdrive;
                IsPendingDetailSyncedToOneDrive = hasOnedrive;
                IsRestoreSourceModalOpen = true;
                return;
            }

            if (!isLocalAvailable && (hasGdrive || hasOnedrive))
            {
                _activeRestoreFromCloud = true;
                var chosenProvider = hasGdrive ? "GoogleDrive" : "OneDrive";
                SetupRestoreModalFromDetail(detail, isCloud: true, cloudProvider: chosenProvider);
                return;
            }

            _activeRestoreFromCloud = false;
            SetupRestoreModalFromDetail(detail, isCloud: false);
            return;
        }

        if (param is BackupRecord record)
        {
            _activeRestoreFromCloud = false;
            SetupRestoreModalFromRecord(record);
        }
    }

    public void ExecuteConfirmRestoreFromLocal()
    {
        IsRestoreSourceModalOpen = false;
        _activeRestoreFromCloud = false;
        if (PendingRestoreDetail != null)
        {
            SetupRestoreModalFromDetail(PendingRestoreDetail, isCloud: false);
            PendingRestoreDetail = null;
        }
    }

    public void ExecuteConfirmRestoreFromCloud()
    {
        ExecuteConfirmRestoreFromCloudProvider("GoogleDrive");
    }

    public void ExecuteConfirmRestoreFromCloudProvider(string providerName)
    {
        IsRestoreSourceModalOpen = false;
        _activeRestoreFromCloud = true;
        _activeCloudProvider = providerName;
        if (PendingRestoreDetail != null)
        {
            SetupRestoreModalFromDetail(PendingRestoreDetail, isCloud: true, cloudProvider: providerName);
            PendingRestoreDetail = null;
        }
    }

    public void ExecuteCancelRestoreSourceModal()
    {
        IsRestoreSourceModalOpen = false;
        PendingRestoreDetail = null;
    }

    private void SetupRestoreModalFromDetail(BackupHistoryDetail detail, bool isCloud, string? cloudProvider = null)
    {
        ActiveRestoreDetail = detail;
        ActiveRestoreRecord = null;
        ActiveRestoreFromCloud = isCloud;
        ActiveCloudProvider = isCloud ? (cloudProvider ?? "GoogleDrive") : "Local";
        ActiveRestoreItems.Clear();

        var items = _backupService.GetRestoreItemsFromBackup(detail);
        foreach (var itm in items)
        {
            itm.PropertyChanged += RestoreItem_PropertyChanged;
            ActiveRestoreItems.Add(itm);
        }

        UpdateModalRestoreStats();
        IsRestoreModalOpen = true;

        LoggingService.LogAction("Open_Restore_Modal", new { Game = detail.GameName, IsCloud = isCloud, CloudProvider = cloudProvider, Items = items.Count });
    }

    private void SetupRestoreModalFromRecord(BackupRecord record)
    {
        ActiveRestoreRecord = record;
        ActiveRestoreDetail = null;
        ActiveRestoreFromCloud = false;
        ActiveCloudProvider = "Local";
        ActiveRestoreItems.Clear();

        var items = _backupService.GetRestoreItemsFromBackup(record);
        foreach (var itm in items)
        {
            itm.PropertyChanged += RestoreItem_PropertyChanged;
            ActiveRestoreItems.Add(itm);
        }

        UpdateModalRestoreStats();
        IsRestoreModalOpen = true;

        LoggingService.LogAction("Open_Restore_Modal", new { Game = record.GameName, Items = items.Count });
    }

    private void RestoreItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreItemTarget.IsSelected))
        {
            UpdateModalRestoreStats();
        }
    }

    private void UpdateModalRestoreStats()
    {
        var selCount = ActiveRestoreItems.Count(x => x.IsSelected);
        var totalCount = ActiveRestoreItems.Count;
        ModalRestoreSelectedCountText = $"Đã chọn: {selCount} / {totalCount} vị trí";
        CanConfirmModalRestore = selCount > 0;
    }

    public void ExecuteCloseRestoreModal()
    {
        IsModalRestoring = false;
        IsRestoreModalOpen = false;
        ActiveRestoreRecord = null;
        ActiveRestoreDetail = null;
        ActiveRestoreItems.Clear();
        NotifyDialogRestoreClosed();
        LoggingService.LogAction("Close_Restore_Modal");
    }

    private void NotifyDialogRestoreClosed()
    {
        if (_dialogService is IBlazorDialogService blazorDialog)
        {
            blazorDialog.IsModalRestoring = false;
            blazorDialog.CloseRestore();
        }
    }

    private void ExecuteModalSelectAllRestoreItems()
    {
        foreach (var itm in ActiveRestoreItems) itm.IsSelected = true;
        UpdateModalRestoreStats();
    }

    private void ExecuteModalDeselectAllRestoreItems()
    {
        foreach (var itm in ActiveRestoreItems) itm.IsSelected = false;
        UpdateModalRestoreStats();
    }

    private void ExecuteModalResetRestorePaths()
    {
        foreach (var itm in ActiveRestoreItems)
        {
            itm.RestoreDestinationPath = PathResolverService.RemapPathToCurrentMachine(itm.OriginalSourcePath);
        }
        UpdateModalRestoreStats();
    }

    private void ExecuteModalToggleRestoreItem(RestoreItemTarget? item)
    {
        if (item == null) return;
        item.IsSelected = !item.IsSelected;
        UpdateModalRestoreStats();
    }

    private async Task ExecuteModalBrowseRestoreDestAsync(RestoreItemTarget? item)
    {
        if (item == null) return;
        var folder = _nativeDialog != null
            ? await _nativeDialog.PickFolderAsync($"Chọn thư mục đích mới để khôi phục cho: {item.DisplayTitle}")
            : null;

        if (!string.IsNullOrEmpty(folder))
        {
            item.RestoreDestinationPath = folder;
            LoggingService.LogAction("Restore_Change_Destination", new { Original = item.OriginalSourcePath, Custom = folder });
        }
    }

    public async Task ExecuteModalRestoreConfirmAsync()
    {
        var selectedItems = ActiveRestoreItems.Where(x => x.IsSelected).ToList();
        if (selectedItems.Count == 0) return;

        IsModalRestoring = true;
        if (_dialogService is IBlazorDialogService blazorDialog)
        {
            blazorDialog.IsModalRestoring = true;
        }
        ModalRestoreProgressPercent = 0;
        ModalRestoreProgressMessage = "Đang khởi tạo quá trình khôi phục...";

        var gameName = ActiveRestoreDetail?.GameName ?? ActiveRestoreRecord?.GameName ?? "Game";
        LoggingService.LogAction("Restore_Execute_Start", new { Game = gameName, SelectedCount = selectedItems.Count, FromCloud = _activeRestoreFromCloud });

        var initialSourceText = _activeRestoreFromCloud
            ? $"Nguồn: Đám mây ({(_activeCloudProvider.Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : "Google Drive")})"
            : "Nguồn: Bản sao lưu cục bộ trên máy";

        _dialogService.ShowProgress(
            $"Khôi Phục Save: {gameName}",
            "Đang chuẩn bị giải nén và khôi phục dữ liệu...",
            0,
            initialSourceText);

        var progress = new Progress<BackupProgress>(r =>
        {
            ModalRestoreProgressPercent = r.Percent;
            ModalRestoreProgressMessage = r.Message;
            var subText = !string.IsNullOrEmpty(r.CurrentFile) ? $"Tệp: {r.CurrentFile}" : initialSourceText;
            _dialogService.UpdateProgress(r.Percent, r.Message, subText);
        });

        try
        {
            if (ActiveRestoreDetail != null)
            {
                var cloudService = _activeRestoreFromCloud ? _cloudManager.GetProvider(_activeCloudProvider) : null;
                await _backupService.RestoreAsync(
                    ActiveRestoreDetail,
                    selectedItems,
                    restoreFromCloud: _activeRestoreFromCloud,
                    cloudService: cloudService,
                    progress: progress);
            }
            else if (ActiveRestoreRecord != null)
            {
                await _backupService.RestoreAsync(ActiveRestoreRecord, selectedItems, progress);
            }

            IsModalRestoring = false;
            _dialogService.CloseProgress();
            ExecuteCloseRestoreModal();

            _eventBus.Publish(new HistoryChangedEvent());

            var hasRevert = RevertService.HasRevertPoint(gameName);
            var successMsg = hasRevert
                ? $"Đã khôi phục thành công {selectedItems.Count} vị trí save game cho '{gameName}'.\n\n(Dữ liệu save cũ đã được lưu dự phòng, bạn có thể bấm 'Hoàn Tác' trong chi tiết game bất cứ lúc nào)."
                : $"Đã khôi phục thành công {selectedItems.Count} vị trí save game cho '{gameName}'.";
            _dialogService.ShowMessage("Khôi Phục Thành Công", successMsg, "Success");
            LoggingService.LogAction("Restore_Execute_Success", new { Game = gameName, RestoredCount = selectedItems.Count, HasRevertPoint = hasRevert });
        }
        catch (Exception ex)
        {
            IsModalRestoring = false;
            _dialogService.CloseProgress();
            LoggingService.Error(ex, "Lỗi khôi phục {Game}: {Message}", gameName, ex.Message);
            _dialogService.ShowMessage("Lỗi Ngoại Lệ Khôi Phục", ex.Message, "Error", ex.StackTrace);
        }
        finally
        {
            IsModalRestoring = false;
            _dialogService.CloseProgress();
            NotifyDialogRestoreClosed();
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
