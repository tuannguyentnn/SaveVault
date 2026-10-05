using Microsoft.Extensions.Logging;
using SaveGameBackup.Core.Services;
using SaveGameBackup.Core.Services.Cloud;
using SaveGameBackup.UI.Services;
using SaveGameBackup.UI.ViewModels;
using SaveGameBackup.UI.ViewModels.SubViewModels;

namespace SaveGameBackup.UI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Tối ưu triệt để: Ngăn WebView2 đóng băng tài nguyên GPU & xả VRAM khi minimize,
        // giúp app mở to lại tức thì (0ms) không bị giật/khựng và không gây giật video nền.
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--disable-backgrounding-occluded-windows " +
            "--disable-renderer-backgrounding " +
            "--disable-background-timer-throttling " +
            "--disable-features=CalculateNativeWinOcclusion,RendererBackgrounding,IntensiveWakeUpThrottling,QuickIntensiveWakeUpThrottlingAfterLoading");

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Core Services
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<LudusaviManifestService>();
        builder.Services.AddSingleton<IHeadlessBrowserService, WebView2HeadlessService>();
        builder.Services.AddSingleton<PCGamingWikiService>(sp => 
            new PCGamingWikiService(headlessBrowser: sp.GetService<IHeadlessBrowserService>()));
        builder.Services.AddSingleton<GeminiUnifiedGameService>();
        builder.Services.AddSingleton<GameSearchCoordinator>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<CloudManagerService>();
        builder.Services.AddSingleton<DatabaseBackupService>();
        builder.Services.AddSingleton<IAppEventBus, AppEventBus>();

        // Dialog Services
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IBlazorDialogService, BlazorDialogService>();
        builder.Services.AddSingleton<INativeDialogService, NativeDialogService>();

        // ViewModels
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().SearchVM);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().BackupVM);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().HistoryVM);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().RestoreVM);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().CloudVM);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<MainViewModel>().SettingsVM);

        var app = builder.Build();
        GameCoverService.HeadlessBrowser = app.Services.GetService<IHeadlessBrowserService>();
        return app;
    }
}
