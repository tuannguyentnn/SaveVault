using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SaveGameBackup.Core.Models;

public class DetectedPathItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Path { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string FormattedSize
    {
        get
        {
            if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F1} KB";
            if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class GameSaveInfo
{
    public string GameName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? SteamAppId { get; set; }
    public string? WikiPageTitle { get; set; }
    public List<string> RawPatterns { get; set; } = new();
    public List<string> ResolvedPaths { get; set; } = new();
    public List<string> DetectedPathsOnDisk { get; set; } = new();
    public List<DetectedPathItem> DetectedPathItems { get; set; } = new();
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool IsFoundOnDisk => DetectedPathsOnDisk.Count > 0 || DetectedPathItems.Count > 0;
    public string Source { get; set; } = "Unknown";
    public DateTime? LastScanned { get; set; }
    public string? OnlineCoverUrl { get; set; }

    public string? WikiUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WikiPageTitle))
            {
                if (WikiPageTitle.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    WikiPageTitle.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return WikiPageTitle;
                }
                return $"https://www.pcgamingwiki.com/wiki/{Uri.EscapeDataString(WikiPageTitle.Trim().Replace(' ', '_'))}";
            }

            if (Source?.Contains("PCGamingWiki", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(GameName))
            {
                return $"https://www.pcgamingwiki.com/wiki/{Uri.EscapeDataString(GameName.Trim().Replace(' ', '_'))}";
            }

            return null;
        }
    }
}

public class BackupManifestItem
{
    public string SubFolder { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
}

public class BackupManifest
{
    public string GameName { get; set; } = string.Empty;
    public DateTime BackupDate { get; set; } = DateTime.Now;
    public List<BackupManifestItem> Items { get; set; } = new();
}

public class BackupRecord
{
    public long Id { get; set; }
    public string GameName { get; set; } = string.Empty;
    private string _backupPath = string.Empty;
    public string BackupPath
    {
        get => _backupPath;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith("{"))
            {
                var loc = BackupPathLocations.FromJson(value);
                _backupPath = loc.LocalPath ?? string.Empty;
            }
            else
            {
                _backupPath = value ?? string.Empty;
            }
        }
    }
    public string SourcePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime BackupDate { get; set; } = DateTime.Now;
    public bool IsCompressed { get; set; }
    public string Status { get; set; } = "Success";
    public string? Note { get; set; }
    public string? CoverPath { get; set; }

    public bool IsCloudSynced { get; set; }
    public string CloudProvider { get; set; } = string.Empty;
    public string CloudFileId { get; set; } = string.Empty;
    public string CloudFileName { get; set; } = string.Empty;
    public DateTime? CloudSyncDate { get; set; }
    public string CloudStatusFormatted => IsCloudSynced ? $"Đã đồng bộ ({CloudProvider})" : "Chưa đồng bộ Cloud";

    public string BackupTypeFormatted => IsCompressed ? "ZIP" : "Thư mục";

    public string FormattedSize
    {
        get
        {
            if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F1} KB";
            if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public List<string> SourcePathsList
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return new List<string>();
            try
            {
                if (SourcePath.TrimStart().StartsWith("["))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(SourcePath);
                    if (list != null && list.Count > 0) return list;
                }
            }
            catch { }

            if (SourcePath.Contains('|'))
            {
                return SourcePath.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            return new List<string> { SourcePath.Trim() };
        }
    }

    public string SavePaths { get; set; } = string.Empty;

    public List<string> SavePathsList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavePaths))
            {
                try
                {
                    if (SavePaths.TrimStart().StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(SavePaths);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }

                if (SavePaths.Contains('|'))
                {
                    return SavePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }

                return new List<string> { SavePaths.Trim() };
            }

            return SourcePathsList;
        }
    }
}

public class AppSettings
{
    public string BackupRootDirectory { get; set; } = string.Empty;
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = false;
    public bool OverwriteExisting { get; set; } = true;
    public bool EnableCloudBackup { get; set; } = false;
    public string SelectedCloudProvider { get; set; } = "GoogleDrive";
    public string GoogleDriveClientId { get; set; } = string.Empty;
    public string GoogleDriveClientSecret { get; set; } = string.Empty;
    public string OneDriveClientId { get; set; } = string.Empty;
}

public class RestoreItemTarget : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _restoreDestinationPath = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string OriginalSourcePath { get; set; } = string.Empty;

    public string RestoreDestinationPath
    {
        get => _restoreDestinationPath;
        set
        {
            if (_restoreDestinationPath != value)
            {
                _restoreDestinationPath = value;
                OnPropertyChanged();
            }
        }
    }

    public string SubFolder { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }

    public string FormattedSize
    {
        get
        {
            if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F1} KB";
            if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SubFolder)) return SubFolder;
            if (!string.IsNullOrWhiteSpace(OriginalSourcePath))
            {
                var trimmed = OriginalSourcePath.TrimEnd('\\', '/');
                var folderName = Path.GetFileName(trimmed);
                return !string.IsNullOrWhiteSpace(folderName) ? folderName : OriginalSourcePath;
            }
            return "Vị trí save game";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class GameBackupSummary : INotifyPropertyChanged
{
    private string _gameName = string.Empty;
    private int _backupCount;
    private DateTime _latestBackupDate;
    private long _latestSizeBytes;
    private long _totalSizeBytes;
    private string _latestBackupType = string.Empty;
    private string _latestBackupPath = string.Empty;
    private BackupRecord? _latestRecord;
    private List<BackupRecord> _records = new();

    public string GameName
    {
        get => _gameName;
        set => SetField(ref _gameName, value);
    }

    public int BackupCount
    {
        get => _backupCount;
        set => SetField(ref _backupCount, value);
    }

    public DateTime LatestBackupDate
    {
        get => _latestBackupDate;
        set => SetField(ref _latestBackupDate, value);
    }

    public long LatestSizeBytes
    {
        get => _latestSizeBytes;
        set => SetField(ref _latestSizeBytes, value);
    }

    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        set => SetField(ref _totalSizeBytes, value);
    }

    public string LatestBackupType
    {
        get => _latestBackupType;
        set => SetField(ref _latestBackupType, value);
    }

    public string LatestBackupPath
    {
        get => _latestBackupPath;
        set => SetField(ref _latestBackupPath, value);
    }

    public BackupRecord? LatestRecord
    {
        get => _latestRecord;
        set => SetField(ref _latestRecord, value);
    }

    public List<BackupRecord> Records
    {
        get => _records;
        set => SetField(ref _records, value);
    }

    public string FormattedLatestSize => FormatBytes(LatestSizeBytes);
    public string FormattedTotalSize => FormatBytes(TotalSizeBytes);
    public string FormattedLatestDate => LatestBackupDate != DateTime.MinValue ? LatestBackupDate.ToString("dd/MM/yyyy HH:mm") : "-";

    public string SavePaths => LatestRecord?.SavePaths ?? string.Empty;
    public List<string> SavePathsList => LatestRecord?.SavePathsList ?? new List<string>();

    public List<string> BackupFolderPaths
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LatestBackupPath))
            {
                try
                {
                    if (LatestBackupPath.TrimStart().StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(LatestBackupPath);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }

                return new List<string> { LatestBackupPath.Trim() };
            }
            return new List<string>();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Đại diện cho 1 dòng trong bảng backup_history (Master table - mỗi Game 1 dòng)
/// </summary>
public class GameHistoryEntry : INotifyPropertyChanged
{
    private long _id;
    private string _gameName = string.Empty;
    private int _backupCount = 1;
    private string _latestBackupPath = string.Empty;
    private DateTime _latestBackupDate = DateTime.Now;
    private long _totalSizeBytes;
    private long _latestSizeBytes;
    private int _latestFileCount;
    private string _savePaths = string.Empty;
    private string _status = "Success";
    private string? _note;

    public long Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string GameName
    {
        get => _gameName;
        set => SetField(ref _gameName, value);
    }

    public int BackupCount
    {
        get => _backupCount;
        set => SetField(ref _backupCount, value);
    }

    public string LatestBackupPath
    {
        get => _latestBackupPath;
        set => SetField(ref _latestBackupPath, value);
    }

    public DateTime LatestBackupDate
    {
        get => _latestBackupDate;
        set => SetField(ref _latestBackupDate, value);
    }

    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        set => SetField(ref _totalSizeBytes, value);
    }

    public long LatestSizeBytes
    {
        get => _latestSizeBytes;
        set => SetField(ref _latestSizeBytes, value);
    }

    public int LatestFileCount
    {
        get => _latestFileCount;
        set => SetField(ref _latestFileCount, value);
    }

    public string SavePaths
    {
        get => _savePaths;
        set => SetField(ref _savePaths, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string? Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    private bool _hasCloudBackup;
    public bool HasCloudBackup
    {
        get => _hasCloudBackup;
        set => SetField(ref _hasCloudBackup, value);
    }

    private string? _coverUrl;
    public string? CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (SetField(ref _coverUrl, value))
            {
                OnPropertyChanged(nameof(CoverDataSrc));
                OnPropertyChanged(nameof(CoverImageSrc));
            }
        }
    }

    private string? _coverPath;
    public string? CoverPath
    {
        get => _coverPath;
        set
        {
            if (SetField(ref _coverPath, value))
            {
                OnPropertyChanged(nameof(CoverImageSrc));
                OnPropertyChanged(nameof(CoverDataSrc));
            }
        }
    }

    public string? CoverImageSrc
    {
        get
        {
            if (!string.IsNullOrEmpty(CoverPath) && File.Exists(CoverPath) && SaveGameBackup.Core.Services.GameCoverService.IsValidImageFile(CoverPath))
            {
                return SaveGameBackup.Core.Services.GameCoverService.GetCoverImageUri(CoverPath);
            }
            var defaultPath = SaveGameBackup.Core.Services.GameCoverService.GetCoverFilePath(GameName);
            if (File.Exists(defaultPath) && SaveGameBackup.Core.Services.GameCoverService.IsValidImageFile(defaultPath))
            {
                return SaveGameBackup.Core.Services.GameCoverService.GetCoverImageUri(defaultPath);
            }
            if (!string.IsNullOrEmpty(CoverUrl) && 
                (CoverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || CoverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                if (SaveGameBackup.Core.Services.GameCoverService.IsNetworkAvailable())
                {
                    return SaveGameBackup.Core.Services.GameCoverService.GetCoverImageUri(CoverUrl);
                }
            }
            return null;
        }
    }

    public string? CoverDataSrc => SaveGameBackup.Core.Services.GameCoverService.GetCoverDataUrl(CoverPath ?? CoverUrl);

    public string FormattedTotalSize => FormatBytes(TotalSizeBytes);
    public string FormattedLatestSize => FormatBytes(LatestSizeBytes);
    public string FormattedLatestDate => LatestBackupDate != DateTime.MinValue ? LatestBackupDate.ToString("dd/MM/yyyy HH:mm") : "-";

    public List<string> SavePathsList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavePaths))
            {
                try
                {
                    if (SavePaths.TrimStart().StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(SavePaths);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }

                if (SavePaths.Contains('|'))
                {
                    return SavePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }

                return new List<string> { SavePaths.Trim() };
            }
            return new List<string>();
        }
    }

    public List<string> BackupFolderPaths
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LatestBackupPath))
            {
                try
                {
                    if (LatestBackupPath.TrimStart().StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(LatestBackupPath);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }

                return new List<string> { LatestBackupPath.Trim() };
            }
            return new List<string>();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Cấu trúc lưu vết đường dẫn sao lưu của 1 snapshot: vị trí file cục bộ (LocalPath) và danh sách URL xem online trên các Cloud (CloudUrls)
/// </summary>
public class BackupPathLocations
{
    public string? LocalPath { get; set; }
    public Dictionary<string, string> CloudUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static BackupPathLocations FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BackupPathLocations();
        try
        {
            if (json.TrimStart().StartsWith("{"))
            {
                var loc = JsonSerializer.Deserialize<BackupPathLocations>(json);
                if (loc != null) return loc;
            }
            return new BackupPathLocations { LocalPath = json.Trim() };
        }
        catch
        {
            return new BackupPathLocations { LocalPath = json?.Trim() };
        }
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
    }
}

/// <summary>
/// Đại diện cho 1 dòng trong bảng backup_history_details (Detail table - từng bản snapshot sao lưu)
/// </summary>
public class BackupHistoryDetail : INotifyPropertyChanged
{
    private long _id;
    private long _gameHistoryId;
    private string _gameName = string.Empty;
    private string _backupPath = string.Empty;
    private BackupPathLocations? _locations;
    private string _sourcePath = string.Empty;
    private string _savePaths = string.Empty;
    private string _manifestJson = string.Empty;
    private int _fileCount;
    private long _totalSizeBytes;
    private DateTime _backupDate = DateTime.Now;
    private bool _isCompressed;
    private string _status = "Success";
    private string? _note;
    private bool _isCloudSynced;
    private string _cloudProvider = string.Empty;
    private string _cloudFileId = string.Empty;
    private string _cloudFileName = string.Empty;
    private DateTime? _cloudSyncDate;
    private string? _coverUrl;
    public string? CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (SetField(ref _coverUrl, value))
            {
                OnPropertyChanged(nameof(CoverDataSrc));
                OnPropertyChanged(nameof(CoverImageSrc));
            }
        }
    }

    private string? _coverPath;
    public string? CoverPath
    {
        get => _coverPath;
        set
        {
            if (SetField(ref _coverPath, value))
            {
                OnPropertyChanged(nameof(CoverImageSrc));
                OnPropertyChanged(nameof(CoverDataSrc));
            }
        }
    }

    public string? CoverImageSrc => SaveGameBackup.Core.Services.GameCoverService.GetCoverImageUri(CoverPath ?? CoverUrl);
    public string? CoverDataSrc => SaveGameBackup.Core.Services.GameCoverService.GetCoverDataUrl(CoverPath ?? CoverUrl);

    public long Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public long GameHistoryId
    {
        get => _gameHistoryId;
        set => SetField(ref _gameHistoryId, value);
    }

    public string GameName
    {
        get => _gameName;
        set => SetField(ref _gameName, value);
    }

    public string BackupPath
    {
        get => _backupPath;
        set
        {
            if (SetField(ref _backupPath, value))
            {
                _locations = null;
                OnPropertyChanged(nameof(LocalBackupPath));
                OnPropertyChanged(nameof(HasLocalBackup));
                OnPropertyChanged(nameof(HasCloudBackup));
                OnPropertyChanged(nameof(HasAnyBackup));
                OnPropertyChanged(nameof(CloudUrls));
            }
        }
    }

    public BackupPathLocations Locations
    {
        get
        {
            _locations ??= BackupPathLocations.FromJson(_backupPath);
            return _locations;
        }
    }

    public void SyncLocationsToBackupPath()
    {
        _backupPath = Locations.ToJson();
        OnPropertyChanged(nameof(BackupPath));
        OnPropertyChanged(nameof(LocalBackupPath));
        OnPropertyChanged(nameof(HasLocalBackup));
        OnPropertyChanged(nameof(HasCloudBackup));
        OnPropertyChanged(nameof(HasAnyBackup));
        OnPropertyChanged(nameof(CloudUrls));
    }

    public string LocalBackupPath => Locations.LocalPath ?? string.Empty;

    public bool HasLocalBackup => !string.IsNullOrEmpty(LocalBackupPath) && (File.Exists(LocalBackupPath) || Directory.Exists(LocalBackupPath));

    public bool HasCloudBackup => Locations.CloudUrls.Count > 0 || IsCloudSynced;

    public bool HasAnyBackup => HasLocalBackup || HasCloudBackup;

    public Dictionary<string, string> CloudUrls => Locations.CloudUrls;

    public void SetLocalPath(string? localPath)
    {
        Locations.LocalPath = localPath;
        SyncLocationsToBackupPath();
    }

    public void SetCloudUrl(string provider, string url)
    {
        Locations.CloudUrls[provider] = url;
        SyncLocationsToBackupPath();
    }

    public void RemoveCloudUrl(string provider)
    {
        Locations.CloudUrls.Remove(provider);
        SyncLocationsToBackupPath();
    }

    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }

    public string SavePaths
    {
        get => _savePaths;
        set => SetField(ref _savePaths, value);
    }

    /// <summary>
    /// Toàn bộ cấu trúc manifest dưới dạng chuỗi JSON, lưu thẳng trong database
    /// </summary>
    public string ManifestJson
    {
        get => _manifestJson;
        set => SetField(ref _manifestJson, value);
    }

    public int FileCount
    {
        get => _fileCount;
        set => SetField(ref _fileCount, value);
    }

    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        set => SetField(ref _totalSizeBytes, value);
    }

    public DateTime BackupDate
    {
        get => _backupDate;
        set => SetField(ref _backupDate, value);
    }

    public bool IsCompressed
    {
        get => _isCompressed;
        set => SetField(ref _isCompressed, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string? Note
    {
        get => _note;
        set => SetField(ref _note, value);
    }

    public bool IsCloudSynced
    {
        get => _isCloudSynced;
        set
        {
            if (SetField(ref _isCloudSynced, value))
            {
                OnPropertyChanged(nameof(CloudStatusFormatted));
            }
        }
    }

    public string CloudProvider
    {
        get => _cloudProvider;
        set
        {
            if (SetField(ref _cloudProvider, value))
            {
                OnPropertyChanged(nameof(CloudStatusFormatted));
            }
        }
    }

    public string CloudFileId
    {
        get => _cloudFileId;
        set => SetField(ref _cloudFileId, value);
    }

    public string CloudFileName
    {
        get => _cloudFileName;
        set => SetField(ref _cloudFileName, value);
    }

    public DateTime? CloudSyncDate
    {
        get => _cloudSyncDate;
        set => SetField(ref _cloudSyncDate, value);
    }

    private List<CloudSyncInfo> _cloudSyncList = new();
    private string _cloudSyncJson = string.Empty;

    public List<CloudSyncInfo> CloudSyncList
    {
        get => _cloudSyncList;
        set
        {
            if (SetField(ref _cloudSyncList, value))
            {
                OnPropertyChanged(nameof(HasMultipleClouds));
                OnPropertyChanged(nameof(CloudStatusFormatted));
            }
        }
    }

    public string CloudSyncJson
    {
        get => _cloudSyncJson;
        set => SetField(ref _cloudSyncJson, value);
    }

    public bool HasMultipleClouds => CloudSyncList.Count > 1;

    public bool IsSyncedTo(string provider) =>
        CloudSyncList.Any(c => c.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public CloudSyncInfo? GetSyncInfo(string provider) =>
        CloudSyncList.FirstOrDefault(c => c.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public string CloudStatusFormatted => IsCloudSynced 
        ? (!string.IsNullOrWhiteSpace(CloudProvider) ? $"Đã đồng bộ ({CloudProvider})" : "Đã đồng bộ Cloud") 
        : "Chưa đồng bộ Cloud";

    public string BackupTypeFormatted => IsCompressed ? "ZIP" : "Thư mục";

    public string FormattedSize
    {
        get
        {
            if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
            if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024.0:F1} KB";
            if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024.0 * 1024.0):F1} MB";
            return $"{TotalSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }

    public List<string> SavePathsList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavePaths))
            {
                try
                {
                    if (SavePaths.TrimStart().StartsWith("["))
                    {
                        var list = JsonSerializer.Deserialize<List<string>>(SavePaths);
                        if (list != null && list.Count > 0) return list;
                    }
                }
                catch { }

                if (SavePaths.Contains('|'))
                {
                    return SavePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }

                return new List<string> { SavePaths.Trim() };
            }

            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                if (SourcePath.Contains('|'))
                {
                    return SourcePath.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
                return new List<string> { SourcePath.Trim() };
            }

            return new List<string>();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}


