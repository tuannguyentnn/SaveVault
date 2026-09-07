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
    }

    public GoogleDriveApiService GoogleDrive => _googleDriveService;
    public OneDriveApiService OneDrive => _oneDriveService;

    public string ActiveProviderName
    {
        get => _activeProviderName;
        set => _activeProviderName = value;
    }

    public ICloudStorageService CurrentProvider =>
        string.Equals(_activeProviderName, "OneDrive", StringComparison.OrdinalIgnoreCase)
            ? _oneDriveService
            : _googleDriveService;

    public ICloudStorageService? GetProvider(string? providerName)
    {
        if (string.Equals(providerName, "OneDrive", StringComparison.OrdinalIgnoreCase))
            return _oneDriveService;
        if (string.Equals(providerName, "GoogleDrive", StringComparison.OrdinalIgnoreCase))
            return _googleDriveService;
        return CurrentProvider;
    }

    public async Task InitializeAsync()
    {
        var savedProvider = await _databaseService.GetSettingAsync("active_cloud_provider");
        if (!string.IsNullOrEmpty(savedProvider))
        {
            _activeProviderName = savedProvider;
        }

        await _googleDriveService.InitializeFromDatabaseAsync();
        await _oneDriveService.InitializeFromDatabaseAsync();
    }

    public async Task SetActiveProviderAsync(string providerName)
    {
        _activeProviderName = providerName;
        await _databaseService.SaveSettingAsync("active_cloud_provider", providerName);
    }

    public void SetActiveProvider(string providerName)
    {
        _activeProviderName = providerName;
        _ = _databaseService.SaveSettingAsync("active_cloud_provider", providerName);
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
