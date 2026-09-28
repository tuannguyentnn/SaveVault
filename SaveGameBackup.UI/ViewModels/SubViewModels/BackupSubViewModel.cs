using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

public class BackupSubViewModel : INotifyPropertyChanged
{
    private readonly BackupService _backupService;
    private readonly SearchSubViewModel _searchVM;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;

    private string _backupDestinationRoot = string.Empty;
    private bool _createTimestampSubfolder = true;
    private bool _autoCompressZip = true;
    private bool _isBackingUp;
    private int _backupProgressPercent;
    private string _backupProgressText = string.Empty;
    private string? _lastBackupPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BackupSubViewModel(
        BackupService backupService,
        SearchSubViewModel searchVM,
        IDialogService dialogService,
        IAppEventBus eventBus,
        INativeDialogService? nativeDialog = null)
    {
        _backupService = backupService;
        _searchVM = searchVM;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _nativeDialog = nativeDialog;

        var config = AppConfigService.GetConfig();
        _backupDestinationRoot = DatabaseService.DefaultBackupDir;

        _createTimestampSubfolder = config.CreateTimestampSubfolder;
        _autoCompressZip = config.AutoCompressZip;

        BackupCommand = new RelayCommand(async _ => await ExecuteBackupAsync(), _ => CanExecuteBackup());
        OpenBackupFolderCommand = new RelayCommand(_ => ExecuteOpenBackupFolder(), _ => !string.IsNullOrEmpty(LastBackupPath));
        BrowseBackupDirectoryCommand = new RelayCommand(async _ => await ExecuteBrowseBackupDirectoryAsync());

        _searchVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SearchSubViewModel.HasGame) ||
                e.PropertyName == nameof(SearchSubViewModel.HasSelectedPaths))
            {
                (BackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        };
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

    public bool IsBackingUp
    {
        get => _isBackingUp;
        set
        {
            if (SetField(ref _isBackingUp, value))
            {
                (BackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                _searchVM.IsSearching = value;
                OnPropertyChanged(nameof(IsNotBackingUp));
            }
        }
    }

    public bool IsNotBackingUp => !IsBackingUp;

    public int BackupProgressPercent
    {
        get => _backupProgressPercent;
        set => SetField(ref _backupProgressPercent, value);
    }

    public string BackupProgressText
    {
        get => _backupProgressText;
        set => SetField(ref _backupProgressText, value);
    }

    public string? LastBackupPath
    {
        get => _lastBackupPath;
        set
        {
            if (SetField(ref _lastBackupPath, value))
            {
                (OpenBackupFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasLastBackupPath));
            }
        }
    }

    public bool HasLastBackupPath => !string.IsNullOrEmpty(LastBackupPath);

    public ICommand BackupCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand BrowseBackupDirectoryCommand { get; }

    private bool CanExecuteBackup()
    {
        return !IsBackingUp && _searchVM.HasGame && _searchVM.HasSelectedPaths;
    }

    public async Task ExecuteBackupAsync()
    {
        if (_searchVM.CurrentGame == null) return;

        var selectedItems = _searchVM.DetectedPathItems.Where(x => x.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            _dialogService.ShowMessage("Lưu ý", "Vui lòng chọn ít nhất một thư mục save game để sao lưu.", "Warning");
            return;
        }

        IsBackingUp = true;
        BackupProgressPercent = 0;
        BackupProgressText = "Đang khởi tạo tác vụ sao lưu...";

        var gameName = _searchVM.CurrentGame.GameName;
        var pathsToBackup = selectedItems.Select(x => x.Path).ToList();

        LoggingService.LogAction("Backup_Start", new
        {
            Game = gameName,
            PathsCount = pathsToBackup.Count,
            AutoCompressZip = AutoCompressZip,
            DestinationRoot = BackupDestinationRoot
        });

        _dialogService.ShowProgress("Sao Lưu Dữ Liệu", $"Đang sao lưu save game cho '{gameName}'...", 0);

        try
        {
            var appSettings = new AppSettings
            {
                BackupRootDirectory = BackupDestinationRoot,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                AutoCompressZip = AutoCompressZip
            };

            var progress = new Progress<BackupProgress>(report =>
            {
                BackupProgressPercent = report.Percent;
                BackupProgressText = report.Message;
                _dialogService.UpdateProgress(report.Percent, report.Message);
            });

            var result = await _backupService.BackupGameAsync(
                _searchVM.CurrentGame,
                appSettings,
                pathsToBackup,
                progress,
                cancellationToken: default,
                onlineCoverUrl: _searchVM.OnlineCoverUrl);

            LastBackupPath = result.BackupPath;

            _dialogService.CloseProgress();

            if (result.Status == "Success" || result.Status == "Thành công")
            {
                var summaryMsg = $"Sao lưu thành công {result.FileCount} tệp tin cho game '{gameName}'.\n\nVị trí sao lưu: {result.BackupPath}";
                _dialogService.ShowMessage("Sao Lưu Hoàn Tất", summaryMsg, "Success");

                LoggingService.LogAction("Backup_Success", new
                {
                    Game = gameName,
                    result.FileCount,
                    result.TotalSizeBytes,
                    result.BackupPath
                });

                _eventBus.Publish(new BackupCompletedEvent(gameName, result.BackupPath, result.FileCount, result.TotalSizeBytes));
                _eventBus.Publish(new HistoryChangedEvent());
            }
            else
            {
                var errMsg = !string.IsNullOrEmpty(result.Note) ? result.Note : "Có lỗi xảy ra trong quá trình sao lưu.";
                _dialogService.ShowMessage("Sao Lưu Thất Bại", errMsg, "Error");
                LoggingService.LogAction("Backup_Failed", new { Game = gameName, Reason = errMsg }, level: "Error");
            }
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Lỗi Ngoại Lệ Sao Lưu", ex.Message, "Error", ex.StackTrace);
            LoggingService.Error(ex, "Lỗi ngoại lệ khi sao lưu game {Game}: {Message}", gameName, ex.Message);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private void ExecuteOpenBackupFolder()
    {
        if (string.IsNullOrEmpty(LastBackupPath)) return;

        try
        {
            var targetDir = File.Exists(LastBackupPath) ? Path.GetDirectoryName(LastBackupPath) : LastBackupPath;
            if (Directory.Exists(targetDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetDir,
                    UseShellExecute = true
                });
                LoggingService.LogAction("Open_Backup_Folder", new { Path = targetDir });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Không thể mở thư mục sao lưu: {Path}", LastBackupPath);
        }
    }

    private async Task ExecuteBrowseBackupDirectoryAsync()
    {
        await Task.CompletedTask;
    }

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
