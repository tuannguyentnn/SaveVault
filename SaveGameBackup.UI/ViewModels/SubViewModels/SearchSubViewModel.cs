using System;
using System.Collections.ObjectModel;
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

public class SearchSubViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _databaseService;
    private readonly GameSearchCoordinator _searchCoordinator;
    private readonly IDialogService _dialogService;
    private readonly IAppEventBus _eventBus;
    private readonly INativeDialogService? _nativeDialog;

    private static readonly string[] DefaultPopularGames = new[]
    {
        "Elden Ring", "Cyberpunk 2077", "Black Myth: Wukong", "Baldur's Gate 3",
        "The Witcher 3", "Hades", "Dark Souls III", "Sekiro: Shadows Die Twice",
        "God of War", "Palworld", "Monster Hunter: World", "Grand Theft Auto V",
        "Red Dead Redemption 2", "Hogwarts Legacy", "Fallout 4"
    };

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

        foreach (var g in DefaultPopularGames)
        {
            PopularGameSuggestions.Add(g);
        }

        SearchCommand = new RelayCommand(async _ => await ExecuteSearchAsync(), _ => !IsSearching && !string.IsNullOrWhiteSpace(SearchQuery));
        AddCustomPathCommand = new RelayCommand(async _ => await ExecuteAddCustomPathAsync());
        SelectAllPathsCommand = new RelayCommand(_ => ExecuteSelectAllPaths(), _ => DetectedPathItems.Count > 0);
        DeselectAllPathsCommand = new RelayCommand(_ => ExecuteDeselectAllPaths(), _ => DetectedPathItems.Count > 0);
        OpenSpecificDetectedPathCommand = new RelayCommand(param => ExecuteOpenSpecificDetectedPath(param as string));
        RemoveDetectedPathCommand = new RelayCommand(param => ExecuteRemoveDetectedPath(param as DetectedPathItem));
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                (SearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    public async Task ExecuteSearchAsync(string? explicitQuery = null)
    {
        var query = !string.IsNullOrWhiteSpace(explicitQuery) ? explicitQuery : SearchQuery;
        if (string.IsNullOrWhiteSpace(query)) return;

        IsSearching = true;
        StatusMessage = $"Đang tìm kiếm vị trí save game cho '{query}'...";
        LoggingService.LogAction("Search_Game_Start", new { Query = query });

        try
        {
            ClearPreviousDetectedPaths();

            var gameInfo = await _searchCoordinator.SearchAndDetectGameAsync(query);
            if (gameInfo == null)
            {
                StatusMessage = $"Không tìm thấy thông tin cấu hình save game cho '{query}'. Bạn có thể thêm đường dẫn thủ công bên dưới.";
                CurrentGame = null;
                IsGameFoundOnDisk = false;
                LoggingService.LogAction("Search_Game_NotFound", new { Query = query });
                return;
            }

            CurrentGame = gameInfo;

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

            if (IsGameFoundOnDisk)
            {
                StatusMessage = $"Tìm thấy {DetectedPathItems.Count} vị trí lưu game trên máy ({DetectedFileCount} tệp, {DetectedSizeFormatted}).";
            }
            else
            {
                StatusMessage = $"Đã tìm thấy thông tin trên mạng ({gameInfo.RawPatterns.Count} mẫu), nhưng chưa thấy file save thực tế trên các ổ đĩa của bạn.";
            }

            LoggingService.LogAction("Search_Game_Success", new
            {
                GameName = gameInfo.GameName,
                FoundPaths = DetectedPathItems.Count,
                TotalFiles = gameInfo.FileCount,
                TotalSize = DetectedSizeFormatted
            });

            _eventBus.Publish(new GameSelectedForBackupEvent(gameInfo.GameName));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi tìm kiếm: {ex.Message}";
            LoggingService.Error(ex, "Lỗi tìm kiếm game: {Message}", ex.Message);
            _dialogService.ShowMessage("Lỗi Tìm Kiếm", ex.Message, "Error", ex.StackTrace);
        }
        finally
        {
            IsSearching = false;
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

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
