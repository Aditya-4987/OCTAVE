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
    private const int DebounceDelayMs = 2000;
    // WATCH-04: a permanently-locked file retries this many times (~2s apart), then
    // gives up until periodic reconciliation picks it up.
    private const int MaxLockRetries = 5;
    // WATCH-08: how often absent roots are re-probed so a plugged-in USB/network
    // root starts being watched without an app restart.
    private const int WatchRetryIntervalMs = 30_000;

    private readonly ILibraryScanner _libraryScanner;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watchersLock = new();
    private volatile bool _disposed;

    // WATCH-03/09: debounce state lives under ONE lock instead of Timer-per-path
    // factories racing inside ConcurrentDictionary.AddOrUpdate. The pending entry
    // is CLAIMED (removed) before its callback does any work, so a filesystem
    // event arriving mid-processing always creates a fresh entry — no event can
    // be dropped by a timer disposal race.
    private sealed class PendingEvent
    {
        public bool IsDeletion;
        public int LockRetries;
        public Timer Timer = null!;
    }

    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);

    // WATCH-08: roots that did not exist at AddMonitoredPath time. They are NOT
    // registered with the scanner (whose reconciliation would see zero files and,
    // pre-SCAN-08, delete everything) and are re-probed periodically.
    private readonly ConcurrentDictionary<string, byte> _deferredPaths = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _watchRetryTimer;

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

        // Startup Recovery Pass: delayed reconciliation run (8 seconds).
        // WATCH-05: faults are observed instead of surfacing as unobserved tasks.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                Debug.WriteLine("[LibraryWatcher] Startup reconciliation trigger initiating...");
                await _libraryScanner.RequestFullReconciliationAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LibraryWatcher] Startup reconciliation failed: {ex.Message}");
            }
        });
    }

    public void AddMonitoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // WATCH-08: reachability is confirmed BEFORE the path joins the scanner's
        // reconciliation set — an offline root registered for deletion-reconciliation
        // is a data-loss amplifier.
        if (!Directory.Exists(path))
        {
            lock (_pendingGate)
            {
                if (_disposed) return;
                _deferredPaths[path] = 0;
                EnsureRetryTimerLocked();
            }
            Debug.WriteLine($"[LibraryWatcher] Directory does not exist yet, deferring watcher: {path}");
            return;
        }

        StartWatching(path);
    }

    public void RemoveMonitoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _deferredPaths.TryRemove(path, out _);
        MaybeStopRetryTimer();

        _libraryScanner.RemoveMonitoredPath(path);

        lock (_watchersLock)
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

    // internal for tests: how many paths currently sit in the debounce window.
    internal int PendingEventCount
    {
        get { lock (_pendingGate) { return _pendingEvents.Count; } }
    }

    // Caller must hold _pendingGate.
    private void EnsureRetryTimerLocked()
    {
        if (_watchRetryTimer != null) return;
        _watchRetryTimer = new Timer(RetryDeferredPaths, null, WatchRetryIntervalMs, WatchRetryIntervalMs);
    }

    private void MaybeStopRetryTimer()
    {
        lock (_pendingGate)
        {
            if (_deferredPaths.IsEmpty && _watchRetryTimer != null)
            {
                _watchRetryTimer.Dispose();
                _watchRetryTimer = null;
            }
        }
    }

    private void RetryDeferredPaths(object? state)
    {
        if (_disposed) return;

        List<string> nowAvailable = new();
        foreach (var path in _deferredPaths.Keys)
        {
            if (Directory.Exists(path))
            {
                nowAvailable.Add(path);
            }
        }

        foreach (var path in nowAvailable)
        {
            // Claim before starting: StartWatching registers with the scanner only
            // once the root is confirmed present.
            if (_deferredPaths.TryRemove(path, out _))
            {
                Debug.WriteLine($"[LibraryWatcher] Deferred root appeared, starting watcher: {path}");
                StartWatching(path);
            }
        }

        MaybeStopRetryTimer();
    }

    private void StartWatching(string path)
    {
        // The root is confirmed reachable here — only NOW may the scanner treat it
        // as authoritative for reconciliation (WATCH-08).
        _libraryScanner.AddMonitoredPath(path);

        lock (_watchersLock)
        {
            if (_disposed) return;

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
                // WATCH-05: overflow fallback must not become an unobserved task.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _libraryScanner.RequestFullReconciliationAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[LibraryWatcher] Overflow reconciliation request failed: {ex.Message}");
                    }
                });
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryScanner.RequestFullReconciliationAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LibraryWatcher] Rename reconciliation request failed: {ex.Message}");
                }
            });
            return;
        }

        // Handle old path: targeted removal (WATCH-01)
        string oldExt = Path.GetExtension(e.OldFullPath).ToLowerInvariant();
        if (IsWatchableAudioPath(e.OldFullPath, oldExt))
        {
            Debug.WriteLine($"[LibraryWatcher] Deleted (via Rename): {e.OldFullPath}");
            EnqueueFileEvent(e.OldFullPath, isDeletion: true);
        }

        // Handle new path: queue incremental scan
        string newExt = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (IsWatchableAudioPath(e.FullPath, newExt))
        {
            Debug.WriteLine($"[LibraryWatcher] Created (via Rename): {e.FullPath}");
            EnqueueFileEvent(e.FullPath, isDeletion: false);
        }
    }

    // internal (not private) so the WATCH tests can drive the debounce state
    // machine deterministically via InternalsVisibleTo — no 2-second timer waits.
    internal void EnqueueFileEvent(string path, bool isDeletion)
    {
        if (_disposed) return;

        // NF-08: metadata-editor temp/backup artifacts ("song.octave_tmp.mp3",
        // "song.octave_bak.mp3") carry real audio extensions, so they used to pass
        // the extension filter and trigger spurious scans/deletes of transient files.
        string fileName = Path.GetFileName(path);
        if (fileName.Contains(".octave_tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".octave_bak", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupportedExtension(ext))
        {
            return;
        }

        lock (_pendingGate)
        {
            if (_disposed) return;

            if (_pendingEvents.TryGetValue(path, out var existing))
            {
                existing.IsDeletion = isDeletion; // latest intent wins
                existing.Timer.Change(TimeSpan.FromMilliseconds(DebounceDelayMs), Timeout.InfiniteTimeSpan);
            }
            else
            {
                var pending = new PendingEvent { IsDeletion = isDeletion };
                // WATCH-09: exactly one Timer per pending path, created under the
                // lock — never inside a retryable dictionary factory.
                pending.Timer = new Timer(OnDebounceElapsed, path, TimeSpan.FromMilliseconds(DebounceDelayMs), Timeout.InfiniteTimeSpan);
                _pendingEvents[path] = pending;
            }
        }
    }

    // internal for tests: firing the debounce callback synchronously (the armed
    // Timer remains a no-op safety net — a claimed entry makes it return early).
    internal void OnDebounceElapsed(object? state)
    {
        if (state is not string path) return;

        // WATCH-03: claim the entry FIRST. From this moment any new event for the
        // path creates a fresh entry + timer, so coalesced changes during our work
        // can never be lost to a disposal race.
        PendingEvent? pending;
        lock (_pendingGate)
        {
            if (_disposed) return;
            if (!_pendingEvents.Remove(path, out pending)) return;
        }

        if (pending.IsDeletion)
        {
            // WATCH-01/02: cheap targeted removal instead of a full multi-root
            // reconciliation sweep for every single-file deletion.
            Debug.WriteLine("[LibraryWatcher] Targeted removal scheduled");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryScanner.RemoveStalePathAsync(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LibraryWatcher] Targeted removal failed for '{path}': {ex.Message}");
                }
            });
            DisposePendingTimer(pending);
            return;
        }

        // Verify file still exists
        if (!File.Exists(path))
        {
            DisposePendingTimer(pending);
            return;
        }

        // WATCH-07: probe with FileShare.Read — concurrent WRITERS must make this
        // open throw so a half-written file is never scanned. The old
        // FileShare.ReadWrite mode succeeded even while another process held the
        // file open for writing.
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            // WATCH-04: bounded retries, then drop until reconciliation sweeps it up.
            lock (_pendingGate)
            {
                if (_disposed) { DisposePendingTimer(pending); return; }

                pending.LockRetries++;
                if (pending.LockRetries <= MaxLockRetries && _pendingEvents.TryAdd(path, pending))
                {
                    // Re-arm only after winning the re-add: a filesystem event landing
                    // between our claim and here already created a NEWER entry for the
                    // path, and clobbering it would strand its timer.
                    pending.Timer.Change(TimeSpan.FromMilliseconds(DebounceDelayMs), Timeout.InfiniteTimeSpan);
                    Debug.WriteLine($"[LibraryWatcher] '{path}' still locked; retry {pending.LockRetries}/{MaxLockRetries}");
                }
                else if (pending.LockRetries > MaxLockRetries)
                {
                    Debug.WriteLine($"[LibraryWatcher] '{path}' stayed locked after {MaxLockRetries} retries; deferring to reconciliation.");
                    DisposePendingTimer(pending);
                }
                else
                {
                    // Lost the race to a fresher event; that entry owns the path now.
                    DisposePendingTimer(pending);
                }
            }
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibraryWatcher] Skipping '{path}': {ex.Message}");
            DisposePendingTimer(pending);
            return;
        }

        DisposePendingTimer(pending);

        // Execute incremental scan outside all locks
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

    private static void DisposePendingTimer(PendingEvent pending)
    {
        try { pending.Timer?.Dispose(); } catch { /* timer may already be dead */ }
    }

    private static bool IsWatchableAudioPath(string fullPath, string ext)
    {
        string fileName = Path.GetFileName(fullPath);
        if (fileName.Contains(".octave_tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".octave_bak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return IsSupportedExtension(ext);
    }

    private static bool IsSupportedExtension(string ext)
    {
        return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".aac" || ext == ".ogg" || ext == ".wav" || ext == ".wma" || ext == ".opus";
    }

    // WATCH-06: dispose under the lock, guarded against in-flight callbacks.
    public void Dispose()
    {
        List<FileSystemWatcher> watchersToDispose;
        List<PendingEvent> pendingToDispose;
        Timer? retryTimer;

        lock (_watchersLock)
        {
            if (_disposed) return;
            _disposed = true;
            watchersToDispose = new List<FileSystemWatcher>(_watchers);
            _watchers.Clear();

            lock (_pendingGate)
            {
                pendingToDispose = new List<PendingEvent>(_pendingEvents.Values);
                _pendingEvents.Clear();
                retryTimer = _watchRetryTimer;
                _watchRetryTimer = null;
            }
        }

        foreach (var watcher in watchersToDispose)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Dispose();
        }

        foreach (var pending in pendingToDispose)
        {
            DisposePendingTimer(pending);
        }

        try { retryTimer?.Dispose(); } catch { }
    }
}
