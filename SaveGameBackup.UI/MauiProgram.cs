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
        builder.Services.AddSingleton<GameSearchCoordinator>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<CloudManagerService>();
        builder.Services.AddSingleton<IAppEventBus, AppEventBus>();

        // Dialog Services
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IBlazorDialogService, BlazorDialogService>();
        builder.Services.AddSingleton<INativeDialogService, NativeDialogService>();

        // ViewModels
        builder.Services.AddSingleton<SearchSubViewModel>();
        builder.Services.AddSingleton<BackupSubViewModel>();
        builder.Services.AddSingleton<HistorySubViewModel>();
        builder.Services.AddSingleton<RestoreSubViewModel>();
        builder.Services.AddSingleton<CloudSubViewModel>();
        builder.Services.AddSingleton<SettingsSubViewModel>();
        builder.Services.AddSingleton<MainViewModel>();

        return builder.Build();
    }
}
