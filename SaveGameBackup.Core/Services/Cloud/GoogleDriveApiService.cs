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
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken) || !string.IsNullOrEmpty(_accessToken);
    public string? CurrentAccountEmail => _currentAccountEmail;

    public async Task InitializeFromDatabaseAsync()
    {
        _refreshToken = await _databaseService.GetSettingAsync("gdrive_refresh_token");
        _currentAccountEmail = await _databaseService.GetSettingAsync("gdrive_account_email");
    }

    public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var clientId = await GetClientIdAsync();
        var clientSecret = await GetClientSecretAsync();

        using var receiver = new OAuthLoopbackReceiver();
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
            throw new InvalidOperationException($"Lỗi lấy token Google Drive: {json}");
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
                await _databaseService.SaveSettingAsync("gdrive_refresh_token", _refreshToken);
            }
        }

        _currentAccountEmail = await GetUserEmailAsync(cancellationToken);
        if (!string.IsNullOrEmpty(_currentAccountEmail))
        {
            await _databaseService.SaveSettingAsync("gdrive_account_email", _currentAccountEmail);
        }

        return true;
    }

    public async Task SignOutAsync()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
        _refreshToken = null;
        _currentAccountEmail = null;

        await _databaseService.SaveSettingAsync("gdrive_refresh_token", string.Empty);
        await _databaseService.SaveSettingAsync("gdrive_account_email", string.Empty);
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
            throw new InvalidOperationException($"Không thể tạo phiên upload trên Google Drive: {err}");
        }

        var uploadUrl = initRes.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(uploadUrl))
        {
            throw new InvalidOperationException("Google Drive không trả về URL upload.");
        }

        // 4. Upload theo từng khối (Chunk size: 512 KB, chuẩn bội số 256 KB)
        const int chunkSize = 512 * 1024;
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
                throw new InvalidOperationException($"Lỗi gửi khối dữ liệu lên Google Drive ({chunkRes.StatusCode}): {err}");
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
            throw new InvalidOperationException($"Không thể tải file từ Google Drive ({response.StatusCode}): {err}");
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
                    ? $"Đang tải từ Google Drive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB / {totalBytes / 1024.0 / 1024.0:F1} MB ({pct}%)"
                    : $"Đang tải từ Google Drive: {bytesReadTotal / 1024.0 / 1024.0:F1} MB..."
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
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
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
            _refreshToken = await _databaseService.GetSettingAsync("gdrive_refresh_token");
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
            throw new InvalidOperationException($"Lỗi làm mới token Google Drive ({res.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
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
            throw new InvalidOperationException($"Không thể tạo thư mục '{folderName}' trên Google Drive: {resJson}");
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

    private async Task<string> GetClientIdAsync()
    {
        var configured = await _databaseService.GetSettingAsync("gdrive_client_id");
        return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DefaultClientId;
    }

    private async Task<string> GetClientSecretAsync()
    {
        var configured = await _databaseService.GetSettingAsync("gdrive_client_secret");
        return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DefaultClientSecret;
    }
}
