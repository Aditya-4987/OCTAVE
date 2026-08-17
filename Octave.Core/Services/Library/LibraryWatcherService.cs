using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Octave.Core.Interfaces;

namespace Octave.Core.Services.Library;

public class LibraryWatcherService : ILibraryWatcherService, IDisposable
{
    private readonly ILibraryScanner _libraryScanner;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _isDeletionFlags = new(StringComparer.OrdinalIgnoreCase);

    public LibraryWatcherService(ILibraryScanner libraryScanner)
        : this(libraryScanner, Array.Empty<string>())
    {
    }

    public LibraryWatcherService(ILibraryScanner libraryScanner, IEnumerable<string> monitoredPaths)
    {
        _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
        if (monitoredPaths == null) throw new ArgumentNullException(nameof(monitoredPaths));

        foreach (var path in monitoredPaths)
        {
            AddMonitoredPath(path);
        }

        // Startup Recovery Pass: delayed reconciliation run (8 seconds)
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            Debug.WriteLine("[LibraryWatcher] Startup reconciliation trigger initiating...");
            await _libraryScanner.RequestFullReconciliationAsync();
        });
    }

    public void AddMonitoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Register path with scanner for reconciliation sweeps
        _libraryScanner.AddMonitoredPath(path);

        if (!Directory.Exists(path))
        {
            Debug.WriteLine($"[LibraryWatcher] Directory does not exist, skipping watcher: {path}");
            return;
        }

        lock (_watchers)
        {
            if (_watchers.Exists(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"[LibraryWatcher] Watcher already active for path: {path}");
                return;
            }

            Debug.WriteLine($"[LibraryWatcher] Starting watcher for directory: {path}");

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += (s, e) =>
            {
                Debug.WriteLine($"[LibraryWatcher] FileSystemWatcher buffer overflow/error for path {path}: {e.GetException()?.Message}");
                _ = _libraryScanner.RequestFullReconciliationAsync();
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void RemoveMonitoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _libraryScanner.RemoveMonitoredPath(path);

        lock (_watchers)
        {
            for (int i = _watchers.Count - 1; i >= 0; i--)
            {
                var w = _watchers[i];
                if (string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[LibraryWatcher] Stopping watcher for directory: {path}");
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                    _watchers.RemoveAt(i);
                }
            }
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        Debug.WriteLine($"[LibraryWatcher] Created: {e.FullPath}");
        EnqueueFileEvent(e.FullPath, isDeletion: false);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Debug.WriteLine($"[LibraryWatcher] Changed: {e.FullPath}");
        EnqueueFileEvent(e.FullPath, isDeletion: false);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        Debug.WriteLine($"[LibraryWatcher] Deleted: {e.FullPath}");
        EnqueueFileEvent(e.FullPath, isDeletion: true);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Debug.WriteLine($"[LibraryWatcher] Renamed: {e.OldFullPath} -> {e.FullPath}");

        // If a directory was renamed or moved, trigger a full reconciliation sweep
        if (Directory.Exists(e.FullPath))
        {
            Debug.WriteLine($"[LibraryWatcher] Directory renamed from {e.OldFullPath} to {e.FullPath}; scheduling reconciliation.");
            _ = _libraryScanner.RequestFullReconciliationAsync();
            return;
        }

        // Handle old path: queue reconciliation
        string oldExt = Path.GetExtension(e.OldFullPath).ToLowerInvariant();
        if (IsSupportedExtension(oldExt))
        {
            Debug.WriteLine($"[LibraryWatcher] Deleted (via Rename): {e.OldFullPath}");
            EnqueueFileEvent(e.OldFullPath, isDeletion: true);
        }

        // Handle new path: queue incremental scan
        string newExt = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (IsSupportedExtension(newExt))
        {
            Debug.WriteLine($"[LibraryWatcher] Created (via Rename): {e.FullPath}");
            EnqueueFileEvent(e.FullPath, isDeletion: false);
        }
    }

    private void EnqueueFileEvent(string path, bool isDeletion)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupportedExtension(ext))
        {
            return;
        }

        _isDeletionFlags[path] = isDeletion;

        _debounceTimers.AddOrUpdate(
            path,
            key => new Timer(TimerCallback, key, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan),
            (key, existingTimer) =>
            {
                try
                {
                    existingTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    return new Timer(TimerCallback, key, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                }
                return existingTimer;
            }
        );
    }

    private void TimerCallback(object? state)
    {
        if (state is not string path) return;

        _isDeletionFlags.TryGetValue(path, out bool isDeletion);

        if (isDeletion)
        {
            if (_debounceTimers.TryRemove(path, out var timer))
            {
                timer.Dispose();
            }
            _isDeletionFlags.TryRemove(path, out _);

            Debug.WriteLine("[LibraryWatcher] Reconciliation scheduled");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryScanner.RequestFullReconciliationAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LibraryWatcher] Full reconciliation request failed: {ex.Message}");
                }
            });
        }
        else
        {
            // Verify file exists
            if (!File.Exists(path))
            {
                if (_debounceTimers.TryRemove(path, out var timer))
                {
                    timer.Dispose();
                }
                _isDeletionFlags.TryRemove(path, out _);
                return;
            }

            // Lock verification
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                // Locked, reschedule timer
                if (_debounceTimers.TryGetValue(path, out var timer))
                {
                    try
                    {
                        timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        _debounceTimers.TryRemove(path, out _);
                        _isDeletionFlags.TryRemove(path, out _);
                        EnqueueFileEvent(path, isDeletion: false);
                    }
                }
                return;
            }
            catch (Exception)
            {
                if (_debounceTimers.TryRemove(path, out var timer))
                {
                    timer.Dispose();
                }
                _isDeletionFlags.TryRemove(path, out _);
                return;
            }

            if (_debounceTimers.TryRemove(path, out var completedTimer))
            {
                completedTimer.Dispose();
            }
            _isDeletionFlags.TryRemove(path, out _);

            // Execute incremental scan
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryScanner.ScanFileAsync(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LibraryWatcher] Failed to scan file '{path}': {ex.Message}");
                }
            });
        }
    }

    private static bool IsSupportedExtension(string ext)
    {
        return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".aac" || ext == ".ogg" || ext == ".wav" || ext == ".wma" || ext == ".opus";
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Dispose();
        }
        _watchers.Clear();

        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }
        _debounceTimers.Clear();
        _isDeletionFlags.Clear();
    }
}
