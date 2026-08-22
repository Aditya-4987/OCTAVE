using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces.External;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Network;

namespace Octave.Core.Services.External.Settings;

public class ExternalDataSettingsService : IExternalDataSettingsService
{
    private const string SettingKey = "ExternalDataSettings";
    private readonly SqliteDbContext _dbContext;
    private readonly IHttpService _httpService;
    private readonly TheAudioDbOptions _theAudioDbOptions;
    private ExternalDataSettings _currentSettings = new();

    public event EventHandler<ExternalDataSettings>? SettingsChanged;

    public ExternalDataSettings CurrentSettings => _currentSettings.Clone();

    public ExternalDataSettingsService(
        SqliteDbContext dbContext,
        IHttpService httpService,
        TheAudioDbOptions theAudioDbOptions)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _theAudioDbOptions = theAudioDbOptions ?? throw new ArgumentNullException(nameof(theAudioDbOptions));
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            string? json = await _dbContext.GetSettingAsync(SettingKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = JsonSerializer.Deserialize<ExternalDataSettings>(json);
                if (loaded != null)
                {
                    _currentSettings = loaded;
                    SyncOptionsWithSettings(_currentSettings);
                    SettingsChanged?.Invoke(this, CurrentSettings);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExternalDataSettingsService] Failed to load settings: {ex.Message}");
        }

        // Default settings if missing or corrupt
        _currentSettings = new ExternalDataSettings();
        SyncOptionsWithSettings(_currentSettings);
        SettingsChanged?.Invoke(this, CurrentSettings);
    }

    public async Task UpdateSettingsAsync(ExternalDataSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        _currentSettings = settings.Clone();
        SyncOptionsWithSettings(_currentSettings);

        try
        {
            string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = false });
            await _dbContext.SetSettingAsync(SettingKey, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExternalDataSettingsService] Failed to persist settings: {ex.Message}");
        }

        SettingsChanged?.Invoke(this, CurrentSettings);
    }

    public async Task<TestConnectionResult> TestTheAudioDbConnectionAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new TestConnectionResult(false, "API key cannot be empty.");
        }

        try
        {
            string baseUrl = _theAudioDbOptions.BaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/{apiKey.Trim()}/search.php?s=Coldplay";

            var result = await _httpService.GetJsonAsync<JsonElement>(
                url,
                "theaudiodb",
                null,
                TimeSpan.FromSeconds(8),
                ct).ConfigureAwait(false);

            int? statusCode = result.StatusCode.HasValue ? (int)result.StatusCode.Value : null;

            if (result.IsSuccess)
            {
                return new TestConnectionResult(true, "Successfully connected to TheAudioDB.", statusCode);
            }

            if (result.StatusCode == System.Net.HttpStatusCode.Unauthorized || result.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return new TestConnectionResult(false, "Authentication failed: TheAudioDB rejected the API key.", statusCode);
            }

            return new TestConnectionResult(false, $"Connection test failed (HTTP {statusCode?.ToString() ?? "Unknown"}). Check the API key and try again.", statusCode);
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResult(false, "Connection test timed out.");
        }
        catch (Exception)
        {
            // Security: Never log or display the raw API key
            return new TestConnectionResult(false, "Could not reach TheAudioDB. Check your internet connection.");
        }
    }

    private void SyncOptionsWithSettings(ExternalDataSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TheAudioDbApiKey))
        {
            _theAudioDbOptions.ApiKey = settings.TheAudioDbApiKey.Trim();
        }
    }
}
