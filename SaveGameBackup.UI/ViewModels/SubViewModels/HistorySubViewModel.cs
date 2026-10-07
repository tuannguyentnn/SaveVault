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

    // SQL Pagination Collections & State (Parallel with RAM Pagination)
    public ObservableCollection<GameHistoryEntry> PagedGameHistories { get; } = new();
    public ObservableCollection<BackupHistoryDetail> PagedHistoryDetails { get; } = new();

    private int _pagedTotalGames;
    public int PagedTotalGames
    {
        get => _pagedTotalGames;
        set => SetField(ref _pagedTotalGames, value);
    }

    private int _pagedTotalSnapshots;
    public int PagedTotalSnapshots
    {
        get => _pagedTotalSnapshots;
        set => SetField(ref _pagedTotalSnapshots, value);
    }

    // Detail pagination & sorting state
    private int _currentDetailPage = 1;
    public int CurrentDetailPage
    {
        get => _currentDetailPage;
        set => SetField(ref _currentDetailPage, value);
    }

    private string _detailSortColumn = "Date";
    public string DetailSortColumn
    {
        get => _detailSortColumn;
        set => SetField(ref _detailSortColumn, value);
    }

    private bool _detailSortAscending = false;
    public bool DetailSortAscending
    {
        get => _detailSortAscending;
        set => SetField(ref _detailSortAscending, value);
    }

    // Main History page tracking
    public int CurrentGamePage { get; set; } = 1;
    public int CurrentGamePageSize { get; set; } = 10;
    public string GameFilterText { get; set; } = string.Empty;
    public string GameSortColumn { get; set; } = "Date";
    public bool GameSortAscending { get; set; } = false;

    public bool UseSqlPagination
    {
        get => AppConfigService.GetConfig().UseSqlPagination;
        set
        {
            var config = AppConfigService.GetConfig();
            if (config.UseSqlPagination != value)
            {
                config.UseSqlPagination = value;
                AppConfigService.SaveConfig(config);
                OnPropertyChanged();
            }
        }
    }

    public void RunOnMainThread(Action action)
    {
        try
        {
            if (Microsoft.Maui.ApplicationModel.MainThread.IsMainThread)
            {
                action();
            }
            else
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(action);
            }
        }
        catch
        {
            action();
        }
    }

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
            if (IsHistoryDetailsModalOpen && SelectedGameHistory != null)
            {
                await RefreshCurrentGameDetailsAsync();
            }
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

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private int _hasPendingRefresh = 0;

    public async Task RefreshHistoryAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            Interlocked.Exchange(ref _hasPendingRefresh, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _hasPendingRefresh, 0);
                await DoRefreshHistoryCoreAsync();
            } while (Interlocked.CompareExchange(ref _hasPendingRefresh, 0, 1) == 1);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task DoRefreshHistoryCoreAsync()
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

            RunOnMainThread(UpdateUiCollections);

            if (UseSqlPagination)
            {
                var pageSize = CurrentGamePageSize > 0 ? CurrentGamePageSize : Math.Max(1, AppConfigService.GetConfig().PageSize);
                var pagedResult = await _databaseService.GetGameHistoriesPagedAsync(
                    CurrentGamePage, 
                    pageSize, 
                    string.IsNullOrEmpty(GameFilterText) ? null : GameFilterText, 
                    GameSortColumn, 
                    GameSortAscending);

                void UpdatePaged()
                {
                    PagedGameHistories.Clear();
                    foreach (var item in pagedResult.Items)
                    {
                        PagedGameHistories.Add(item);
                    }
                    PagedTotalGames = pagedResult.TotalItems;
                }

                RunOnMainThread(UpdatePaged);

                OnPropertyChanged(nameof(PagedGameHistories));
                OnPropertyChanged(nameof(PagedTotalGames));
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

    public async Task LoadGameHistoriesPagedAsync(int pageNumber, int pageSize, string? filter = null, string sortColumn = "Date", bool sortAscending = false)
    {
        try
        {
            CurrentGamePage = pageNumber;
            CurrentGamePageSize = pageSize;
            GameFilterText = filter ?? string.Empty;
            GameSortColumn = sortColumn;
            GameSortAscending = sortAscending;

            if (UseSqlPagination)
            {
                var result = await _databaseService.GetGameHistoriesPagedAsync(pageNumber, pageSize, filter, sortColumn, sortAscending);
                void UpdateUiCollections()
                {
                    PagedGameHistories.Clear();
                    foreach (var item in result.Items)
                    {
                        PagedGameHistories.Add(item);
                    }
                    PagedTotalGames = result.TotalItems;
                }

                RunOnMainThread(UpdateUiCollections);

                OnPropertyChanged(nameof(PagedGameHistories));
                OnPropertyChanged(nameof(PagedTotalGames));
            }
            else
            {
                if (GameHistories.Count == 0)
                {
                    await RefreshHistoryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi nạp danh sách game theo trang: {Message}", ex.Message);
        }
    }

    public async Task LoadGameDetailsPagedAsync(long gameHistoryId, int pageNumber, int pageSize, bool sortAscending = false)
    {
        try
        {
            CurrentDetailPage = pageNumber;
            DetailSortAscending = sortAscending;

            if (UseSqlPagination)
            {
                var result = await _databaseService.GetHistoryDetailsPagedAsync(gameHistoryId, pageNumber, pageSize, sortAscending);
                void UpdateUiCollections()
                {
                    PagedHistoryDetails.Clear();
                    foreach (var item in result.Items)
                    {
                        PagedHistoryDetails.Add(item);
                    }
                    PagedTotalSnapshots = result.TotalItems;
                }

                RunOnMainThread(UpdateUiCollections);

                OnPropertyChanged(nameof(PagedHistoryDetails));
                OnPropertyChanged(nameof(PagedTotalSnapshots));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi nạp snapshot theo trang: {Message}", ex.Message);
        }
    }

    public async Task RefreshCurrentGameDetailsAsync()
    {
        if (SelectedGameHistory == null) return;
        
        try
        {
            var freshDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(SelectedGameHistory.Id);
            
            RunOnMainThread(() =>
            {
                CurrentHistoryDetails.Clear();
                foreach (var fd in freshDetails)
                {
                    CurrentHistoryDetails.Add(fd);
                }
            });

            if (UseSqlPagination)
            {
                var pageSize = Math.Max(1, AppConfigService.GetConfig().PageSize);
                await LoadGameDetailsPagedAsync(SelectedGameHistory.Id, CurrentDetailPage, pageSize, DetailSortAscending);
            }
            else
            {
                PagedTotalSnapshots = freshDetails.Count;
            }

            if (SelectedHistoryDetail != null)
            {
                SelectedHistoryDetail = PagedHistoryDetails.FirstOrDefault(d => d.Id == SelectedHistoryDetail.Id) 
                                     ?? CurrentHistoryDetails.FirstOrDefault(d => d.Id == SelectedHistoryDetail.Id);
            }
            else
            {
                SelectedHistoryDetail = PagedHistoryDetails.FirstOrDefault() ?? CurrentHistoryDetails.FirstOrDefault();
            }

            OnPropertyChanged(nameof(CurrentHistoryDetails));
            OnPropertyChanged(nameof(PagedHistoryDetails));
            OnPropertyChanged(nameof(PagedTotalSnapshots));
            OnPropertyChanged(nameof(SelectedHistoryDetail));
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi refresh chi tiết game: {Message}", ex.Message);
        }
    }

    public async Task ExecuteOpenGameDetailsAsync(object? param)
    {
        GameHistoryEntry? entry = null;
        if (param is GameHistoryEntry ghe) entry = ghe;
        else if (param is GameBackupSummary summary)
        {
            entry = GameHistories.FirstOrDefault(g => string.Equals(g.GameName, summary.GameName, StringComparison.OrdinalIgnoreCase))
                 ?? PagedGameHistories.FirstOrDefault(g => string.Equals(g.GameName, summary.GameName, StringComparison.OrdinalIgnoreCase));
        }
        else if (param is string name)
        {
            entry = GameHistories.FirstOrDefault(g => string.Equals(g.GameName, name, StringComparison.OrdinalIgnoreCase))
                 ?? PagedGameHistories.FirstOrDefault(g => string.Equals(g.GameName, name, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null) return;

        SelectedHistoryGameTitle = entry.GameName;
        SelectedGameHistory = entry;
        RefreshCurrentRevertPoint(entry.GameName);

        CurrentDetailPage = 1;
        DetailSortColumn = "Date";
        DetailSortAscending = false;

        CurrentHistoryDetails.Clear();
        PagedHistoryDetails.Clear();

        try
        {
            var allDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(entry.Id);
            void PopulateAll()
            {
                foreach (var d in allDetails)
                {
                    CurrentHistoryDetails.Add(d);
                }
            }
            RunOnMainThread(PopulateAll);

            if (UseSqlPagination)
            {
                var pageSize = Math.Max(1, AppConfigService.GetConfig().PageSize);
                var result = await _databaseService.GetHistoryDetailsPagedAsync(entry.Id, 1, pageSize, sortAscending: false);
                PagedTotalSnapshots = result.TotalItems;
                void PopulatePaged()
                {
                    foreach (var d in result.Items)
                    {
                        PagedHistoryDetails.Add(d);
                    }
                }
                RunOnMainThread(PopulatePaged);
            }
            else
            {
                PagedTotalSnapshots = allDetails.Count;
            }

            SelectedHistoryDetail = PagedHistoryDetails.FirstOrDefault() ?? CurrentHistoryDetails.FirstOrDefault();
            IsHistoryDetailsModalOpen = true;

            OnPropertyChanged(nameof(CurrentHistoryDetails));
            OnPropertyChanged(nameof(PagedHistoryDetails));
            OnPropertyChanged(nameof(PagedTotalSnapshots));
            OnPropertyChanged(nameof(SelectedGameHistory));
            OnPropertyChanged(nameof(SelectedHistoryDetail));
            OnPropertyChanged(nameof(IsHistoryDetailsModalOpen));

            LoggingService.LogAction("View_Game_Snapshots", new { Game = entry.GameName, UseSql = UseSqlPagination });
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

            long gameHistoryId = detail.GameHistoryId;

            // 1. Tải danh sách snapshot mới nhất từ SQLite
            var freshDetails = await _databaseService.GetHistoryDetailsByGameIdAsync(gameHistoryId);

            if (freshDetails.Count == 0)
            {
                // Không còn bản sao lưu nào cho game này -> Đóng modal chi tiết
                void ClearDetails()
                {
                    CurrentHistoryDetails.Clear();
                    PagedHistoryDetails.Clear();
                    PagedTotalSnapshots = 0;
                    SelectedHistoryDetail = null;
                    SelectedGameHistory = null;
                    SelectedGameSummary = null;
                    IsHistoryDetailsModalOpen = false;
                }
                RunOnMainThread(ClearDetails);

                if (_dialogService is IBlazorDialogService blazorDialog)
                {
                    blazorDialog.CloseDetails();
                }

                var toRemoveGame = GameHistories.FirstOrDefault(g => g.Id == gameHistoryId || string.Equals(g.GameName, detail.GameName, StringComparison.OrdinalIgnoreCase));
                if (toRemoveGame != null) GameHistories.Remove(toRemoveGame);

                var toRemovePaged = PagedGameHistories.FirstOrDefault(g => g.Id == gameHistoryId || string.Equals(g.GameName, detail.GameName, StringComparison.OrdinalIgnoreCase));
                if (toRemovePaged != null) PagedGameHistories.Remove(toRemovePaged);
                if (PagedTotalGames > 0) PagedTotalGames--;
            }
            else
            {
                // Cập nhật lại toàn bộ danh sách chi tiết
                void UpdateDetails()
                {
                    CurrentHistoryDetails.Clear();
                    foreach (var fd in freshDetails)
                    {
                        CurrentHistoryDetails.Add(fd);
                    }
                }
                RunOnMainThread(UpdateDetails);

                // Cập nhật thông tin tổng của game (số lượng snapshot, dung lượng, ngày mới nhất)
                var allGames = await _databaseService.GetGameHistoriesAsync();
                var updatedEntry = allGames.FirstOrDefault(g => g.Id == gameHistoryId);
                if (updatedEntry != null)
                {
                    SelectedGameHistory = updatedEntry;

                    var gIdx = -1;
                    for (int i = 0; i < GameHistories.Count; i++)
                    {
                        if (GameHistories[i].Id == gameHistoryId) { gIdx = i; break; }
                    }
                    if (gIdx >= 0) GameHistories[gIdx] = updatedEntry;

                    var pIdx = -1;
                    for (int i = 0; i < PagedGameHistories.Count; i++)
                    {
                        if (PagedGameHistories[i].Id == gameHistoryId) { pIdx = i; break; }
                    }
                    if (pIdx >= 0) PagedGameHistories[pIdx] = updatedEntry;
                }

                // Cập nhật PagedHistoryDetails
                var pageSize = Math.Max(1, AppConfigService.GetConfig().PageSize);
                var totalPages = Math.Max(1, (int)Math.Ceiling(freshDetails.Count / (double)pageSize));
                if (CurrentDetailPage > totalPages) CurrentDetailPage = totalPages;
                if (CurrentDetailPage < 1) CurrentDetailPage = 1;

                if (UseSqlPagination)
                {
                    await LoadGameDetailsPagedAsync(gameHistoryId, CurrentDetailPage, pageSize, DetailSortAscending);
                }
                else
                {
                    PagedTotalSnapshots = freshDetails.Count;
                }

                SelectedHistoryDetail = PagedHistoryDetails.FirstOrDefault(d => d.Id != detail.Id)
                                     ?? CurrentHistoryDetails.FirstOrDefault(d => d.Id != detail.Id);
            }

            // Làm mới cả danh sách game ở tab chính
            await RefreshHistoryAsync();

            OnPropertyChanged(nameof(CurrentHistoryDetails));
            OnPropertyChanged(nameof(PagedHistoryDetails));
            OnPropertyChanged(nameof(PagedTotalSnapshots));
            OnPropertyChanged(nameof(SelectedGameHistory));
            OnPropertyChanged(nameof(SelectedHistoryDetail));
            OnPropertyChanged(nameof(IsHistoryDetailsModalOpen));

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

            void ClearGameFromUi()
            {
                GameHistories.Remove(entry);
                var pagedItem = PagedGameHistories.FirstOrDefault(g => g.Id == entry.Id || string.Equals(g.GameName, entry.GameName, StringComparison.OrdinalIgnoreCase));
                if (pagedItem != null) PagedGameHistories.Remove(pagedItem);
                if (PagedTotalGames > 0) PagedTotalGames--;

                CurrentHistoryDetails.Clear();
                PagedHistoryDetails.Clear();
                PagedTotalSnapshots = 0;
                SelectedGameHistory = null;
                SelectedGameSummary = null;
                SelectedHistoryDetail = null;
                IsHistoryDetailsModalOpen = false;
            }
            RunOnMainThread(ClearGameFromUi);

            if (_dialogService is IBlazorDialogService blazorDialog)
            {
                blazorDialog.CloseDetails();
            }

            await RefreshHistoryAsync();

            OnPropertyChanged(nameof(GameHistories));
            OnPropertyChanged(nameof(PagedGameHistories));
            OnPropertyChanged(nameof(PagedTotalGames));
            OnPropertyChanged(nameof(IsHistoryDetailsModalOpen));

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
