using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using SaveGameBackup.Core.Services;

#if WINDOWS
using Microsoft.Web.WebView2.Core;
#endif

namespace SaveGameBackup.UI.Services;

/// <summary>
/// Triển khai dịch vụ trình duyệt ngầm Microsoft Edge WebView2 (Headless)
/// Dùng để tải trang web và vượt qua Cloudflare Challenge một cách tự nhiên bằng engine Chromium trên Windows.
/// </summary>
public class WebView2HeadlessService : IHeadlessBrowserService
{
    public bool IsAvailable
    {
        get
        {
#if WINDOWS
            return OperatingSystem.IsWindows();
#else
            return false;
#endif
        }
    }

    public async Task<string?> FetchPageContentAsync(string url, int timeoutSeconds = 6, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

#if WINDOWS
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            linkedCts.Token.Register(() => tcs.TrySetResult(null));

            // Chạy trên Main UI Thread của ứng dụng Windows
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var webView = new Microsoft.UI.Xaml.Controls.WebView2();
                    await webView.EnsureCoreWebView2Async();

                    webView.CoreWebView2.Settings.IsScriptEnabled = true;
                    webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;

                    // Lắng nghe sự kiện chuyển trang hoàn tất (sau khi giải challenge)
                    webView.NavigationCompleted += async (s, e) =>
                    {
                        try
                        {
                            if (!e.IsSuccess)
                            {
                                tcs.TrySetResult(null);
                                return;
                            }

                            // Chờ nhẹ 500ms để script / challenge DOM ổn định
                            await Task.Delay(500);

                            // Trích xuất nội dung văn bản (JSON hoặc HTML)
                            var rawText = await webView.ExecuteScriptAsync("document.body ? document.body.innerText : document.documentElement.outerHTML");
                            if (!string.IsNullOrEmpty(rawText) && rawText != "null")
                            {
                                // ExecuteScriptAsync trả về chuỗi JSON-encoded, cần giải mã
                                var unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(rawText);
                                tcs.TrySetResult(unescaped ?? rawText);
                            }
                            else
                            {
                                tcs.TrySetResult(null);
                            }
                        }
                        catch
                        {
                            tcs.TrySetResult(null);
                        }
                    };

                    webView.Source = new Uri(url);
                }
                catch (Exception ex)
                {
                    LoggingService.LogAction("WebView2_Headless_Init_Error", new { Message = ex.Message }, level: "Debug");
                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("WebView2_Headless_Fetch_Failed", new { Url = url, Message = ex.Message }, level: "Debug");
            return null;
        }
#else
        return null;
#endif
    }
}
