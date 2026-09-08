using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.UI.Services;

public enum ConfirmDeleteType
{
    None,
    Snapshot,
    Game,
    General
}

public interface IDialogService : INotifyPropertyChanged
{
    // Universal Message Popup
    bool IsMessageModalOpen { get; }
    string MessageModalTitle { get; }
    string MessageModalContent { get; }
    string MessageModalType { get; }
    string? MessageModalDetails { get; }
    bool HasMessageModalDetails { get; }
    ICommand CloseMessageModalCommand { get; }

    string? MessageActionUrl { get; }
    string? MessageActionText { get; }
    void ShowMessage(string title, string content, string type = "Info", string? details = null, string? actionUrl = null, string? actionText = null);
    void CloseMessage();

    // Confirm Modal
    bool IsConfirmModalOpen { get; }
    string ConfirmModalTitle { get; }
    string ConfirmModalMessage { get; }
    string ConfirmModalTargetPath { get; }
    bool ShowDeleteCloudOption { get; set; }
    bool DeleteAlsoFromCloud { get; set; }
    bool IsConfirmModalDeleting { get; }
    int ConfirmModalProgressPercent { get; }
    string ConfirmModalProgressText { get; }
    ICommand ConfirmModalExecuteCommand { get; }
    ICommand CancelConfirmModalCommand { get; }

    // Specialized Delete Confirmations
    ConfirmDeleteType DeleteType { get; }
    SaveGameBackup.Core.Models.BackupHistoryDetail? TargetDetail { get; }
    SaveGameBackup.Core.Models.GameHistoryEntry? TargetGameHistory { get; }
    bool DeleteLocalChecked { get; set; }
    Dictionary<string, bool> CloudDeleteSelections { get; }
    string SecurityInputText { get; set; }

    void ShowConfirmDeleteSnapshot(SaveGameBackup.Core.Models.BackupHistoryDetail detail, Func<bool, List<string>, Task> onConfirm);
    void ShowConfirmDeleteGame(SaveGameBackup.Core.Models.GameHistoryEntry gameHistory, Func<bool, bool, Task> onConfirm);

    void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false);
    void CancelConfirm();
    void SetConfirmDeletingState(bool isDeleting, int percent = 0, string text = "");
    Task ExecuteConfirmActionAsync();

    // Progress Modal
    bool IsProgressModalOpen { get; }
    string ActiveProgressTitle { get; }
    string ActiveProgressMessage { get; }
    int ActiveProgressPercent { get; }
    string ActiveProgressSubText { get; }
    bool CanCancelProgress { get; }
    Action? OnCancelProgressAction { get; }
    void CancelActiveProgress();

    void ShowProgress(string title, string message = "", int percent = 0, string subText = "", Action? onCancel = null);
    void UpdateProgress(int percent, string? message = null, string? subText = null);
    void CloseProgress();
}

public class DialogService : IDialogService
{
    // Universal Message Popup States
    private bool _isMessageModalOpen;
    private string _messageModalTitle = string.Empty;
    private string _messageModalContent = string.Empty;
    private string _messageModalType = "Info";
    private string? _messageModalDetails;

    // Confirm Modal States
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

    private ConfirmDeleteType _deleteType = ConfirmDeleteType.None;
    private SaveGameBackup.Core.Models.BackupHistoryDetail? _targetDetail;
    private SaveGameBackup.Core.Models.GameHistoryEntry? _targetGameHistory;
    private bool _deleteLocalChecked = true;
    private readonly Dictionary<string, bool> _cloudDeleteSelections = new(StringComparer.OrdinalIgnoreCase);
    private string _securityInputText = string.Empty;
    private Func<bool, List<string>, Task>? _pendingSnapshotConfirmAction;
    private Func<bool, bool, Task>? _pendingGameConfirmAction;

    // Progress Modal States
    private bool _isProgressModalOpen;
    private string _activeProgressTitle = "Đang xử lý...";
    private string _activeProgressMessage = string.Empty;
    private int _activeProgressPercent;
    private string _activeProgressSubText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DialogService()
    {
        CloseMessageModalCommand = new RelayCommand(_ => CloseMessage());
        CancelConfirmModalCommand = new RelayCommand(_ => CancelConfirm(), _ => !IsConfirmModalDeleting);
        ConfirmModalExecuteCommand = new RelayCommand(async _ => await ExecuteConfirmActionAsync(), _ => !IsConfirmModalDeleting);
    }

    // --- Message Popup Properties ---
    public bool IsMessageModalOpen
    {
        get => _isMessageModalOpen;
        private set => SetField(ref _isMessageModalOpen, value);
    }

    public string MessageModalTitle
    {
        get => _messageModalTitle;
        private set => SetField(ref _messageModalTitle, value);
    }

    public string MessageModalContent
    {
        get => _messageModalContent;
        private set => SetField(ref _messageModalContent, value);
    }

    public string MessageModalType
    {
        get => _messageModalType;
        private set => SetField(ref _messageModalType, value);
    }

    public string? MessageModalDetails
    {
        get => _messageModalDetails;
        private set
        {
            if (SetField(ref _messageModalDetails, value))
            {
                OnPropertyChanged(nameof(HasMessageModalDetails));
            }
        }
    }

    public bool HasMessageModalDetails => !string.IsNullOrWhiteSpace(MessageModalDetails);

    public ICommand CloseMessageModalCommand { get; }

    private string? _messageActionUrl;
    private string? _messageActionText;

    public string? MessageActionUrl
    {
        get => _messageActionUrl;
        private set => SetField(ref _messageActionUrl, value);
    }

    public string? MessageActionText
    {
        get => _messageActionText;
        private set => SetField(ref _messageActionText, value);
    }

    public void ShowMessage(string title, string content, string type = "Info", string? details = null, string? actionUrl = null, string? actionText = null)
    {
        MessageModalTitle = title;
        MessageModalContent = content;
        MessageModalType = type;
        MessageModalDetails = details;
        MessageActionUrl = actionUrl;
        MessageActionText = actionText;
        IsMessageModalOpen = true;

        LoggingService.LogAction("Dialog_ShowMessage", new { Title = title, Type = type, HasDetails = !string.IsNullOrEmpty(details) });
    }

    public void CloseMessage()
    {
        IsMessageModalOpen = false;
        MessageModalDetails = null;
        MessageActionUrl = null;
        MessageActionText = null;
        LoggingService.LogAction("Dialog_CloseMessage");
    }

    // --- Confirm Modal Properties ---
    public bool IsConfirmModalOpen
    {
        get => _isConfirmModalOpen;
        private set => SetField(ref _isConfirmModalOpen, value);
    }

    public string ConfirmModalTitle
    {
        get => _confirmModalTitle;
        private set => SetField(ref _confirmModalTitle, value);
    }

    public string ConfirmModalMessage
    {
        get => _confirmModalMessage;
        private set => SetField(ref _confirmModalMessage, value);
    }

    public string ConfirmModalTargetPath
    {
        get => _confirmModalTargetPath;
        private set => SetField(ref _confirmModalTargetPath, value);
    }

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
        private set => SetField(ref _isConfirmModalDeleting, value);
    }

    public int ConfirmModalProgressPercent
    {
        get => _confirmModalProgressPercent;
        private set => SetField(ref _confirmModalProgressPercent, value);
    }

    public string ConfirmModalProgressText
    {
        get => _confirmModalProgressText;
        private set => SetField(ref _confirmModalProgressText, value);
    }

    public ConfirmDeleteType DeleteType
    {
        get => _deleteType;
        private set => SetField(ref _deleteType, value);
    }

    public SaveGameBackup.Core.Models.BackupHistoryDetail? TargetDetail
    {
        get => _targetDetail;
        private set => SetField(ref _targetDetail, value);
    }

    public SaveGameBackup.Core.Models.GameHistoryEntry? TargetGameHistory
    {
        get => _targetGameHistory;
        private set => SetField(ref _targetGameHistory, value);
    }

    public bool DeleteLocalChecked
    {
        get => _deleteLocalChecked;
        set => SetField(ref _deleteLocalChecked, value);
    }

    public Dictionary<string, bool> CloudDeleteSelections => _cloudDeleteSelections;

    public string SecurityInputText
    {
        get => _securityInputText;
        set => SetField(ref _securityInputText, value);
    }

    public ICommand ConfirmModalExecuteCommand { get; }
    public ICommand CancelConfirmModalCommand { get; }

    public void ShowConfirmDeleteSnapshot(SaveGameBackup.Core.Models.BackupHistoryDetail detail, Func<bool, List<string>, Task> onConfirm)
    {
        DeleteType = ConfirmDeleteType.Snapshot;
        TargetDetail = detail;
        TargetGameHistory = null;
        ConfirmModalTitle = "XÁC NHẬN XÓA BẢN SAO LƯU";
        ConfirmModalMessage = $"Bạn đang chuẩn bị xóa bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm} của game '{detail.GameName}'.\nVui lòng chọn các vị trí lưu trữ muốn xóa:";
        ConfirmModalTargetPath = detail.LocalBackupPath;

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
        _pendingConfirmAction = null;

        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;
        IsConfirmModalOpen = true;

        OnPropertyChanged(nameof(CloudDeleteSelections));
        OnPropertyChanged(nameof(DeleteLocalChecked));
        OnPropertyChanged(nameof(DeleteAlsoFromCloud));
        OnPropertyChanged(nameof(SecurityInputText));
        LoggingService.LogAction("Dialog_ShowConfirmDeleteSnapshot", new { Game = detail.GameName, DetailId = detail.Id });
    }

    public void ShowConfirmDeleteGame(SaveGameBackup.Core.Models.GameHistoryEntry gameHistory, Func<bool, bool, Task> onConfirm)
    {
        DeleteType = ConfirmDeleteType.Game;
        TargetDetail = null;
        TargetGameHistory = gameHistory;
        ConfirmModalTitle = "CẢNH BÁO NGUY HIỂM: XÓA TOÀN BỘ GAME";
        ConfirmModalMessage = $"Bạn đang yêu cầu xóa TOÀN BỘ lịch sử và tất cả {gameHistory.BackupCount} bản sao lưu của game '{gameHistory.GameName}'.\nĐể tiếp tục, vui lòng nhập chính xác 'Delete All' vào ô bên dưới:";
        ConfirmModalTargetPath = gameHistory.LatestBackupPath;

        DeleteLocalChecked = true;
        ShowDeleteCloudOption = true;
        DeleteAlsoFromCloud = false;
        SecurityInputText = string.Empty;
        CloudDeleteSelections.Clear();

        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = onConfirm;
        _pendingConfirmAction = null;

        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;
        IsConfirmModalOpen = true;

        OnPropertyChanged(nameof(DeleteAlsoFromCloud));
        OnPropertyChanged(nameof(DeleteLocalChecked));
        OnPropertyChanged(nameof(SecurityInputText));
        LoggingService.LogAction("Dialog_ShowConfirmDeleteGame", new { Game = gameHistory.GameName });
    }

    public void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false)
    {
        DeleteType = ConfirmDeleteType.General;
        TargetDetail = null;
        TargetGameHistory = null;
        ConfirmModalTitle = title;
        ConfirmModalMessage = message;
        ConfirmModalTargetPath = targetPath;
        _pendingConfirmAction = onConfirm;
        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = null;
        ShowDeleteCloudOption = showCloudOption;
        DeleteAlsoFromCloud = false;
        DeleteLocalChecked = false;
        SecurityInputText = string.Empty;
        CloudDeleteSelections.Clear();
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;
        IsConfirmModalOpen = true;

        OnPropertyChanged(nameof(DeleteAlsoFromCloud));
        OnPropertyChanged(nameof(DeleteLocalChecked));
        OnPropertyChanged(nameof(SecurityInputText));
        LoggingService.LogAction("Dialog_ShowConfirm", new { Title = title, TargetPath = targetPath, ShowCloudOption = showCloudOption });
    }

    public void CancelConfirm()
    {
        if (IsConfirmModalDeleting) return;
        IsConfirmModalOpen = false;
        _pendingConfirmAction = null;
        _pendingSnapshotConfirmAction = null;
        _pendingGameConfirmAction = null;
        DeleteType = ConfirmDeleteType.None;
        TargetDetail = null;
        TargetGameHistory = null;
        SecurityInputText = string.Empty;
        ConfirmModalTitle = string.Empty;
        ConfirmModalMessage = string.Empty;
        ConfirmModalTargetPath = string.Empty;
        ShowDeleteCloudOption = false;
        DeleteAlsoFromCloud = false;
        DeleteLocalChecked = false;
        CloudDeleteSelections.Clear();
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;
        LoggingService.LogAction("Dialog_CancelConfirm");
    }

    public void SetConfirmDeletingState(bool isDeleting, int percent = 0, string text = "")
    {
        IsConfirmModalDeleting = isDeleting;
        ConfirmModalProgressPercent = percent;
        ConfirmModalProgressText = text;
    }

    public async Task ExecuteConfirmActionAsync()
    {
        try
        {
            // Close the confirm dialog BEFORE running the action so ProgressModal displays immediately!
            IsConfirmModalOpen = false;

            if (DeleteType == ConfirmDeleteType.Snapshot && _pendingSnapshotConfirmAction != null)
            {
                var action = _pendingSnapshotConfirmAction;
                var delLocal = TargetDetail?.HasLocalBackup == true && DeleteLocalChecked;
                var clouds = CloudDeleteSelections.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                _pendingSnapshotConfirmAction = null;
                LoggingService.LogAction("Dialog_ConfirmDeleteSnapshotExecuted", new { delLocal, cloudsCount = clouds.Count });
                await action.Invoke(delLocal, clouds);
            }
            else if (DeleteType == ConfirmDeleteType.Game && _pendingGameConfirmAction != null)
            {
                var action = _pendingGameConfirmAction;
                var delLocal = DeleteLocalChecked;
                var delCloud = DeleteAlsoFromCloud;
                _pendingGameConfirmAction = null;
                LoggingService.LogAction("Dialog_ConfirmDeleteGameExecuted", new { delLocal, delCloud });
                await action.Invoke(delLocal, delCloud);
            }
            else if (_pendingConfirmAction != null)
            {
                var action = _pendingConfirmAction;
                _pendingConfirmAction = null;
                LoggingService.LogAction("Dialog_ConfirmExecuted", new { Title = ConfirmModalTitle, DeleteAlsoFromCloud });
                await action.Invoke();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi thực thi hành động xác nhận: {Message}", ex.Message);
            ShowMessage("Lỗi thực hiện", ex.Message, "Error", ex.StackTrace);
        }
        finally
        {
            IsConfirmModalOpen = false;
            _pendingConfirmAction = null;
            _pendingSnapshotConfirmAction = null;
            _pendingGameConfirmAction = null;
            DeleteType = ConfirmDeleteType.None;
            TargetDetail = null;
            TargetGameHistory = null;
            SecurityInputText = string.Empty;
            ConfirmModalTitle = string.Empty;
            ConfirmModalMessage = string.Empty;
            ConfirmModalTargetPath = string.Empty;
            ShowDeleteCloudOption = false;
            DeleteAlsoFromCloud = false;
            DeleteLocalChecked = false;
            CloudDeleteSelections.Clear();
            IsConfirmModalDeleting = false;
            ConfirmModalProgressPercent = 0;
            ConfirmModalProgressText = string.Empty;
        }
    }

    // --- Progress Modal Properties ---
    public bool IsProgressModalOpen
    {
        get => _isProgressModalOpen;
        private set => SetField(ref _isProgressModalOpen, value);
    }

    public string ActiveProgressTitle
    {
        get => _activeProgressTitle;
        private set => SetField(ref _activeProgressTitle, value);
    }

    public string ActiveProgressMessage
    {
        get => _activeProgressMessage;
        private set => SetField(ref _activeProgressMessage, value);
    }

    public int ActiveProgressPercent
    {
        get => _activeProgressPercent;
        private set => SetField(ref _activeProgressPercent, value);
    }

    public string ActiveProgressSubText
    {
        get => _activeProgressSubText;
        private set => SetField(ref _activeProgressSubText, value);
    }

    private Action? _onCancelProgressAction;

    public Action? OnCancelProgressAction
    {
        get => _onCancelProgressAction;
        private set
        {
            if (SetField(ref _onCancelProgressAction, value))
            {
                OnPropertyChanged(nameof(CanCancelProgress));
            }
        }
    }

    public bool CanCancelProgress => _onCancelProgressAction != null;

    public void CancelActiveProgress()
    {
        var act = _onCancelProgressAction;
        act?.Invoke();
    }

    public void ShowProgress(string title, string message = "", int percent = 0, string subText = "", Action? onCancel = null)
    {
        ActiveProgressTitle = title;
        ActiveProgressMessage = message;
        ActiveProgressPercent = percent;
        ActiveProgressSubText = subText;
        OnCancelProgressAction = onCancel;
        IsProgressModalOpen = true;

        LoggingService.LogAction("Dialog_ShowProgress", new { Title = title, Message = message });
    }

    public void UpdateProgress(int percent, string? message = null, string? subText = null)
    {
        ActiveProgressPercent = percent;
        if (message != null) ActiveProgressMessage = message;
        if (subText != null) ActiveProgressSubText = subText;
    }

    public void CloseProgress()
    {
        IsProgressModalOpen = false;
        OnCancelProgressAction = null;
        LoggingService.LogAction("Dialog_CloseProgress");
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
