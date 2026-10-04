using System;

namespace SaveGameBackup.Core.Models;

/// <summary>
/// Đại diện cho thông tin phiên bản (Version/Build) của game được cài đặt trên máy.
/// </summary>
public class GameVersionInfo
{
    /// <summary>
    /// Chuỗi hiển thị phiên bản thân thiện với người dùng (VD: "v2.31", "Build 25016043", "v1.6.659.0.8").
    /// </summary>
    public string DisplayVersion { get; set; } = string.Empty;

    /// <summary>
    /// FileVersion đọc trực tiếp từ metadata của file .exe (thông qua FileVersionInfo).
    /// </summary>
    public string? RawFileVersion { get; set; }

    /// <summary>
    /// ProductVersion đọc trực tiếp từ metadata của file .exe.
    /// </summary>
    public string? RawProductVersion { get; set; }

    /// <summary>
    /// Steam Build ID đọc từ appmanifest_<appid>.acf (nếu là game Steam).
    /// </summary>
    public string? SteamBuildId { get; set; }

    /// <summary>
    /// GOG Build ID hoặc version đọc từ Registry/goggame-*.info (nếu là game GOG).
    /// </summary>
    public string? GogBuildId { get; set; }

    /// <summary>
    /// Đường dẫn đến file thực thi .exe của game đã được phân tích.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Thư mục cài đặt game phát hiện được.
    /// </summary>
    public string? InstallDirectory { get; set; }

    /// <summary>
    /// Nguồn nhận diện phiên bản: "SteamManifest", "GogRegistry", "EpicManifest", "WindowsRegistry", "ExecutableMetadata", "DirectoryMarker".
    /// </summary>
    public string DetectionSource { get; set; } = "Unknown";

    /// <summary>
    /// Cơ chế phát hiện chi tiết: "OnlineFastProbe", "OnlineExeSearch", "LocalRecursiveBFS", "MetadataMatch", "SteamManifest", "GogRegistry", "SQLiteCache".
    /// </summary>
    public string DetectionMechanism { get; set; } = "LocalRecursiveBFS";

    /// <summary>
    /// Nguồn mạng trực tuyến cung cấp thông tin (nếu có): "GitHub Community Catalog", "Steam Launch Manifest", "PCGamingWiki", "Offline / Local".
    /// </summary>
    public string? OnlineSource { get; set; }

    /// <summary>
    /// Thời gian thực thi định vị file exe (tính bằng mili-giây).
    /// </summary>
    public double ResolutionDurationMs { get; set; }

    /// <summary>
    /// Ngày sửa đổi cuối cùng của file .exe game.
    /// </summary>
    public DateTime? ExecutableModifiedDate { get; set; }

    /// <summary>
    /// Thời điểm phát hiện phiên bản.
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Đã phát hiện được phiên bản hay chưa.
    /// </summary>
    public bool IsDetected => !string.IsNullOrWhiteSpace(DisplayVersion) &&
                              !DisplayVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                              !DisplayVersion.Equals("Không tìm ra phiên bản", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayVersion;
}
