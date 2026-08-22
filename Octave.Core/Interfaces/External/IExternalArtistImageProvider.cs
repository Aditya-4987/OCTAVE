using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Models;

namespace Octave.Core.Interfaces.External;

public interface IExternalArtistImageProvider
{
    string ProviderName { get; }
    bool IsEnabled { get; }
    int Priority { get; }

    Task<IReadOnlyList<string>> SearchArtistImageUrlsAsync(
        string artistName,
        ExternalIds? externalIds = null,
        CancellationToken ct = default);
}
