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
    public string BackupPath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime BackupDate { get; set; } = DateTime.Now;
    public bool IsCompressed { get; set; }
    public string Status { get; set; } = "Success";
    public string? Note { get; set; }

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
/// Đại diện cho 1 dòng trong bảng backup_history_details (Detail table - từng bản snapshot sao lưu)
/// </summary>
public class BackupHistoryDetail : INotifyPropertyChanged
{
    private long _id;
    private long _gameHistoryId;
    private string _gameName = string.Empty;
    private string _backupPath = string.Empty;
    private string _sourcePath = string.Empty;
    private string _savePaths = string.Empty;
    private string _manifestJson = string.Empty;
    private int _fileCount;
    private long _totalSizeBytes;
    private DateTime _backupDate = DateTime.Now;
    private bool _isCompressed;
    private string _status = "Success";
    private string? _note;

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
        set => SetField(ref _backupPath, value);
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


