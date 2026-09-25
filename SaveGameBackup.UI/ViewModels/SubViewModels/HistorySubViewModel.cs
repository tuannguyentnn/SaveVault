using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
        SyncMissingCoversCommand = new RelayCommand(async _ => await ExecuteSyncMissingCoversAsync(), _ => !IsSyncingCovers);
        RevertGameSaveCommand = new RelayCommand(async _ => await ExecuteRevertGameSaveAsync(), _ => HasRevertPoint);
        ClearRevertPointsCommand = new RelayCommand(_ => ExecuteClearRevertPoints(), _ => HasRevertPoint);

        _eventBus.Subscribe<BackupCompletedEvent>(async _ =>
        {
            await RefreshHistoryAsync();
        });

        _eventBus.Subscribe<HistoryChangedEvent>(async _ =>
        {
            await RefreshHistoryAsync();
        });
    }

    private bool _isSyncingCovers;
    public bool IsSyncingCovers
    {
        get => _isSyncingCovers;
        set
        {
            if (SetField(ref _isSyncingCovers, value))
            {
                (SyncMissingCoversCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private CancellationTokenSource? _syncCoversCts;
    public void CancelSyncCovers() => _syncCoversCts?.Cancel();

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

    private RevertPointInfo? _currentRevertPoint;
    public RevertPointInfo? CurrentRevertPoint
    {
        get => _currentRevertPoint;
        set
        {
            if (SetField(ref _currentRevertPoint, value))
            {
                OnPropertyChanged(nameof(HasRevertPoint));
                (RevertGameSaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ClearRevertPointsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasRevertPoint => CurrentRevertPoint != null;

    public ICommand OpenGameDetailsCommand { get; }
    public bool CanCloseModal => true;
    public ICommand CloseModalCommand { get; }
    public ICommand CloseGameDetailsModalCommand => CloseModalCommand;
    public ICommand RequestDeleteHistoryDetailCommand { get; }
    public ICommand RequestDeleteGameHistoryCommand { get; }
    public ICommand DeleteRecordCommand { get; }
    public ICommand OpenRecordFolderCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand SyncMissingCoversCommand { get; }
    public ICommand RevertGameSaveCommand { get; }
    public ICommand ClearRevertPointsCommand { get; }

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

            if (Microsoft.Maui.ApplicationModel.MainThread.IsMainThread)
            {
                UpdateUiCollections();
            }
            else
            {
                await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(UpdateUiCollections);
            }

            OnPropertyChanged(nameof(GameHistories));
            OnPropertyChanged(nameof(GroupedBackupHistory));
            OnPropertyChanged(nameof(BackupHistory));

            LoggingService.LogAction("History_Refreshed", new { TotalGames = entries.Count, TotalRecords = rawRecords.Count });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi làm mới lịch sử: {Message}", ex.Message);
        }
    }

    public async Task ExecuteSyncMissingCoversAsync()
    {
        if (IsSyncingCovers) return;

        if (!GameCoverService.IsNetworkAvailable())
        {
            _dialogService.ShowMessage("Không Có Kết Nối Mạng", "Vui lòng kết nối internet để thực hiện đồng bộ ảnh bìa cho các game.", "Warning");
            return;
        }

        // 1. Quét tìm danh sách các game chưa có ảnh hợp lệ trên máy
        var missingGames = GameHistories
            .Where(g => !GameCoverService.HasLocalCover(g.GameName))
            .ToList();

        if (missingGames.Count == 0)
        {
            _dialogService.ShowMessage("Đồng Bộ Ảnh Game", "Tất cả các tựa game trong lịch sử sao lưu đã có ảnh bìa đầy đủ!", "Information");
            return;
        }

        IsSyncingCovers = true;
        _syncCoversCts = new CancellationTokenSource();
        int total = missingGames.Count;
        int successCount = 0;

        _dialogService.ShowProgress(
            "Đồng Bộ Ảnh Bìa Game",
            $"Đang chuẩn bị tìm ảnh cho {total} tựa game...",
            0,
            subText: $"0/{total}",
            onCancel: () => CancelSyncCovers());

        try
        {
            for (int i = 0; i < total; i++)
            {
                if (_syncCoversCts.IsCancellationRequested) break;

                var entry = missingGames[i];
                int currentPercent = (int)((i * 100.0) / total);
                _dialogService.UpdateProgress(currentPercent, $"Đang tìm và tải ảnh bìa cho '{entry.GameName}'...", $"{i + 1}/{total}");

                try
                {
                    var cover = await GameCoverService.EnsureCoverForGameAsync(entry.GameName, cancellationToken: _syncCoversCts.Token);
                    if (!string.IsNullOrEmpty(cover) && GameCoverService.IsValidImageFile(cover))
                    {
                        entry.CoverPath = cover;
                        entry.CoverUrl = cover;
                        await _databaseService.UpdateCoverPathAsync(entry.GameName, cover);
                        successCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Lỗi tải ảnh game '{Game}' trong đồng bộ: {Message}", entry.GameName, ex.Message);
                }
            }

            _dialogService.CloseProgress();

            if (_syncCoversCts.IsCancellationRequested)
            {
                _dialogService.ShowMessage("Đồng Bộ Ảnh Game", $"Đã dừng đồng bộ theo yêu cầu. Đã hoàn tất {successCount}/{total} tựa game.", "Information");
            }
            else
            {
                _dialogService.ShowMessage("Đồng Bộ Hoàn Tất", $"Đã đồng bộ thành công {successCount}/{total} ảnh bìa game!", "Success");
            }

            OnPropertyChanged(nameof(GameHistories));
        }
        catch (OperationCanceledException)
        {
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Đồng Bộ Ảnh Game", $"Đã dừng đồng bộ theo yêu cầu. Đã hoàn tất {successCount}/{total} tựa game.", "Information");
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            LoggingService.Error(ex, "Lỗi xảy ra trong quá trình đồng bộ ảnh game");
            _dialogService.ShowMessage("Lỗi Đồng Bộ", $"Đã xảy ra lỗi: {ex.Message}", "Error");
        }
        finally
        {
            _syncCoversCts?.Dispose();
            _syncCoversCts = null;
            IsSyncingCovers = false;
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
        SelectedGameHistory = entry;
        RefreshCurrentRevertPoint(entry.GameName);
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

    public void RefreshCurrentRevertPoint(string? gameName = null)
    {
        var name = gameName ?? SelectedHistoryGameTitle ?? SelectedGameHistory?.GameName;
        if (string.IsNullOrWhiteSpace(name))
        {
            CurrentRevertPoint = null;
        }
        else
        {
            CurrentRevertPoint = RevertService.GetLatestRevertPoint(name);
        }
    }

    public async Task ExecuteRevertGameSaveAsync()
    {
        var gameName = SelectedHistoryGameTitle ?? SelectedGameHistory?.GameName;
        if (string.IsNullOrWhiteSpace(gameName)) return;

        var revertPoint = CurrentRevertPoint ?? RevertService.GetLatestRevertPoint(gameName);
        if (revertPoint == null)
        {
            _dialogService.ShowMessage("Hoàn Tác Save Game", "Không tìm thấy điểm hoàn tác nào khả dụng cho game này.", "Warning");
            return;
        }

        _dialogService.ShowConfirm(
            "Xác Nhận Hoàn Tác (Revert)",
            $"Bạn có chắc chắn muốn hoàn tác save game '{gameName}' về trạng thái trước lần Restore gần nhất ({revertPoint.FormattedDate})?\n\nToàn bộ dữ liệu save hiện tại của game sẽ được thay thế bằng dữ liệu an toàn này.",
            revertPoint.FilePath,
            async () =>
            {
                _dialogService.ShowProgress("Đang Hoàn Tác Save Game", "Đang khôi phục lại dữ liệu từ điểm hoàn tác...", 15);
                var progress = new Progress<BackupProgress>(p =>
                {
                    _dialogService.UpdateProgress(p.Percent, p.Message);
                });

                try
                {
                    await RevertService.RevertGameSaveAsync(gameName, revertPoint.FilePath, progress);
                    _dialogService.CloseProgress();
                    _dialogService.ShowMessage("Hoàn Tác Thành Công", $"Đã khôi phục thành công dữ liệu save game '{gameName}' về trạng thái trước khi Restore!", "Success");
                }
                catch (Exception ex)
                {
                    _dialogService.CloseProgress();
                    LoggingService.Error(ex, "Lỗi khi hoàn tác save game {Game}: {Message}", gameName, ex.Message);
                    _dialogService.ShowMessage("Lỗi Hoàn Tác", $"Không thể hoàn tác save game: {ex.Message}", "Error");
                }
            });
    }

    public void ExecuteClearRevertPoints()
    {
        var gameName = SelectedHistoryGameTitle ?? SelectedGameHistory?.GameName;
        if (string.IsNullOrWhiteSpace(gameName)) return;

        var revertPoint = CurrentRevertPoint ?? RevertService.GetLatestRevertPoint(gameName);
        if (revertPoint == null) return;

        _dialogService.ShowConfirm(
            "Xác Nhận Xóa Bản Hoàn Tác",
            $"Bạn có chắc chắn muốn xóa bản nén hoàn tác của game '{gameName}' ({revertPoint.DisplaySize})?\n\nSau khi xóa, bạn sẽ giải phóng dung lượng ổ đĩa nhưng không thể tự động hoàn tác lại bản save cũ này được nữa.",
            revertPoint.FilePath,
            () =>
            {
                try
                {
                    RevertService.ClearRevertPoints(gameName);
                    RefreshCurrentRevertPoint(gameName);
                    _dialogService.ShowMessage("Đã Dọn Dẹp", $"Đã xóa sạch bản nén hoàn tác của game '{gameName}'.", "Success");
                }
                catch (Exception ex)
                {
                    LoggingService.Error(ex, "Lỗi khi xóa bản hoàn tác của {Game}: {Message}", gameName, ex.Message);
                    _dialogService.ShowMessage("Lỗi", $"Không thể xóa bản hoàn tác: {ex.Message}", "Error");
                }
                return Task.CompletedTask;
            });
    }

    public void RequestDeleteHistoryDetail(BackupHistoryDetail? detail)
    {
        if (detail == null) return;
        _dialogService.ShowConfirmDeleteSnapshot(detail, async (deleteLocal, cloudProviders) =>
        {
            await ExecuteDeleteSnapshotAsync(detail, deleteLocal, cloudProviders);
        });
    }

    private async Task ExecuteDeleteSnapshotAsync(BackupHistoryDetail detail, bool deleteLocal, List<string> cloudProviders)
    {
        _dialogService.ShowProgress("Đang Xóa Bản Snapshot", $"Đang chuẩn bị xóa bản snapshot ngày {detail.BackupDate:dd/MM/yyyy HH:mm}...", 10);
        LoggingService.LogAction("Delete_Snapshot_Start", new { Game = detail.GameName, DetailId = detail.Id, deleteLocal, Clouds = cloudProviders });

        var progress = new Progress<BackupProgress>(p =>
        {
            _dialogService.UpdateProgress(p.Percent, p.Message);
        });

        try
        {
            await _backupService.DeleteSnapshotWithProgressAsync(
                detail, 
                deleteLocal: deleteLocal,
                cloudProvidersToDelete: cloudProviders,
                cloudService: _cloudManager.CurrentProvider, 
                progress: progress,
                cloudServiceResolver: providerName => _cloudManager.GetProvider(providerName));

            if (!detail.HasAnyBackup)
            {
                CurrentHistoryDetails.Remove(detail);
                if (CurrentHistoryDetails.Count == 0)
                {
                    IsHistoryDetailsModalOpen = false;
                }
            }
            else
            {
                try
                {
                    var freshDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(detail.GameHistoryId);
                    CurrentHistoryDetails.Clear();
                    foreach (var fd in freshDetails)
                    {
                        CurrentHistoryDetails.Add(fd);
                    }
                }
                catch { }
            }
            SelectedHistoryDetail = null;
            await RefreshHistoryAsync();

            LoggingService.LogAction("Delete_Snapshot_Success", new { DetailId = detail.Id });
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Xóa Thành Công", "Đã xử lý xóa bản snapshot thành công.", "Success");
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
            LoggingService.Error(ex, "Lỗi khi xóa snapshot {Id}: {Message}", detail.Id, ex.Message);
            _dialogService.ShowMessage("Lỗi Xóa Snapshot", ex.Message, "Error", ex.StackTrace);
        }
    }

    public void RequestDeleteGameHistory(GameHistoryEntry? entry)
    {
        if (entry == null) return;
        _dialogService.ShowConfirmDeleteGame(entry, async (deleteLocal, deleteCloud) =>
        {
            await ExecuteDeleteGameAsync(entry, deleteLocal, deleteCloud);
        });
    }

    private async Task ExecuteDeleteGameAsync(GameHistoryEntry entry, bool deleteLocal, bool deleteFromCloud)
    {
        _dialogService.ShowProgress("Đang Xóa Toàn Bộ Game", $"Đang xóa toàn bộ sao lưu của {entry.GameName}...", 10);
        LoggingService.LogAction("Delete_Game_Start", new { Game = entry.GameName, Snapshots = entry.BackupCount, deleteLocal, deleteFromCloud });

        var progress = new Progress<BackupProgress>(p =>
        {
            _dialogService.UpdateProgress(p.Percent, p.Message);
        });

        try
        {
            await _backupService.DeleteGameHistoryWithProgressAsync(
                entry, 
                deleteLocal: deleteLocal,
                cloudProvidersToDelete: null,
                deleteFromCloud: deleteFromCloud,
                cloudService: _cloudManager.CurrentProvider, 
                progress: progress,
                cloudServiceResolver: providerName => _cloudManager.GetProvider(providerName));

            GameHistories.Remove(entry);
            SelectedGameHistory = null;
            SelectedGameSummary = null;
            IsHistoryDetailsModalOpen = false;
            await RefreshHistoryAsync();

            LoggingService.LogAction("Delete_Game_Success", new { Game = entry.GameName });
            _dialogService.CloseProgress();
            _dialogService.ShowMessage("Xóa Thành Công", $"Đã xóa sạch toàn bộ sao lưu của '{entry.GameName}'.", "Success");
        }
        catch (Exception ex)
        {
            _dialogService.CloseProgress();
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

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
