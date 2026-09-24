namespace SaveGameBackup.UI;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
    }

    private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        try
        {
            var coversDir = SaveGameBackup.Core.Services.GameCoverService.GetCoverDirectory();
            e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "covers.local",
                coversDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            var tempCoversDir = SaveGameBackup.Core.Services.GameCoverService.GetTempCoverDirectory();
            e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "tempcovers.local",
                tempCoversDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting virtual host mapping: {ex.Message}");
        }
#endif
    }
}
