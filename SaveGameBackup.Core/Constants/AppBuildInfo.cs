using System.Collections.Generic;

namespace SaveGameBackup.Core.Constants;

/// <summary>
/// Chá»©a thÃ´ng tin á»©ng dá»¥ng Ä‘Æ°á»£c náº¡p tÄ©nh trá»±c tiáº¿p vÃ o mÃ£ nguá»“n lÃºc build tá»« file config/info.json.
/// GiÃºp á»©ng dá»¥ng khá»Ÿi cháº¡y tá»©c thÃ¬ 0ms (Zero Disk I/O) mÃ  khÃ´ng cáº§n Ä‘á»c á»• Ä‘Ä©a má»—i khi má»Ÿ app.
/// </summary>
public static class AppBuildInfo
{
    public const string AppName = "SaveVault";
    public const string AppTitle = "SaveVault - Game Save Backup & Cloud Sync";
    public const string Version = "1.0.0";
    public const string BuildDate = "2026-09-29";
    public const string Author = "Nguyễn Anh Tuấn (tuannguyen01101995)";
    public const string Description = "Ứng dụng chuyên nghiệp tự động nhận diện, sao lưu và đồng bộ save game lên đám mây (Google Drive & OneDrive) dành riêng cho game thủ PC.";
    public const string License = "MIT License - Tự do sử dụng và tùy biến cho mục đích cá nhân phi thương mại.";

    public static class TechStack
    {
        public const string Framework = ".NET 10 MAUI & Blazor Hybrid";
        public const string Database = "SQLite (Dapper)";
        public const string Styling = "TailwindCSS v3 Modern Cyberpunk";
        public const string Cloud = "Google Drive v3 API & Microsoft Graph v1.0";
        public const string ManifestDb = "Ludusavi Community (12.000+ Games)";
    }

    public static class Links
    {
        public const string GitHub = "https://github.com/tuannguyen01101995/SaveVault";
        public const string Issues = "https://github.com/tuannguyen01101995/SaveVault/issues";
        public const string Releases = "https://github.com/tuannguyen01101995/SaveVault/releases";
        public const string Guide = "https://github.com/tuannguyen01101995/SaveVault/blob/main/README.md";
    }

    public static class Support
    {
        public const string Email = "tuannguyen01101995@gmail.com";
        public const string Community = "SaveVault Support Community";
        public const string Telegram = "https://t.me/savevault_support";
    }

    public static readonly IReadOnlyList<string> Features = new[]
    {
        "Tự động quét và nhận diện save game Steam, Epic, Ubisoft, GOG, AppData, Documents",
        "Tích hợp cơ sở dữ liệu mở hơn 12.000+ tựa game từ cộng đồng Ludusavi",
        "Nén ZIP chuẩn hóa kèm mã kiểm tra SHA256 chống hỏng file",
        "Đồng bộ hai chiều Google Drive & OneDrive với chính sách lưu trữ thông minh",
        "Hệ thống ảnh chụp an toàn (Safety Snapshots) bảo vệ tuyệt đối dữ liệu",
        "Cơ chế tự động cập nhật bản build mới với tính năng tự hoàn tác (Rollback)",
    };
}
