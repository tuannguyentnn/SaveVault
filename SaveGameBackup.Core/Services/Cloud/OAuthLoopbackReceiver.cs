using System.Net;
using System.Net.Sockets;

namespace SaveGameBackup.Core.Services.Cloud;

public class OAuthLoopbackReceiver : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly string _redirectUri;

    public OAuthLoopbackReceiver(int preferredPort = 0, string? path = "callback", bool includeTrailingSlashInRedirectUri = true)
    {
        _port = preferredPort > 0 ? preferredPort : GetRandomUnusedPort();
        _listener = new HttpListener();

        if (string.IsNullOrWhiteSpace(path))
        {
            _redirectUri = includeTrailingSlashInRedirectUri ? $"http://localhost:{_port}/" : $"http://localhost:{_port}";
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        }
        else
        {
            var clean = path.Trim('/');
            _redirectUri = $"http://localhost:{_port}/{clean}/";
            _listener.Prefixes.Add($"http://localhost:{_port}/{clean}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/{clean}/");
        }
    }

    public string RedirectUri => _redirectUri;
    public int Port => _port;

    public void Start()
    {
        if (!_listener.IsListening)
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException hex)
            {
                if (hex.ErrorCode == 32 || hex.ErrorCode == 183 || hex.ErrorCode == 5)
                {
                    throw new InvalidOperationException(
                        $"Không thể mở cổng mạng OAuth {_port} (Mã lỗi {hex.ErrorCode}: \"{hex.Message}\").\n\n" +
                        "Nguyên nhân: Cổng này hiện đang bị chiếm giữ hoặc nằm trong dải cổng loại trừ (Excluded Port Range) của Windows NAT (Hyper-V / WSL2 / Docker).\n\n" +
                        "Cách xử lý nhanh nhất:\n" +
                        "1. Mở Command Prompt (CMD) hoặc PowerShell bằng quyền Quản trị viên (Run as Administrator).\n" +
                        "2. Chạy lệnh:\n" +
                        "   net stop winnat && net start winnat\n" +
                        "3. Quay lại ứng dụng và bấm nút kết nối OneDrive.", hex);
                }

                throw new InvalidOperationException($"Lỗi khởi động dịch vụ nhận mã xác thực OAuth trên cổng {_port}: {hex.Message}", hex);
            }
        }
    }

    public async Task<(string? Code, string? Error)> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var getContextTask = _listener.GetContextAsync();
            using (cts.Token.Register(() =>
            {
                try { _listener.Stop(); } catch { }
            }))
            {
                var context = await getContextTask;
                var query = context.Request.QueryString;
                var code = query["code"];
                var error = query["error"] ?? query["error_description"];

                var isSuccess = !string.IsNullOrEmpty(code);
                var htmlResponse = GetCallbackHtmlResponse(isSuccess, error);
                var buffer = System.Text.Encoding.UTF8.GetBytes(htmlResponse);

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();

                return (code, error);
            }
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (null, "Quá trình đăng nhập đã bị hủy bởi người dùng.");
            }
            return (null, $"Lỗi chờ phản hồi đăng nhập: {ex.Message}");
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            Stop();
            _listener.Close();
        }
        catch { }
    }

    private static int GetRandomUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetCallbackHtmlResponse(bool isSuccess, string? error)
    {
        var title = isSuccess ? "Đăng nhập thành công!" : "Đăng nhập thất bại";
        var icon = isSuccess ? "✓" : "✕";
        var iconColor = isSuccess ? "#34D399" : "#EF4444";
        var iconBg = isSuccess ? "#064E3B" : "#7F1D1D";
        var message = isSuccess
            ? "Tài khoản lưu trữ đám mây đã được liên kết thành công với <b>Omnisave</b>.<br/><br/>Bạn có thể đóng tab trình duyệt này và quay lại ứng dụng."
            : $"Không thể xác thực tài khoản: {System.Net.WebUtility.HtmlEncode(error ?? "Không rõ nguyên nhân")}.<br/><br/>Vui lòng thử lại.";

        return $@"<!DOCTYPE html>
<html lang=""vi"">
<head>
    <meta charset=""utf-8""/>
    <title>{title} - Omnisave</title>
    <style>
        body {{
            background-color: #0B0F19;
            color: #F8FAFC;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            align-items: center;
            justify-content: center;
            height: 100vh;
            margin: 0;
        }}
        .card {{
            background-color: #101726;
            border: 1px solid #1E293B;
            border-radius: 16px;
            padding: 40px;
            max-width: 480px;
            text-align: center;
            box-shadow: 0 20px 40px rgba(0, 0, 0, 0.7);
        }}
        .icon {{
            width: 64px;
            height: 64px;
            border-radius: 50%;
            background-color: {iconBg};
            color: {iconColor};
            font-size: 32px;
            font-weight: bold;
            display: flex;
            align-items: center;
            justify-content: center;
            margin: 0 auto 20px auto;
        }}
        h1 {{
            font-size: 22px;
            margin: 0 0 12px 0;
            color: #F1F5F9;
        }}
        p {{
            color: #94A3B8;
            font-size: 14px;
            line-height: 1.6;
            margin: 0;
        }}
        .brand {{
            margin-top: 24px;
            font-size: 12px;
            color: #38BDF8;
            font-weight: 600;
            letter-spacing: 1px;
            text-transform: uppercase;
        }}
    </style>
</head>
<body>
    <div class=""card"">
        <div class=""icon"">{icon}</div>
        <h1>{title}</h1>
        <p>{message}</p>
        <div class=""brand"">Omnisave Cloud Sync</div>
    </div>
</body>
</html>";
    }
}
