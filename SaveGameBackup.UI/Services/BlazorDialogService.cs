using System.ComponentModel;
using System.Runtime.CompilerServices;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.UI.Services;

public interface IBlazorDialogService : INotifyPropertyChanged
{
    // 1. Game Details Modal (Image 3.JPG)
    bool IsDetailsModalOpen { get; }
    GameHistoryEntry? DetailsGameHistory { get; }
    List<BackupHistoryDetail> CurrentGameDetails { get; }
    BackupHistoryDetail? SelectedDetail { get; set; }
    void OpenDetails(GameHistoryEntry gameHistory, List<BackupHistoryDetail> details);
    void CloseDetails();

    // 2. Restore Modal (Image 4.JPG)
    bool IsRestoreModalOpen { get; }
    BackupHistoryDetail? ActiveRestoreDetail { get; }
    List<RestoreItemTarget> ActiveRestoreItems { get; }
    bool ActiveRestoreFromCloud { get; }
    string ActiveCloudProvider { get; }
    bool IsModalRestoring { get; set; }
    int RestoreProgressPercent { get; set; }
    string RestoreProgressMessage { get; set; }
    void OpenRestore(BackupHistoryDetail detail, List<RestoreItemTarget> items, bool fromCloud = false, string cloudProvider = "GoogleDrive");
    void CloseRestore();

    // 3. Sync Cloud Select Modal (Image 5.JPG)
    bool IsSyncCloudSelectModalOpen { get; }
    BackupHistoryDetail? TargetSyncDetail { get; }
    void OpenSyncCloudSelect(BackupHistoryDetail detail);
    void CloseSyncCloudSelect();

    // 4. Restore Source Modal
    bool IsRestoreSourceModalOpen { get; }
    BackupHistoryDetail? PendingRestoreSourceDetail { get; }
    void OpenRestoreSource(BackupHistoryDetail detail);
    void CloseRestoreSource();

    // 5. Universal Message Modal (Image 6.JPG)
    bool IsMessageModalOpen { get; }
    string MessageTitle { get; }
    string MessageContent { get; }
    string MessageType { get; } // Success, Info, Warning, Error
    string? MessageActionUrl { get; }
    string? MessageActionText { get; }
    void ShowMessage(string title, string content, string type = "Info", string? actionUrl = null, string? actionText = null);
    void CloseMessage();

    // 6. Confirm Danger Modal (Image 7.JPG)
    bool IsConfirmModalOpen { get; }
    string ConfirmTitle { get; }
    string ConfirmMessage { get; }
    string ConfirmTargetPath { get; }
    bool ShowDeleteCloudOption { get; }
    bool DeleteAlsoFromCloud { get; set; }
    bool IsConfirmDeleting { get; set; }
    int ConfirmProgressPercent { get; set; }
    string ConfirmProgressText { get; set; }
    Func<Task>? OnConfirmAction { get; }

    ConfirmDeleteType DeleteType { get; }
    BackupHistoryDetail? TargetDetail { get; }
    GameHistoryEntry? TargetGameHistory { get; }
    bool DeleteLocalChecked { get; set; }
    Dictionary<string, bool> CloudDeleteSelections { get; }
    string SecurityInputText { get; set; }

    void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false);
    void ShowConfirmDeleteSnapshot(BackupHistoryDetail detail, Func<bool, List<string>, Task> onConfirm);
    void ShowConfirmDeleteGame(GameHistoryEntry gameHistory, Func<bool, bool, Task> onConfirm);
    Task ExecuteConfirmActionAsync();
    void CloseConfirm();

    // 7. Progress Modal
    bool IsProgressModalOpen { get; }
    string ProgressTitle { get; }
    string ProgressMessage { get; }
    string ProgressSubText { get; }
    int ProgressPercent { get; }
    Action? OnCancelProgressAction { get; }
    void ShowProgress(string title, string message = "", int percent = 0, string subText = "", Action? onCancel = null);
    void UpdateProgress(int percent, string? message = null, string? subText = null);
    void CloseProgress();

    // Toast Notification
    string? ActiveToastMessage { get; }
    string ActiveToastType { get; }
    bool HasToast { get; }
    void ShowToast(string message, string type = "Success");
    void HideToast();

    // Master Modal state
    bool IsAnyModalOpen { get; }
    void CloseTopModal();
}

public class BlazorDialogService : IBlazorDialogService
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // 1. Details
    public bool IsDetailsModalOpen { get; private set; }
    public GameHistoryEntry? DetailsGameHistory { get; private set; }
    public List<BackupHistoryDetail> CurrentGameDetails { get; private set; } = new();
    public BackupHistoryDetail? SelectedDetail { get; set; }

    public void OpenDetails(GameHistoryEntry gameHistory, List<BackupHistoryDetail> details)
    {
        DetailsGameHistory = gameHistory;
        CurrentGameDetails = details;
        SelectedDetail = details.FirstOrDefault();
        IsDetailsModalOpen = true;
        Notify(nameof(IsDetailsModalOpen));
        Notify(nameof(DetailsGameHistory));
        Notify(nameof(CurrentGameDetails));
        Notify(nameof(SelectedDetail));
        Notify(nameof(IsAnyModalOpen));
    }

    public void CloseDetails()
    {
        IsDetailsModalOpen = false;
        Notify(nameof(IsDetailsModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // 2. Restore
    public bool IsRestoreModalOpen { get; private set; }
    public BackupHistoryDetail? ActiveRestoreDetail { get; private set; }
    public List<RestoreItemTarget> ActiveRestoreItems { get; private set; } = new();
    public bool ActiveRestoreFromCloud { get; private set; }
    public string ActiveCloudProvider { get; private set; } = "GoogleDrive";
    public bool IsModalRestoring { get; set; }
    public int RestoreProgressPercent { get; set; }
    public string RestoreProgressMessage { get; set; } = string.Empty;

    public void OpenRestore(BackupHistoryDetail detail, List<RestoreItemTarget> items, bool fromCloud = false, string cloudProvider = "GoogleDrive")
    {
        ActiveRestoreDetail = detail;
        ActiveRestoreItems = items;
        ActiveRestoreFromCloud = fromCloud;
        ActiveCloudProvider = cloudProvider;
        IsModalRestoring = false;
        RestoreProgressPercent = 0;
        RestoreProgressMessage = string.Empty;
        IsRestoreModalOpen = true;
        Notify(nameof(IsRestoreModalOpen));
        Notify(nameof(ActiveRestoreDetail));
        Notify(nameof(ActiveRestoreItems));
        Notify(nameof(ActiveRestoreFromCloud));
        Notify(nameof(ActiveCloudProvider));
        Notify(nameof(IsAnyModalOpen));
    }

    public void CloseRestore()
    {
        IsModalRestoring = false;
        IsRestoreModalOpen = false;
        Notify(nameof(IsRestoreModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // 3. Sync Cloud Select
    public bool IsSyncCloudSelectModalOpen { get; private set; }
    public BackupHistoryDetail? TargetSyncDetail { get; private set; }

    public void OpenSyncCloudSelect(BackupHistoryDetail detail)
    {
        TargetSyncDetail = detail;
        IsSyncCloudSelectModalOpen = true;
        Notify(nameof(IsSyncCloudSelectModalOpen));
        Notify(nameof(TargetSyncDetail));
        Notify(nameof(IsAnyModalOpen));
    }

    public void CloseSyncCloudSelect()
    {
        IsSyncCloudSelectModalOpen = false;
        Notify(nameof(IsSyncCloudSelectModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // 4. Restore Source Modal
    public bool IsRestoreSourceModalOpen { get; private set; }
    public BackupHistoryDetail? PendingRestoreSourceDetail { get; private set; }

    public void OpenRestoreSource(BackupHistoryDetail detail)
    {
        PendingRestoreSourceDetail = detail;
        IsRestoreSourceModalOpen = true;
        Notify(nameof(IsRestoreSourceModalOpen));
        Notify(nameof(PendingRestoreSourceDetail));
        Notify(nameof(IsAnyModalOpen));
    }

    public void CloseRestoreSource()
    {
        IsRestoreSourceModalOpen = false;
        Notify(nameof(IsRestoreSourceModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // 5. Message
    public bool IsMessageModalOpen { get; private set; }
    public string MessageTitle { get; private set; } = string.Empty;
    public string MessageContent { get; private set; } = string.Empty;
    public string MessageType { get; private set; } = "Info";

    public string? MessageActionUrl { get; private set; }
    public string? MessageActionText { get; private set; }

    public void ShowMessage(string title, string content, string type = "Info", string? actionUrl = null, string? actionText = null)
    {
        MessageTitle = title;
        MessageContent = content;
        MessageType = type;
        MessageActionUrl = actionUrl;
        MessageActionText = actionText;
        IsMessageModalOpen = true;
        Notify(nameof(IsMessageModalOpen));
        Notify(nameof(MessageTitle));
        Notify(nameof(MessageContent));
        Notify(nameof(MessageType));
        Notify(nameof(MessageActionUrl));
        Notify(nameof(MessageActionText));
        Notify(nameof(IsAnyModalOpen));
    }

    public void CloseMessage()
    {
        IsMessageModalOpen = false;
        MessageActionUrl = null;
        MessageActionText = null;
        Notify(nameof(IsMessageModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // 6. Confirm
    public bool IsConfirmModalOpen { get; private set; }
    public string ConfirmTitle { get; private set; } = string.Empty;
    public string ConfirmMessage { get; private set; } = string.Empty;
    public string ConfirmTargetPath { get; private set; } = string.Empty;
    public bool ShowDeleteCloudOption { get; private set; }
    public bool DeleteAlsoFromCloud { get; set; }
    public bool IsConfirmDeleting { get; set; }
    public int ConfirmProgressPercent { get; set; }
    public string ConfirmProgressText { get; set; } = string.Empty;
    public Func<Task>? OnConfirmAction { get; private set; }

    public ConfirmDeleteType DeleteType { get; private set; } = ConfirmDeleteType.None;
    public BackupHistoryDetail? TargetDetail { get; private set; }
    public GameHistoryEntry? TargetGameHistory { get; private set; }
    public bool DeleteLocalChecked { get; set; } = true;
    public Dictionary<string, bool> CloudDeleteSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SecurityInputText { get; set; } = string.Empty;

    private Func<bool, List<string>, Task>? _pendingSnapshotConfirmAction;
    private Func<bool, bool, Task>? _pendingGameConfirmAction;

    public void ShowConfirmDeleteSnapshot(BackupHistoryDetail detail, Func<bool, List<string>, Task> onConfirm)
    {
        DeleteType = ConfirmDeleteType.Snapshot;
        TargetDetail = detail;
        TargetGameHistory = null;
        ConfirmTitle = "XÁC NHẬN XÓA BẢN SAO LƯU";
        ConfirmMessage = $"Bạn đang chuẩn bị xóa bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm} của game '{detail.GameName}'.\nVui lòng chọn các vị trí lưu trữ muốn xóa:";
        ConfirmTargetPath = detail.LocalBackupPath;

        DeleteLocalChecked = false;
        CloudDeleteSelections.Clear();
        foreach (var provider in detail.CloudUrls.Keys)
        {
            CloudDeleteSelections[provider] = false;
        }
        foreach (var cs in detail.CloudSyncList)
        {
            if (!string.IsNullOrEmpty(cs.Provider) && !CloudDeleteSelections.ContainsKey(cs.Provider))
            {
                CloudDeleteSelections[cs.Provider] = false;
            }
        }

        SecurityInputText = string.Empty;
        ShowDeleteCloudOption = false;
        DeleteAlsoFromCloud = false;

        _pendingSnapshotConfirmAction = onConfirm;
        _pendingGameConfirmAction = null;
        OnConfirmAction = null;

        IsConfirmDeleting = false;
        ConfirmProgressPercent = 0;
        ConfirmProgressText = string.Empty;
        IsConfirmModalOpen = true;

        Notify(nameof(IsConfirmModalOpen));
        Notify(nameof(ConfirmTitle));
        Notify(nameof(ConfirmMessage));
        Notify(nameof(ConfirmTargetPath));
        Notify(nameof(DeleteType));
        Notify(nameof(TargetDetail));
        Notify(nameof(DeleteLocalChecked));
        Notify(nameof(DeleteAlsoFromCloud));
        Notify(nameof(CloudDeleteSelections));
        Notify(nameof(SecurityInputText));
        Notify(nameof(IsAnyModalOpen));
    }

    public void ShowConfirmDeleteGame(GameHistoryEntry gameHistory, Func<bool, bool, Task> onConfirm)
    {
        DeleteType = ConfirmDeleteType.Game;
        TargetDetail = null;
        TargetGameHistory = gameHistory;
        ConfirmTitle = "CẢNH BÁO NGUY HIỂM: XÓA TOÀN BỘ GAME";
        ConfirmMessage = $"Bạn đang yêu cầu xóa TOÀN BỘ lịch sử và tất cả {gameHistory.BackupCount} bản sao lưu của game '{gameHistory.GameName}'.\nĐể tiếp tục, vui lòng nhập chính xác 'Delete All' vào ô bên dưới:";
        ConfirmTargetPath = gameHistory.LatestBackupPath;

        DeleteLocalChecked = true;
        ShowDeleteCloudOption = true;
        DeleteAlsoFromCloud = false;
        SecurityInputText = string.Empty;
        CloudDeleteSelections.Clear();

        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = onConfirm;
        OnConfirmAction = null;

        IsConfirmDeleting = false;
        ConfirmProgressPercent = 0;
        ConfirmProgressText = string.Empty;
        IsConfirmModalOpen = true;

        Notify(nameof(IsConfirmModalOpen));
        Notify(nameof(ConfirmTitle));
        Notify(nameof(ConfirmMessage));
        Notify(nameof(ConfirmTargetPath));
        Notify(nameof(DeleteType));
        Notify(nameof(TargetGameHistory));
        Notify(nameof(DeleteLocalChecked));
        Notify(nameof(DeleteAlsoFromCloud));
        Notify(nameof(SecurityInputText));
        Notify(nameof(IsAnyModalOpen));
    }

    public void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false)
    {
        DeleteType = ConfirmDeleteType.General;
        TargetDetail = null;
        TargetGameHistory = null;
        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = null;

        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmTargetPath = targetPath;
        OnConfirmAction = onConfirm;
        ShowDeleteCloudOption = showCloudOption;
        DeleteAlsoFromCloud = false;
        DeleteLocalChecked = false;
        SecurityInputText = string.Empty;
        CloudDeleteSelections.Clear();
        IsConfirmDeleting = false;
        ConfirmProgressPercent = 0;
        ConfirmProgressText = string.Empty;
        IsConfirmModalOpen = true;
        Notify(nameof(IsConfirmModalOpen));
        Notify(nameof(ConfirmTitle));
        Notify(nameof(ConfirmMessage));
        Notify(nameof(ConfirmTargetPath));
        Notify(nameof(ShowDeleteCloudOption));
        Notify(nameof(DeleteAlsoFromCloud));
        Notify(nameof(DeleteLocalChecked));
        Notify(nameof(SecurityInputText));
        Notify(nameof(DeleteType));
        Notify(nameof(IsAnyModalOpen));
    }

    public async Task ExecuteConfirmActionAsync()
    {
        try
        {
            IsConfirmModalOpen = false;
            Notify(nameof(IsConfirmModalOpen));
            Notify(nameof(IsAnyModalOpen));

            if (DeleteType == ConfirmDeleteType.Snapshot && _pendingSnapshotConfirmAction != null)
            {
                var action = _pendingSnapshotConfirmAction;
                var delLocal = TargetDetail?.HasLocalBackup == true && DeleteLocalChecked;
                var clouds = CloudDeleteSelections.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                _pendingSnapshotConfirmAction = null;
                await action.Invoke(delLocal, clouds);
            }
            else if (DeleteType == ConfirmDeleteType.Game && _pendingGameConfirmAction != null)
            {
                var action = _pendingGameConfirmAction;
                var delLocal = DeleteLocalChecked;
                var delCloud = DeleteAlsoFromCloud;
                _pendingGameConfirmAction = null;
                await action.Invoke(delLocal, delCloud);
            }
            else if (OnConfirmAction != null)
            {
                var action = OnConfirmAction;
                OnConfirmAction = null;
                await action.Invoke();
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Lỗi thực hiện", ex.Message, "Error");
        }
        finally
        {
            CloseConfirm();
        }
    }

    public void CloseConfirm()
    {
        if (IsConfirmDeleting) return;
        IsConfirmModalOpen = false;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmTargetPath = string.Empty;
        OnConfirmAction = null;
        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = null;
        DeleteType = ConfirmDeleteType.None;
        TargetDetail = null;
        TargetGameHistory = null;
        SecurityInputText = string.Empty;
        ShowDeleteCloudOption = false;
        DeleteAlsoFromCloud = false;
        DeleteLocalChecked = false;
        CloudDeleteSelections.Clear();
        IsConfirmDeleting = false;
        ConfirmProgressPercent = 0;
        ConfirmProgressText = string.Empty;
        Notify(nameof(IsConfirmModalOpen));
        Notify(nameof(ConfirmTitle));
        Notify(nameof(ConfirmMessage));
        Notify(nameof(ConfirmTargetPath));
        Notify(nameof(ShowDeleteCloudOption));
        Notify(nameof(DeleteAlsoFromCloud));
        Notify(nameof(DeleteLocalChecked));
        Notify(nameof(DeleteType));
        Notify(nameof(TargetDetail));
        Notify(nameof(TargetGameHistory));
        Notify(nameof(SecurityInputText));
        Notify(nameof(IsAnyModalOpen));
    }

    // 7. Progress
    public bool IsProgressModalOpen { get; private set; }
    public string ProgressTitle { get; private set; } = string.Empty;
    public string ProgressMessage { get; private set; } = string.Empty;
    public string ProgressSubText { get; private set; } = string.Empty;
    public int ProgressPercent { get; private set; }
    public Action? OnCancelProgressAction { get; private set; }

    public void ShowProgress(string title, string message = "", int percent = 0, string subText = "", Action? onCancel = null)
    {
        ProgressTitle = title;
        ProgressMessage = message;
        ProgressPercent = percent;
        ProgressSubText = subText;
        OnCancelProgressAction = onCancel;
        IsProgressModalOpen = true;
        Notify(nameof(IsProgressModalOpen));
        Notify(nameof(ProgressTitle));
        Notify(nameof(ProgressMessage));
        Notify(nameof(ProgressPercent));
        Notify(nameof(ProgressSubText));
        Notify(nameof(IsAnyModalOpen));
    }

    public void UpdateProgress(int percent, string? message = null, string? subText = null)
    {
        ProgressPercent = percent;
        if (message != null) ProgressMessage = message;
        if (subText != null) ProgressSubText = subText;
        Notify(nameof(ProgressPercent));
        Notify(nameof(ProgressMessage));
        Notify(nameof(ProgressSubText));
    }

    public void CloseProgress()
    {
        IsProgressModalOpen = false;
        Notify(nameof(IsProgressModalOpen));
        Notify(nameof(IsAnyModalOpen));
    }

    // Toast
    public string? ActiveToastMessage { get; private set; }
    public string ActiveToastType { get; private set; } = "Success";
    public bool HasToast => !string.IsNullOrEmpty(ActiveToastMessage);

    public void ShowToast(string message, string type = "Success")
    {
        ActiveToastMessage = message;
        ActiveToastType = type;
        Notify(nameof(ActiveToastMessage));
        Notify(nameof(ActiveToastType));
        Notify(nameof(HasToast));

        Task.Delay(3500).ContinueWith(_ => HideToast());
    }

    public void HideToast()
    {
        ActiveToastMessage = null;
        Notify(nameof(ActiveToastMessage));
        Notify(nameof(HasToast));
    }

    // Master
    public bool IsAnyModalOpen =>
        IsMessageModalOpen ||
        IsConfirmModalOpen ||
        IsProgressModalOpen ||
        IsDetailsModalOpen ||
        IsRestoreModalOpen ||
        IsRestoreSourceModalOpen ||
        IsSyncCloudSelectModalOpen;

    public void CloseTopModal()
    {
        if (IsMessageModalOpen) { CloseMessage(); return; }
        if (IsConfirmModalOpen && !IsConfirmDeleting) { CloseConfirm(); return; }
        if (IsSyncCloudSelectModalOpen) { CloseSyncCloudSelect(); return; }
        if (IsRestoreSourceModalOpen) { CloseRestoreSource(); return; }
        if (IsRestoreModalOpen && !IsModalRestoring) { CloseRestore(); return; }
        if (IsDetailsModalOpen) { CloseDetails(); return; }
    }
}
