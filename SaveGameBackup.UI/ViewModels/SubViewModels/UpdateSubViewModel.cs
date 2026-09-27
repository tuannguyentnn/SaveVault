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
    private bool _isCheckingForUpdate;
    private bool _isDownloading;
    private int _downloadPercent;
    private string _downloadProgressText = string.Empty;
    private string _statusMessage = string.Empty;
    private string? _busyWarningMessage;
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
        SkipVersionCommand = new RelayCommand(_ => ExecuteSkipVersion(), _ => !IsDownloading);
        RemindLaterCommand = new RelayCommand(_ => ExecuteRemindLater(), _ => !IsDownloading);
        CancelDownloadCommand = new RelayCommand(_ => CancelDownload(), _ => IsDownloading);
        RefreshBusyStatusCommand = new RelayCommand(_ => ExecuteRefreshBusyStatus());
    }

    public string CurrentVersion => _updateService.GetCurrentVersion();

    public bool IsUpdateModalOpen
    {
        get => _isUpdateModalOpen;
        set => SetField(ref _isUpdateModalOpen, value);
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

    public UpdateInfo? UpdateInfo
    {
        get => _updateInfo;
        set
        {
            if (SetField(ref _updateInfo, value))
            {
                (ApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand CheckForUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand SkipVersionCommand { get; }
    public ICommand RemindLaterCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RefreshBusyStatusCommand { get; }

    /// <summary>
    /// Kiểm tra phiên bản mới ngầm khi app khởi động xong.
    /// </summary>
    public async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            // Trì hoãn 2 giây để nhường tài nguyên cho giao diện tải hoàn tất
            await Task.Delay(2000);

            var (isAvailable, info, _) = await _updateService.CheckForUpdateAsync(isSilent: true);
            if (isAvailable && info != null)
            {
                UpdateInfo = info;
                BusyWarningMessage = null;
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
        IsUpdateModalOpen = false;
    }

    /// <summary>
    /// Đóng modal để lần sau nhắc lại.
    /// </summary>
    public void ExecuteRemindLater()
    {
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

        // PRE-UPDATE SAFETY CHECK: Kiểm tra có tiến trình quan trọng nào đang chạy không
        var (isBusy, reason) = _checkBusyFunc();
        if (isBusy)
        {
            BusyWarningMessage = reason;
            LoggingService.Warn("Cập nhật bị hoãn do tiến trình đang bận: {Reason}", reason);
            return;
        }

        BusyWarningMessage = null;
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

            // 1. Tải gói publish.zip về thư mục temp trong app
            var downloadUrl = UpdateInfo.DownloadUrl;
            var zipPath = await _updateService.DownloadUpdatePackageAsync(downloadUrl, progress, _downloadCts.Token);

            DownloadProgressText = "Đang giải nén các file cập nhật...";
            await Task.Delay(300);

            // 2. Giải nén vào temp/extracted
            var extractedDir = _updateService.ExtractUpdatePackage(zipPath);

            DownloadProgressText = "Đang chuẩn bị khởi động lại ứng dụng...";
            await Task.Delay(300);

            // 3. Tạo runner script
            int currentPid = Process.GetCurrentProcess().Id;
            var runnerScript = _updateService.GenerateRunnerScript(extractedDir, currentPid);

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
            DownloadProgressText = "Đã hủy tải bản cập nhật.";
            StatusMessage = "Đã hủy thao tác cập nhật.";
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi trong quá trình cập nhật ứng dụng: {Message}", ex.Message);
            _dialogService.ShowMessage("Lỗi Cập Nhật", $"Quá trình cập nhật thất bại: {ex.Message}", "Error");
            StatusMessage = $"Lỗi: {ex.Message}";
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
