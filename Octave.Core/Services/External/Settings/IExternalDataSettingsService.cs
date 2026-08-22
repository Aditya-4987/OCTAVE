using System;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Services.External.Settings;

public interface IExternalDataSettingsService
{
    event EventHandler<ExternalDataSettings>? SettingsChanged;
    ExternalDataSettings CurrentSettings { get; }
    Task LoadSettingsAsync();
    Task UpdateSettingsAsync(ExternalDataSettings settings);
    Task<TestConnectionResult> TestTheAudioDbConnectionAsync(string apiKey, CancellationToken ct = default);
}
