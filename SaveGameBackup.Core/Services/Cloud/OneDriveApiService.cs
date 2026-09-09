using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services.Cloud;

public class OneDriveApiService : ICloudStorageService
{
    public const string DefaultClientId = "b15665d9-eda6-4092-8539-0eec376afd59";
    public const string DefaultClientSecret = "qtyfaBBYA403=unZUP40~_#";
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
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken) || (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry);
    public string? CurrentAccountEmail => _currentAccountEmail;

    public async Task InitializeFromDatabaseAsync()
    {
        var config = AppConfigService.GetConfig();
        _refreshToken = config.OneDriveRefreshToken;
        _accessToken = config.OneDriveAccessToken;
        _accessTokenExpiry = config.OneDriveAccessTokenExpiry;
        _currentAccountEmail = config.OneDriveAccountEmail;

        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry)
        {
            LoggingService.LogAction("OneDrive_Session_Restored", new { Email = _currentAccountEmail, Expiry = _accessTokenExpiry });
            return;
        }

        if (!string.IsNullOrEmpty(_refreshToken))
        {
            try
            {
                await EnsureAccessTokenAsync(CancellationToken.None);
                LoggingService.LogAction("OneDrive_Silent_Token_Refreshed", new { Email = _currentAccountEmail, Expiry = _accessTokenExpiry });
            }
            catch (Exception ex)
            {
                LoggingService.Warn("OneDrive: Không thể làm mới token trong nền lúc khởi động: {Message}", ex.Message);
            }
        }
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var clientId = await GetClientIdAsync();
        var clientSecret = await GetClientSecretAsync();

        bool isDefault = string.IsNullOrWhiteSpace(clientId) || clientId.Equals(DefaultClientId, StringComparison.OrdinalIgnoreCase);
        if (isDefault)
        {
            clientId = DefaultClientId;
            clientSecret = DefaultClientSecret;
        }

        // PKCE Flow
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        // Microsoft Entra ID with DefaultClientId expects http://localhost:53682/
        // Custom Azure public client with redirect URI 'http://localhost' can use dynamic port
        int preferredPort = isDefault ? 53682 : 0;
        bool includeSlash = isDefault;

        using var receiver = new OAuthLoopbackReceiver(preferredPort: preferredPort, path: null, includeTrailingSlashInRedirectUri: includeSlash);
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

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            tokenParams["client_secret"] = clientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errMessage = ExtractApiErrorMessage(json);
            throw new InvalidOperationException($"Lỗi lấy token OneDrive: {errMessage}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        if (root.TryGetProperty("refresh_token", out var refProp))
        {
            _refreshToken = refProp.GetString();
        }

        _currentAccountEmail = await GetUserEmailAsync(cancellationToken);

        AppConfigService.UpdateConfig(cfg =>
        {
            if (!string.IsNullOrEmpty(_refreshToken))
                cfg.OneDriveRefreshToken = _refreshToken;
            if (!string.IsNullOrEmpty(_accessToken))
                cfg.OneDriveAccessToken = _accessToken;
            cfg.OneDriveAccessTokenExpiry = _accessTokenExpiry;
            if (!string.IsNullOrEmpty(_currentAccountEmail))
                cfg.OneDriveAccountEmail = _currentAccountEmail;
        });

        LoggingService.LogAction("OneDrive_Login_Success", new { Email = _currentAccountEmail });
        return true;
    }

    public async Task SignOutAsync()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
        _refreshToken = null;
        _currentAccountEmail = null;

        AppConfigService.UpdateConfig(cfg =>
        {
            cfg.OneDriveRefreshToken = null;
            cfg.OneDriveAccessToken = null;
            cfg.OneDriveAccessTokenExpiry = DateTime.MinValue;
            cfg.OneDriveAccountEmail = null;
        });

        LoggingService.LogAction("OneDrive_Signed_Out");
        await Task.CompletedTask;
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
            var errMessage = ExtractApiErrorMessage(sessionJson);
            throw new InvalidOperationException($"Không thể tạo phiên upload OneDrive ({sessionRes.StatusCode}): {errMessage}");
        }

        using var sessionDoc = JsonDocument.Parse(sessionJson);
        var uploadUrl = sessionDoc.RootElement.GetProperty("uploadUrl").GetString();
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new InvalidOperationException("OneDrive không trả về uploadUrl.");
        }

        // Kích thước khối (Chunk size) chuẩn hóa qua CloudChunkOptimizer (chuẩn bội số 320 KiB của OneDrive)
        int chunkSize = CloudChunkOptimizer.CalculateChunkSize(totalBytes, ProviderName);
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
                var errMessage = ExtractApiErrorMessage(err);
                throw new InvalidOperationException($"Lỗi gửi khối dữ liệu lên OneDrive ({chunkRes.StatusCode}): {errMessage}");
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

        HttpResponseMessage? response = null;
        try
        {
            var downloadUrl = $"{GraphApiDriveEndpoint}/items/{remoteFileId}/content";
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Fallback: Nếu endpoint /content trả về lỗi, thử lấy @microsoft.graph.downloadUrl từ driveItem metadata
                response.Dispose();
                response = null;

                var metaUrl = $"{GraphApiDriveEndpoint}/items/{remoteFileId}?select=id,@microsoft.graph.downloadUrl,size";
                using var metaReq = new HttpRequestMessage(HttpMethod.Get, metaUrl);
                metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                using var metaRes = await _httpClient.SendAsync(metaReq, cancellationToken);
                if (metaRes.IsSuccessStatusCode)
                {
                    var metaJson = await metaRes.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(metaJson);
                    if (doc.RootElement.TryGetProperty("@microsoft.graph.downloadUrl", out var dlElem))
                    {
                        var directDlUrl = dlElem.GetString();
                        if (!string.IsNullOrEmpty(directDlUrl))
                        {
                            response = await _httpClient.GetAsync(directDlUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        }
                    }
                }
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                var err = response != null ? await response.Content.ReadAsStringAsync(cancellationToken) : "Không nhận được phản hồi";
                var errMessage = ExtractApiErrorMessage(err);
                var status = response != null ? response.StatusCode.ToString() : "NoResponse";
                throw new InvalidOperationException($"Không thể tải file từ OneDrive ({status}): {errMessage}");
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
        finally
        {
            response?.Dispose();
        }
    }

    public async Task<bool> DeleteFileAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteFileId)) return true;

        await EnsureAccessTokenAsync(cancellationToken);

        var deleteUrl = $"{GraphApiDriveEndpoint}/items/{remoteFileId}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var errContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errMessage = ExtractApiErrorMessage(errContent);
            LoggingService.Warn("OneDrive: Xóa file {FileId} thất bại ({StatusCode}): {Error}", remoteFileId, response.StatusCode, errMessage);
            throw new InvalidOperationException($"Không thể xóa file từ OneDrive ({response.StatusCode}): {errMessage}");
        }

        return true;
    }

    public async Task<bool> DeleteFolderAsync(string remoteFolderName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteFolderName)) return true;
        try
        {
            await EnsureAccessTokenAsync(cancellationToken);
            var deleteUrl = $"{GraphApiDriveEndpoint}/root:/SaveVault_Backups/{Uri.EscapeDataString(remoteFolderName)}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(req, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var errContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var errMessage = ExtractApiErrorMessage(errContent);
                LoggingService.Warn("OneDrive: Xóa thư mục '{Folder}' thất bại ({StatusCode}): {Error}", remoteFolderName, response.StatusCode, errMessage);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("OneDrive: Lỗi xóa thư mục game '{Folder}': {Message}", remoteFolderName, ex.Message);
            return false;
        }
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
            _refreshToken = AppConfigService.GetConfig().OneDriveRefreshToken;
        }

        if (string.IsNullOrEmpty(_refreshToken))
        {
            throw new InvalidOperationException("Chưa đăng nhập tài khoản OneDrive! Vui lòng vào Cài đặt để kết nối tài khoản.");
        }

        var clientId = await GetClientIdAsync() ?? throw new InvalidOperationException("Chưa cấu hình OneDrive Application (Client) ID!");
        var clientSecret = await GetClientSecretAsync();

        var refreshParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["scope"] = Scopes
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            refreshParams["client_secret"] = clientSecret;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(refreshParams)
        };

        var res = await _httpClient.SendAsync(req, cancellationToken);
        var json = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            var errMessage = ExtractApiErrorMessage(json);
            throw new InvalidOperationException($"Lỗi làm mới token OneDrive ({res.StatusCode}): {errMessage}");
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
            }
        }

        AppConfigService.UpdateConfig(cfg =>
        {
            if (!string.IsNullOrEmpty(_refreshToken))
                cfg.OneDriveRefreshToken = _refreshToken;
            cfg.OneDriveAccessToken = _accessToken;
            cfg.OneDriveAccessTokenExpiry = _accessTokenExpiry;
        });
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

    private Task<string> GetClientIdAsync()
    {
        var config = AppConfigService.GetConfig();
        if (config.UseCustomOneDriveApi && !string.IsNullOrWhiteSpace(config.OneDriveClientId))
        {
            return Task.FromResult(config.OneDriveClientId.Trim());
        }
        return Task.FromResult(DefaultClientId);
    }

    private Task<string?> GetClientSecretAsync()
    {
        var config = AppConfigService.GetConfig();
        if (config.UseCustomOneDriveApi && !string.IsNullOrWhiteSpace(config.OneDriveClientId))
        {
            return Task.FromResult<string?>(null);
        }
        return Task.FromResult<string?>(DefaultClientSecret);
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

    private static string ExtractApiErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Lỗi không xác định từ máy chủ OneDrive.";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.ValueKind == JsonValueKind.String)
                {
                    var str = errorProp.GetString();
                    if (root.TryGetProperty("error_description", out var descProp))
                        return $"{str}: {descProp.GetString()}";
                    return str ?? raw;
                }
                if (errorProp.ValueKind == JsonValueKind.Object && errorProp.TryGetProperty("message", out var msgProp))
                {
                    return msgProp.GetString() ?? raw;
                }
            }
            if (root.TryGetProperty("error_description", out var edProp))
            {
                return edProp.GetString() ?? raw;
            }
            if (root.TryGetProperty("message", out var mProp))
            {
                return mProp.GetString() ?? raw;
            }
        }
        catch
        {
            // fallback if not valid JSON
        }
        return raw.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
    }
}
