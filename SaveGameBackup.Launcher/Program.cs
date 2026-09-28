using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SaveGameBackup.Launcher;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetExe = Path.Combine(baseDir, "app", "SaveVault.exe");

            if (!File.Exists(targetExe))
            {
                MessageBox(IntPtr.Zero,
                    $"Không tìm thấy tệp thực thi chính của ứng dụng tại:\n{targetExe}\n\nVui lòng giải nén đầy đủ hoặc kiểm tra lại thư mục cài đặt.",
                    "SaveVault - Lỗi Khởi Động", 0x10); // MB_ICONERROR
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = baseDir,
                UseShellExecute = true
            };

            if (args != null && args.Length > 0)
            {
                startInfo.Arguments = string.Join(" ", args);
            }

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero,
                $"Có lỗi khi khởi chạy SaveVault:\n{ex.Message}",
                "SaveVault - Lỗi Khởi Động", 0x10);
        }
    }
}
