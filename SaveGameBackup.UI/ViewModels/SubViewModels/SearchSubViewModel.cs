using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;
using SaveGameBackup.UI.Services;

namespace SaveGameBackup.UI.ViewModels.SubViewModels;

public class SearchSubViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly GameSearchCoordinator _searchCoordinator;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;

    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private string _statusMessage = "Sẵn sàng. Hãy nhập tên game để tìm kiếm vị trí save game.";

    private GameSaveInfo? _currentGame;
    private bool _hasGame;
    private string _detectedSizeFormatted = "0 B";
    private int _detectedFileCount;
    private bool _isGameFoundOnDisk;

    private int _selectedPathCount;
    private int _selectedFileCount;
    private string _selectedSizeFormatted = "0 B";
    private bool _hasSelectedPaths;
    private string? _onlineCoverUrl;

    private bool _isGameDropdownOpen;
    private TaskCompletionSource<string?>? _candidateSelectionTcs;
    private CancellationTokenSource? _searchCts;
    private int _searchEpoch;

    public bool IsGameDropdownOpen
    {
        get => _isGameDropdownOpen;
        set
        {
            if (SetField(ref _isGameDropdownOpen, value))
            {
                OnPropertyChanged(nameof(IsGameSelectModalOpen));
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsGameSelectModalOpen
    {
        get => _isGameDropdownOpen;
        set => IsGameDropdownOpen = value;
    }

    public ObservableCollection<string> GameCandidates { get; } = new();

    public string? OnlineCoverUrl
    {
        get => _onlineCoverUrl;
        set => SetField(ref _onlineCoverUrl, value);
    }

    public ObservableCollection<string> PopularGameSuggestions { get; } = new();
    public ObservableCollection<DetectedPathItem> DetectedPathItems { get; } = new();
    public ObservableCollection<string> DetectedPathsList { get; } = new();
    public ObservableCollection<string> OnlinePatternsList { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchSubViewModel(
        DatabaseService databaseService,
        GameSearchCoordinator searchCoordinator,
        IDialogService dialogService,
        IAppEventBus eventBus,
        INativeDialogService? nativeDialog = null)
    {
        _databaseService = databaseService;
        _searchCoordinator = searchCoordinator;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _nativeDialog = nativeDialog;

        SearchCommand = new RelayCommand(async _ => await ExecuteSearchAsync(), _ => (!IsSearching || IsGameDropdownOpen) && !string.IsNullOrWhiteSpace(SearchQuery));
        AddCustomPathCommand = new RelayCommand(async _ => await ExecuteAddCustomPathAsync());
        SelectAllPathsCommand = new RelayCommand(_ => ExecuteSelectAllPaths(), _ => DetectedPathItems.Count > 0);
        DeselectAllPathsCommand = new RelayCommand(_ => ExecuteDeselectAllPaths(), _ => DetectedPathItems.Count > 0);
        OpenSpecificDetectedPathCommand = new RelayCommand(param => ExecuteOpenSpecificDetectedPath(param as string));
        RemoveDetectedPathCommand = new RelayCommand(param => ExecuteRemoveDetectedPath(param as DetectedPathItem));
        SelectCandidateCommand = new RelayCommand(param => ExecuteSelectCandidate(param as string));
        CancelCandidateModalCommand = new RelayCommand(_ => ExecuteCancelCandidate());
        PickExecutableFileManuallyCommand = new RelayCommand(async _ => await PickExecutableFileManuallyAsync());

        // Lắng nghe sự kiện Backup thành công để tự động nạp lại 10 game gợi ý từ cache
        _eventBus.Subscribe<BackupCompletedEvent>(async _ =>
        {
            await LoadRecentCacheSuggestionsAsync();
        });

        // Nạp danh sách gợi ý từ SQLite Cache gần nhất
        _ = LoadRecentCacheSuggestionsAsync();
    }

    private void RunOnMainThread(Action action)
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

    public async Task LoadRecentCacheSuggestionsAsync()
    {
        try
        {
            var recent = await _databaseService.GetRecentCachedGamesAsync(10);
            RunOnMainThread(() =>
            {
                PopularGameSuggestions.Clear();
                foreach (var g in recent)
                {
                    if (!string.IsNullOrWhiteSpace(g.GameName))
                    {
                        PopularGameSuggestions.Add(g.GameName);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Lỗi nạp danh sách game gần đây từ SQLite cache: {Message}", ex.Message);
        }
    }

    public async Task<bool> DeleteGameCacheAsync(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return false;

        try
        {
            var success = await _databaseService.DeleteGameCacheAsync(gameName);
            if (success)
            {
                LoggingService.LogAction("Game_Cache_Deleted", new { GameName = gameName });
                StatusMessage = $"Đã xóa cache cho game '{gameName}'.";
                await LoadRecentCacheSuggestionsAsync();
            }
            return success;
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi xóa cache game {Game}: {Message}", gameName, ex.Message);
            StatusMessage = $"Lỗi xóa cache cho game '{gameName}': {ex.Message}";
            return false;
        }
    }

    public Task<string?> PromptChooseCandidateAsync(List<string> candidates)
    {
        _candidateSelectionTcs = new TaskCompletionSource<string?>();
        RunOnMainThread(() =>
        {
            GameCandidates.Clear();
            foreach (var c in candidates.Take(5))
            {
                GameCandidates.Add(c);
            }
            IsGameDropdownOpen = true;
        });
        return _candidateSelectionTcs.Task;
    }

    private void ExecuteSelectCandidate(string? chosen)
    {
        IsGameDropdownOpen = false;
        _candidateSelectionTcs?.TrySetResult(chosen);
    }

    private void ExecuteCancelCandidate()
    {
        IsGameDropdownOpen = false;
        _candidateSelectionTcs?.TrySetResult(null);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
                if (string.IsNullOrWhiteSpace(value))
                {
                    ResetGameInfo();
                }
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (SetField(ref _isSearching, value))
            {
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public GameSaveInfo? CurrentGame
    {
        get => _currentGame;
        set
        {
            if (SetField(ref _currentGame, value))
            {
                HasGame = value != null;
            }
        }
    }

    public bool HasGame
    {
        get => _hasGame;
        set => SetField(ref _hasGame, value);
    }

    public string DetectedSizeFormatted
    {
        get => _detectedSizeFormatted;
        set => SetField(ref _detectedSizeFormatted, value);
    }

    public int DetectedFileCount
    {
        get => _detectedFileCount;
        set => SetField(ref _detectedFileCount, value);
    }

    public bool IsGameFoundOnDisk
    {
        get => _isGameFoundOnDisk;
        set => SetField(ref _isGameFoundOnDisk, value);
    }

    public int SelectedPathCount
    {
        get => _selectedPathCount;
        set => SetField(ref _selectedPathCount, value);
    }

    public int SelectedFileCount
    {
        get => _selectedFileCount;
        set => SetField(ref _selectedFileCount, value);
    }

    public string SelectedSizeFormatted
    {
        get => _selectedSizeFormatted;
        set => SetField(ref _selectedSizeFormatted, value);
    }

    public bool HasSelectedPaths
    {
        get => _hasSelectedPaths;
        set => SetField(ref _hasSelectedPaths, value);
    }

    public ICommand SearchCommand { get; }
    public ICommand AddCustomPathCommand { get; }
    public ICommand SelectAllPathsCommand { get; }
    public ICommand DeselectAllPathsCommand { get; }
    public ICommand OpenSpecificDetectedPathCommand { get; }
    public ICommand RemoveDetectedPathCommand { get; }
    public ICommand SelectCandidateCommand { get; }
    public ICommand CancelCandidateModalCommand { get; }
    public ICommand PickExecutableFileManuallyCommand { get; }

    public async Task ExecuteSearchAsync(string? explicitQuery = null)
    {
        var query = !string.IsNullOrWhiteSpace(explicitQuery) ? explicitQuery : SearchQuery;
        if (string.IsNullOrWhiteSpace(query)) return;

        // Nếu dropdown đang mở hoặc đang chờ chọn từ tìm kiếm trước, hủy lượt chờ đó để tìm kiếm mới ngay
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        var epoch = ++_searchEpoch;

        if (IsGameDropdownOpen || (_candidateSelectionTcs != null && !_candidateSelectionTcs.Task.IsCompleted))
        {
            IsGameDropdownOpen = false;
            _candidateSelectionTcs?.TrySetResult(null);
        }

        IsSearching = true;
        StatusMessage = $"Đang tìm kiếm vị trí save game cho '{query}'...";
        LoggingService.LogAction("Search_Game_Start", new { Query = query });

        try
        {
            ClearPreviousDetectedPaths();

            var progressReporter = new Progress<string>(msg =>
            {
                if (epoch == _searchEpoch)
                {
                    StatusMessage = msg;
                }
            });

            var gameInfo = await _searchCoordinator.SearchAndDetectGameAsync(query, progressReporter, PromptChooseCandidateAsync, ct);
            ct.ThrowIfCancellationRequested();

            if (epoch != _searchEpoch) return;

            if (gameInfo == null)
            {
                StatusMessage = $"Không tìm thấy thông tin cấu hình save game cho '{query}' hoặc bạn đã hủy chọn. Bạn có thể thêm đường dẫn thủ công bên dưới.";
                CurrentGame = null;
                IsGameFoundOnDisk = false;
                LoggingService.LogAction("Search_Game_NotFound", new { Query = query });
                return;
            }

            CurrentGame = gameInfo;
            if (!string.IsNullOrWhiteSpace(gameInfo.GameName) && !string.Equals(SearchQuery, gameInfo.GameName, StringComparison.OrdinalIgnoreCase))
            {
                _searchQuery = gameInfo.GameName;
                OnPropertyChanged(nameof(SearchQuery));
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }

            // Nếu không có mạng: hoàn toàn không cần tải hình cover online
            if (!GameCoverService.IsNetworkAvailable())
            {
                if (GameCoverService.HasLocalCover(gameInfo.GameName))
                {
                    var localFile = GameCoverService.GetCoverFilePath(gameInfo.GameName);
                    OnlineCoverUrl = GameCoverService.GetCoverImageUri(localFile);
                }
                else
                {
                    OnlineCoverUrl = null;
                    gameInfo.OnlineCoverUrl = null;
                }
            }
            else
            {
                // Có mạng: Tải ảnh bìa về Temp/covers/ và lấy URI ảo cục bộ https://tempcovers.local/{fileName}
                try
                {
                    var tempUri = await GameCoverService.DownloadToTempCoverAsync(
                        gameInfo.GameName,
                        gameInfo.OnlineCoverUrl,
                        gameInfo.SteamAppId,
                        ct);

                    if (epoch != _searchEpoch) return;

                    OnlineCoverUrl = tempUri;
                    if (!string.IsNullOrEmpty(tempUri))
                    {
                        if (string.IsNullOrEmpty(gameInfo.OnlineCoverUrl) || gameInfo.OnlineCoverUrl.Contains(".local", StringComparison.OrdinalIgnoreCase))
                        {
                            var realWebUrl = await GameCoverService.FindOnlineCoverUrlAsync(gameInfo.GameName, gameInfo.SteamAppId, ct);
                            if (!string.IsNullOrEmpty(realWebUrl) && !realWebUrl.Contains(".local", StringComparison.OrdinalIgnoreCase))
                            {
                                gameInfo.OnlineCoverUrl = realWebUrl;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Lỗi tải ảnh cover tạm cho {Game}: {Message}", gameInfo.GameName, ex.Message);
                    if (epoch == _searchEpoch)
                    {
                        OnlineCoverUrl = null;
                    }
                }
            }

            if (epoch != _searchEpoch) return;

            // Nạp các pattern online
            foreach (var p in gameInfo.RawPatterns)
            {
                OnlinePatternsList.Add(p);
            }

            // Nạp các đường dẫn phát hiện
            foreach (var item in gameInfo.DetectedPathItems)
            {
                item.PropertyChanged += DetectedPathItem_PropertyChanged;
                DetectedPathItems.Add(item);
                DetectedPathsList.Add(item.Path);
            }

            DetectedSizeFormatted = FormatBytes(gameInfo.TotalSizeBytes);
            DetectedFileCount = gameInfo.FileCount;
            IsGameFoundOnDisk = DetectedPathItems.Count > 0;

            UpdateSelectedPathsStats();

            // Luôn đọc lại file thực thi (.exe) trên đĩa để lấy phiên bản mới nhất
            var freshVersion = ResolveLatestVersion(
                gameInfo.GameName,
                gameInfo.ExecutablePath,
                gameInfo.CurrentGameVersion != "Không tìm ra phiên bản" ? gameInfo.CurrentGameVersion : null,
                gameInfo.OnlineSource);

            if (freshVersion != null && freshVersion.IsDetected)
            {
                gameInfo.DetectedVersion = freshVersion;
            }

            if (IsGameFoundOnDisk)
            {
                StatusMessage = $"Tìm thấy {DetectedPathItems.Count} vị trí lưu game trên máy ({DetectedFileCount} tệp, {DetectedSizeFormatted}) từ nguồn: {gameInfo.Source}.";
            }
            else
            {
                StatusMessage = $"Đã tìm thấy thông tin từ nguồn '{gameInfo.Source}' ({gameInfo.RawPatterns.Count} mẫu), nhưng chưa thấy file save thực tế trên các ổ đĩa của bạn.";
            }

            LoggingService.LogAction("Search_Game_Success", new
            {
                GameName = gameInfo.GameName,
                Source = gameInfo.Source,
                FoundPaths = DetectedPathItems.Count,
                TotalFiles = gameInfo.FileCount,
                TotalSize = DetectedSizeFormatted
            });

            _eventBus.Publish(new GameSelectedForBackupEvent(gameInfo.GameName));

            // Làm mới danh sách 10 game gợi ý gần nhất từ SQLite cache
            _ = LoadRecentCacheSuggestionsAsync();
        }
        catch (OperationCanceledException)
        {
            // Bị hủy bởi lượt tìm kiếm mới hơn, bỏ qua an toàn
        }
        catch (Exception ex)
        {
            if (epoch == _searchEpoch)
            {
                StatusMessage = $"Lỗi khi tìm kiếm: {ex.Message}";
                LoggingService.Error(ex, "Lỗi tìm kiếm game: {Message}", ex.Message);
                _dialogService.ShowMessage("Lỗi Tìm Kiếm", ex.Message, "Error", ex.StackTrace);
            }
        }
        finally
        {
            if (epoch == _searchEpoch)
            {
                IsSearching = false;
            }
        }
    }

    private void DetectedPathItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetectedPathItem.IsSelected))
        {
            UpdateSelectedPathsStats();
        }
    }

    public void UpdateSelectedPathsStats()
    {
        var selected = DetectedPathItems.Where(x => x.IsSelected).ToList();
        SelectedPathCount = selected.Count;
        SelectedFileCount = selected.Sum(x => x.FileCount);
        long totalBytes = selected.Sum(x => x.TotalSizeBytes);
        SelectedSizeFormatted = FormatBytes(totalBytes);
        HasSelectedPaths = SelectedPathCount > 0;

        (SelectAllPathsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeselectAllPathsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task ExecuteAddCustomPathAsync()
    {
        var folder = _nativeDialog != null
            ? await _nativeDialog.PickFolderAsync("Chọn thư mục chứa Save Game muốn thêm vào danh sách")
            : null;

        if (!string.IsNullOrEmpty(folder))
        {
            if (!DetectedPathItems.Any(x => string.Equals(x.Path, folder, StringComparison.OrdinalIgnoreCase)))
            {
                int fileCount = 0;
                long sizeBytes = 0;
                if (Directory.Exists(folder))
                {
                    try
                    {
                        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                        fileCount = files.Length;
                        sizeBytes = files.Sum(f => new FileInfo(f).Length);
                    }
                    catch { }
                }

                var item = new DetectedPathItem
                {
                    Path = folder,
                    FileCount = fileCount,
                    TotalSizeBytes = sizeBytes,
                    IsSelected = true
                };
                item.PropertyChanged += DetectedPathItem_PropertyChanged;
                DetectedPathItems.Add(item);
                DetectedPathsList.Add(folder);

                if (CurrentGame == null)
                {
                    CurrentGame = new GameSaveInfo
                    {
                        GameName = !string.IsNullOrWhiteSpace(SearchQuery) ? SearchQuery : Path.GetFileName(folder),
                        Source = "Manual"
                    };
                }

                IsGameFoundOnDisk = true;
                UpdateSelectedPathsStats();

                LoggingService.LogAction("Add_Custom_Path", new { Folder = folder, Game = CurrentGame.GameName });
            }
        }
    }

    private void ExecuteSelectAllPaths()
    {
        foreach (var item in DetectedPathItems)
        {
            item.IsSelected = true;
        }
        UpdateSelectedPathsStats();
        LoggingService.LogAction("Select_All_Paths", new { Count = DetectedPathItems.Count });
    }

    private void ExecuteDeselectAllPaths()
    {
        foreach (var item in DetectedPathItems)
        {
            item.IsSelected = false;
        }
        UpdateSelectedPathsStats();
        LoggingService.LogAction("Deselect_All_Paths", new { Count = DetectedPathItems.Count });
    }

    private void ExecuteOpenSpecificDetectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _dialogService.ShowMessage("Thông báo", "Đường dẫn thư mục không tồn tại trên máy tính.", "Warning");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            LoggingService.LogAction("Open_Explorer_Path", new { Path = path });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Không thể mở Explorer: {Path}", path);
        }
    }

    /// <summary>
    /// Mở thư mục chứa file thực thi (.exe) đã tìm được trong Windows Explorer và tự động chọn (highlight) file đó.
    /// </summary>
    public void OpenExecutableFolder()
    {
        var exePath = CurrentGame?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _dialogService.ShowMessage("Thông báo", "Chưa phát hiện được đường dẫn file thực thi của game này.", "Warning");
            return;
        }

        try
        {
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{exePath}\"",
                    UseShellExecute = true
                });
                LoggingService.LogAction("Open_Executable_Folder", new { Path = exePath, Mode = "SelectFile" });
                return;
            }

            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
                LoggingService.LogAction("Open_Executable_Folder", new { Path = dir, Mode = "Directory" });
                return;
            }

            _dialogService.ShowMessage("Thông báo", "File hoặc thư mục thực thi không còn tồn tại trên máy tính.", "Warning");
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Không thể mở thư mục file thực thi: {Path}", exePath);
            _dialogService.ShowMessage("Lỗi", $"Không thể mở thư mục: {ex.Message}", "Error");
        }
    }

    /// <summary>
    /// Cho phép người dùng chọn file thực thi (.exe) thủ công từ máy tính để phân tích và cập nhật phiên bản game.
    /// </summary>
    public async Task PickExecutableFileManuallyAsync()
    {
        if (CurrentGame == null)
        {
            _dialogService.ShowMessage("Thông báo", "Vui lòng tìm kiếm hoặc chọn một game trước khi chỉ định file thực thi.", "Warning");
            return;
        }

        if (_nativeDialog == null)
        {
            _dialogService.ShowMessage("Lỗi", "Dịch vụ chọn tệp không khả dụng trên hệ thống này.", "Error");
            return;
        }

        try
        {
            var selectedFile = await _nativeDialog.PickExecutableFileAsync("Chọn file thực thi (.exe) của game");
            if (string.IsNullOrWhiteSpace(selectedFile))
            {
                return; // Người dùng đã hủy chọn
            }

            if (!File.Exists(selectedFile))
            {
                _dialogService.ShowMessage("Lỗi", "File được chọn không tồn tại trên hệ thống.", "Error");
                return;
            }

            if (!selectedFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                _dialogService.ShowMessage("Cảnh báo", "Vui lòng chọn một file thực thi có định dạng .exe.", "Warning");
                return;
            }

            var dir = Path.GetDirectoryName(selectedFile) ?? string.Empty;
            var versionInfo = GameVersionDetectorService.ExtractVersionFromExe(selectedFile, dir);

            if (versionInfo == null)
            {
                var fi = new FileInfo(selectedFile);
                versionInfo = new GameVersionInfo
                {
                    DisplayVersion = $"Build {fi.LastWriteTime:yyyy.MM.dd}",
                    ExecutablePath = selectedFile,
                    InstallDirectory = dir,
                    ExecutableModifiedDate = fi.LastWriteTime,
                    DetectionSource = "ExecutableMetadata"
                };
            }

            versionInfo.DetectionMechanism = "Manual";
            versionInfo.OnlineSource = "Manual Selection";
            CurrentGame.DetectedVersion = versionInfo;

            var detectedVersion = versionInfo.DisplayVersion;
            OnPropertyChanged(nameof(CurrentGame));
            LoggingService.LogAction("Manual_Exe_Selected", new
            {
                Game = CurrentGame.GameName,
                ExePath = selectedFile,
                Version = detectedVersion
            });

            _dialogService.ShowMessage(
                "Thành công",
                $"Đã cập nhật file thực thi cho '{CurrentGame.GameName}':\n• Phiên bản: {detectedVersion}\n• File: {selectedFile}",
                "Success");
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Lỗi khi chọn file thực thi thủ công");
            _dialogService.ShowMessage("Lỗi", $"Không thể phân tích file thực thi: {ex.Message}", "Error");
        }
    }

    public void OpenWikiPage()
    {
        var url = CurrentGame?.WikiUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            LoggingService.LogAction("Open_PCGamingWiki_Web", new { Url = url, Game = CurrentGame?.GameName });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Không thể mở trang PCGamingWiki: {Url}", url);
        }
    }

    public void OpenSteamStorePage(string? steamAppId = null)
    {
        var id = !string.IsNullOrWhiteSpace(steamAppId) ? steamAppId : CurrentGame?.SteamAppId;
        if (string.IsNullOrWhiteSpace(id)) return;

        var url = $"https://store.steampowered.com/app/{Uri.EscapeDataString(id.Trim())}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            LoggingService.LogAction("Open_Steam_Store_Web", new { Url = url, SteamAppId = id, Game = CurrentGame?.GameName });
        }
        catch (Exception ex)
        {
            LoggingService.Error(ex, "Không thể mở trang Steam Store: {Url}", url);
        }
    }

    private void ExecuteRemoveDetectedPath(DetectedPathItem? item)
    {
        if (item == null) return;

        item.PropertyChanged -= DetectedPathItem_PropertyChanged;
        DetectedPathItems.Remove(item);
        DetectedPathsList.Remove(item.Path);

        UpdateSelectedPathsStats();
        IsGameFoundOnDisk = DetectedPathItems.Count > 0;

        LoggingService.LogAction("Remove_Detected_Path", new { RemovedPath = item.Path, Remaining = DetectedPathItems.Count });
    }

    public void ClearPreviousDetectedPaths()
    {
        foreach (var item in DetectedPathItems)
        {
            item.PropertyChanged -= DetectedPathItem_PropertyChanged;
        }
        DetectedPathItems.Clear();
        DetectedPathsList.Clear();
        OnlinePatternsList.Clear();
        SelectedPathCount = 0;
        SelectedFileCount = 0;
        SelectedSizeFormatted = "0 B";
        HasSelectedPaths = false;
    }

    public void ResetGameInfo()
    {
        ClearPreviousDetectedPaths();
        CurrentGame = null;
        OnlineCoverUrl = null;
        HasGame = false;
        DetectedSizeFormatted = "0 B";
        DetectedFileCount = 0;
        IsGameFoundOnDisk = false;
        StatusMessage = string.Empty;
        _eventBus.Publish(new GameSelectedForBackupEvent(string.Empty));
    }

    public void UpdateGameName(string newName)
    {
        if (CurrentGame != null && !string.IsNullOrWhiteSpace(newName))
        {
            CurrentGame.GameName = newName.Trim();
            OnPropertyChanged(nameof(CurrentGame));
            _eventBus.Publish(new GameSelectedForBackupEvent(CurrentGame.GameName));

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!GameCoverService.IsNetworkAvailable())
                    {
                        if (GameCoverService.HasLocalCover(CurrentGame.GameName))
                        {
                            var localFile = GameCoverService.GetCoverFilePath(CurrentGame.GameName);
                            var localUri = GameCoverService.GetCoverImageUri(localFile);
                            if (CurrentGame != null && CurrentGame.GameName == newName.Trim())
                            {
                                OnlineCoverUrl = localUri;
                                CurrentGame.OnlineCoverUrl = localUri;
                            }
                        }
                        return;
                    }

                    var tempUri = await GameCoverService.DownloadToTempCoverAsync(CurrentGame.GameName);
                    if (CurrentGame != null && CurrentGame.GameName == newName.Trim())
                    {
                        OnlineCoverUrl = tempUri;
                        CurrentGame.OnlineCoverUrl = tempUri;
                    }
                }
                catch { }
            });
        }
    }

    /// <summary>
    /// Nạp dữ liệu cấu hình trực tiếp từ một bản snapshot cụ thể (BackupHistoryDetail) lên giao diện Tab 1 để chuẩn bị sao lưu.
    /// Trích xuất toàn bộ đường dẫn từ bản snapshot (SavePathsList, SourcePath, ManifestJson) và hợp nhất (parse thêm) vào dữ liệu hiện có trong Cache.
    /// Đồng thời nạp thông tin phiên bản, file .exe và nguồn tìm ra .exe.
    /// </summary>
    public async Task LoadGameFromDetailAsync(BackupHistoryDetail detail)
    {
        if (detail == null || string.IsNullOrWhiteSpace(detail.GameName)) return;

        ClearPreviousDetectedPaths();

        var gameName = detail.GameName;
        _searchQuery = gameName;
        OnPropertyChanged(nameof(SearchQuery));
        (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();

        StatusMessage = $"Đang nạp và hợp nhất cấu hình từ bản sao lưu ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss} cho '{gameName}'...";

        try
        {
            // 1. Trích xuất tất cả các đường dẫn từ bản snapshot (SavePathsList, SourcePath, ManifestJson Items)
            var snapshotPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in detail.SavePathsList)
            {
                if (!string.IsNullOrWhiteSpace(p)) snapshotPaths.Add(p.Trim());
            }
            if (!string.IsNullOrWhiteSpace(detail.SourcePath))
            {
                foreach (var p in detail.SourcePath.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(p)) snapshotPaths.Add(p.Trim());
                }
            }

            // Trích xuất thêm từ ManifestJson nếu có các Items con
            if (!string.IsNullOrWhiteSpace(detail.ManifestJson))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(detail.ManifestJson);
                    if (manifest?.Items != null)
                    {
                        foreach (var item in manifest.Items)
                        {
                            if (!string.IsNullOrWhiteSpace(item.SourcePath))
                            {
                                snapshotPaths.Add(item.SourcePath.Trim());
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Lấy dữ liệu hiện có (Cache của game từ SQLite) để parse thêm vào
            var cachedGame = await _databaseService.GetCachedGameAsync(gameName);
            
            // Tập hợp toàn bộ RawPatterns: giữ lại các pattern gốc PCGW / Ludusavi và bổ sung thêm các đường dẫn từ snapshot
            var allRawPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cachedGame?.RawPatterns != null)
            {
                foreach (var p in cachedGame.RawPatterns)
                {
                    if (!string.IsNullOrWhiteSpace(p)) allRawPatterns.Add(p.Trim());
                }
            }
            foreach (var p in snapshotPaths)
            {
                if (!string.IsNullOrWhiteSpace(p)) allRawPatterns.Add(p.Trim());
            }

            // 3. Phân giải tất cả các mẫu (patterns) thành các đường dẫn thực tế trên máy tính
            var pathResolver = _searchCoordinator.PathResolver ?? new PathResolverService();
            var candidateConcretePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Bổ sung các đường dẫn từ snapshot
            foreach (var p in snapshotPaths)
            {
                if (p.Contains("{{p|", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = pathResolver.ResolveRawPattern(p);
                    foreach (var r in resolved) candidateConcretePaths.Add(r);
                }
                else
                {
                    candidateConcretePaths.Add(p);
                }
            }

            // Phân giải các RawPatterns từ Cache (ví dụ {{p|userprofile}}\..., {{p|steam}}\..., v.v.)
            foreach (var pat in allRawPatterns)
            {
                if (pat.Contains("{{p|", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = pathResolver.ResolveRawPattern(pat);
                    foreach (var r in resolved) candidateConcretePaths.Add(r);
                }
                else
                {
                    candidateConcretePaths.Add(pat);
                }
            }

            // 4. Kiểm tra các thư mục/tệp tin thực tế trên đĩa (LOẠI BỎ hoàn toàn các mẫu {{p|...}} hoặc đường dẫn ảo không tồn tại)
            var detectedItems = pathResolver.InspectDetectedPathItems(candidateConcretePaths);

            // Nếu InspectDetectedPathItems không tìm thấy gì nhưng snapshot có đường dẫn cụ thể trên đĩa,
            // kiểm tra thêm các đường dẫn cụ thể (không chứa {{p|...}}) từ snapshot
            if (detectedItems.Count == 0 && snapshotPaths.Count > 0)
            {
                foreach (var sp in snapshotPaths)
                {
                    if (!sp.Contains("{{p|", StringComparison.OrdinalIgnoreCase))
                    {
                        var item = InspectPath(sp);
                        if (item.FileCount > 0 || Directory.Exists(item.Path) || File.Exists(item.Path))
                        {
                            detectedItems.Add(item);
                        }
                    }
                }
            }

            foreach (var item in detectedItems)
            {
                item.PropertyChanged += DetectedPathItem_PropertyChanged;
                DetectedPathItems.Add(item);
                DetectedPathsList.Add(item.Path);
                OnlinePatternsList.Add(item.Path);
            }

            // 4. Nạp ảnh bìa cục bộ (ưu tiên Covers/ -> detail.CoverPath -> cache)
            string? coverUri = null;
            if (!string.IsNullOrEmpty(detail.CoverPath) && File.Exists(detail.CoverPath))
            {
                coverUri = GameCoverService.GetCoverImageUri(detail.CoverPath);
            }
            else
            {
                var localCover = GameCoverService.GetCoverFilePath(gameName);
                if (File.Exists(localCover))
                {
                    coverUri = GameCoverService.GetCoverImageUri(localCover);
                }
                else if (!string.IsNullOrEmpty(cachedGame?.OnlineCoverUrl))
                {
                    coverUri = cachedGame.OnlineCoverUrl;
                }
                else
                {
                    var tempCover = GameCoverService.GetTempCoverFilePath(gameName);
                    if (File.Exists(tempCover))
                    {
                        coverUri = GameCoverService.GetTempCoverImageUri(tempCover);
                    }
                }
            }

            OnlineCoverUrl = coverUri;

            // 5. Luôn đọc lại file thực thi (.exe) trên đĩa để lấy phiên bản mới nhất từ snapshot / cache / quét mới
            var versionStr = detail.GameVersion ?? cachedGame?.CurrentGameVersion;
            var exePath = detail.ExecutablePath ?? cachedGame?.ExecutablePath;
            var exeSource = detail.ExeSource ?? cachedGame?.OnlineSource ?? cachedGame?.DetectedVersion?.DetectionSource;

            var versionInfo = ResolveLatestVersion(
                gameName,
                exePath,
                versionStr,
                exeSource);

            // 6. Tạo GameSaveInfo và nạp vào ViewModel
            var gameInfo = new GameSaveInfo
            {
                GameName = gameName,
                SteamAppId = detail.SteamAppId ?? cachedGame?.SteamAppId,
                WikiPageTitle = cachedGame?.WikiPageTitle,
                Source = cachedGame?.Source ?? "Chi tiết lịch sử sao lưu (Snapshot)",
                RawPatterns = allRawPatterns.ToList(),
                ResolvedPaths = candidateConcretePaths.ToList(),
                DetectedPathItems = detectedItems,
                DetectedPathsOnDisk = detectedItems.Where(i => i.FileCount > 0 || Directory.Exists(i.Path) || File.Exists(i.Path)).Select(i => i.Path).ToList(),
                TotalSizeBytes = detectedItems.Where(i => i.IsSelected).Sum(i => i.TotalSizeBytes),
                FileCount = detectedItems.Where(i => i.IsSelected).Sum(i => i.FileCount),
                OnlineCoverUrl = coverUri,
                DetectedVersion = versionInfo,
                LastScanned = DateTime.Now
            };

            CurrentGame = gameInfo;
            DetectedSizeFormatted = FormatBytes(gameInfo.TotalSizeBytes);
            DetectedFileCount = gameInfo.FileCount;
            IsGameFoundOnDisk = DetectedPathItems.Any(i => i.FileCount > 0 || Directory.Exists(i.Path) || File.Exists(i.Path));

            UpdateSelectedPathsStats();

            // 7. Cập nhật / lưu lại cache với dữ liệu đã được parse thêm vào
            try
            {
                await _databaseService.SaveGameCacheAsync(gameInfo);
            }
            catch (Exception exCache)
            {
                LoggingService.Warn("Lỗi cập nhật cache khi nạp snapshot: {Message}", exCache.Message);
            }

            if (IsGameFoundOnDisk)
            {
                StatusMessage = $"Đã nạp và hợp nhất {DetectedPathItems.Count} vị trí lưu ({DetectedFileCount} tệp, {DetectedSizeFormatted}) từ bản snapshot ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss}. Sẵn sàng sao lưu!";
            }
            else
            {
                StatusMessage = $"Đã nạp {DetectedPathItems.Count} vị trí lưu từ bản snapshot ngày {detail.BackupDate:dd/MM/yyyy HH:mm:ss}, nhưng các thư mục này hiện chưa thấy trên máy của bạn.";
            }

            _eventBus.Publish(new GameSelectedForBackupEvent(gameName));

            LoggingService.LogAction("Detail_Snapshot_Merged_For_Backup", new
            {
                GameName = gameName,
                DetailId = detail.Id,
                detail.BackupDate,
                MergedCount = allRawPatterns.Count,
                DetectedCount = detectedItems.Count
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi nạp cấu hình từ bản sao lưu: {ex.Message}";
            LoggingService.Error(ex, "Lỗi nạp game từ snapshot chi tiết cho {Game}: {Message}", gameName, ex.Message);
        }
    }

    /// <summary>
    /// Nạp dữ liệu cấu hình trực tiếp từ lịch sử sao lưu (History) lên giao diện Tab 1.
    /// Hoạt động 100% offline, không gọi API online, phản hồi tức thì.
    /// </summary>
    public async Task LoadGameFromHistoryAsync(GameHistoryEntry historyEntry)
    {
        if (historyEntry == null || string.IsNullOrWhiteSpace(historyEntry.GameName)) return;

        ClearPreviousDetectedPaths();

        var gameName = historyEntry.GameName;
        _searchQuery = gameName;
        OnPropertyChanged(nameof(SearchQuery));
        (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();

        StatusMessage = $"Đang nạp cấu hình sao lưu cho '{gameName}' từ cơ sở dữ liệu lịch sử...";

        try
        {
            // 1. Lấy snapshot mới nhất của game từ SQLite
            var details = await _databaseService.GetHistoryDetailsByGameIdAsync(historyEntry.Id);
            var latestSnapshot = details?.OrderByDescending(s => s.BackupDate).ThenByDescending(s => s.Id).FirstOrDefault();

            var paths = new List<string>();
            if (latestSnapshot != null && latestSnapshot.SavePathsList.Count > 0)
            {
                paths.AddRange(latestSnapshot.SavePathsList);
            }
            else if (historyEntry.SavePathsList.Count > 0)
            {
                paths.AddRange(historyEntry.SavePathsList);
            }

            // 2. Kiểm tra các thư mục/tệp tin thực tế trên đĩa
            var detectedItems = new List<DetectedPathItem>();
            foreach (var p in paths)
            {
                var item = InspectPath(p);
                item.PropertyChanged += DetectedPathItem_PropertyChanged;
                detectedItems.Add(item);
                DetectedPathItems.Add(item);
                DetectedPathsList.Add(item.Path);
                OnlinePatternsList.Add(p);
            }

            // 3. Nạp ảnh bìa cục bộ (ưu tiên Covers/ -> Temp/covers/)
            string? coverUri = null;
            if (!string.IsNullOrEmpty(historyEntry.CoverPath) && File.Exists(historyEntry.CoverPath))
            {
                coverUri = GameCoverService.GetCoverImageUri(historyEntry.CoverPath);
            }
            else
            {
                var localCover = GameCoverService.GetCoverFilePath(gameName);
                if (File.Exists(localCover))
                {
                    coverUri = GameCoverService.GetCoverImageUri(localCover);
                }
                else
                {
                    var tempCover = GameCoverService.GetTempCoverFilePath(gameName);
                    if (File.Exists(tempCover))
                    {
                        coverUri = GameCoverService.GetTempCoverImageUri(tempCover);
                    }
                }
            }

            OnlineCoverUrl = coverUri;

            // 4. Tạo GameSaveInfo và nạp vào ViewModel - Luôn đọc lại file thực thi (.exe) trên đĩa lấy phiên bản mới nhất
            var versionStr = latestSnapshot?.GameVersion ?? historyEntry.GameVersion;
            var exePath = latestSnapshot?.ExecutablePath ?? historyEntry.ExecutablePath;
            var exeSource = latestSnapshot?.ExeSource ?? historyEntry.ExeSource;

            var versionInfo = ResolveLatestVersion(
                gameName,
                exePath,
                versionStr,
                exeSource);

            var effectiveSteamAppId = latestSnapshot?.SteamAppId ?? historyEntry.SteamAppId;
            if (string.IsNullOrEmpty(effectiveSteamAppId))
            {
                try
                {
                    var cached = await _databaseService.GetCachedGameAsync(gameName);
                    effectiveSteamAppId = cached?.SteamAppId;
                }
                catch { }
            }

            var gameInfo = new GameSaveInfo
            {
                GameName = gameName,
                SteamAppId = effectiveSteamAppId,
                Source = "Lịch sử sao lưu",
                RawPatterns = paths,
                ResolvedPaths = paths,
                DetectedPathItems = detectedItems,
                DetectedPathsOnDisk = detectedItems.Select(i => i.Path).ToList(),
                TotalSizeBytes = detectedItems.Sum(i => i.TotalSizeBytes),
                FileCount = detectedItems.Sum(i => i.FileCount),
                OnlineCoverUrl = coverUri,
                DetectedVersion = versionInfo,
                LastScanned = DateTime.Now
            };

            CurrentGame = gameInfo;
            DetectedSizeFormatted = FormatBytes(gameInfo.TotalSizeBytes);
            DetectedFileCount = gameInfo.FileCount;
            IsGameFoundOnDisk = DetectedPathItems.Count > 0;

            UpdateSelectedPathsStats();

            if (IsGameFoundOnDisk)
            {
                StatusMessage = $"Đã nạp {DetectedPathItems.Count} vị trí lưu từ lịch sử ({DetectedFileCount} tệp, {DetectedSizeFormatted}). Sẵn sàng sao lưu lại!";
            }
            else
            {
                StatusMessage = $"Đã nạp thông tin game '{gameName}' từ lịch sử, nhưng chưa thấy file save thực tế trên các ổ đĩa của bạn.";
            }

            _eventBus.Publish(new GameSelectedForBackupEvent(gameName));

            LoggingService.LogAction("History_Game_Loaded_For_Backup", new
            {
                GameName = gameName,
                PathsCount = paths.Count,
                DetectedCount = detectedItems.Count
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi nạp cấu hình từ lịch sử: {ex.Message}";
            LoggingService.Error(ex, "Lỗi nạp game từ lịch sử cho {Game}: {Message}", gameName, ex.Message);
        }
    }

    /// <summary>
    /// Đảm bảo luôn đọc lại file thực thi (.exe) trên đĩa để lấy phiên bản mới nhất khi nạp game vào giao diện sao lưu
    /// (chỉ giữ lại duy nhất Ưu tiên 1: Đọc trực tiếp từ file .exe đã xác định).
    /// </summary>
    private static GameVersionInfo? ResolveLatestVersion(
        string gameName,
        string? preferredExePath,
        string? fallbackVersionStr,
        string? exeSource)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        // Ưu tiên 1: Nếu đã có đường dẫn .exe chỉ định (từ snapshot, history, hoặc cache) và file này tồn tại trên máy
        if (!string.IsNullOrWhiteSpace(preferredExePath) && File.Exists(preferredExePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(preferredExePath) ?? string.Empty;
                var freshInfo = GameVersionDetectorService.ExtractVersionFromExe(preferredExePath, dir);
                if (freshInfo != null && freshInfo.IsDetected)
                {
                    freshInfo.OnlineSource = !string.IsNullOrWhiteSpace(exeSource) ? exeSource : "ExecutableMetadata";
                    freshInfo.DetectionMechanism = "ExecutableMetadata";
                    freshInfo.DetectionSource = !string.IsNullOrWhiteSpace(exeSource) ? exeSource : "ExecutableFile";
                    LoggingService.LogAction("Fresh_Exe_Version_Extracted", new
                    {
                        GameName = gameName,
                        ExePath = preferredExePath,
                        Version = freshInfo.DisplayVersion,
                        Source = freshInfo.OnlineSource
                    });
                    return freshInfo;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Lỗi đọc file .exe trực tiếp cho {Game}: {Message}", gameName, ex.Message);
            }
        }

        // Fallback: Nếu không có file .exe trên máy hoặc không đọc được, fallback về phiên bản đã ghi nhận trước đó (nếu có)
        if (!string.IsNullOrWhiteSpace(fallbackVersionStr))
        {
            return new GameVersionInfo
            {
                DisplayVersion = fallbackVersionStr,
                ExecutablePath = preferredExePath,
                DetectionSource = exeSource ?? "HistoricalRecord",
                DetectionMechanism = "HistoricalFallback",
                OnlineSource = exeSource
            };
        }

        return new GameVersionInfo
        {
            DisplayVersion = "Không tìm ra phiên bản",
            DetectionSource = "NotDetected",
            DetectionMechanism = "None",
            OnlineSource = "Offline / Local"
        };
    }

    private static DetectedPathItem InspectPath(string path)
    {
        int fileCount = 0;
        long sizeBytes = 0;
        if (Directory.Exists(path))
        {
            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                fileCount = files.Length;
                sizeBytes = files.Sum(f => new FileInfo(f).Length);
            }
            catch { }
        }
        else if (File.Exists(path))
        {
            try
            {
                fileCount = 1;
                sizeBytes = new FileInfo(path).Length;
            }
            catch { }
        }

        return new DetectedPathItem
        {
            Path = path,
            FileCount = fileCount,
            TotalSizeBytes = sizeBytes,
            IsSelected = true
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
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
