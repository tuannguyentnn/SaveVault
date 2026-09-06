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
}

public class AppSettings
{
    public string BackupRootDirectory { get; set; } = string.Empty;
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = false;
    public bool OverwriteExisting { get; set; } = true;
}
