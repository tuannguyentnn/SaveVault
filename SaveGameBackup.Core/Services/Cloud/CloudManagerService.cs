using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SaveGameBackup.Core.Services.Cloud;

public class CloudManagerService
{
    private readonly GoogleDriveApiService _googleDriveService;
    private readonly OneDriveApiService _oneDriveService;
    private readonly DatabaseService _databaseService;
    private string _activeProviderName = "GoogleDrive";

    public CloudManagerService(DatabaseService databaseService, HttpClient? httpClient = null)
    {
        _databaseService = databaseService;
        _googleDriveService = new GoogleDriveApiService(databaseService, httpClient);
        _oneDriveService = new OneDriveApiService(databaseService, httpClient);

        // Nạp active provider từ app_config.json
        var savedProvider = AppConfigService.GetConfig().ActiveCloudProvider;
        if (!string.IsNullOrEmpty(savedProvider))
        {
            _activeProviderName = savedProvider;
        }
    }

    private readonly Dictionary<string, ICloudStorageService> _customProviders = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterCustomProvider(string name, ICloudStorageService service)
    {
        _customProviders[name] = service;
    }

    public GoogleDriveApiService GoogleDrive => _googleDriveService;
    public OneDriveApiService OneDrive => _oneDriveService;

    public string ActiveProviderName
    {
        get => _activeProviderName;
        set => SetActiveProvider(value);
    }

    public ICloudStorageService CurrentProvider =>
        string.Equals(_activeProviderName, "OneDrive", StringComparison.OrdinalIgnoreCase)
            ? _oneDriveService
            : _googleDriveService;

    public IEnumerable<ICloudStorageService> GetAllProviders() => new ICloudStorageService[] { _googleDriveService, _oneDriveService };

    public ICloudStorageService? GetProvider(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return CurrentProvider;
        if (_customProviders.TryGetValue(providerName, out var custom)) return custom;
        if (providerName.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            return _oneDriveService;
        if (providerName.Contains("Google", StringComparison.OrdinalIgnoreCase))
            return _googleDriveService;
        return CurrentProvider;
    }

    /// <summary>
    /// Nạp cấu hình, tokens và khôi phục phiên đăng nhập từ app_config.json
    /// </summary>
    public async Task InitializeAsync()
    {
        var savedProvider = AppConfigService.GetConfig().ActiveCloudProvider;
        if (!string.IsNullOrEmpty(savedProvider))
        {
            _activeProviderName = savedProvider;
        }

        await _googleDriveService.InitializeFromDatabaseAsync();
        await _oneDriveService.InitializeFromDatabaseAsync();

        LoggingService.LogAction("CloudManager_Initialized", new
        {
            ActiveProvider = _activeProviderName,
            GoogleDriveLoggedIn = _googleDriveService.IsAuthenticated,
            GoogleDriveEmail = _googleDriveService.CurrentAccountEmail,
            OneDriveLoggedIn = _oneDriveService.IsAuthenticated,
            OneDriveEmail = _oneDriveService.CurrentAccountEmail
        });
    }

    public async Task SetActiveProviderAsync(string providerName)
    {
        _activeProviderName = providerName;
        AppConfigService.UpdateConfig(cfg => cfg.ActiveCloudProvider = providerName);
        LoggingService.LogAction("Cloud_Active_Provider_Changed", new { Provider = providerName });
        await Task.CompletedTask;
    }

    public void SetActiveProvider(string providerName)
    {
        _activeProviderName = providerName;
        AppConfigService.UpdateConfig(cfg => cfg.ActiveCloudProvider = providerName);
        LoggingService.LogAction("Cloud_Active_Provider_Changed", new { Provider = providerName });
    }

    public bool IsLoggedIn() => CurrentProvider.IsAuthenticated;

    public string? GetSavedUserEmail() => CurrentProvider.CurrentAccountEmail;

    public Task<bool> LoginAsync(CancellationToken ct = default) => CurrentProvider.AuthenticateAsync(ct);

    public async Task LogoutAsync() => await CurrentProvider.SignOutAsync();

    public void Logout()
    {
        try
        {
            CurrentProvider.SignOutAsync().GetAwaiter().GetResult();
        }
        catch { }
    }

    public Task<bool> DeleteFileAsync(string fileId, CancellationToken ct = default)
        => CurrentProvider.DeleteFileAsync(fileId, ct);
}
