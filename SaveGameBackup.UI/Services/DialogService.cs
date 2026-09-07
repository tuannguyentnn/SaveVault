using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.UI.Services;

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

    void ShowMessage(string title, string content, string type = "Info", string? details = null);
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

    void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false);
    void CancelConfirm();
    void SetConfirmDeletingState(bool isDeleting, int percent = 0, string text = "");

    // Progress Modal
    bool IsProgressModalOpen { get; }
    string ActiveProgressTitle { get; }
    string ActiveProgressMessage { get; }
    int ActiveProgressPercent { get; }
    string ActiveProgressSubText { get; }

    void ShowProgress(string title, string message = "", int percent = 0, string subText = "");
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

    public void ShowMessage(string title, string content, string type = "Info", string? details = null)
    {
        MessageModalTitle = title;
        MessageModalContent = content;
        MessageModalType = type;
        MessageModalDetails = details;
        IsMessageModalOpen = true;

        LoggingService.LogAction("Dialog_ShowMessage", new { Title = title, Type = type, HasDetails = !string.IsNullOrEmpty(details) });
    }

    public void CloseMessage()
    {
        IsMessageModalOpen = false;
        MessageModalDetails = null;
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

    public ICommand ConfirmModalExecuteCommand { get; }
    public ICommand CancelConfirmModalCommand { get; }

    public void ShowConfirm(string title, string message, string targetPath, Func<Task> onConfirm, bool showCloudOption = false)
    {
        ConfirmModalTitle = title;
        ConfirmModalMessage = message;
        ConfirmModalTargetPath = targetPath;
        _pendingConfirmAction = onConfirm;
        ShowDeleteCloudOption = showCloudOption;
        DeleteAlsoFromCloud = false;
        IsConfirmModalDeleting = false;
        ConfirmModalProgressPercent = 0;
        ConfirmModalProgressText = string.Empty;
        IsConfirmModalOpen = true;

        LoggingService.LogAction("Dialog_ShowConfirm", new { Title = title, TargetPath = targetPath, ShowCloudOption = showCloudOption });
    }

    public void CancelConfirm()
    {
        if (IsConfirmModalDeleting) return;
        IsConfirmModalOpen = false;
        _pendingConfirmAction = null;
        LoggingService.LogAction("Dialog_CancelConfirm");
    }

    public void SetConfirmDeletingState(bool isDeleting, int percent = 0, string text = "")
    {
        IsConfirmModalDeleting = isDeleting;
        ConfirmModalProgressPercent = percent;
        ConfirmModalProgressText = text;
    }

    private async Task ExecuteConfirmActionAsync()
    {
        if (_pendingConfirmAction != null)
        {
            try
            {
                LoggingService.LogAction("Dialog_ConfirmExecuted", new { Title = ConfirmModalTitle, DeleteAlsoFromCloud });
                await _pendingConfirmAction.Invoke();
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
                IsConfirmModalDeleting = false;
            }
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

    public void ShowProgress(string title, string message = "", int percent = 0, string subText = "")
    {
        ActiveProgressTitle = title;
        ActiveProgressMessage = message;
        ActiveProgressPercent = percent;
        ActiveProgressSubText = subText;
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
