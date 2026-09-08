using System.Diagnostics;

namespace SaveGameBackup.UI.Services;

public interface INativeDialogService
{
    Task<string?> PickFolderAsync(string? title = null);
    Task<string?> PickDatabaseFileAsync(string? title = null);
    void OpenFolderInExplorer(string folderPath);
    void OpenFileInExplorer(string filePath);
    void OpenUrl(string url);
}

public class NativeDialogService : INativeDialogService
{
    public async Task<string?> PickFolderAsync(string? title = null)
    {
#if WINDOWS
        try
        {
            return await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                folderPicker.FileTypeFilter.Add("*");

                var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                if (window != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
                }

                var folder = await folderPicker.PickSingleFolderAsync();
                return folder?.Path;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PickFolderAsync error: {ex.Message}");
            return null;
        }
#else
        return await Task.FromResult<string?>(null);
#endif
    }

    public async Task<string?> PickDatabaseFileAsync(string? title = null)
    {
#if WINDOWS
        try
        {
            return await Microsoft.Maui.ApplicationModel.MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var filePicker = new Windows.Storage.Pickers.FileOpenPicker();
                filePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                filePicker.FileTypeFilter.Add(".db");
                filePicker.FileTypeFilter.Add(".sqlite");
                filePicker.FileTypeFilter.Add("*");

                var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                if (window != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
                }

                var file = await filePicker.PickSingleFileAsync();
                return file?.Path;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PickDatabaseFileAsync error: {ex.Message}");
            return null;
        }
#else
        return await Task.FromResult<string?>(null);
#endif
    }

    public void OpenFolderInExplorer(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            else
            {
                var parent = Path.GetDirectoryName(folderPath);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = parent,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
            }
        }
        catch { }
    }

    public void OpenFileInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                OpenFolderInExplorer(Path.GetDirectoryName(filePath) ?? filePath);
            }
        }
        catch { }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
