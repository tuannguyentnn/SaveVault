using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.Core.Services.Cloud;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

public class HistorySubViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly BackupService _backupService;
    private readonly CloudManagerService _cloudManager;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;

    private GameHistoryEntry? _selectedGameHistory;
    private GameBackupSummary? _selectedGameSummary;
    private BackupRecord? _selectedHistoryRecord;
    private BackupHistoryDetail? _selectedHistoryDetail;

    private bool _isHistoryDetailsModalOpen;
    private string _selectedHistoryGameTitle = string.Empty;

    public ObservableCollection<GameHistoryEntry> GameHistories { get; } = new();
    public ObservableCollection<GameBackupSummary> GroupedBackupHistory { get; } = new();
    public ObservableCollection<BackupRecord> BackupHistory { get; } = new();
    public ObservableCollection<BackupHistoryDetail> CurrentHistoryDetails { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistorySubViewModel(
        DatabaseService databaseService,
        BackupService backupService,
        CloudManagerService cloudManager,
        IDialogService dialogService,
        IAppEventBus eventBus)
    {
        _databaseService = databaseService;
        _backupService = backupService;
        _cloudManager = cloudManager;
        _dialogService = dialogService;
        _eventBus = eventBus;

        OpenGameDetailsCommand = new RelayCommand(async param => await ExecuteOpenGameDetailsAsync(param));
        CloseModalCommand = new RelayCommand(_ => ExecuteCloseDetailsModal());
        RequestDeleteHistoryDetailCommand = new RelayCommand(param => RequestDeleteHistoryDetail(param as BackupHistoryDetail));
        RequestDeleteGameHistoryCommand = new RelayCommand(param => RequestDeleteGameHistory(param as GameHistoryEntry));
        DeleteRecordCommand = new RelayCommand(param => ExecuteDeleteRecord(param as BackupRecord));
        OpenRecordFolderCommand = new RelayCommand(param => ExecuteOpenRecordFolder(param as BackupRecord));
        RefreshHistoryCommand = new RelayCommand(async _ => await RefreshHistoryAsync());

        _eventBus.Subscribe<BackupCompletedEvent>(async _ =>
        {
            await RefreshHistoryAsync();
        });

        _eventBus.Subscribe<HistoryChangedEvent>(async _ =>
        {
            await RefreshHistoryAsync();
        });
    }

    public GameHistoryEntry? SelectedGameHistory
    {
        get => _selectedGameHistory;
        set => SetField(ref _selectedGameHistory, value);
    }

    public GameBackupSummary? SelectedGameSummary
    {
        get => _selectedGameSummary;
        set => SetField(ref _selectedGameSummary, value);
    }

    public BackupRecord? SelectedHistoryRecord
    {
        get => _selectedHistoryRecord;
        set => SetField(ref _selectedHistoryRecord, value);
    }

    public BackupHistoryDetail? SelectedHistoryDetail
    {
        get => _selectedHistoryDetail;
        set => SetField(ref _selectedHistoryDetail, value);
    }

    public bool IsHistoryDetailsModalOpen
    {
        get => _isHistoryDetailsModalOpen;
        set => SetField(ref _isHistoryDetailsModalOpen, value);
    }

    public string SelectedHistoryGameTitle
    {
        get => _selectedHistoryGameTitle;
        set => SetField(ref _selectedHistoryGameTitle, value);
    }

    public ICommand OpenGameDetailsCommand { get; }
    public bool CanCloseModal => true;
    public ICommand CloseModalCommand { get; }
    public ICommand CloseGameDetailsModalCommand => CloseModalCommand;
    public ICommand RequestDeleteHistoryDetailCommand { get; }
    public ICommand RequestDeleteGameHistoryCommand { get; }
    public ICommand DeleteRecordCommand { get; }
    public ICommand OpenRecordFolderCommand { get; }
    public ICommand RefreshHistoryCommand { get; }

    public async Task RefreshHistoryAsync()
    {
        try
        {
            var rawRecords = await _databaseService.GetBackupHistoryAsync();
            var entries = await _databaseService.GetGameHistoriesAsync();

            void UpdateUiCollections()
            {
                BackupHistory.Clear();
                foreach (var r in rawRecords)
                {
                    BackupHistory.Add(r);
                }

                GameHistories.Clear();
                GroupedBackupHistory.Clear();

                foreach (var e in entries)
                {
                    GameHistories.Add(e);

                    GroupedBackupHistory.Add(new GameBackupSummary
                    {
                        GameName = e.GameName,
                        BackupCount = e.BackupCount,
                        TotalSizeBytes = e.TotalSizeBytes,
                        LatestBackupDate = e.LatestBackupDate,
                        LatestBackupPath = e.LatestBackupPath
                    });
                }
            }

            if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdateUiCollections);
            }
            else
            {
                UpdateUiCollections();
            }

            LoggingService.LogAction("History_Refreshed", new { TotalGames = entries.Count, TotalRecords = rawRecords.Count });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi làm mới lịch sử: {Message}", ex.Message);
        }
    }

    public async Task ExecuteOpenGameDetailsAsync(object? param)
    {
        GameHistoryEntry? entry = null;
        if (param is GameHistoryEntry ghe) entry = ghe;
        else if (param is GameBackupSummary summary)
        {
            entry = GameHistories.FirstOrDefault(g => string.Equals(g.GameName, summary.GameName, StringComparison.OrdinalIgnoreCase));
        }
        else if (param is string name)
        {
            entry = GameHistories.FirstOrDefault(g => string.Equals(g.GameName, name, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null) return;

        SelectedHistoryGameTitle = entry.GameName;
        CurrentHistoryDetails.Clear();

        try
        {
            var details = await _databaseService.GetHistoryDetailsByGameIdAsync(entry.Id);
            foreach (var d in details)
            {
                CurrentHistoryDetails.Add(d);
            }

            IsHistoryDetailsModalOpen = true;
            LoggingService.LogAction("View_Game_Snapshots", new { Game = entry.GameName, SnapshotCount = details.Count });
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessage("Lỗi", $"Không thể tải chi tiết lịch sử: {ex.Message}", "Error");
            LoggingService.Error(ex, "Lỗi nạp snapshot của game {Game}: {Message}", entry.GameName, ex.Message);
        }
    }

    public void ExecuteCloseDetailsModal()
    {
        IsHistoryDetailsModalOpen = false;
        LoggingService.LogAction("Close_Game_Snapshots_Modal");
    }

    public void RequestDeleteHistoryDetail(BackupHistoryDetail? detail)
    {
        if (detail == null) return;

        var hasCloud = !string.IsNullOrEmpty(detail.CloudFileId) || detail.IsCloudSynced;
        var dateFormatted = detail.BackupDate.ToString("dd/MM/yyyy HH:mm");
        var msg = $"Bạn có chắc chắn muốn xóa bản snapshot ngày {dateFormatted} của game '{detail.GameName}' không?\n\nFile trên ổ cứng sẽ bị xóa vĩnh viễn.";

        _dialogService.ShowConfirm(
            "Xác Nhận Xóa Bản Snapshot",
            msg,
            detail.BackupPath,
            async () =>
            {
                await ExecuteDeleteSnapshotAsync(detail, _dialogService.DeleteAlsoFromCloud);
            },
            showCloudOption: hasCloud);
    }

    private async Task ExecuteDeleteSnapshotAsync(BackupHistoryDetail detail, bool deleteFromCloud)
    {
        _dialogService.SetConfirmDeletingState(true, 10, "Đang chuẩn bị xóa bản snapshot...");
        LoggingService.LogAction("Delete_Snapshot_Start", new { Game = detail.GameName, DetailId = detail.Id, DeleteFromCloud = deleteFromCloud });

        var progress = new Progress<BackupProgress>(p =>
        {
            _dialogService.SetConfirmDeletingState(true, p.Percent, p.Message);
        });

        try
        {
            await _backupService.DeleteSnapshotWithProgressAsync(
                detail, 
                deleteFromCloud, 
                _cloudManager.CurrentProvider, 
                progress,
                cloudServiceResolver: providerName => _cloudManager.GetProvider(providerName));

            CurrentHistoryDetails.Remove(detail);
            if (CurrentHistoryDetails.Count == 0)
            {
                IsHistoryDetailsModalOpen = false;
            }
            await RefreshHistoryAsync();

            LoggingService.LogAction("Delete_Snapshot_Success", new { DetailId = detail.Id });
            _dialogService.ShowMessage("Xóa Thành Công", "Đã xóa bản snapshot thành công.", "Success");
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi xóa snapshot {Id}: {Message}", detail.Id, ex.Message);
            _dialogService.ShowMessage("Lỗi Xóa Snapshot", ex.Message, "Error", ex.StackTrace);
        }
    }

    public void RequestDeleteGameHistory(GameHistoryEntry? entry)
    {
        if (entry == null) return;

        var msg = $"CẢNH BÁO NGUY HIỂM:\n\nBạn đang yêu cầu xóa TOÀN BỘ lịch sử và tất cả {entry.BackupCount} bản sao lưu của game '{entry.GameName}'.\n\nTất cả file trên ổ cứng sẽ bị xóa.";

        _dialogService.ShowConfirm(
            "Xác Nhận Xóa Game Khỏi Lịch Sử",
            msg,
            entry.LatestBackupPath,
            async () =>
            {
                await ExecuteDeleteGameAsync(entry, _dialogService.DeleteAlsoFromCloud);
            },
            showCloudOption: true);
    }

    private async Task ExecuteDeleteGameAsync(GameHistoryEntry entry, bool deleteFromCloud)
    {
        _dialogService.SetConfirmDeletingState(true, 10, $"Đang xóa toàn bộ sao lưu của {entry.GameName}...");
        LoggingService.LogAction("Delete_Game_Start", new { Game = entry.GameName, Snapshots = entry.BackupCount, DeleteFromCloud = deleteFromCloud });

        var progress = new Progress<BackupProgress>(p =>
        {
            _dialogService.SetConfirmDeletingState(true, p.Percent, p.Message);
        });

        try
        {
            await _backupService.DeleteGameHistoryWithProgressAsync(
                entry, 
                deleteFromCloud, 
                _cloudManager.CurrentProvider, 
                progress,
                cloudServiceResolver: providerName => _cloudManager.GetProvider(providerName));

            GameHistories.Remove(entry);
            IsHistoryDetailsModalOpen = false;
            await RefreshHistoryAsync();

            LoggingService.LogAction("Delete_Game_Success", new { Game = entry.GameName });
            _dialogService.ShowMessage("Xóa Thành Công", $"Đã xóa sạch toàn bộ sao lưu của '{entry.GameName}'.", "Success");
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi xóa game {Game}: {Message}", entry.GameName, ex.Message);
            _dialogService.ShowMessage("Lỗi Xóa Game", ex.Message, "Error", ex.StackTrace);
        }
    }

    private void ExecuteDeleteRecord(BackupRecord? record)
    {
        if (record == null) return;
        _dialogService.ShowConfirm(
            "Xác Nhận Xóa Bản Ghi",
            $"Bạn có muốn xóa bản ghi sao lưu ID: {record.Id} của game '{record.GameName}' không?",
            record.BackupPath,
            async () =>
            {
                await _databaseService.DeleteBackupRecordAsync(record.Id);
                BackupHistory.Remove(record);
                await RefreshHistoryAsync();
            });
    }

    private void ExecuteOpenRecordFolder(BackupRecord? record)
    {
        if (record == null || string.IsNullOrEmpty(record.BackupPath)) return;
        try
        {
            var targetDir = File.Exists(record.BackupPath) ? Path.GetDirectoryName(record.BackupPath) : record.BackupPath;
            if (Directory.Exists(targetDir))
            {
                Process.Start(new ProcessStartInfo { FileName = targetDir, UseShellExecute = true });
            }
        }
        catch { }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
