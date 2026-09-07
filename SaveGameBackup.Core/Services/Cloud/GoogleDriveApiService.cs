using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.Core.Services.Cloud;

public class GoogleDriveApiService : ICloudStorageService
{
    private const string DefaultClientId = "948839073105-mockclientid.apps.googleusercontent.com";
    private const string DefaultClientSecret = "GOCSPX-mockclientsecret";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string DriveApiFilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string DriveApiUploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable";
    private const string Scope = "https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.email";

    private readonly DatabaseService _databaseService;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _accessTokenExpiry = DateTime.MinValue;
    private string? _refreshToken;
    private string? _currentAccountEmail;

    public GoogleDriveApiService(DatabaseService databaseService, HttpClient? httpClient = null)
    {
        _databaseService = databaseService;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string ProviderName => "GoogleDrive";
    public string DisplayName => "Google Drive (REST API)";
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken) || (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry);
    public string? CurrentAccountEmail => _currentAccountEmail;

    public async Task InitializeFromDatabaseAsync()
    {
        var config = AppConfigService.GetConfig();
        _refreshToken = config.GoogleDriveRefreshToken;
        _accessToken = config.GoogleDriveAccessToken;
        _accessTokenExpiry = config.GoogleDriveAccessTokenExpiry;
        _currentAccountEmail = config.GoogleDriveAccountEmail;

        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry)
        {
            LoggingService.LogAction("GoogleDrive_Session_Restored", new { Email = _currentAccountEmail, Expiry = _accessTokenExpiry });
            return;
        }

        if (!string.IsNullOrEmpty(_refreshToken))
        {
            try
            {
                await EnsureAccessTokenAsync(CancellationToken.None);
                LoggingService.LogAction("GoogleDrive_Silent_Token_Refreshed", new { Email = _currentAccountEmail, Expiry = _accessTokenExpiry });
            }
            catch (Exception ex)
            {
                LoggingService.Warn("GoogleDrive: Không thể làm mới token trong nền lúc khởi động: {Message}", ex.Message);
            }
        }
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var clientId = await GetClientIdAsync();
        var clientSecret = await GetClientSecretAsync();

        // Google OAuth Desktop App flow uses http://127.0.0.1:{port}/ or http://localhost:{port}/ (RFC 8252 loopback)
        using var receiver = new OAuthLoopbackReceiver(preferredPort: 0, path: null, includeTrailingSlashInRedirectUri: true);
        var state = Guid.NewGuid().ToString("N");
        var authUrl = $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(receiver.RedirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={Uri.EscapeDataString(Scope)}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&state={state}";

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch
        {
            // Nếu không mở được tự động, tiếp tục chờ receiver
        }

        var (code, error) = await receiver.WaitForCallbackAsync(TimeSpan.FromMinutes(3), cancellationToken);

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException($"Đăng nhập Google Drive thất bại: {error ?? "Không nhận được mã xác thực."}");
        }

        // Đổi code lấy tokens
        var tokenParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = receiver.RedirectUri,
            ["grant_type"] = "authorization_code"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errMessage = ExtractApiErrorMessage(json);
            throw new InvalidOperationException($"Lỗi lấy token Google Drive: {errMessage}");
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
                cfg.GoogleDriveRefreshToken = _refreshToken;
            if (!string.IsNullOrEmpty(_accessToken))
                cfg.GoogleDriveAccessToken = _accessToken;
            cfg.GoogleDriveAccessTokenExpiry = _accessTokenExpiry;
            if (!string.IsNullOrEmpty(_currentAccountEmail))
                cfg.GoogleDriveAccountEmail = _currentAccountEmail;
        });

        LoggingService.LogAction("GoogleDrive_Login_Success", new { Email = _currentAccountEmail });
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
            cfg.GoogleDriveRefreshToken = null;
            cfg.GoogleDriveAccessToken = null;
            cfg.GoogleDriveAccessTokenExpiry = DateTime.MinValue;
            cfg.GoogleDriveAccountEmail = null;
        });

        LoggingService.LogAction("GoogleDrive_Signed_Out");
        await Task.CompletedTask;
    }

    public async Task<string?> GetUserEmailAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_currentAccountEmail)) return _currentAccountEmail;

        await EnsureAccessTokenAsync(cancellationToken);
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("email", out var emailProp))
            {
                _currentAccountEmail = emailProp.GetString();
                return _currentAccountEmail;
            }
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
            Message = "Đang chuẩn bị thư mục trên Google Drive..."
        });

        // 1. Tạo hoặc lấy thư mục gốc SaveVault_Backups
        var rootFolderId = await GetOrCreateFolderAsync("SaveVault_Backups", null, cancellationToken);

        // 2. Tạo hoặc lấy thư mục game con
        var gameFolderId = await GetOrCreateFolderAsync(remoteGameFolderName, rootFolderId, cancellationToken);

        // 3. Khởi tạo Resumable Upload Session
        progress?.Report(new BackupProgress
        {
            Percent = 10,
            CurrentFile = fileName,
            TotalBytes = totalBytes,
            ProcessedBytes = 0,
            Message = "Đang khởi tạo phiên tải lên Google Drive..."
        });

        var metaJson = JsonSerializer.Serialize(new
        {
            name = fileName,
            parents = new[] { gameFolderId }
        });

        using var initReq = new HttpRequestMessage(HttpMethod.Post, DriveApiUploadEndpoint);
        initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        initReq.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        initReq.Headers.Add("X-Upload-Content-Length", totalBytes.ToString());
        initReq.Content = new StringContent(metaJson, Encoding.UTF8, "application/json");

        var initRes = await _httpClient.SendAsync(initReq, cancellationToken);
        if (!initRes.IsSuccessStatusCode)
        {
            var err = await initRes.Content.ReadAsStringAsync(cancellationToken);
            var errMessage = ExtractApiErrorMessage(err);
            throw new InvalidOperationException($"Không thể tạo phiên upload trên Google Drive: {errMessage}");
        }

        var uploadUrl = initRes.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new InvalidOperationException("Google Drive không trả về URL upload.");
        }

        // 4. Upload theo từng khối (Chunk size chuẩn hóa qua CloudChunkOptimizer, chuẩn bội số 256 KiB của Google Drive):
        int chunkSize = CloudChunkOptimizer.CalculateChunkSize(totalBytes, ProviderName);
        byte[] buffer = new byte[chunkSize];
        long bytesSent = 0;

        using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var stopwatch = Stopwatch.StartNew();

        string fileId = string.Empty;

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
            chunkReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

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
                Message = $"Đang tải lên Google Drive: {bytesSent / 1024.0 / 1024.0:F1} MB / {totalBytes / 1024.0 / 1024.0:F1} MB ({speedMb:F1} MB/s)"
            });

            if (chunkRes.IsSuccessStatusCode)
            {
                // Hoàn tất upload
                var finishJson = await chunkRes.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(finishJson);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    fileId = idProp.GetString() ?? string.Empty;
                }
                break;
            }
            else if ((int)chunkRes.StatusCode == 308) // Resume Incomplete
            {
                // Tiếp tục chunk kế tiếp
                continue;
            }
            else
            {
                var err = await chunkRes.Content.ReadAsStringAsync(cancellationToken);
                var errMessage = ExtractApiErrorMessage(err);
                throw new InvalidOperationException($"Lỗi gửi khối dữ liệu lên Google Drive ({chunkRes.StatusCode}): {errMessage}");
            }
        }

        progress?.Report(new BackupProgress
        {
            Percent = 100,
            CurrentFile = fileName,
            ProcessedBytes = totalBytes,
            TotalBytes = totalBytes,
            Message = "Đồng bộ lên Google Drive hoàn tất!"
        });

        return new CloudUploadResult
        {
            Success = true,
            Provider = ProviderName,
            FileId = fileId,
            FileName = fileName,
            FileSizeBytes = totalBytes,
            WebUrl = !string.IsNullOrEmpty(fileId) ? $"https://drive.google.com/file/d/{fileId}/view" : null
        };
    }

    public async Task<string> DownloadFileAsync(
        string remoteFileId,
        string localDestinationPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var downloadUrl = $"{DriveApiFilesEndpoint}/{remoteFileId}?alt=media";
        using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            var errMessage = ExtractApiErrorMessage(err);
            throw new InvalidOperationException($"Không thể tải file từ Google Drive ({response.StatusCode}): {errMessage}");
        }

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var dir = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(localDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[256 * 1024]; // 256 KB buffer cho tốc độ đọc stream cao nhất
        long bytesReadTotal = 0;
        int read;
        var stopwatch = Stopwatch.StartNew();

        while ((read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesReadTotal += read;

            int pct = totalBytes > 0 ? (int)((double)bytesReadTotal / totalBytes * 100) : 50;
            var speedMb = stopwatch.Elapsed.TotalSeconds > 0 ? (bytesReadTotal / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds : 0;
            var speedText = $"{speedMb:F1} MB/s";

            progress?.Report(new BackupProgress
            {
                Percent = Math.Clamp(pct, 0, 100),
                CurrentFile = Path.GetFileName(localDestinationPath),
                ProcessedBytes = bytesReadTotal,
                TotalBytes = Math.Max(totalBytes, bytesReadTotal),
                SpeedText = speedText,
                Message = totalBytes > 0 
                    ? $"Đang tải từ Google Drive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB / {totalBytes / 1024.0 / 1024.0:F1} MB ({speedText})"
                    : $"Đang tải từ Google Drive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB ({speedText})..."
            });
        }

        return localDestinationPath;
    }

    public async Task<bool> DeleteFileAsync(string remoteFileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteFileId)) return true;

        await EnsureAccessTokenAsync(cancellationToken);

        var deleteUrl = $"{DriveApiFilesEndpoint}/{remoteFileId}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var errContent = await response.Content.ReadAsStringAsync(cancellationToken);
            LoggingService.Warn("GoogleDrive: Xóa file {FileId} thất bại ({StatusCode}): {Error}", remoteFileId, response.StatusCode, errContent);
            throw new InvalidOperationException($"Không thể xóa file từ Google Drive ({response.StatusCode})");
        }

        return true;
    }

    public async Task<List<CloudFileInfo>> ListBackupsAsync(
        string? remoteGameFolderName = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var list = new List<CloudFileInfo>();
        string query = "trashed = false and mimeType != 'application/vnd.google-apps.folder'";

        if (!string.IsNullOrEmpty(remoteGameFolderName))
        {
            var rootFolderId = await FindFolderIdAsync("SaveVault_Backups", null, cancellationToken);
            if (rootFolderId != null)
            {
                var gameFolderId = await FindFolderIdAsync(remoteGameFolderName, rootFolderId, cancellationToken);
                if (gameFolderId != null)
                {
                    query += $" and '{gameFolderId}' in parents";
                }
            }
        }

        var url = $"{DriveApiFilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=files(id,name,size,modifiedTime,webViewLink)&pageSize=100";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("files", out var filesProp))
            {
                foreach (var file in filesProp.EnumerateArray())
                {
                    list.Add(new CloudFileInfo
                    {
                        Id = file.GetProperty("id").GetString() ?? string.Empty,
                        Name = file.GetProperty("name").GetString() ?? string.Empty,
                        SizeBytes = file.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sz) ? sz : 0,
                        ModifiedTime = file.TryGetProperty("modifiedTime", out var mt) && DateTime.TryParse(mt.GetString(), out var dt) ? dt : null,
                        WebUrl = file.TryGetProperty("webViewLink", out var w) ? w.GetString() : null
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
            _refreshToken = AppConfigService.GetConfig().GoogleDriveRefreshToken;
        }

        if (string.IsNullOrEmpty(_refreshToken))
        {
            throw new InvalidOperationException("Chưa đăng nhập tài khoản Google Drive! Vui lòng vào Cài đặt để kết nối tài khoản.");
        }

        var clientId = await GetClientIdAsync();
        var clientSecret = await GetClientSecretAsync();

        var refreshParams = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = _refreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(refreshParams)
        };

        var res = await _httpClient.SendAsync(req, cancellationToken);
        var json = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            var errMessage = ExtractApiErrorMessage(json);
            throw new InvalidOperationException($"Lỗi làm mới token Google Drive ({res.StatusCode}): {errMessage}");
        }

        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        AppConfigService.UpdateConfig(cfg =>
        {
            cfg.GoogleDriveAccessToken = _accessToken;
            cfg.GoogleDriveAccessTokenExpiry = _accessTokenExpiry;
        });
    }

    private async Task<string> GetOrCreateFolderAsync(string folderName, string? parentFolderId, CancellationToken cancellationToken)
    {
        var existingId = await FindFolderIdAsync(folderName, parentFolderId, cancellationToken);
        if (!string.IsNullOrEmpty(existingId)) return existingId;

        var meta = new Dictionary<string, object>
        {
            ["name"] = folderName,
            ["mimeType"] = "application/vnd.google-apps.folder"
        };

        if (!string.IsNullOrEmpty(parentFolderId))
        {
            meta["parents"] = new[] { parentFolderId };
        }

        var json = JsonSerializer.Serialize(meta);
        using var req = new HttpRequestMessage(HttpMethod.Post, DriveApiFilesEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var res = await _httpClient.SendAsync(req, cancellationToken);
        var resJson = await res.Content.ReadAsStringAsync(cancellationToken);

        if (!res.IsSuccessStatusCode)
        {
            var errMessage = ExtractApiErrorMessage(resJson);
            throw new InvalidOperationException($"Không thể tạo thư mục '{folderName}' trên Google Drive: {errMessage}");
        }

        using var doc = JsonDocument.Parse(resJson);
        return doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    private async Task<string?> FindFolderIdAsync(string folderName, string? parentFolderId, CancellationToken cancellationToken)
    {
        var query = $"name = '{folderName.Replace("'", "\\'")}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        if (!string.IsNullOrEmpty(parentFolderId))
        {
            query += $" and '{parentFolderId}' in parents";
        }

        var url = $"{DriveApiFilesEndpoint}?q={Uri.EscapeDataString(query)}&fields=files(id)&pageSize=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("files", out var filesProp))
            {
                foreach (var file in filesProp.EnumerateArray())
                {
                    return file.GetProperty("id").GetString();
                }
            }
        }

        return null;
    }

    private Task<string> GetClientIdAsync()
    {
        var configured = AppConfigService.GetConfig().GoogleDriveClientId;
        return Task.FromResult(!string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DefaultClientId);
    }

    private Task<string> GetClientSecretAsync()
    {
        var configured = AppConfigService.GetConfig().GoogleDriveClientSecret;
        return Task.FromResult(!string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DefaultClientSecret);
    }

    private static string ExtractApiErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Lỗi không xác định từ máy chủ Google Drive.";
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
