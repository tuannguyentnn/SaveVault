# 🎮 Game Save Backup Tool (.NET 10 + SQLite)

Ứng dụng desktop hiện đại trên nền tảng **.NET 10 (WPF)** và cơ sở dữ liệu **SQLite**, cho phép bạn chỉ cần **nhập tên bất kỳ game nào**, hệ thống sẽ:
1. **Tự động tìm kiếm vị trí lưu save game trên mạng** (qua PCGamingWiki MediaWiki API, cơ sở dữ liệu offline Ludusavi và danh mục hơn 50+ game phổ biến).
2. **Quét và phát hiện file save thực tế trên máy tính của bạn** (phân giải các biến môi trường Windows như `%USERPROFILE%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%DOCUMENTS%`, `Saved Games`, thư mục Steam userdata `<Steam-path>\userdata\<user-id>\<app-id>`, ký tự đại diện `*`,...).
3. **Sao lưu (Backup) 1-Click** toàn bộ file save vào thư mục đích với tên folder là tên game (hỗ trợ tạo snapshot theo thời gian và nén file `.zip`).
4. **Lưu trữ lịch sử và khôi phục (Restore)** hoàn chỉnh với SQLite.

---

## 🚀 Tính năng nổi bật

- 🔎 **Tìm kiếm thông minh & Đa nguồn**:
  - Tích hợp MediaWiki / OpenSearch API từ PCGamingWiki để tra cứu thời gian thực.
  - Tích hợp sẵn cơ sở dữ liệu Ludusavi Catalog cho các game bom tấn (Elden Ring, Cyberpunk 2077, Black Myth: Wukong, Baldur's Gate 3, The Witcher 3, Hades, God of War, Palworld, Monster Hunter, v.v.).
  - Tìm kiếm dự phòng thông minh (Heuristic Fallback) quét các thư mục `%APPDATA%`, `%LOCALAPPDATA%`, `Saved Games`, `Documents/My Games`.
- 💾 **Cơ sở dữ liệu SQLite**:
  - Tự động cache kết quả tìm kiếm game giúp các lần tra cứu sau diễn ra tức thì ngay cả khi mất mạng.
  - Quản lý bảng lịch sử `backup_history` với đầy đủ thông tin: Tên game, ngày giờ, số lượng tệp, dung lượng (KB/MB/GB), trạng thái và đường dẫn.
  - Lưu cài đặt cấu hình người dùng `settings`.
- 📁 **Tổ chức thư mục sao lưu chuẩn mực**:
  - Thư mục backup được tạo tự động với tên là tên game (ví dụ: `Backups/Elden Ring/...`).
  - Hỗ trợ tùy chọn tạo Snapshot kèm ngày giờ (`yyyy-MM-dd_HH-mm-ss`) để bạn lưu nhiều phiên bản save.
  - Hỗ trợ nén file `.zip` tiết kiệm dung lượng ổ cứng.
- 🔄 **Tính năng Khôi phục (Restore)**:
  - Cho phép phục hồi trực tiếp dữ liệu từ bản sao lưu (cả dạng Folder và dạng ZIP) ngược lại vị trí save gốc của game chỉ với 1 click.
- 🎨 **Giao diện Modern Dark UI**:
  - WPF trên .NET 10 với giao diện Dark Mode cao cấp.
  - Nút gợi ý nhanh các tựa game nổi tiếng.
  - Thanh tiến trình và thông báo trạng thái trực tiếp trong quá trình sao lưu/khôi phục.
  - Mở nhanh thư mục Save gốc hoặc thư mục Backup trong Windows Explorer.

---

## 🛠 Cấu trúc dự án

```text
BackupSaveGames/
├── SaveGameBackup.slnx              # Solution XML (.NET 10)
├── LaunchApp.bat                    # Phím tắt khởi chạy ứng dụng 1-click
├── SaveGameBackup.Core/            # Thư viện Core Logic (.NET 10)
│   ├── Models/
│   │   └── GameModels.cs           # GameSaveInfo, BackupRecord, AppSettings
│   └── Services/
│       ├── DatabaseService.cs       # Quản lý SQLite database (save_backup.db)
│       ├── PathResolverService.cs   # Phân giải đường dẫn Windows, Steam, biến MT
│       ├── PCGamingWikiService.cs   # Truy vấn API PCGamingWiki trực tuyến
│       ├── LudusaviDatabaseService.cs # Danh mục game & save path định sẵn
│       ├── BackupService.cs         # Copy thư mục, nén ZIP, khôi phục save
│       └── GameSearchCoordinator.cs # Bộ điều phối tìm kiếm & quét ổ đĩa
├── SaveGameBackup.UI/              # Giao diện WPF Desktop (.NET 10 Windows)
│   ├── MainWindow.xaml             # Giao diện Dark Mode, Card UI, History DataGrid
│   ├── MainWindow.xaml.cs          # Logic giao diện & xử lý phím tắt
│   └── ViewModels/
│       └── MainViewModel.cs        # MVVM ViewModel, DataBinding, Commands
└── SaveGameBackup.Tests/           # Dự án kiểm thử tự động (.NET 10 Console)
    └── Program.cs                  # Bộ test tích hợp kiểm tra toàn bộ luồng
```

---

## 💻 Hướng dẫn chạy ứng dụng

### Cách 1: Chạy bằng file Batch
Nhấp đúp chuột vào file:
```cmd
LaunchApp.bat
```

### Cách 2: Chạy qua .NET CLI
Mở terminal trong thư mục này và chạy:
```bash
dotnet run --project SaveGameBackup.UI/SaveGameBackup.UI.csproj
```

### Chạy bộ kiểm thử tự động:
```bash
dotnet run --project SaveGameBackup.Tests/SaveGameBackup.Tests.csproj
```
