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

    public async Task<string?> FetchPageContentAsync(string url, int timeoutSeconds = 12, CancellationToken cancellationToken = default)
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

                    var completed = false;
                    webView.NavigationCompleted += async (s, e) =>
                    {
                        if (completed) return;
                        try
                        {
                            // Kiểm tra lặp mỗi 500ms xem Cloudflare Challenge đã giải quyết xong chưa
                            var maxPolls = timeoutSeconds * 2;
                            for (int i = 0; i < maxPolls; i++)
                            {
                                if (linkedCts.Token.IsCancellationRequested || completed) break;

                                var checkScript = @"(function() {
                                    const title = document.title || '';
                                    const body = document.body ? document.body.innerText : '';
                                    const isChl = title.includes('Just a moment') || body.includes('Just a moment') || body.includes('Checking your browser');
                                    return JSON.stringify({ isChl: isChl, content: body || document.documentElement.outerHTML });
                                })()";

                                var resJson = await webView.ExecuteScriptAsync(checkScript);
                                if (!string.IsNullOrEmpty(resJson) && resJson != "null")
                                {
                                    var unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(resJson);
                                    if (!string.IsNullOrEmpty(unescaped))
                                    {
                                        using var doc = System.Text.Json.JsonDocument.Parse(unescaped);
                                        var isChl = doc.RootElement.GetProperty("isChl").GetBoolean();
                                        if (!isChl)
                                        {
                                            var content = doc.RootElement.GetProperty("content").GetString();
                                            if (!string.IsNullOrWhiteSpace(content))
                                            {
                                                completed = true;
                                                tcs.TrySetResult(content);
                                                return;
                                            }
                                        }
                                    }
                                }

                                await Task.Delay(500, linkedCts.Token);
                            }

                            if (!completed)
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

    public async Task<byte[]?> FetchImageBytesAsync(string url, int timeoutSeconds = 12, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

#if WINDOWS
        try
        {
            var tcs = new TaskCompletionSource<byte[]?>();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            linkedCts.Token.Register(() => tcs.TrySetResult(null));

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var webView = new Microsoft.UI.Xaml.Controls.WebView2();
                    await webView.EnsureCoreWebView2Async();

                    webView.CoreWebView2.Settings.IsScriptEnabled = true;
                    webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;

                    var completed = false;
                    webView.NavigationCompleted += async (s, e) =>
                    {
                        if (completed) return;
                        try
                        {
                            var maxPolls = timeoutSeconds * 2;
                            for (int i = 0; i < maxPolls; i++)
                            {
                                if (linkedCts.Token.IsCancellationRequested || completed) break;

                                // Trích xuất ảnh trực tiếp từ thẻ <img> trên trang bằng HTML5 Canvas
                                var extractScript = @"(function() {
                                    const title = document.title || '';
                                    if (title.includes('Just a moment')) {
                                        return JSON.stringify({ ready: false });
                                    }

                                    const img = document.querySelector('img');
                                    if (img && (img.naturalWidth > 0 || img.width > 0)) {
                                        try {
                                            const canvas = document.createElement('canvas');
                                            canvas.width = img.naturalWidth || img.width;
                                            canvas.height = img.naturalHeight || img.height;
                                            const ctx = canvas.getContext('2d');
                                            ctx.drawImage(img, 0, 0);
                                            return JSON.stringify({ ready: true, data: canvas.toDataURL('image/jpeg', 0.95) });
                                        } catch (err) {}
                                    }

                                    return JSON.stringify({ ready: false });
                                })()";

                                var resJson = await webView.ExecuteScriptAsync(extractScript);
                                if (!string.IsNullOrEmpty(resJson) && resJson != "null")
                                {
                                    var unescaped = System.Text.Json.JsonSerializer.Deserialize<string>(resJson);
                                    if (!string.IsNullOrEmpty(unescaped))
                                    {
                                        using var doc = System.Text.Json.JsonDocument.Parse(unescaped);
                                        if (doc.RootElement.TryGetProperty("ready", out var readyProp) && readyProp.GetBoolean())
                                        {
                                            var dataUrl = doc.RootElement.GetProperty("data").GetString();
                                            if (!string.IsNullOrEmpty(dataUrl))
                                            {
                                                var commaIdx = dataUrl.IndexOf(',');
                                                var base64 = commaIdx >= 0 ? dataUrl[(commaIdx + 1)..] : dataUrl;
                                                var bytes = Convert.FromBase64String(base64);
                                                completed = true;
                                                tcs.TrySetResult(bytes);
                                                return;
                                            }
                                        }
                                    }
                                }

                                await Task.Delay(500, linkedCts.Token);
                            }

                            // Fallback: Thử tải blob qua JavaScript fetch API trong context đã bypass của WebView2
                            if (!completed && !linkedCts.Token.IsCancellationRequested)
                            {
                                var fetchScript = $@"(async function() {{
                                    try {{
                                        const res = await fetch('{url}');
                                        if (!res.ok) return null;
                                        const blob = await res.blob();
                                        return new Promise((resolve) => {{
                                            const reader = new FileReader();
                                            reader.onloadend = () => resolve(reader.result);
                                            reader.readAsDataURL(blob);
                                        }});
                                    }} catch (e) {{
                                        return null;
                                    }}
                                }})()";

                                var fetchResult = await webView.ExecuteScriptAsync(fetchScript);
                                if (!string.IsNullOrEmpty(fetchResult) && fetchResult != "null")
                                {
                                    var dataUrl = System.Text.Json.JsonSerializer.Deserialize<string>(fetchResult);
                                    if (!string.IsNullOrEmpty(dataUrl))
                                    {
                                        var commaIdx = dataUrl.IndexOf(',');
                                        var base64 = commaIdx >= 0 ? dataUrl[(commaIdx + 1)..] : dataUrl;
                                        var bytes = Convert.FromBase64String(base64);
                                        completed = true;
                                        tcs.TrySetResult(bytes);
                                        return;
                                    }
                                }
                            }

                            tcs.TrySetResult(null);
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
                    LoggingService.LogAction("WebView2_Headless_Image_Init_Error", new { Message = ex.Message }, level: "Debug");
                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            LoggingService.LogAction("WebView2_Headless_Image_Fetch_Failed", new { Url = url, Message = ex.Message }, level: "Debug");
            return null;
        }
#else
        return null;
#endif
    }
}
