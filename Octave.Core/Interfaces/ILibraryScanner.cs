using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Services.Library;

namespace Octave.Core.Interfaces;

public interface ILibraryScanner
{
    event EventHandler<LibraryScanProgressEventArgs>? ScanProgressChanged;
    event EventHandler? LibraryChanged;
    Task ScanAsync(string rootPath, CancellationToken ct);
    Task ScanFileAsync(string path);
    Task RemoveStalePathAsync(string path);
    Task RequestFullReconciliationAsync();
    IReadOnlyList<string> MonitoredPaths { get; }
    void AddMonitoredPath(string path);
}
