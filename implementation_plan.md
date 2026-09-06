# Kế hoạch phát triển: Game Save Backup Tool (.NET 10 + SQLite)

Ứng dụng cho phép người dùng chỉ cần **nhập tên game**, hệ thống sẽ:
1. **Tìm kiếm thông tin đường dẫn save game trực tuyến** (thông qua PCGamingWiki Cargo/MediaWiki API, Ludusavi database, Steam Community) và lưu vào SQLite cache.
2. **Phân giải và quét đường dẫn trên máy tính** (xử lý các biến môi trường Windows như `%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%DOCUMENTS%`, `Saved Games`, đường dẫn Steam `<Steam-folder>/userdata/<user-id>/<appid>`, wildcard `*`, ...).
3. **Thực hiện sao lưu (Backup)** toàn bộ thư mục/file save game vào thư mục đích với tên folder là tên game (hỗ trợ copy dạng folder hoặc đóng gói .zip/snapshot theo thời gian).
4. **Quản lý lịch sử và phục hồi (Restore)** thông qua cơ sở dữ liệu SQLite cục bộ.

---

## 1. Kiến trúc hệ thống & Công nghệ

- **Nền tảng**: .NET 10 (`net10.0-windows` C#)
- **Giao diện (UI)**: WPF với giao diện Dark Mode hiện đại (Card UI, thanh tìm kiếm thông minh, hiển thị trạng thái phát hiện file save, nút Backup 1-click, bảng lịch sử và nhật ký sao lưu thời gian thực).
- **Cơ sở dữ liệu**: SQLite (`Microsoft.Data.Sqlite` / `Dapper`) lưu trữ:
  - Cache thông tin game & đường dẫn save tìm được từ internet.
  - Lịch sử các lần backup (thời gian, dung lượng, số lượng file, trạng thái, đường dẫn gốc, đường dẫn backup).
  - Cấu hình ứng dụng (thư mục lưu backup mặc định, chế độ nén zip/folder, tự động ghi đè hay tạo snapshot timestamp).
- **Online Path Finder**:
  - `PCGamingWikiService`: Truy vấn MediaWiki / Cargo API để lấy chính xác cú pháp lưu trữ của game trên Windows.
  - `LudusaviDatabaseService`: Tích hợp / tra cứu mẫu đường dẫn từ kho dữ liệu mã nguồn mở Ludusavi (hơn 10,000+ game PC).
  - `FallbackHeuristicSearch`: Tự động tìm kiếm trong các thư mục thông dụng (`%LOCALAPPDATA%`, `%APPDATA%`, `Saved Games`, `Documents/My Games`, Steam userdata).

---

## 2. Chi tiết các thành phần (Components)

### A. Core Engine (`SaveBackup.Core`)
1. **`PathResolver`**:
   - Thay thế các biến hệ thống: `%USERPROFILE%`, `%LOCALAPPDATA%`, `%APPDATA%`, `%DOCUMENTS%`, `%PUBLIC%`, `%PROGRAMDATA%`.
   - Tìm kiếm thư mục Steam cục bộ (từ Registry hoặc các ổ đĩa `C:`, `D:`, `E:` -> `Steam/userdata/<user-id>/<appid>`).
   - Xử lý wildcard `*` (ví dụ: `%LOCALAPPDATA%\GameName\Saved\SaveGames\*.sav`).
2. **`GameInfoFetcher`**:
   - Kết nối online tìm kiếm theo tên game (API PCGamingWiki Cargo / Web Parser).
   - Trích xuất danh sách các vị trí lưu file save trên Windows.
3. **`BackupManager`**:
   - Quét sự tồn tại của file save trên máy.
   - Sao chép đệ quy thư mục/file sang `[TargetBackupFolder]/[GameName]/...`
   - Hỗ trợ tùy chọn tạo bản snapshot có kèm timestamp (ví dụ: `GameName_2026-09-06_22-30`) hoặc đồng bộ trực tiếp vào folder `[GameName]`.
   - Hỗ trợ chức năng **Restore** (khôi phục save ngược lại vị trí gốc khi cần).
4. **`DatabaseService` (SQLite)**:
   - Tự động khởi tạo file database `save_backup.db`.
   - Bảng `Games`: `Id`, `GameName`, `RawPathsJson`, `ResolvedPath`, `IsFoundOnDisk`, `LastUpdated`.
   - Bảng `BackupHistory`: `Id`, `GameName`, `BackupPath`, `SourcePath`, `FileCount`, `TotalSizeBytes`, `CreatedAt`, `BackupType`.
   - Bảng `Settings`: `Key`, `Value`.

### B. Giao diện người dùng (WPF Desktop App)
- **Search Bar**: Nhập tên game với gợi ý nhanh & nút "Tìm kiếm & Quét máy".
- **Game Info Card**:
  - Tên game chuẩn hóa.
  - Các đường dẫn mẫu tìm thấy trên mạng.
  - Đường dẫn thực tế phát hiện trên ổ đĩa máy tính (kèm kích thước data và số file).
- **Hành động**:
  - Nút **"Sao lưu ngay (Backup)"**: Tiến hành copy dữ liệu với thanh tiến trình.
  - Nút **"Mở thư mục Save"** & **"Mở thư mục Backup"**.
  - Nút **"Khôi phục (Restore)"**.
- **Tab Lịch sử (Backup History)**: Danh sách các bản backup đã thực hiện, cho phép xem lại, mở folder hoặc xóa bản cũ.
- **Tab Cài đặt (Settings)**: Thay đổi đường dẫn thư mục sao lưu mặc định, cấu hình chế độ backup.

---

## 3. Kế hoạch triển khai (Proposed Changes)

### [NEW] Solution & Project Structure
- `SaveGameBackup.sln`
- `SaveGameBackup.Core/` (Class library chứa Engine logic, SQLite, HTTP Fetcher, Path Resolver)
  - `Models/GameData.cs`, `Models/BackupRecord.cs`, `Models/AppSettings.cs`
  - `Services/DatabaseService.cs` (SQLite init, CRUD)
  - `Services/PCGamingWikiService.cs` (Online API fetcher)
  - `Services/LudusaviDatabaseService.cs` (Game save database mapping)
  - `Services/PathResolverService.cs` (Phân giải biến môi trường & quét ổ đĩa)
  - `Services/BackupService.cs` (Copy file, nén zip, tính dung lượng, restore)
- `SaveGameBackup.UI/` (WPF Application .NET 10)
  - `MainWindow.xaml` & `MainWindow.xaml.cs` (Giao diện hiện đại)
  - `ViewModels/MainViewModel.cs` (MVVM DataBinding, Async commands, Live status)
  - `Resources/` (Styles, Dark theme brushes, Icons)

---

## 4. Kế hoạch kiểm thử & Xác minh (Verification Plan)

1. **Build Verification**:
   - Chạy `dotnet build` trên .NET 10 đảm bảo 0 lỗi, 0 warning.
2. **Unit / Integration Tests**:
   - Kiểm tra tra cứu API online với các game phổ biến (ví dụ: *The Witcher 3*, *Cyberpunk 2077*, *Elden Ring*, *Hades*, *Stardew Valley*).
   - Kiểm tra giải mã đường dẫn (`%APPDATA%`, `%LOCALAPPDATA%`, Steam userdata).
   - Kiểm tra tạo thư mục backup, copy file, lưu lịch sử vào SQLite và đọc lại.
3. **End-to-End Test**:
   - Khởi chạy ứng dụng, nhập tên game thực tế, quét và thực hiện backup thử nghiệm dữ liệu sang thư mục chỉ định.
