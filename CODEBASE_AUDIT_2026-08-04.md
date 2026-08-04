# OCTAVE Codebase Audit

**Audit date:** 4 August 2026  
**Scope:** Read-only source review of the OCTAVE desktop application and core library.  
**Reviewed:** 49 C# files and 13 XAML files.  
**Changes made during audit:** None, apart from creating this report.

## Purpose

This document records issues observed during a static code review. It is intended to give an implementation developer the context needed to investigate, validate, and prioritize the problems. It deliberately does not prescribe implementation approaches.

## Severity scale

| Severity | Meaning |
| --- | --- |
| P0 | High risk of irreversible user-data loss or a release-blocking failure. |
| P1 | Serious correctness or reliability issue affecting normal user workflows. |
| P2 | Noticeable functional inconsistency, persistence defect, or reliability gap. |
| P3 | Quality, maintainability, or coverage concern that should be scheduled. |

---

## AUD-001 — Bulk duplicate deletion permanently deletes user files

**Severity:** P0  
**Area:** Settings / duplicate management  
**Affected code:** `Octave.Desktop/ViewModels/ShellViewModel.cs`, `DeleteAllDuplicates()` (around lines 462–482)

### Observed behavior

The bulk **Delete All Duplicates** command loops through every detected duplicate group, retains the first item in each group, calls `File.Delete` for every other file, and then removes the corresponding track from the library database.

The operation has no confirmation dialog, does not move files to the Recycle Bin, does not present the exact list of files selected for deletion, and does not provide an undo path.

### User impact

- A single click can permanently remove a large number of music files.
- Users cannot recover the deleted files through the Windows Recycle Bin.
- The action may delete a user’s preferred copy because the retained item is simply the first item in the group, not an explicitly chosen version.
- A partially failed batch can leave a mixed state: some files removed, some database rows removed, and other files left untouched.

### Conditions to reproduce

1. Add two or more tracks that the duplicate detector groups together.
2. Open **Settings** and run **Scan for Duplicates**.
3. Select **Delete All Duplicates**.
4. Observe that all but the first track in every group are directly deleted from disk.

### Related behavior

The single-track duplicate-delete path in `SettingsPage.xaml.cs` instead asks for confirmation and uses the Recycle Bin. The bulk and single-item flows therefore have materially different data-loss behavior.

---

## AUD-002 — Duplicate grouping is not reliable enough to be used as a destructive selector

**Severity:** P1  
**Area:** Database / duplicate management  
**Affected code:** `Octave.Core/Services/Database/SqliteDbContext.cs`, `GetDuplicatesAsync()` (around lines 601–650)

### Observed behavior

Duplicate candidates are initially grouped by normalized track title and artist name. Each candidate group is then divided by physical file size. Any subgroup containing more than one track is reported as a duplicate group.

The group ordering derives from the database query order and dictionary insertion order. The first item is subsequently treated as the version to retain by the bulk delete command.

### User impact

- Different recordings can be classified as duplicates when their title, artist, and byte length happen to match.
- Actual duplicates can be missed when metadata differs, such as a title suffix, artist spelling variation, whitespace, remaster annotation, or embedded tag change.
- Different encodings and editions are not evaluated for audio identity; users may regard them as distinct versions even when names are similar.
- The version retained during bulk removal is not necessarily the highest quality, preferred format, newest file, or user-selected copy.

### Conditions to reproduce

1. Add recordings with matching title and artist metadata and equal file sizes.
2. Run duplicate scanning.
3. Observe that they appear as a deletion group regardless of recording identity, quality, or user preference.

---

## AUD-003 — Persisted player state writes can complete out of order

**Severity:** P1  
**Area:** Playback queue / startup resume  
**Affected code:** `Octave.Core/Services/Playback/QueueService.cs`, `PersistStateUnlocked()` and `MaybeSaveProgress()` (around lines 125–171)

### Observed behavior

Queue state and progress are written using independent fire-and-forget `Task.Run` operations. The operations wait on a shared semaphore before writing to SQLite, but they are created asynchronously and have no generation/version validation.

Structural queue changes write the entire queue and player snapshot. Position updates write player progress independently. A task that captured older state can acquire the semaphore after a task that captured newer state.

### User impact

- On the next application start, the restored queue can differ from the queue the user last saw.
- A cleared queue can reappear after restart.
- An older current-track index or seek position can overwrite a newer one.
- Saved shuffle/repeat/volume values may not represent the user’s last action.

### Conditions to reproduce

1. Start playback so periodic progress persistence is active.
2. Quickly perform several queue operations, such as enqueue, reorder, clear, seek, or change repeat mode.
3. Close the application soon after the changes.
4. Reopen it and inspect the restored queue and playback position.

### Technical notes

`SavePlayerStateAsync()` replaces the full `SavedQueue` table, while `UpdatePlaybackProgressAsync()` updates the `PlayerState` row only. Both paths can be queued concurrently from the service.

---

## AUD-004 — Stale track-end events can advance a newly started track

**Severity:** P1  
**Area:** Playback queue / end-of-track handling  
**Affected code:** `Octave.Core/Services/Playback/QueueService.cs`, `HandleTrackEndedAsync()` (around lines 653–706)

### Observed behavior

When the audio engine raises `TrackEnded`, the queue service captures the current queue index and track ID, asynchronously writes playback history, then checks whether the current queue index still equals the captured index.

The final validity check compares only the numeric index. It does not verify that the current queue item, track identity, playback sequence, or stream lifecycle is the same event that originally ended.

### User impact

- A late end event can skip a track that the user deliberately restarted.
- A user can press play on the same queue position while the history write is pending and then be unexpectedly advanced to another track.
- Repeat-one behavior can be interrupted by an outdated end event.
- Playback history can be recorded for a track/end event that no longer represents the active playback session.

### Conditions to reproduce

1. Let a track reach its end, creating the asynchronous end-of-track workflow.
2. Before the history write and continuation finish, restart the same item or begin another playback operation that leaves the same queue index active.
3. Observe a possible unexpected auto-advance after the later operation.

---

## AUD-005 — Folder watchers may be duplicated and missed watcher errors are not recovered

**Severity:** P1  
**Area:** Library monitoring  
**Affected code:** `Octave.Core/Services/Library/LibraryWatcherService.cs`, `AddMonitoredPath()` (around lines 43–73)

### Observed behavior

`AddMonitoredPath()` creates a new `FileSystemWatcher` whenever called with an existing directory. It does not check whether the same path is already being watched. The corresponding scanner registration and watcher list can therefore contain duplicates.

The watcher subscribes to Created, Changed, Deleted, and Renamed events, but not to the `Error` event. A FileSystemWatcher buffer overflow or underlying watcher failure is therefore not surfaced through a recovery path.

### User impact

- File operations may trigger duplicate scans and duplicate database/library refresh work.
- Large library changes can cause increased CPU, disk I/O, and UI refresh activity.
- If the watcher loses events, the library can become stale until a later full reconciliation occurs.
- Removing a folder may not remove every duplicate watcher if paths are represented differently (for example, case differences or normalization differences).

### Conditions to reproduce

1. Add the same music folder more than once through folder-management flows.
2. Modify a supported audio file in that folder.
3. Observe repeated watcher activity or repeated incremental scans.

---

## AUD-006 — Reconciliation enumerates a mutable monitored-path collection outside its lock

**Severity:** P1  
**Area:** Library reconciliation  
**Affected code:** `Octave.Core/Services/Library/LocalLibraryScanner.cs`, `MonitoredPaths` and `RequestFullReconciliationAsync()` (around lines 31–37 and 579)

### Observed behavior

`MonitoredPaths` returns a read-only wrapper over the underlying mutable `List<string>`. The wrapper prevents callers from editing through that reference, but it still reflects changes made by `AddMonitoredPath()` or `RemoveMonitoredPath()`.

`RequestFullReconciliationAsync()` iterates this wrapper after the lock has been released. A folder add or remove that occurs during enumeration can alter the underlying list.

### User impact

- Full reconciliation can terminate with a collection-modified exception.
- A newly added or removed folder can cause a reconciliation pass to process an incomplete or inconsistent folder set.
- Startup recovery and watcher-triggered reconciliation can become unreliable while users manage folders.

### Conditions to reproduce

1. Start a full reconciliation, including the delayed watcher startup reconciliation.
2. Add or remove a monitored folder while the reconciliation is enumerating monitored paths.
3. Observe the reconciliation task for an exception or incomplete processing.

---

## AUD-007 — Shuffle mode does not preserve the pre-shuffle order across restart

**Severity:** P2  
**Area:** Playback queue / session restoration  
**Affected code:** `Octave.Core/Services/Playback/QueueService.cs`, `RestoreAsync()` (around lines 192–205) and `SetShuffle()` (around lines 417–475)

### Observed behavior

The persisted session stores only the active queue order. When the session is restored, both `_activeQueue` and `_unshuffledQueue` are rebuilt in that same stored order, then the saved shuffle flag is applied.

While shuffle is enabled during the original session, `_activeQueue` represents the shuffled order and `_unshuffledQueue` represents the original order. That distinction is not retained in the persisted data.

### User impact

- After restarting the application with shuffle enabled, turning shuffle off does not return the queue to the original ordering.
- The queue appears to have a different natural order after restart.
- Queue reordering while shuffled can produce persistence results that differ from the prior session’s intent.

### Conditions to reproduce

1. Add several tracks in a known order.
2. Enable shuffle and close the application.
3. Reopen the application and disable shuffle.
4. Compare the resulting queue order with the pre-shuffle order.

---

## AUD-008 — Volume-only changes are not reliably persisted

**Severity:** P2  
**Area:** Playback controls / session restoration  
**Affected code:** `Octave.Desktop/ViewModels/ShellViewModel.cs`, `Volume` property (around lines 82–94); `Octave.Core/Services/Playback/QueueService.cs`, persistence flow (around lines 125–171)

### Observed behavior

The UI volume property writes directly to `IAudioPlayerService.Volume`. It does not call the queue service’s volume operation or otherwise trigger a player-state persistence event.

The queue persistence path records volume on structural queue changes and periodic playback-progress updates. A user who changes volume while playback is paused or stopped and then exits may not have that final volume recorded.

### User impact

- The application can restart at an older volume level rather than the level the user last selected.
- The visible slider state and restored state can be inconsistent depending on whether playback was active after the volume change.

### Conditions to reproduce

1. Pause playback or stop with a queue loaded.
2. Change the volume using the main playback bar.
3. Exit before another queue transition or periodic progress save.
4. Reopen the application and inspect the restored volume.

---

## AUD-009 — Playlist schema forbids adding the same track more than once

**Severity:** P2  
**Area:** Playlist management  
**Affected code:** `Octave.Core/Services/Database/SqliteDbContext.cs`, `PlaylistTracks` schema (around line 87) and `AddTrackToPlaylistAsync()` (around lines 899–909)

### Observed behavior

`PlaylistTracks` uses `(PlaylistId, TrackId)` as its primary key. Adding a track uses `INSERT OR IGNORE`, so an attempt to add a track already present in the playlist completes silently without creating another entry.

The playback queue uses independent `QueueItem` IDs and supports repeated appearances of the same track, whereas playlists do not.

### User impact

- Users cannot intentionally place the same song more than once in a playlist.
- The UI does not explain that a repeated add was ignored.
- Playlist behavior differs from queue behavior in a way users may not expect.

### Conditions to reproduce

1. Add a track to a playlist.
2. Use the context menu to add the same track to that playlist again.
3. Observe that the playlist remains unchanged without feedback.

---

## AUD-010 — Duplicate file moves have collision and consistency risks

**Severity:** P2  
**Area:** Settings / duplicate management  
**Affected code:** `Octave.Desktop/Views/SettingsPage.xaml.cs`, `MoveDuplicate_Click()` (around lines 65–94)

### Observed behavior

The move action constructs a destination using the chosen folder plus the original file name, then invokes `File.Move`. It does not present a collision decision when a file with the same name already exists at the destination. After a successful move, it removes the original track from the database.

### User impact

- A name collision causes the move to fail and only emits a debug-log message.
- If file movement succeeds but later database handling fails, disk and library state can diverge.
- Moving into another monitored folder creates a period where the file may be absent from the library until watcher/reconciliation processing occurs.

### Conditions to reproduce

1. Choose a duplicate track to move.
2. Select a destination containing a file with the same name.
3. Observe the operation failure and absence of user-facing feedback.

---

## AUD-011 — Automated test coverage is absent

**Severity:** P1 release-readiness concern  
**Area:** Repository-wide

### Observed behavior

The solution contains the `Octave.Core` and `Octave.Desktop` projects, plus a `test_fx` console project. No project references a standard .NET test SDK or a test framework, and no automated unit, integration, persistence, or UI test suite was identified.

### User impact

- Playback queue concurrency, restart restoration, destructive library actions, database migrations, and file-watcher behavior have no automated regression safety net.
- Changes in these areas can reintroduce previously resolved defects without detection before release.
- Current functionality cannot be objectively verified as bug-free through repeatable test execution.

### High-risk untested workflows

- Queue persistence when rapid playback and queue actions overlap.
- Playback state restoration after application restart.
- File delete, move, and duplicate-management actions.
- Scanner reconciliation with disconnected drives, inaccessible directories, and cancellation.
- FileSystemWatcher overflow and rename/change event sequences.
- SQLite schema migration from earlier database versions.

---

## AUD-012 — Existing build output reports a high-severity SQLite native dependency vulnerability

**Severity:** P1 release-readiness concern  
**Area:** Dependencies  
**Evidence:** Existing repository `build.log`; direct package reference in `Octave.Core/Octave.Core.csproj`

### Observed behavior

The repository’s existing build log reports NuGet warning `NU1903` for `SQLitePCLRaw.lib.e_sqlite3` version `2.1.11`, identified as having a known high-severity vulnerability (`GHSA-2m69-gcr7-jv3q`). The warning is reported for both the core and desktop projects.

### User impact

- A release may ship with a known-vulnerable native SQLite dependency.
- Security scanning and distribution pipelines may fail or require an explicit exception.
- The exact resolved dependency graph must be verified against the current package restore because the existing build log predates the current uncommitted working-tree changes.

---

## Audit limitations and validation status

- This was a static review; the application was not launched and no files were modified during the audit other than this document.
- The workspace already contained uncommitted source, IDE, and build-artifact changes before the review. They were not altered.
- `git diff --check` reported no whitespace errors in the source modifications examined.
- The repository contains a historical successful build log, but it predates current uncommitted source changes. It is not evidence that the current working tree builds or passes runtime validation.
- No automated test suite was available to execute.

## Suggested triage order

This is a prioritization statement, not an implementation prescription:

1. AUD-001 and AUD-002: destructive duplicate workflows.
2. AUD-003 and AUD-004: playback/session correctness.
3. AUD-005 and AUD-006: library-monitoring reliability.
4. AUD-007 through AUD-010: state consistency and user-facing edge cases.
5. AUD-011 and AUD-012: release validation and dependency governance.
