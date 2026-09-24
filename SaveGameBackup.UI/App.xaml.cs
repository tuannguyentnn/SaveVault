namespace SaveGameBackup.UI;

public partial class App : Application
{
    public const int WindowWidth = 1400;
    public const int WindowHeight = 950;

    public App()
    {
        InitializeComponent();

        // 1. Khi mở app: Xóa sạch toàn bộ data ảnh tạm trong Temp/covers/
        SaveGameBackup.Core.Services.GameCoverService.ClearTempCovers();

        // 2. Khi thoát app qua ProcessExit: Xóa sạch toàn bộ data ảnh tạm
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            SaveGameBackup.Core.Services.GameCoverService.ClearTempCovers();
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "SaveVault - Game Save Backup Tool (.NET 10 & SQLite & Cloud Sync)",
            Width = WindowWidth,
            Height = WindowHeight,
            MinimumWidth = WindowWidth,
            MinimumHeight = WindowHeight
        };

        // 3. Khi đóng cửa sổ / thoát app: Xóa sạch ảnh tạm
        window.Destroying += (s, e) =>
        {
            SaveGameBackup.Core.Services.GameCoverService.ClearTempCovers();
        };

        window.Created += (s, e) =>
        {
#if WINDOWS
            var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWindow != null)
            {
                var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                if (appWindow != null)
                {
                    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                    if (displayArea != null)
                    {
                        var centeredPosition = appWindow.Position;
                        centeredPosition.X = Math.Max(0, (displayArea.WorkArea.Width - WindowWidth) / 2);
                        centeredPosition.Y = Math.Max(0, (displayArea.WorkArea.Height - WindowHeight) / 2);
                        appWindow.Move(centeredPosition);
                        appWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
                    }
                }
            }
#endif
        };

        return window;
    }
}
