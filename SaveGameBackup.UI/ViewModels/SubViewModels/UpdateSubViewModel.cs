using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

/// <summary>
/// SubViewModel quản lý trạng thái kiểm tra, hiển thị thông báo và tiến trình cập nhật ứng dụng.
/// </summary>
public class UpdateSubViewModel : INotifyPropertyChanged
{
    private readonly UpdateService _updateService;
    private readonly Func<(bool isBusy, string reason)> _checkBusyFunc;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;

    private bool _isUpdateModalOpen;
    private bool _isChangelogModalOpen;
    private List<ChangelogItem> _changelogHistory = new();
    private bool _isCheckingForUpdate;
    private bool _isDownloading;
    private int _downloadPercent;
    private string _downloadProgressText = string.Empty;
    private string _statusMessage = string.Empty;
    private string? _busyWarningMessage;
    private string? _downloadErrorMessage;
    private UpdateInfo? _updateInfo;
    private CancellationTokenSource? _downloadCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateSubViewModel(
        UpdateService? updateService = null,
        Func<(bool isBusy, string reason)>? checkBusyFunc = null,
        IDialogService? dialogService = null,
        IAppEventBus? eventBus = null)
    {
        _updateService = updateService ?? new UpdateService();
        _checkBusyFunc = checkBusyFunc ?? (() => (false, string.Empty));
        _dialogService = dialogService ?? new DialogService();
        _eventBus = eventBus ?? new AppEventBus();

        CheckForUpdateCommand = new RelayCommand(async _ => await CheckForUpdateManualAsync(), _ => !IsDownloading && !IsCheckingForUpdate);
        ApplyUpdateCommand = new RelayCommand(async _ => await ExecuteApplyUpdateAsync(), _ => !IsDownloading && UpdateInfo != null);
        RetryDownloadCommand = new RelayCommand(async _ => await ExecuteApplyUpdateAsync(), _ => !IsDownloading && UpdateInfo != null);
        SkipVersionCommand = new RelayCommand(_ => ExecuteSkipVersion(), _ => !IsDownloading);
        RemindLaterCommand = new RelayCommand(_ => ExecuteRemindLater(), _ => !IsDownloading);
        CancelDownloadCommand = new RelayCommand(_ => CancelDownload(), _ => IsDownloading);
        RefreshBusyStatusCommand = new RelayCommand(_ => ExecuteRefreshBusyStatus());
        OpenChangelogModalCommand = new RelayCommand(async _ => await ExecuteOpenChangelogModalAsync());
        CloseChangelogModalCommand = new RelayCommand(_ => IsChangelogModalOpen = false);
    }

    public string CurrentVersion => _updateService.GetCurrentVersion();

    public bool IsUpdateModalOpen
    {
        get => _isUpdateModalOpen;
        set => SetField(ref _isUpdateModalOpen, value);
    }

    public bool IsChangelogModalOpen
    {
        get => _isChangelogModalOpen;
        set => SetField(ref _isChangelogModalOpen, value);
    }

    public List<ChangelogItem> ChangelogHistory
    {
        get => _changelogHistory;
        set => SetField(ref _changelogHistory, value);
    }

    public bool IsCheckingForUpdate
    {
        get => _isCheckingForUpdate;
        set
        {
            if (SetField(ref _isCheckingForUpdate, value))
            {
                (CheckForUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetField(ref _isDownloading, value))
            {
                (CheckForUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RetryDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SkipVersionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RemindLaterCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CancelDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public int DownloadPercent
    {
        get => _downloadPercent;
        set => SetField(ref _downloadPercent, value);
    }

    public string DownloadProgressText
    {
        get => _downloadProgressText;
        set => SetField(ref _downloadProgressText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string? BusyWarningMessage
    {
        get => _busyWarningMessage;
        set
        {
            if (SetField(ref _busyWarningMessage, value))
            {
                OnPropertyChanged(nameof(HasBusyWarning));
            }
        }
    }

    public bool HasBusyWarning => !string.IsNullOrEmpty(BusyWarningMessage);

    public string? DownloadErrorMessage
    {
        get => _downloadErrorMessage;
        set
        {
            if (SetField(ref _downloadErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasDownloadError));
                (RetryDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasDownloadError => !string.IsNullOrEmpty(DownloadErrorMessage);

    public UpdateInfo? UpdateInfo
    {
        get => _updateInfo;
        set
        {
            if (SetField(ref _updateInfo, value))
            {
                (ApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RetryDownloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand CheckForUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand RetryDownloadCommand { get; }
    public ICommand SkipVersionCommand { get; }
    public ICommand RemindLaterCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RefreshBusyStatusCommand { get; }
    public ICommand OpenChangelogModalCommand { get; }
    public ICommand CloseChangelogModalCommand { get; }

    /// <summary>
    /// Mở modal hiển thị nhật ký phiên bản (Changelog).
    /// </summary>
    public async Task ExecuteOpenChangelogModalAsync()
    {
        try
        {
            var history = await _updateService.GetChangelogHistoryAsync();
            ChangelogHistory = history ?? new List<ChangelogItem>();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi khi tải nhật ký phiên bản: {Message}", ex.Message);
        }
        IsChangelogModalOpen = true;
    }

    /// <summary>
    /// Kiểm tra trạng thái hậu cập nhật (được gọi khi ứng dụng khởi động).
    /// Phát hiện và thông báo nếu có cờ báo lỗi (update_error.flag) hoặc cờ cập nhật thành công (update_success.flag).
    /// </summary>
    public async Task CheckPostUpdateStatusAsync()
    {
        try
        {
            // Kiểm tra xem lần cập nhật trước có bị rollback (hoàn tác tự động) hay không
            if (_updateService.HasUpdateRollbackFlag(out var rollbackMsg))
            {
                LoggingService.Warn("Phát hiện cờ rollback từ lần cập nhật trước: {RollbackReason}", rollbackMsg);
                LoggingService.LogAction("Update_Rollback_Confirmed", new { Reason = rollbackMsg });

                var detailMsg = string.IsNullOrWhiteSpace(rollbackMsg)
                    ? "Sao chép tệp tin cập nhật thất bại (có thể do tệp đang bị khóa hoặc quyền ghi)."
                    : rollbackMsg;

                _dialogService.ShowMessage(
                    "Cập Nhật Thất Bại - Đã Tự Động Khôi Phục",
                    $"Quá trình cập nhật Omnisave lên phiên bản mới đã gặp sự cố khi sao chép tệp tin:\n\n{detailMsg}\n\nHệ thống đã tự động hoàn tác (rollback) và khôi phục an toàn phiên bản hiện tại (v{CurrentVersion}). Toàn bộ dữ liệu save và cấu hình của bạn không bị ảnh hưởng.\n\nBạn có thể thử kiểm tra và cập nhật lại sau từ tab Cài đặt.",
                    "Warning");
                return;
            }

            if (_updateService.HasUpdateErrorFlag(out var errorMsg))
            {
                LoggingService.Warn("Phát hiện cờ lỗi từ lần cập nhật trước: {Error}", errorMsg);
                var detailMsg = string.IsNullOrWhiteSpace(errorMsg)
                    ? "Quá trình sao chép tệp tin cập nhật thất bại (có thể do tệp đang bị khóa bởi tiến trình khác hoặc quyền ghi). Vui lòng kiểm tra lại quyền ghi hoặc xem data\\temp\\update.log."
                    : errorMsg;

                _dialogService.ShowMessage(
                    "Lỗi Cập Nhật Trước Đó",
                    $"Quá trình cập nhật Omnisave ở lần chạy trước đã gặp sự cố:\n\n{detailMsg}\n\nBạn có thể thử kiểm tra và cập nhật lại từ tab Cài đặt.",
                    "Error");
                return;
            }

            if (_updateService.HasUpdateSuccessFlag(out var updatedVersion))
            {
                var verStr = !string.IsNullOrWhiteSpace(updatedVersion) ? $"v{updatedVersion.Trim().TrimStart('v', 'V')}" : $"v{CurrentVersion}";
                LoggingService.LogAction("Update_Success_Confirmed", new { Version = verStr });

                _dialogService.ShowMessage(
                    "Cập Nhật Thành Công",
                    $"Chúc mừng! Omnisave đã được cập nhật thành công lên phiên bản {verStr}!\n\nBạn có thể vào Cài đặt để xem thông tin chi tiết và lịch sử thay đổi.",
                    "Success");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi kiểm tra trạng thái hậu cập nhật: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Kiểm tra phiên bản mới ngầm khi app khởi động xong.
    /// </summary>
    public async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            // 1. Kiểm tra trạng thái hậu cập nhật trước (nếu có cờ error/success từ runner script)
            await CheckPostUpdateStatusAsync();

            // Trì hoãn 2 giây để nhường tài nguyên cho giao diện tải hoàn tất
            await Task.Delay(2000);

            var (isAvailable, info, _) = await _updateService.CheckForUpdateAsync(isSilent: true);
            if (isAvailable && info != null)
            {
                UpdateInfo = info;
                BusyWarningMessage = null;
                DownloadErrorMessage = null;
                IsUpdateModalOpen = true;
                LoggingService.LogAction("Update_Modal_Shown_OnStartup", new { TargetVersion = info.Version });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi kiểm tra cập nhật lúc khởi động: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Kiểm tra phiên bản mới khi người dùng nhấn nút thủ công trong tab Cài đặt.
    /// </summary>
    public async Task CheckForUpdateManualAsync()
    {
        IsCheckingForUpdate = true;
        StatusMessage = "Đang kiểm tra phiên bản mới từ repository...";

        try
        {
            var (isAvailable, info, message) = await _updateService.CheckForUpdateAsync(isSilent: false);
            if (isAvailable && info != null)
            {
                UpdateInfo = info;
                BusyWarningMessage = null;
                DownloadErrorMessage = null;
                IsUpdateModalOpen = true;
                StatusMessage = string.Empty;
            }
            else
            {
                StatusMessage = message;
                _dialogService.ShowMessage("Kiểm Tra Cập Nhật", message, isAvailable ? "Info" : "Success");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi kiểm tra: {ex.Message}";
            _dialogService.ShowMessage("Kiểm Tra Cập Nhật", $"Không thể kết nối đến máy chủ: {ex.Message}", "Error");
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    /// <summary>
    /// Bỏ qua phiên bản hiện tại và lưu vào config.
    /// </summary>
    public void ExecuteSkipVersion()
    {
        if (UpdateInfo != null)
        {
            _updateService.SkipVersion(UpdateInfo.Version);
        }
        DownloadErrorMessage = null;
        IsUpdateModalOpen = false;
    }

    /// <summary>
    /// Đóng modal để lần sau nhắc lại.
    /// </summary>
    public void ExecuteRemindLater()
    {
        DownloadErrorMessage = null;
        IsUpdateModalOpen = false;
    }

    /// <summary>
    /// Kiểm tra lại trạng thái bận.
    /// </summary>
    public void ExecuteRefreshBusyStatus()
    {
        var (isBusy, reason) = _checkBusyFunc();
        BusyWarningMessage = isBusy ? reason : null;
    }

    /// <summary>
    /// Hủy tiến trình tải cập nhật.
    /// </summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Thực thi quy trình kiểm tra an toàn, tải và kích hoạt updater.
    /// </summary>
    public async Task ExecuteApplyUpdateAsync()
    {
        if (UpdateInfo == null) return;

        using var trace = LoggingService.BeginTrace("AutoUpdate_ExecuteApplyUpdate", null);

        // PRE-UPDATE SAFETY CHECK: Kiểm tra có tiến trình quan trọng nào đang chạy không
        var (isBusy, reason) = _checkBusyFunc();
        if (isBusy)
        {
            BusyWarningMessage = reason;
            LoggingService.Warn("Cập nhật bị hoãn do tiến trình đang bận: {Reason}", reason);
            return;
        }

        BusyWarningMessage = null;
        DownloadErrorMessage = null;
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadProgressText = "Bắt đầu tải xuống bản cập nhật...";
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                DownloadPercent = p.Percent;
                DownloadProgressText = p.DownloadedSizeText;
                StatusMessage = p.StatusMessage;
            });

            // 1. Tải gói update_package.zip về thư mục temp trong app
            var downloadUrl = UpdateInfo.DownloadUrl;
            long expectedBytes = UpdateInfo.PackageSizeBytes ?? -1L;
            var zipPath = await _updateService.DownloadUpdatePackageAsync(downloadUrl, progress, _downloadCts.Token, expectedBytes);

            DownloadProgressText = "Đang giải nén các file cập nhật...";
            await Task.Delay(300);

            // 2. Giải nén vào temp/extracted
            var extractedDir = _updateService.ExtractUpdatePackage(zipPath);

            DownloadProgressText = "Đang chuẩn bị khởi động lại ứng dụng...";
            await Task.Delay(300);

            // 3. Tạo runner script
            int currentPid = Process.GetCurrentProcess().Id;
            var runnerScript = _updateService.GenerateRunnerScript(extractedDir, currentPid, UpdateInfo.Version);

            // 4. Kích hoạt runner script độc lập
            _updateService.LaunchRunnerScript(runnerScript);

            LoggingService.LogAction("Update_Launched_Runner", new { Script = runnerScript });

            // 5. Đóng ứng dụng hiện tại để script chép đè file
            await Task.Delay(500);

#if WINDOWS
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                Microsoft.Maui.Controls.Application.Current?.Quit();
                Environment.Exit(0);
            });
#else
            Environment.Exit(0);
#endif
        }
        catch (OperationCanceledException)
        {
            DownloadErrorMessage = null;
            DownloadProgressText = "Đã hủy tải bản cập nhật.";
            StatusMessage = "Đã hủy thao tác cập nhật.";
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi trong quá trình cập nhật ứng dụng: {Message}", ex.Message);
            DownloadErrorMessage = ex.Message;
            StatusMessage = $"Lỗi: {ex.Message}";
            IsUpdateModalOpen = true;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
