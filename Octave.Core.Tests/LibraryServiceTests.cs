using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Database;
using Octave.Core.Services.Library;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// TEST-14: LibraryService's folder-management orchestration (DB row + scanner
/// registration + watcher registration + scan trigger + deletion cascade) had no
/// direct coverage — the facade was assumed correct because its dependencies were
/// tested individually. These tests drive the REAL SqliteDbContext with mocked
/// scanner/watcher so the wiring itself is pinned.
/// </summary>
public class LibraryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteDbContext _dbContext;
    private readonly Mock<ILibraryScanner> _scannerMock = new();
    private readonly Mock<ILibraryWatcherService> _watcherMock = new();
    private readonly LibraryService _service;

    public LibraryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Octave_LibSvcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbContext = new SqliteDbContext(Path.Combine(_tempDir, "libsvc.db"));
        _dbContext.InitializeAsync().GetAwaiter().GetResult();
        _service = new LibraryService(_dbContext, _scannerMock.Object, _watcherMock.Object);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ToggleFavoriteAsync_FlipsStateAndRaisesEventEachTime()
    {
        var track = new Track("fav1", "Fav Song", "ar1", "Artist", "al1", "Album", 180, "http://test/1.mp3", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        int favoritesEvents = 0;
        _service.FavoritesChanged += (_, _) => favoritesEvents++;

        Assert.False(await _service.IsFavoriteAsync("fav1"));

        Assert.True(await _service.ToggleFavoriteAsync("fav1"));   // becomes favorite
        Assert.True(await _service.IsFavoriteAsync("fav1"));
        Assert.Equal(1, favoritesEvents);

        Assert.False(await _service.ToggleFavoriteAsync("fav1"));  // reverts
        Assert.False(await _service.IsFavoriteAsync("fav1"));
        Assert.Equal(2, favoritesEvents);
    }

    [Fact]
    public async Task AddFolderAsync_PersistsRow_RegistersScannerAndWatcher_TriggersScan()
    {
        string folder = Path.Combine(_tempDir, "watched");
        Directory.CreateDirectory(folder);

        await _service.AddFolderAsync(folder, CancellationToken.None);

        Assert.Contains(folder, await _dbContext.GetMonitoredFoldersAsync(), StringComparer.OrdinalIgnoreCase);
        _scannerMock.Verify(s => s.AddMonitoredPath(folder), Times.Once);
        _scannerMock.Verify(s => s.ScanAsync(folder, It.IsAny<CancellationToken>()), Times.Once);
        _watcherMock.Verify(w => w.AddMonitoredPath(folder), Times.Once);
    }

    [Fact]
    public async Task AddFolderAsync_BlankPath_IsFullyIgnored()
    {
        await _service.AddFolderAsync("", CancellationToken.None);
        await _service.AddFolderAsync("   ", CancellationToken.None);

        Assert.Empty(await _dbContext.GetMonitoredFoldersAsync());
        _scannerMock.Verify(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _watcherMock.Verify(w => w.AddMonitoredPath(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveFolderAsync_RemovesRow_DeletesOnlyTracksUnderPath_RaisesLibraryUpdated()
    {
        string folder = Path.Combine(_tempDir, "removal");
        Directory.CreateDirectory(folder);
        await _dbContext.AddMonitoredFolderAsync(folder);

        var underRoot = new Track("under1", "Under", "ar1", "Artist", "al1", "Album", 180,
            Path.Combine(folder, "a.mp3"), 1, 2024, DateTime.UtcNow);
        var elsewhere = new Track("elsewhere1", "Elsewhere", "ar2", "Artist2", "al2", "Album2", 180,
            Path.Combine(_tempDir, "keep.mp3"), 2, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(underRoot);
        await _dbContext.UpsertTrackAsync(elsewhere);

        int updated = 0;
        _service.LibraryUpdated += (_, _) => updated++;

        await _service.RemoveFolderAsync(folder);

        Assert.DoesNotContain(folder, await _dbContext.GetMonitoredFoldersAsync(), StringComparer.OrdinalIgnoreCase);
        var remaining = await _dbContext.GetAllTracksAsync();
        Assert.DoesNotContain(remaining, t => t.Id == "under1");       // cascade reached it
        Assert.Contains(remaining, t => t.Id == "elsewhere1");         // outside the folder — untouched
        _scannerMock.Verify(s => s.RemoveMonitoredPath(folder), Times.Once);
        _watcherMock.Verify(w => w.RemoveMonitoredPath(folder), Times.Once);
        Assert.Equal(1, updated);
    }

    [Fact]
    public void ScannerLibraryChanged_SurfacesAsServiceLibraryUpdated()
    {
        int updated = 0;
        _service.LibraryUpdated += (_, _) => updated++;

        _scannerMock.Raise(s => s.LibraryChanged += null, EventArgs.Empty);

        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task RescanAllAsync_ScansEachRegisteredFolder_AndStopsOnCancellation()
    {
        string f1 = Path.Combine(_tempDir, "r1");
        string f2 = Path.Combine(_tempDir, "r2");
        Directory.CreateDirectory(f1);
        Directory.CreateDirectory(f2);
        await _dbContext.AddMonitoredFolderAsync(f1);
        await _dbContext.AddMonitoredFolderAsync(f2);

        await _service.RescanAllAsync(CancellationToken.None);

        _scannerMock.Verify(s => s.ScanAsync(f1, It.IsAny<CancellationToken>()), Times.Once);
        _scannerMock.Verify(s => s.ScanAsync(f2, It.IsAny<CancellationToken>()), Times.Once);

        // A pre-cancelled token aborts before any additional scan is issued.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RescanAllAsync(cts.Token));

        _scannerMock.Verify(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ClearDatabaseAsync_UnregistersMonitoredPaths_WipesDb_RaisesEvents()
    {
        string f1 = Path.Combine(_tempDir, "clear1");
        string f2 = Path.Combine(_tempDir, "clear2");
        Directory.CreateDirectory(f1);
        Directory.CreateDirectory(f2);
        await _dbContext.AddMonitoredFolderAsync(f1);
        await _dbContext.AddMonitoredFolderAsync(f2);

        var track = new Track("tr_libclear", "Track", "ar1", "Artist", "al1", "Album", 180, Path.Combine(f1, "song.mp3"), 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);
        await _dbContext.AddFavoriteAsync(track.Id);

        int libraryUpdatedEvents = 0;
        int favoritesChangedEvents = 0;
        _service.LibraryUpdated += (_, _) => libraryUpdatedEvents++;
        _service.FavoritesChanged += (_, _) => favoritesChangedEvents++;

        await _service.ClearDatabaseAsync(preserveSettings: false);

        // Verify scanner & watcher unregistrations
        _scannerMock.Verify(s => s.RemoveMonitoredPath(f1), Times.Once);
        _scannerMock.Verify(s => s.RemoveMonitoredPath(f2), Times.Once);
        _watcherMock.Verify(w => w.RemoveMonitoredPath(f1), Times.Once);
        _watcherMock.Verify(w => w.RemoveMonitoredPath(f2), Times.Once);

        // Verify DB was cleared
        Assert.Empty(await _dbContext.GetAllTracksAsync());
        Assert.Empty(await _dbContext.GetMonitoredFoldersAsync());
        Assert.Empty(await _dbContext.GetFavoritesAsync());

        // Verify events were fired
        Assert.Equal(1, libraryUpdatedEvents);
        Assert.Equal(1, favoritesChangedEvents);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_TogglesStateAtomically_AndRaisesFavoritesChanged()
    {
        var track = new Track("tr_fav_toggle", "Fav Track", "ar1", "Artist", "al1", "Album", 180, "http://test/fav.mp3", 1, 2024, DateTime.UtcNow);
        await _dbContext.UpsertTrackAsync(track);

        int events = 0;
        _service.FavoritesChanged += (_, _) => events++;

        // Initial state: not favorite -> toggle -> becomes favorite (returns true)
        bool state1 = await _service.ToggleFavoriteAsync(track.Id);
        Assert.True(state1);
        Assert.True(await _service.IsFavoriteAsync(track.Id));
        Assert.Equal(1, events);

        // Second toggle -> becomes not favorite (returns false)
        bool state2 = await _service.ToggleFavoriteAsync(track.Id);
        Assert.False(state2);
        Assert.False(await _service.IsFavoriteAsync(track.Id));
        Assert.Equal(2, events);
    }
}
