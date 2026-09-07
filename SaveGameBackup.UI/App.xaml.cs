using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Khởi tạo Serilog ghi log JSON tại Root/Logs/
            LoggingService.Initialize();

            // 2. Ghi nhận sự kiện khởi động ứng dụng và cấu hình hệ thống
            LoggingService.LogAction("App_Startup", new
            {
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessId = Environment.ProcessId,
                CommandLineArgs = e.Args
            });

            // 3. Đăng ký Global Unhandled Exception Handlers
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LoggingService.Error(e.Exception, "[UnhandledException:Dispatcher] Đã xảy ra lỗi không xử lý được trên giao diện chính: {Message}", e.Exception.Message);
            LoggingService.LogAction("Unhandled_Exception_UI", new
            {
                ExceptionType = e.Exception.GetType().FullName,
                Message = e.Exception.Message,
                StackTrace = e.Exception.StackTrace
            }, level: "Error", ex: e.Exception);

            // Cho phép người dùng tiếp tục nếu có thể, tránh crash đột ngột
            e.Handled = true;

            MessageBox.Show(
                $"Đã xảy ra lỗi hệ thống:\n\n{e.Exception.Message}\n\nThông tin chi tiết đã được ghi vào file log JSON trong thư mục Logs.",
                "SaveVault - Lỗi Hệ Thống",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LoggingService.Error(ex, "[UnhandledException:AppDomain] Lỗi nghiêm trọng AppDomain (IsTerminating: {IsTerminating}): {Message}", e.IsTerminating, ex.Message);
                LoggingService.LogAction("Fatal_Exception_AppDomain", new
                {
                    IsTerminating = e.IsTerminating,
                    ExceptionType = ex.GetType().FullName,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace
                }, level: "Error", ex: ex);
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LoggingService.Error(e.Exception, "[UnhandledException:TaskScheduler] Lỗi bất đồng bộ UnobservedTaskException: {Message}", e.Exception.Message);
            LoggingService.LogAction("Unhandled_Exception_Task", new
            {
                Message = e.Exception.Message,
                InnerExceptions = e.Exception.InnerExceptions.Count
            }, level: "Error", ex: e.Exception);

            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LoggingService.LogAction("App_Exit", new { ExitCode = e.ApplicationExitCode });
            LoggingService.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
