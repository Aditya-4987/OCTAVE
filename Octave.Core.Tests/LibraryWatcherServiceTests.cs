using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Services.Library;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Acceptance tests for the watcher batch (CODEBASE_AUDIT §15 Batch 9):
/// WATCH-08 offline roots never join reconciliation, WATCH-01/02 deletions
/// take the targeted-removal path, WATCH-04/07 a mid-write file fails the
/// FileShare.Read probe and is deferred with bounded retries, WATCH-09
/// coalescing keeps exactly one pending entry per path, and WATCH-06
/// dispose guards later events.
///
/// The debounce state machine is driven synchronously through internal members
/// (InternalsVisibleTo): EnqueueFileEvent registers the pending entry, then
/// OnDebounceElapsed fires it without waiting out the real 2-second timer.
/// </summary>
public class LibraryWatcherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ILibraryScanner> _scannerMock;
    private readonly LibraryWatcherService _watcher;

    public LibraryWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_WatchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _scannerMock = new Mock<ILibraryScanner>();
        // Startup reconciliation pass fires after 8s against the mock — harmless.
        _watcher = new LibraryWatcherService(_scannerMock.Object);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private string SupportedPath(string name = "song.mp3") => Path.Combine(_tempDir, name);

    private static Task CompletedTask() => Task.CompletedTask;

    // =================================================================
    // WATCH-08: an absent root is NOT registered with the scanner.
    // =================================================================

    [Fact]
    public void AddMonitoredPath_AbsentRoot_IsDeferredFromScannerRegistration()
    {
        string missingRoot = Path.Combine(_tempDir, "not_plugged_in_yet");

        _watcher.AddMonitoredPath(missingRoot);

        // Pre-WATCH-08 this registered the path for deletion-reconciliation while
        // unreachable — the SCAN-08 mass-delete amplifier. It must not.
        _scannerMock.Verify(s => s.AddMonitoredPath(missingRoot), Times.Never);
    }

    [Fact]
    public void AddMonitoredPath_PresentRoot_RegistersWithScanner()
    {
        string presentRoot = Path.Combine(_tempDir, "real_root");
        Directory.CreateDirectory(presentRoot);

        _watcher.AddMonitoredPath(presentRoot);

        _scannerMock.Verify(s => s.AddMonitoredPath(presentRoot), Times.Once);
    }

    // =================================================================
    // WATCH-01/02: single-file deletions use RemoveStalePathAsync.
    // =================================================================

    [Fact]
    public async Task DeletionDebounce_UsesTargetedRemoval_NotFullReconciliation()
    {
        string deletedFile = SupportedPath("deleted_song.mp3");
        var removalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scannerMock.Setup(s => s.RemoveStalePathAsync(deletedFile))
            .Returns(() => { removalTcs.TrySetResult(); return CompletedTask(); });

        _watcher.EnqueueFileEvent(deletedFile, isDeletion: true);
        Assert.Equal(1, _watcher.PendingEventCount);

        _watcher.OnDebounceElapsed(deletedFile);

        await removalTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _scannerMock.Verify(s => s.RemoveStalePathAsync(deletedFile), Times.Once);
        _scannerMock.Verify(s => s.RequestFullReconciliationAsync(), Times.Never);
    }

    // =================================================================
    // WATCH-04/07: a file being written fails the FileShare.Read probe,
    // is retried a bounded number of times, then dropped until
    // reconciliation picks it up — and scans cleanly once writable.
    // =================================================================

    [Fact]
    public async Task MidWriteFile_FailsLockProbe_IsDeferredAndScannedOnceWritable()
    {
        string busyFile = SupportedPath("midwrite.mp3");
        await File.WriteAllBytesAsync(busyFile, new byte[] { 0xFF, 0xFB, 0x90, 0x64 });

        // Simulate an active writer: exclusive share denies our FileShare.Read probe.
        using (var writerHold = File.Open(busyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Exactly 1 initial probe + 5 retries (WATCH-04's ~5 cap): on the sixth
            // failure the pending entry must be DROPPED, not re-armed forever.
            // (No re-enqueue past this point or a fresh entry would restart the count.)
            for (int i = 0; i < 6; i++)
            {
                _watcher.EnqueueFileEvent(busyFile, isDeletion: false);
                _watcher.OnDebounceElapsed(busyFile);
            }

            _scannerMock.Verify(s => s.ScanFileAsync(busyFile), Times.Never);
            Assert.Equal(0, _watcher.PendingEventCount); // dropped, not retried forever
        }

        // Once writable again, a fresh event scans it exactly once.
        var scannedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scannerMock.Setup(s => s.ScanFileAsync(busyFile))
            .Returns(() => { scannedTcs.TrySetResult(); return CompletedTask(); });

        _watcher.EnqueueFileEvent(busyFile, isDeletion: false);
        _watcher.OnDebounceElapsed(busyFile);

        await scannedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _scannerMock.Verify(s => s.ScanFileAsync(busyFile), Times.Once);
    }

    // =================================================================
    // WATCH-09: rapid events for one path collapse to a single entry.
    // =================================================================

    [Fact]
    public void RapidEventsForSamePath_CoalesceToSinglePendingEntry()
    {
        string path = SupportedPath("storm.mp3");

        _watcher.EnqueueFileEvent(path, isDeletion: false);
        _watcher.EnqueueFileEvent(path, isDeletion: true);
        _watcher.EnqueueFileEvent(path, isDeletion: false);

        Assert.Equal(1, _watcher.PendingEventCount);
    }

    [Fact]
    public async Task EventArrivingAfterClaim_IsNotLost()
    {
        // WATCH-03's core property: an event landing while the callback is already
        // processing creates a FRESH entry that survives the first callback's work.
        string path = SupportedPath("racy.mp3");
        await File.WriteAllBytesAsync(path, new byte[] { 0xFF, 0xFB, 0x90, 0x64 });
        var scanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scannerMock.Setup(s => s.ScanFileAsync(path))
            .Returns(() => { scanGate.TrySetResult(); return CompletedTask(); });

        _watcher.EnqueueFileEvent(path, isDeletion: false);
        // Fire #1 claims the entry; the callback now sits outside the lock doing its probe.
        _watcher.OnDebounceElapsed(path);
        await scanGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The event that arrived during processing must still be pending afterwards.
        _watcher.EnqueueFileEvent(path, isDeletion: false);
        Assert.Equal(1, _watcher.PendingEventCount);
    }

    // =================================================================
    // WATCH-06: Dispose stops all further activity.
    // =================================================================

    [Fact]
    public void Dispose_ClearsPendingState_AndIgnoresLaterEvents()
    {
        string path = SupportedPath("disposed.mp3");
        _watcher.EnqueueFileEvent(path, isDeletion: false);

        _watcher.Dispose();
        Assert.Equal(0, _watcher.PendingEventCount);

        _watcher.EnqueueFileEvent(path, isDeletion: false);
        Assert.Equal(0, _watcher.PendingEventCount);

        _scannerMock.Verify(s => s.ScanFileAsync(It.IsAny<string>()), Times.Never);
        _scannerMock.Verify(s => s.RemoveStalePathAsync(It.IsAny<string>()), Times.Never);
    }
}
