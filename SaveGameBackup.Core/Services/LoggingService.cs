using System;
using System.Collections.Generic;
using System.IO;
using Serilog;
using Serilog.Formatting.Json;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Dịch vụ quản lý ghi nhật ký (Logging) tập trung sử dụng Serilog.
/// Lưu log dưới dạng JSON chuẩn, đặt tại thư mục Logs ở Root của ứng dụng,
/// tự động tách file theo định dạng yyyy-MM_{xx}.json khi file log vượt quá 5MB.
/// </summary>
public static class LoggingService
{
    private static bool _isInitialized;
    private static readonly object _initLock = new();
    private static string _logDirectory = string.Empty;

    public static string LogDirectory => _logDirectory;

    /// <summary>
    /// Bật/tắt việc ghi log cho các hành động tương tác giao diện mở/đóng tab và modal.
    /// Mặc định: false (Tạm thời ẩn theo yêu cầu người dùng, chỉ ghi log các chức năng).
    /// </summary>
    public static bool EnableTabAndModalLogging { get; set; } = false;

    private static readonly HashSet<string> _tabAndModalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tab_Navigated",
        "Open_Restore_Modal",
        "Close_Restore_Modal",
        "Open_Sync_Cloud_Modal",
        "Close_Sync_Cloud_Modal",
        "View_Game_Snapshots",
        "Close_Game_Snapshots_Modal",
        "Update_Modal_Shown_OnStartup",
        "Cloud_Tab_Switch_Provider",
        "Dialog_ShowMessage",
        "Dialog_CloseMessage"
    };

    /// <summary>
    /// Kiểm tra xem một actionName có thuộc nhóm hành động mở/đóng tab hoặc modal hay không.
    /// </summary>
    public static bool IsTabOrModalAction(string actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName)) return false;
        if (_tabAndModalActions.Contains(actionName)) return true;

        // Heuristic fallback nhận diện mở/đóng modal hoặc chuyển tab hoặc dialog thông báo
        if (actionName.Contains("Modal", StringComparison.OrdinalIgnoreCase)) return true;
        if (actionName.Contains("Tab_Switch", StringComparison.OrdinalIgnoreCase)) return true;
        if (actionName.StartsWith("Switch_Tab", StringComparison.OrdinalIgnoreCase)) return true;
        if (actionName.Contains("Tab_Navigat", StringComparison.OrdinalIgnoreCase)) return true;
        if (actionName.StartsWith("Dialog_", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Khởi tạo cấu hình Serilog.
    /// </summary>
    public static void Initialize(string? customLogDir = null)
    {
        lock (_initLock)
        {
            if (_isInitialized) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(customLogDir))
                {
                    _logDirectory = customLogDir;
                }
                else
                {
                    var rootDir = DatabaseService.GetDefaultProjectRoot();
                    _logDirectory = Path.Combine(rootDir, "data", "logs");

                    // Tự động di chuyển log cũ từ <AppRoot>/logs sang <AppRoot>/data/logs
                    MigrateLegacyLogs(rootDir, _logDirectory);
                }

                Directory.CreateDirectory(_logDirectory);

                // Cấu hình file log với tên base là omnisave-log.json
                // Serilog sẽ tự động thêm Hậu tố thời gian (theo tháng) và đánh số thứ tự khi vượt quá dung lượng.
                var logFilePath = Path.Combine(_logDirectory, "omnisave-log.json");

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "Omnisave")
                    .Enrich.WithProperty("Environment", "Production")
                    .WriteTo.File(
                        formatter: new JsonFormatter(renderMessage: true),
                        path: logFilePath,
                        rollingInterval: RollingInterval.Month,
                        fileSizeLimitBytes: 100 * 1024 * 1024, // 100 MB
                        rollOnFileSizeLimit: true,
                        shared: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1),
                        retainedFileCountLimit: 100)
                    .CreateLogger();

                _isInitialized = true;
                Log.Information("=== [Omnisave] Khởi động hệ thống Logging thành công tại {LogDirectory} ===", _logDirectory);
            }
            catch (Exception ex)
            {
                // Fallback nếu có lỗi IO
                Console.WriteLine($"[LoggingService] Không thể khởi tạo Serilog: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Ghi nhận một hành động của ứng dụng hoặc người dùng kèm thông tin ngữ cảnh có cấu trúc.
    /// </summary>
    public static void LogAction(string actionName, object? details = null, string level = "Info", Exception? ex = null)
    {
        // Tạm thời ẩn các logs cho các hành động mở tab, modal. Chỉ ghi log các chức năng thôi.
        if (!EnableTabAndModalLogging && IsTabOrModalAction(actionName))
        {
            return;
        }

        EnsureInitialized();

        switch (level?.ToUpperInvariant())
        {
            case "WARN":
            case "WARNING":
                if (ex != null)
                    Log.Warning(ex, "[Action:{ActionName}] {Details}", actionName, details);
                else
                    Log.Warning("[Action:{ActionName}] {Details}", actionName, details);
                break;

            case "ERROR":
                if (ex != null)
                    Log.Error(ex, "[Action:{ActionName}] {Details}", actionName, details);
                else
                    Log.Error("[Action:{ActionName}] {Details}", actionName, details);
                break;

            case "DEBUG":
                Log.Debug("[Action:{ActionName}] {Details}", actionName, details);
                break;

            default:
                Log.Information("[Action:{ActionName}] {Details}", actionName, details);
                break;
        }
    }

    public static void Info(string message, params object?[] propertyValues)
    {
        EnsureInitialized();
        Log.Information(message, propertyValues);
    }

    public static void Warn(string message, params object?[] propertyValues)
    {
        EnsureInitialized();
        Log.Warning(message, propertyValues);
    }

    public static void Error(Exception? ex, string message, params object?[] propertyValues)
    {
        EnsureInitialized();
        if (ex != null)
            Log.Error(ex, message, propertyValues);
        else
            Log.Error(message, propertyValues);
    }

    public static void CloseAndFlush()
    {
        try
        {
            Log.Information("=== [Omnisave] Tắt hệ thống Logging - Đóng và đẩy toàn bộ log ra đĩa ===");
            Log.CloseAndFlush();
        }
        catch { }
    }

    private static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            Initialize();
        }
    }

    private static void MigrateLegacyLogs(string rootDir, string targetLogDir)
    {
        try
        {
            var oldLogDirs = new[] { Path.Combine(rootDir, "logs"), Path.Combine(rootDir, "Logs") };
            foreach (var oldDir in oldLogDirs)
            {
                if (Directory.Exists(oldDir) && !string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(targetLogDir), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(targetLogDir);
                    foreach (var file in Directory.GetFiles(oldDir, "*.json"))
                    {
                        var destFile = Path.Combine(targetLogDir, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Move(file, destFile);
                        }
                    }

                    if (Directory.GetFiles(oldDir).Length == 0 && Directory.GetDirectories(oldDir).Length == 0)
                    {
                        Directory.Delete(oldDir, false);
                    }
                }
            }
        }
        catch { }
    }

    public static string? CurrentTraceId => ActionTraceScope.CurrentTraceId;

    /// <summary>
    /// Bắt đầu một vòng đời Trace để theo dõi log. Tất cả các log nằm trong scope trả về 
    /// sẽ tự động có thuộc tính TraceId. Có thể truyền forceTraceId để khôi phục trace từ file tạm.
    /// </summary>
    public static IDisposable BeginTrace(string actionName, object? details = null, string? forceTraceId = null)
    {
        EnsureInitialized();
        return new ActionTraceScope(actionName, details, forceTraceId);
    }

    /// <summary>
    /// Lớp quản lý vòng đời của một Trace, tự động đẩy TraceId vào LogContext và dọn dẹp khi kết thúc.
    /// Kèm theo đo lường thời gian thực thi (Stopwatch).
    /// </summary>
    private class ActionTraceScope : IDisposable
    {
        private static readonly System.Threading.AsyncLocal<string?> _currentTraceId = new();
        public static string? CurrentTraceId => _currentTraceId.Value;

        private readonly string _actionName;
        private readonly IDisposable _logContext;
        private readonly System.Diagnostics.Stopwatch _stopwatch;
        private readonly bool _isRootScope;

        public ActionTraceScope(string actionName, object? details = null, string? forceTraceId = null)
        {
            _actionName = actionName;
            string traceId;

            if (!string.IsNullOrEmpty(forceTraceId))
            {
                traceId = forceTraceId;
                _currentTraceId.Value = traceId;
                _isRootScope = true;
            }
            else if (string.IsNullOrEmpty(_currentTraceId.Value))
            {
                traceId = Guid.NewGuid().ToString("N");
                _currentTraceId.Value = traceId;
                _isRootScope = true;
            }
            else
            {
                traceId = _currentTraceId.Value;
                _isRootScope = false;
            }
            
            // Đẩy TraceId vào LogContext
            _logContext = Serilog.Context.LogContext.PushProperty("TraceId", traceId);
            
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Ghi log bắt đầu hành động với TraceId
            LoggingService.Info($"[{_actionName}_Started]", details ?? new { });
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            // Ghi log kết thúc với thời gian chạy
            LoggingService.Info($"[{_actionName}_Completed]", new { ElapsedMs = _stopwatch.ElapsedMilliseconds });
            
            // Hủy bỏ context (xóa push property khỏi Serilog)
            _logContext.Dispose();

            // Nếu đây là gốc khởi tạo TraceId, xóa nó khỏi AsyncLocal để dọn dẹp
            if (_isRootScope)
            {
                _currentTraceId.Value = null;
            }
        }
    }
}
