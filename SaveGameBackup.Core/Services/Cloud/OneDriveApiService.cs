using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services.Cloud;

public class OneDriveApiService : ICloudStorageService
{
    private const string DefaultClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c"; // Microsoft public desktop app id
    private const string AuthEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string GraphApiMeEndpoint = "https://graph.microsoft.com/v1.0/me";
    private const string GraphApiDriveEndpoint = "https://graph.microsoft.com/v1.0/me/drive";
    private const string Scopes = "Files.ReadWrite offline_access User.Read";

    private readonly DatabaseService _databaseService;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _accessTokenExpiry = DateTime.MinValue;
    private string? _refreshToken;
    private string? _currentAccountEmail;

    public OneDriveApiService(DatabaseService databaseService, HttpClient? httpClient = null)
    {
        _databaseService = databaseService;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string ProviderName => "OneDrive";
    public string DisplayName => "Microsoft OneDrive (REST API)";
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken) || !string.IsNullOrEmpty(_accessToken);
    public string? CurrentAccountEmail => _currentAccountEmail;

    public async Task InitializeFromDatabaseAsync()
    {
        _refreshToken = await _databaseService.GetSettingAsync("onedrive_refresh_token");
        _currentAccountEmail = await _databaseService.GetSettingAsync("onedrive_account_email");
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var clientId = await GetClientIdAsync();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Bạn chưa cấu hình OneDrive Application (Client) ID!\n\n" +
                "Do Microsoft bảo vệ tài khoản người dùng, bạn cần đăng ký 1 Client ID (hoàn toàn miễn phí) trên Microsoft Entra / Azure Portal với Redirect URI là 'http://localhost'.\n\n" +
                "Vui lòng vào tab 'Cài đặt' > mục 'Cấu hình Cloud API Credentials' để nhập Client ID của bạn.");
        }

        // PKCE Flow
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        // Microsoft Entra ID expects http://localhost:{port} (RFC 8252 loopback)
        using var receiver = new OAuthLoopbackReceiver(preferredPort: 0, path: null, includeTrailingSlashInRedirectUri: false);
        var state = Guid.NewGuid().ToString("N");

        var authUrl = $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&response_type=code" +
                      $"&redirect_uri={Uri.EscapeDataString(receiver.RedirectUri)}" +
                      $"&response_mode=query" +
                      $"&scope={Uri.EscapeDataString(Scopes)}" +
                      $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                      $"&code_challenge_method=S256" +
                      $"&state={state}";

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch { }

        var (code, error) = await receiver.WaitForCallbackAsync(TimeSpan.FromMinutes(3), cancellationToken);

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException($"Đăng nhập OneDrive thất bại: {error ?? "Không nhận được mã xác thực."}");
        }

        var tokenParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = receiver.RedirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = Scopes
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lỗi lấy token OneDrive: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        if (root.TryGetProperty("refresh_token", out var refProp))
        {
            _refreshToken = refProp.GetString();
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                await _databaseService.SaveSettingAsync("onedrive_refresh_token", _refreshToken);
            }
        }

        _currentAccountEmail = await GetUserEmailAsync(cancellationToken);
        if (!string.IsNullOrEmpty(_currentAccountEmail))
        {
            await _databaseService.SaveSettingAsync("onedrive_account_email", _currentAccountEmail);
        }

        return true;
    }

    public async Task SignOutAsync()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
        _refreshToken = null;
        _currentAccountEmail = null;

        await _databaseService.SaveSettingAsync("onedrive_refresh_token", string.Empty);
        await _databaseService.SaveSettingAsync("onedrive_account_email", string.Empty);
    }

    public async Task<string?> GetUserEmailAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_currentAccountEmail)) return _currentAccountEmail;

        await EnsureAccessTokenAsync(cancellationToken);
        using var req = new HttpRequestMessage(HttpMethod.Get, GraphApiMeEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.TryGetProperty("userPrincipalName", out var upnProp))
            {
                _currentAccountEmail = upnProp.GetString();
            }
            else if (root.TryGetProperty("mail", out var mailProp))
            {
                _currentAccountEmail = mailProp.GetString();
            }
            return _currentAccountEmail;
        }

        return null;
    }

    public async Task<CloudUploadResult> UploadFileAsync(
        string localFilePath,
        string remoteGameFolderName,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException("File sao lưu không tồn tại trên máy tính!", localFilePath);
        }

        await EnsureAccessTokenAsync(cancellationToken);

        var fileInfo = new FileInfo(localFilePath);
        var totalBytes = fileInfo.Length;
        var fileName = Path.GetFileName(localFilePath);

        progress?.Report(new BackupProgress
        {
            Percent = 5,
            CurrentFile = fileName,
            TotalBytes = totalBytes,
            ProcessedBytes = 0,
            Message = "Đang kiểm tra thư mục trên OneDrive..."
        });

        // Đảm bảo cấu trúc thư mục SaveVault_Backups/{remoteGameFolderName} đã được tạo trên OneDrive
        var gameFolderId = await EnsureFolderHierarchyAsync(remoteGameFolderName, cancellationToken);

        progress?.Report(new BackupProgress
        {
            Percent = 8,
            CurrentFile = fileName,
            TotalBytes = totalBytes,
            ProcessedBytes = 0,
            Message = "Đang khởi tạo phiên tải lên OneDrive..."
        });

        // Tạo Upload Session qua Microsoft Graph API theo folder ID đã xác thực
        var sessionUrl = $"{GraphApiDriveEndpoint}/items/{gameFolderId}:/{Uri.EscapeDataString(fileName)}:/createUploadSession";
        var sessionPayload = JsonSerializer.Serialize(new
        {
            item = new Dictionary<string, string>
            {
                ["@microsoft.graph.conflictBehavior"] = "replace"
            }
        });

        using var sessionReq = new HttpRequestMessage(HttpMethod.Post, sessionUrl);
        sessionReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        sessionReq.Content = new StringContent(sessionPayload, Encoding.UTF8, "application/json");

        var sessionRes = await _httpClient.SendAsync(sessionReq, cancellationToken);
        var sessionJson = await sessionRes.Content.ReadAsStringAsync(cancellationToken);

        if (!sessionRes.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Không thể tạo phiên upload OneDrive ({sessionRes.StatusCode}): {sessionJson}");
        }

        using var sessionDoc = JsonDocument.Parse(sessionJson);
        var uploadUrl = sessionDoc.RootElement.GetProperty("uploadUrl").GetString();
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new InvalidOperationException("OneDrive không trả về uploadUrl.");
        }

        // Kích thước khối (Chunk size) thích ứng theo dung lượng file (chuẩn bội số 320 KiB, tối đa 60 MiB theo Microsoft Graph):
        // - File < 100 MB: 10.5 MB (320 KiB * 32) giúp tiến trình % mượt mà
        // - File 100 MB - 500 MB: 20.97 MB (320 KiB * 64) tối ưu tốc độ
        // - File >= 500 MB (1GB+): 31.45 MB (320 KiB * 96) giúp 1 GB chỉ cần ~32 lần gửi, tận dụng tối đa băng thông
        int chunkSize = totalBytes switch
        {
            >= 500L * 1024 * 1024 => 320 * 1024 * 96, // ~31.5 MB / chunk
            >= 100L * 1024 * 1024 => 320 * 1024 * 64, // ~21 MB / chunk
            _ => 320 * 1024 * 32                       // ~10.5 MB / chunk
        };

        byte[] buffer = new byte[chunkSize];
        long bytesSent = 0;

        using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var stopwatch = Stopwatch.StartNew();
        string fileId = string.Empty;
        string? webUrl = null;

        while (bytesSent < totalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentChunkSize = (int)Math.Min(chunkSize, totalBytes - bytesSent);
            int read = await fileStream.ReadAsync(buffer.AsMemory(0, currentChunkSize), cancellationToken);

            var startByte = bytesSent;
            var endByte = bytesSent + read - 1;

            using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            chunkReq.Content = new ByteArrayContent(buffer, 0, read);
            chunkReq.Content.Headers.ContentRange = new ContentRangeHeaderValue(startByte, endByte, totalBytes);
            chunkReq.Content.Headers.ContentLength = read;

            var chunkRes = await _httpClient.SendAsync(chunkReq, cancellationToken);
            bytesSent += read;

            var pct = 10 + (int)((double)bytesSent / totalBytes * 88.0);
            var speedMb = stopwatch.Elapsed.TotalSeconds > 0 ? (bytesSent / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds : 0;

            progress?.Report(new BackupProgress
            {
                Percent = Math.Min(pct, 98),
                CurrentFile = fileName,
                ProcessedBytes = bytesSent,
                TotalBytes = totalBytes,
                SpeedText = $"{speedMb:F1} MB/s",
                Message = $"Đang tải lên OneDrive: {bytesSent / 1024.0 / 1024.0:F1} MB / {totalBytes / 1024.0 / 1024.0:F1} MB ({speedMb:F1} MB/s)"
            });

            if (chunkRes.StatusCode == System.Net.HttpStatusCode.Created || chunkRes.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // Toàn bộ file đã hoàn tất tải lên, OneDrive trả về metadata của file
                var finishJson = await chunkRes.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(finishJson);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    fileId = idProp.GetString() ?? string.Empty;
                }
                if (doc.RootElement.TryGetProperty("webUrl", out var webProp))
                {
                    webUrl = webProp.GetString();
                }
                break;
            }
            else if (chunkRes.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // HTTP 202 Accepted: Khối hiện tại đã nhận thành công, tiếp tục gửi khối kế tiếp
                continue;
            }
            else
            {
                var err = await chunkRes.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Lỗi gửi khối dữ liệu lên OneDrive ({chunkRes.StatusCode}): {err}");
            }
        }

        if (string.IsNullOrEmpty(fileId))
        {
            throw new InvalidOperationException("OneDrive chưa xác nhận hoàn tất tải file lên.");
        }

        progress?.Report(new BackupProgress
        {
            Percent = 100,
            CurrentFile = fileName,
            ProcessedBytes = totalBytes,
            TotalBytes = totalBytes,
            Message = "Đồng bộ lên OneDrive hoàn tất!"
        });

        return new CloudUploadResult
        {
            Success = true,
            Provider = ProviderName,
            FileId = fileId,
            FileName = fileName,
            FileSizeBytes = totalBytes,
            WebUrl = webUrl
        };
    }

    public async Task<string> DownloadFileAsync(
        string remoteFileId,
        string localDestinationPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var downloadUrl = $"{GraphApiDriveEndpoint}/items/{remoteFileId}/content";
        using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Không thể tải file từ OneDrive ({response.StatusCode}): {err}");
        }

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var dir = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(localDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[64 * 1024];
        long bytesReadTotal = 0;
        int read;

        while ((read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReadTotal += read;

            int pct = totalBytes > 0 ? (int)((double)bytesReadTotal / totalBytes * 100) : 50;
            progress?.Report(new BackupProgress
            {
                Percent = Math.Clamp(pct, 0, 100),
                CurrentFile = Path.GetFileName(localDestinationPath),
                ProcessedBytes = bytesReadTotal,
                TotalBytes = Math.Max(totalBytes, bytesReadTotal),
                Message = totalBytes > 0 
                    ? $"Đang tải từ OneDrive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB / {totalBytes / 1024.0 / 1024.0:F1} MB ({pct}%)"
                    : $"Đang tải từ OneDrive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB..."
            });
        }

        return localDestinationPath;
    }

    public async Task<bool> DeleteFileAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteFileId)) return true;

        await EnsureAccessTokenAsync(cancellationToken);

        var deleteUrl = $"{GraphApiDriveEndpoint}/items/{remoteFileId}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(req, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
    }

    public async Task<List<CloudFileInfo>> ListBackupsAsync(
        string? remoteGameFolderName = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var list = new List<CloudFileInfo>();
        string url = string.IsNullOrEmpty(remoteGameFolderName)
            ? $"{GraphApiDriveEndpoint}/root:/SaveVault_Backups:/children"
            : $"{GraphApiDriveEndpoint}/root:/SaveVault_Backups/{Uri.EscapeDataString(remoteGameFolderName)}:/children";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("value", out var itemsProp))
            {
                foreach (var item in itemsProp.EnumerateArray())
                {
                    if (item.TryGetProperty("folder", out _)) continue;

                    list.Add(new CloudFileInfo
                    {
                        Id = item.GetProperty("id").GetString() ?? string.Empty,
                        Name = item.GetProperty("name").GetString() ?? string.Empty,
                        SizeBytes = item.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        ModifiedTime = item.TryGetProperty("lastModifiedDateTime", out var mt) && DateTime.TryParse(mt.GetString(), out var dt) ? dt : null,
                        WebUrl = item.TryGetProperty("webUrl", out var w) ? w.GetString() : null
                    });
                }
            }
        }

        return list;
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry)
        {
            return;
        }

        if (string.IsNullOrEmpty(_refreshToken))
        {
            _refreshToken = await _databaseService.GetSettingAsync("onedrive_refresh_token");
        }

        if (string.IsNullOrEmpty(_refreshToken))
        {
            throw new InvalidOperationException("Chưa đăng nhập tài khoản OneDrive! Vui lòng vào Cài đặt để kết nối tài khoản.");
        }

        var clientId = await GetClientIdAsync() ?? throw new InvalidOperationException("Chưa cấu hình OneDrive Application (Client) ID!");

        var refreshParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["scope"] = Scopes
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(refreshParams)
        };

        var res = await _httpClient.SendAsync(req, cancellationToken);
        var json = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Lỗi làm mới token OneDrive ({res.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        if (doc.RootElement.TryGetProperty("refresh_token", out var refProp))
        {
            var newRef = refProp.GetString();
            if (!string.IsNullOrEmpty(newRef))
            {
                _refreshToken = newRef;
                await _databaseService.SaveSettingAsync("onedrive_refresh_token", _refreshToken);
            }
        }
    }

    private async Task<string> EnsureFolderHierarchyAsync(string remoteGameFolderName, CancellationToken cancellationToken)
    {
        // 1. Đảm bảo thư mục gốc "SaveVault_Backups" tồn tại
        var rootFolderId = await GetOrCreateFolderAsync("root", "SaveVault_Backups", cancellationToken);

        // 2. Đảm bảo thư mục con theo tên game tồn tại
        var gameFolderId = await GetOrCreateFolderAsync(rootFolderId, remoteGameFolderName, cancellationToken);

        return gameFolderId;
    }

    private async Task<string> GetOrCreateFolderAsync(string parentFolderIdOrRoot, string folderName, CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        // Kiểm tra xem thư mục đã có sẵn chưa
        string getUrl = parentFolderIdOrRoot == "root"
            ? $"{GraphApiDriveEndpoint}/root:/{Uri.EscapeDataString(folderName)}"
            : $"{GraphApiDriveEndpoint}/items/{parentFolderIdOrRoot}:/{Uri.EscapeDataString(folderName)}";

        using (var getReq = new HttpRequestMessage(HttpMethod.Get, getUrl))
        {
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            var getRes = await _httpClient.SendAsync(getReq, cancellationToken);

            if (getRes.IsSuccessStatusCode)
            {
                var content = await getRes.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString() ?? string.Empty;
                }
            }
        }

        // Chưa có -> Tạo mới thư mục
        string createUrl = parentFolderIdOrRoot == "root"
            ? $"{GraphApiDriveEndpoint}/root/children"
            : $"{GraphApiDriveEndpoint}/items/{parentFolderIdOrRoot}/children";

        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = folderName,
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "fail"
        });

        using (var createReq = new HttpRequestMessage(HttpMethod.Post, createUrl))
        {
            createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            createReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var createRes = await _httpClient.SendAsync(createReq, cancellationToken);
            var createContent = await createRes.Content.ReadAsStringAsync(cancellationToken);

            if (createRes.IsSuccessStatusCode || createRes.StatusCode == System.Net.HttpStatusCode.Created)
            {
                using var doc = JsonDocument.Parse(createContent);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString() ?? string.Empty;
                }
            }
        }

        // Nếu xảy ra xung đột (vừa được tạo đồng thời 409 Conflict), thử lấy lại lần nữa
        using (var retryReq = new HttpRequestMessage(HttpMethod.Get, getUrl))
        {
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            var retryRes = await _httpClient.SendAsync(retryReq, cancellationToken);
            if (retryRes.IsSuccessStatusCode)
            {
                var retryContent = await retryRes.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(retryContent);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    return idProp.GetString() ?? string.Empty;
                }
            }
        }

        throw new InvalidOperationException($"Không thể tạo thư mục '{folderName}' trên OneDrive.");
    }

    private async Task<string?> GetClientIdAsync()
    {
        var configured = await _databaseService.GetSettingAsync("onedrive_client_id");
        return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : null;
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
