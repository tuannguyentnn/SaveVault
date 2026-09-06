namespace SaveGameBackup.Core.Models;

public class GameSaveInfo
{
    public string GameName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? SteamAppId { get; set; }
    public string? WikiPageTitle { get; set; }
    public List<string> RawPatterns { get; set; } = new();
    public List<string> ResolvedPaths { get; set; } = new();
    public List<string> DetectedPathsOnDisk { get; set; } = new();
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool IsFoundOnDisk => DetectedPathsOnDisk.Count > 0;
    public string Source { get; set; } = "Unknown";
    public DateTime? LastScanned { get; set; }
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
}

public class AppSettings
{
    public string BackupRootDirectory { get; set; } = string.Empty;
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool AutoCompressZip { get; set; } = false;
    public bool OverwriteExisting { get; set; } = true;
}
