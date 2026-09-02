using Microsoft.Data.Sqlite;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Services.Database;

public class SqliteDbContext : ILyricsRepository
{
    private readonly string _connectionString;

    public SqliteDbContext(string connectionStringOrPath)
    {
        if (string.IsNullOrWhiteSpace(connectionStringOrPath))
            throw new ArgumentNullException(nameof(connectionStringOrPath));

        _connectionString = connectionStringOrPath.Contains('=')
            ? connectionStringOrPath
            : $"Data Source={connectionStringOrPath}";
    }

    // Async open + PRAGMA batch (DB-02): every caller of this context is async, so
    // opening connections synchronously only blocked the calling thread. The
    // try/catch guarantees a failed Open/PRAGMA never leaks the connection (DB-07).
    private async Task<SqliteConnection> CreateConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA foreign_keys = ON;" +
                // DB-01: WAL allows many readers + one writer, but a second concurrent
                // writer (scanner batch-commit vs UI player-state write) fails with
                // SQLITE_BUSY immediately unless it waits. busy_timeout makes writers
                // queue politely instead of throwing — the single highest-leverage
                // fix in this layer.
                "PRAGMA busy_timeout = 5000;" +
                "PRAGMA synchronous = NORMAL;";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync()
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();

        // WAL mode is persistent in the SQLite DB header; set once at init.
        cmd.CommandText = "PRAGMA journal_mode = WAL;";
        await cmd.ExecuteNonQueryAsync();
        
        // Fully restored STRICT mode DDL exactly matching the frozen architecture
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Artists (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                Bio TEXT,
                ArtworkUrl TEXT
            ) STRICT;

            CREATE TABLE IF NOT EXISTS Albums (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL COLLATE NOCASE,
                ArtistId TEXT NOT NULL,
                ArtistName TEXT NOT NULL,
                Year INTEGER NOT NULL,
                ArtworkUrl TEXT,
                FOREIGN KEY(ArtistId) REFERENCES Artists(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS Tracks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL COLLATE NOCASE,
                ArtistId TEXT NOT NULL,
                ArtistName TEXT NOT NULL,
                AlbumId TEXT NOT NULL,
                AlbumTitle TEXT NOT NULL,
                DurationSeconds REAL NOT NULL,
                SourceUri TEXT NOT NULL,
                TrackNumber INTEGER NOT NULL DEFAULT 1,
                Year INTEGER NOT NULL,
                DateAdded INTEGER NOT NULL, -- Stored explicitly as Unix Epoch Seconds
                Genre TEXT NOT NULL DEFAULT '',
                ReplayGain REAL NOT NULL DEFAULT 0.0,
                Disc INTEGER NOT NULL DEFAULT 1, -- SCAN-11: disc number within a multi-disc album
                LastModified INTEGER NOT NULL DEFAULT 0,
                FileSize INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(ArtistId) REFERENCES Artists(Id) ON DELETE CASCADE,
                FOREIGN KEY(AlbumId) REFERENCES Albums(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS Playlists (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL COLLATE NOCASE,
                Description TEXT,
                CreatedAt INTEGER NOT NULL -- Stored explicitly as Unix Epoch Seconds
            ) STRICT;

            CREATE TABLE IF NOT EXISTS PlaylistTracks (
                Id TEXT PRIMARY KEY,
                PlaylistId TEXT NOT NULL,
                TrackId TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                FOREIGN KEY(PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS PlaybackHistory (
                Id TEXT PRIMARY KEY,
                TrackId TEXT NOT NULL,
                PlayedAt INTEGER NOT NULL, -- Stored explicitly as Unix Epoch Seconds
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT;

            -- Single-row snapshot of the player so the queue can be resumed after restart.
            CREATE TABLE IF NOT EXISTS PlayerState (
                Id INTEGER PRIMARY KEY CHECK(Id = 1),
                CurrentIndex INTEGER NOT NULL,
                PositionSeconds REAL NOT NULL,
                Volume REAL NOT NULL,
                IsShuffle INTEGER NOT NULL CHECK(IsShuffle IN (0, 1)),
                RepeatMode INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            ) STRICT;

            -- The persisted active queue order (track ids only). Rebuilt from Tracks on load.
            CREATE TABLE IF NOT EXISTS SavedQueue (
                SortOrder INTEGER PRIMARY KEY,
                TrackId TEXT NOT NULL
            ) STRICT;

            -- The persisted original unshuffled queue order.
            CREATE TABLE IF NOT EXISTS SavedUnshuffledQueue (
                SortOrder INTEGER PRIMARY KEY,
                TrackId TEXT NOT NULL
            ) STRICT;

            -- User-managed set of library folders to scan and watch.
            CREATE TABLE IF NOT EXISTS MonitoredFolders (
                Path TEXT PRIMARY KEY
            ) STRICT;

            -- Favorited tracks.
            CREATE TABLE IF NOT EXISTS Favorites (
                TrackId TEXT PRIMARY KEY,
                AddedAt INTEGER NOT NULL,
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS LyricsCache (
                TrackId TEXT PRIMARY KEY,
                PlainLyrics TEXT,
                SyncedLyrics TEXT,
                HasPlainLyrics INTEGER NOT NULL CHECK(HasPlainLyrics IN (0, 1)),
                HasSyncedLyrics INTEGER NOT NULL CHECK(HasSyncedLyrics IN (0, 1)),
                IsNotFound INTEGER NOT NULL DEFAULT 0 CHECK(IsNotFound IN (0, 1)),
                CachedAt INTEGER NOT NULL,
                LastCheckedAt INTEGER NOT NULL,
                Source TEXT,
                LrclibRecordId INTEGER,
                SyncedSource TEXT,
                StaticSource TEXT,
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER PRIMARY KEY,
                AppliedAt INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_tracks_artist ON Tracks(ArtistId);
            CREATE INDEX IF NOT EXISTS idx_tracks_album ON Tracks(AlbumId);
            CREATE INDEX IF NOT EXISTS idx_tracks_title ON Tracks(Title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_tracks_sourceuri ON Tracks(SourceUri);
            CREATE INDEX IF NOT EXISTS idx_albums_title ON Albums(Title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_artists_name ON Artists(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_playlisttracks_playlist ON PlaylistTracks(PlaylistId, SortOrder);
            CREATE INDEX IF NOT EXISTS idx_history_time ON PlaybackHistory(PlayedAt DESC);

            CREATE VIRTUAL TABLE IF NOT EXISTS TracksFts USING fts5(
                TrackId UNINDEXED,
                Title,
                ArtistName,
                AlbumTitle,
                tokenize = 'unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS trg_tracks_fts_insert AFTER INSERT ON Tracks BEGIN
                INSERT INTO TracksFts(TrackId, Title, ArtistName, AlbumTitle)
                VALUES (new.Id, new.Title, new.ArtistName, new.AlbumTitle);
            END;

            CREATE TRIGGER IF NOT EXISTS trg_tracks_fts_delete AFTER DELETE ON Tracks BEGIN
                DELETE FROM TracksFts WHERE TrackId = old.Id;
            END;

            CREATE TRIGGER IF NOT EXISTS trg_tracks_fts_update AFTER UPDATE ON Tracks BEGIN
                DELETE FROM TracksFts WHERE TrackId = old.Id;
                INSERT INTO TracksFts(TrackId, Title, ArtistName, AlbumTitle)
                VALUES (new.Id, new.Title, new.ArtistName, new.AlbumTitle);
            END;";

        await cmd.ExecuteNonQueryAsync();

        // Schema migrations: Genre, ReplayGain, Disc, LyricsCache columns
        await TryAddColumnAsync(conn, "Tracks", "Genre", "TEXT NOT NULL DEFAULT ''");
        await TryAddColumnAsync(conn, "Tracks", "ReplayGain", "REAL NOT NULL DEFAULT 0.0");
        await TryAddColumnAsync(conn, "Tracks", "Disc", "INTEGER NOT NULL DEFAULT 1");
        await TryAddColumnAsync(conn, "LyricsCache", "LastCheckedAt", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "LyricsCache", "Source", "TEXT");
        await TryAddColumnAsync(conn, "LyricsCache", "LrclibRecordId", "INTEGER");
        await TryAddColumnAsync(conn, "LyricsCache", "SyncedSource", "TEXT");
        await TryAddColumnAsync(conn, "LyricsCache", "StaticSource", "TEXT");

        using (var vCmd = conn.CreateCommand())
        {
            vCmd.CommandText = "INSERT OR IGNORE INTO SchemaVersion (Version, AppliedAt) VALUES (1, @now);";
            vCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            await vCmd.ExecuteNonQueryAsync();
        }

        // Backfill TracksFts if unpopulated
        try
        {
            using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = @"
                INSERT INTO TracksFts (TrackId, Title, ArtistName, AlbumTitle)
                SELECT Id, Title, ArtistName, AlbumTitle FROM Tracks
                WHERE Id NOT IN (SELECT TrackId FROM TracksFts);";
            await ftsCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SqliteDbContext] TracksFts backfill: {ex.Message}");
        }

        // Lightweight migrations for databases created with obsolete/removed columns.
        await TryDropColumnAsync(conn, "Artists", "IsLocal");
        await TryDropColumnAsync(conn, "Albums", "Provider");
        await TryDropColumnAsync(conn, "Tracks", "Provider");
        await TryDropColumnAsync(conn, "Playlists", "IsLocalOnly");

        await TryAddColumnAsync(conn, "Tracks", "LastModified", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Tracks", "FileSize", "INTEGER NOT NULL DEFAULT 0");

        // Clean up obsolete external enrichment tables if present from older versions.
        using (var dropObsoleteCmd = conn.CreateCommand())
        {
            dropObsoleteCmd.CommandText = @"
                DROP TABLE IF EXISTS ExternalDataCache;
                DROP TABLE IF EXISTS LibraryEnrichmentState;
                DROP TABLE IF EXISTS LibraryEnrichmentSessions;";
            await dropObsoleteCmd.ExecuteNonQueryAsync();
        }

        // Migration for PlaylistTracks surrogate key (Id)
        bool hasPlaylistTrackId = await CheckColumnExistsAsync(conn, "PlaylistTracks", "Id");
        if (!hasPlaylistTrackId)
        {
            // DDL is transactional in SQLite, so run this destructive rebuild inside an
            // explicit transaction (DB-05): a crash after DROP but before RENAME would
            // otherwise auto-commit each statement and lose every playlist-track row.
            using var migrateCmd = conn.CreateCommand();
            migrateCmd.CommandText = @"
                BEGIN IMMEDIATE;
                DROP TABLE IF EXISTS PlaylistTracks_Temp;
                CREATE TABLE PlaylistTracks_Temp (
                    Id TEXT PRIMARY KEY,
                    PlaylistId TEXT NOT NULL,
                    TrackId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL,
                    FOREIGN KEY(PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                    FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
                ) STRICT;
                INSERT INTO PlaylistTracks_Temp (Id, PlaylistId, TrackId, SortOrder)
                SELECT lower(hex(randomblob(16))), PlaylistId, TrackId, SortOrder FROM PlaylistTracks;
                DROP TABLE PlaylistTracks;
                ALTER TABLE PlaylistTracks_Temp RENAME TO PlaylistTracks;
                COMMIT;";
            try
            {
                await migrateCmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Connection disposal below also rolls back, but roll back explicitly
                // so a caller reusing pooled connections can never inherit a live tx.
                using var rollbackCmd = conn.CreateCommand();
                rollbackCmd.CommandText = "ROLLBACK;";
                try { await rollbackCmd.ExecuteNonQueryAsync(); } catch { /* tx already closed */ }
                throw;
            }
        }
    }

    private static async Task<bool> CheckColumnExistsAsync(SqliteConnection conn, string table, string column)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @col;";
        check.Parameters.Add(new SqliteParameter("@col", column));
        return Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
    }

    // Adds a column if it isn't already present (idempotent, STRICT-safe).
    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string definition)
    {
        bool exists = await CheckColumnExistsAsync(conn, table, column);
        if (exists) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync();
    }

    // Drops a column if present (idempotent, SQLite 3.35+ safe).
    private static async Task TryDropColumnAsync(SqliteConnection conn, string table, string column)
    {
        bool exists = await CheckColumnExistsAsync(conn, table, column);
        if (!exists) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        await alter.ExecuteNonQueryAsync();
    }

    public async Task UpsertArtistAsync(Artist artist, SqliteTransaction? tx = null)
    {
        SqliteConnection? localConn = null;
        SqliteConnection conn;
        if (tx != null)
        {
            conn = tx.Connection ?? throw new InvalidOperationException("Transaction has no associated connection.");
        }
        else
        {
            localConn = await CreateConnectionAsync();
            conn = localConn;
        }

        try
        {
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            cmd.CommandText = @"
                INSERT INTO Artists (Id, Name, Bio, ArtworkUrl)
                VALUES (@id, @name, @bio, @artworkUrl)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Bio = excluded.Bio,
                    ArtworkUrl = excluded.ArtworkUrl;";

            cmd.Parameters.Add(new SqliteParameter("@id", artist.Id));
            cmd.Parameters.Add(new SqliteParameter("@name", artist.Name));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)artist.Bio ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@artworkUrl", (object?)artist.ArtworkUrl ?? DBNull.Value));

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            localConn?.Dispose();
        }
    }

    public async Task UpsertAlbumAsync(Album album, SqliteTransaction? tx = null)
    {
        SqliteConnection? localConn = null;
        SqliteConnection conn;
        if (tx != null)
        {
            conn = tx.Connection ?? throw new InvalidOperationException("Transaction has no associated connection.");
        }
        else
        {
            localConn = await CreateConnectionAsync();
            conn = localConn;
        }

        try
        {
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            cmd.CommandText = @"
                INSERT INTO Albums (Id, Title, ArtistId, ArtistName, Year, ArtworkUrl)
                VALUES (@id, @title, @artistId, @artistName, @year, @artworkUrl)
                ON CONFLICT(Id) DO UPDATE SET
                    Title = excluded.Title,
                    ArtistId = excluded.ArtistId,
                    ArtistName = excluded.ArtistName,
                    Year = excluded.Year,
                    ArtworkUrl = COALESCE(excluded.ArtworkUrl, Albums.ArtworkUrl);";

            cmd.Parameters.Add(new SqliteParameter("@id", album.Id));
            cmd.Parameters.Add(new SqliteParameter("@title", album.Title));
            cmd.Parameters.Add(new SqliteParameter("@artistId", album.ArtistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", album.ArtistName));
            cmd.Parameters.Add(new SqliteParameter("@year", album.Year));
            cmd.Parameters.Add(new SqliteParameter("@artworkUrl", (object?)album.ArtworkUrl ?? DBNull.Value));

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            localConn?.Dispose();
        }
    }

    public async Task UpsertTrackAsync(Track track, SqliteTransaction? tx = null)
    {
        SqliteConnection? localConn = null;
        SqliteTransaction? localTx = null;
        try
        {
            SqliteConnection conn;
            SqliteTransaction effectiveTx;
            if (tx != null)
            {
                conn = tx.Connection ?? throw new InvalidOperationException("Transaction has no associated connection.");
                effectiveTx = tx;
            }
            else
            {
                // DB-04: the Artist/Album/Track trio must be atomic on the tx==null
                // path too — without a wrapping transaction a mid-failure leaves a
                // half-written graph (e.g. an Album row with no Track).
                localConn = await CreateConnectionAsync();
                localTx = (SqliteTransaction)await localConn.BeginTransactionAsync();
                conn = localConn;
                effectiveTx = localTx;
            }

            using (var insertArtist = conn.CreateCommand())
            {
                insertArtist.Transaction = effectiveTx;
                insertArtist.CommandText = "INSERT OR IGNORE INTO Artists (Id, Name) VALUES (@id, @name);";
                insertArtist.Parameters.Add(new SqliteParameter("@id", track.ArtistId));
                insertArtist.Parameters.Add(new SqliteParameter("@name", track.ArtistName));
                await insertArtist.ExecuteNonQueryAsync();
            }

            using (var insertAlbum = conn.CreateCommand())
            {
                insertAlbum.Transaction = effectiveTx;
                insertAlbum.CommandText = "INSERT OR IGNORE INTO Albums (Id, Title, ArtistId, ArtistName, Year) VALUES (@id, @title, @artistId, @artistName, @year);";
                insertAlbum.Parameters.Add(new SqliteParameter("@id", track.AlbumId));
                insertAlbum.Parameters.Add(new SqliteParameter("@title", track.AlbumTitle));
                insertAlbum.Parameters.Add(new SqliteParameter("@artistId", track.ArtistId));
                insertAlbum.Parameters.Add(new SqliteParameter("@artistName", track.ArtistName));
                insertAlbum.Parameters.Add(new SqliteParameter("@year", track.Year));
                await insertAlbum.ExecuteNonQueryAsync();
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = effectiveTx;

            // DateAdded converted to Epoch Seconds to match strictly typed schema
            long epochSeconds = ((DateTimeOffset)track.DateAdded).ToUnixTimeSeconds();

            long lastModified = 0;
            long fileSize = 0;
            try
            {
                if (System.IO.File.Exists(track.SourceUri))
                {
                    var fi = new System.IO.FileInfo(track.SourceUri);
                    lastModified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                    fileSize = fi.Length;
                }
            }
            catch { }

            cmd.CommandText = @"
                INSERT INTO Tracks (Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, TrackNumber, Year, DateAdded, Genre, ReplayGain, Disc, LastModified, FileSize)
                VALUES (@id, @title, @artistId, @artistName, @albumId, @albumTitle, @durationSeconds, @sourceUri, @trackNumber, @year, @dateAdded, @genre, @replayGain, @disc, @lastModified, @fileSize)
                ON CONFLICT(Id) DO UPDATE SET
                    Title = excluded.Title,
                    ArtistId = excluded.ArtistId,
                    ArtistName = excluded.ArtistName,
                    AlbumId = excluded.AlbumId,
                    AlbumTitle = excluded.AlbumTitle,
                    DurationSeconds = excluded.DurationSeconds,
                    SourceUri = excluded.SourceUri,
                    TrackNumber = excluded.TrackNumber,
                    Year = excluded.Year,
                    DateAdded = excluded.DateAdded,
                    Genre = excluded.Genre,
                    ReplayGain = excluded.ReplayGain,
                    Disc = excluded.Disc,
                    LastModified = excluded.LastModified,
                    FileSize = excluded.FileSize;";

            cmd.Parameters.Add(new SqliteParameter("@id", track.Id));
            cmd.Parameters.Add(new SqliteParameter("@title", track.Title));
            cmd.Parameters.Add(new SqliteParameter("@artistId", track.ArtistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", track.ArtistName));
            cmd.Parameters.Add(new SqliteParameter("@albumId", track.AlbumId));
            cmd.Parameters.Add(new SqliteParameter("@albumTitle", track.AlbumTitle));
            cmd.Parameters.Add(new SqliteParameter("@durationSeconds", track.DurationSeconds));
            cmd.Parameters.Add(new SqliteParameter("@sourceUri", track.SourceUri));
            cmd.Parameters.Add(new SqliteParameter("@trackNumber", track.TrackNumber));
            cmd.Parameters.Add(new SqliteParameter("@year", track.Year));
            cmd.Parameters.Add(new SqliteParameter("@dateAdded", epochSeconds));
            cmd.Parameters.Add(new SqliteParameter("@genre", track.Genre ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@replayGain", track.ReplayGain));
            // SCAN-11: tags occasionally carry Disc=0 when the field is present but
            // empty — clamp to disc 1 so ordering never sees a zero disc.
            cmd.Parameters.Add(new SqliteParameter("@disc", Math.Max(1, track.DiscNumber)));
            cmd.Parameters.Add(new SqliteParameter("@lastModified", lastModified));
            cmd.Parameters.Add(new SqliteParameter("@fileSize", fileSize));

            await cmd.ExecuteNonQueryAsync();

            if (localTx != null)
            {
                await localTx.CommitAsync();
            }
        }
        catch
        {
            if (localTx != null)
            {
                try { await localTx.RollbackAsync(); } catch { /* connection already broken */ }
            }
            throw;
        }
        finally
        {
            localTx?.Dispose();
            localConn?.Dispose();
        }
    }

    public async Task<Dictionary<string, (string Id, long LastModified, long FileSize)>> GetTrackFileSignaturesUnderPathAsync(string rootPath)
    {
        var map = new Dictionary<string, (string Id, long LastModified, long FileSize)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rootPath)) return map;

        string normalizedRoot;
        try
        {
            normalizedRoot = System.IO.Path.GetFullPath(rootPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        }
        catch
        {
            normalizedRoot = rootPath;
        }

        string likePattern = normalizedRoot.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, SourceUri, LastModified, FileSize FROM Tracks WHERE SourceUri LIKE @prefix ESCAPE '\\';";
        cmd.Parameters.Add(new SqliteParameter("@prefix", likePattern));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string id = reader.GetString(0);
            string uri = reader.GetString(1);
            long lastMod = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            long size = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            try
            {
                map[System.IO.Path.GetFullPath(uri)] = (id, lastMod, size);
            }
            catch
            {
                map[uri] = (id, lastMod, size);
            }
        }
        return map;
    }

    public async Task<List<Track>> GetAllTracksAsync()
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TrackColumns} FROM Tracks ORDER BY Title ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(ReadTrack(reader));
        }
        return tracks;
    }

    public async Task<int> GetTotalTrackCountAsync()
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Tracks;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
    
    public async Task<List<Artist>> GetAllArtistsAsync()
    {
        var artists = new List<Artist>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Bio, ArtworkUrl FROM Artists ORDER BY Name ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);

            artists.Add(new Artist(id, name, bio, artworkUrl));
        }
        return artists;
    }

    public async Task<List<Album>> GetAllAlbumsAsync()
    {
        var albums = new List<Album>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl FROM Albums ORDER BY Title ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var year = reader.GetInt32(4);
            var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);

            albums.Add(new Album(id, title, artistId, artistName, year, artworkUrl));
        }
        return albums;
    }

    public async Task<List<Track>> GetTracksByAlbumAsync(string albumId)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        // SCAN-11: album view orders by disc before track so multi-disc sets keep
        // their disc boundaries (disc 1 trk 1 no longer collides with disc 2 trk 1).
        cmd.CommandText = $@"
            SELECT {TrackColumns}
            FROM Tracks
            WHERE AlbumId = @albumId
            ORDER BY Disc ASC, TrackNumber ASC;";

        cmd.Parameters.Add(new SqliteParameter("@albumId", albumId));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(ReadTrack(reader));
        }
        return tracks;
    }

    public async Task<List<Track>> GetTracksByArtistAsync(string artistId)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        
        string? artistName = null;
        using (var nameCmd = conn.CreateCommand())
        {
            nameCmd.CommandText = "SELECT Name FROM Artists WHERE Id = @artistId LIMIT 1;";
            nameCmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
            var nameResult = await nameCmd.ExecuteScalarAsync();
            if (nameResult != null && nameResult != DBNull.Value)
            {
                artistName = nameResult.ToString();
            }
        }

        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            // SCAN-11: disc-aware ordering within each album.
            cmd.CommandText = $@"
                SELECT {TrackColumns}
                FROM Tracks
                WHERE ArtistId = @artistId
                   OR ArtistName LIKE '%' || @artistName || '%'
                ORDER BY Year DESC, AlbumTitle ASC, Disc ASC, TrackNumber ASC;";
            cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", artistName));
        }
        else
        {
            cmd.CommandText = $@"
                SELECT {TrackColumns}
                FROM Tracks
                WHERE ArtistId = @artistId
                ORDER BY Year DESC, AlbumTitle ASC, Disc ASC, TrackNumber ASC;";
            cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(ReadTrack(reader));
        }
        return tracks;
    }

    public async Task<Album?> GetAlbumByIdAsync(string albumId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl FROM Albums WHERE Id = @albumId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@albumId", albumId));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var year = reader.GetInt32(4);
            var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);

            return new Album(id, title, artistId, artistName, year, artworkUrl);
        }
        return null;
    }

    public async Task<Album?> GetAlbumByTitleAsync(string title, string? artistId = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl FROM Albums WHERE Title = @title COLLATE NOCASE AND ArtistId = @artistId LIMIT 1;";
            cmd.Parameters.Add(new SqliteParameter("@title", title.Trim()));
            cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
        }
        else
        {
            cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl FROM Albums WHERE Title = @title COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.Add(new SqliteParameter("@title", title.Trim()));
        }

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var albumTitle = reader.GetString(1);
            var artId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var year = reader.GetInt32(4);
            var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);

            return new Album(id, albumTitle, artId, artistName, year, artworkUrl);
        }
        return null;
    }

    public async Task<List<Album>> GetAlbumsByIdsAsync(IEnumerable<string> albumIds)
    {
        if (albumIds == null) return new List<Album>();
        var ids = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(albumIds));
        if (ids.Count == 0) return new List<Album>();

        var result = new List<Album>();
        const int batchSize = 500;
        using var conn = await CreateConnectionAsync();

        for (int offset = 0; offset < ids.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, ids.Count - offset);
            using var cmd = conn.CreateCommand();

            var paramNames = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string p = "@alb" + i;
                paramNames.Add(p);
                cmd.Parameters.Add(new SqliteParameter(p, ids[offset + i]));
            }

            cmd.CommandText = $@"
                SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl 
                FROM Albums 
                WHERE Id IN ({string.Join(",", paramNames)});";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var artistId = reader.GetString(2);
                var artistName = reader.GetString(3);
                var year = reader.GetInt32(4);
                var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);

                result.Add(new Album(id, title, artistId, artistName, year, artworkUrl));
            }
        }

        return result;
    }

    public async Task<Artist?> GetArtistByIdAsync(string artistId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Bio, ArtworkUrl FROM Artists WHERE Id = @artistId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);

            return new Artist(id, name, bio, artworkUrl);
        }
        return null;
    }

    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Bio, ArtworkUrl FROM Artists WHERE Name = @name COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@name", name.Trim()));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var artistName = reader.GetString(1);
            var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);

            return new Artist(id, artistName, bio, artworkUrl);
        }
        return null;
    }
    
    public async Task LogPlaybackHistoryAsync(string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO PlaybackHistory (Id, TrackId, PlayedAt) VALUES (@id, @trackId, @playedAt);";

        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));
        cmd.Parameters.Add(new SqliteParameter("@playedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        await cmd.ExecuteNonQueryAsync();
    }
    
    // Public helper required for Ticket #003 Consumer Transaction batching
    public async Task<SqliteTransaction> BeginTransactionAsync()
    {
        // CreateConnectionAsync() already opens the connection; opening it again is
        // redundant (and ADO.NET throws on a second Open of an open connection).
        var conn = await CreateConnectionAsync();
        return (SqliteTransaction)await conn.BeginTransactionAsync();
    }

    // EDITOR-02: deletes artist/album rows that no track references anymore.
    // MUST run inside the caller's transaction, after the re-pointing track
    // upsert. The NOT EXISTS guards are load-bearing: both foreign keys are
    // ON DELETE CASCADE, so an unguarded "DELETE FROM Artists" would take every
    // still-referenced track down with the parent row.
    public async Task DeleteOrphanedArtistsAndAlbumsAsync(
        IEnumerable<string> artistIds,
        IEnumerable<string> albumIds,
        SqliteTransaction tx)
    {
        if (tx == null) throw new ArgumentNullException(nameof(tx));
        var conn = tx.Connection ?? throw new InvalidOperationException("Transaction has no associated connection.");

        foreach (string id in artistIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(id)) continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Artists WHERE Id = @id AND NOT EXISTS (SELECT 1 FROM Tracks WHERE ArtistId = @id);";
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        foreach (string id in albumIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(id)) continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Albums WHERE Id = @id AND NOT EXISTS (SELECT 1 FROM Tracks WHERE AlbumId = @id);";
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task<Track?> GetTrackByIdAsync(string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TrackColumns} FROM Tracks WHERE Id = @trackId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTrack(reader);
        }
        return null;
    }

    // ---- Duplicate detection ----------------------------------------------

    // High-confidence duplicate detection: clusters tracks sharing title, artist,
    // duration within ±1.5s tolerance and quality ranking.
    public async Task<List<DuplicateGroup>> GetDuplicatesAsync()
    {
        var groups = new List<DuplicateGroup>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {TrackColumns}
            FROM Tracks
            ORDER BY LOWER(TRIM(Title)), LOWER(TRIM(ArtistName));";

        var allTracks = new List<Track>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                allTracks.Add(ReadTrack(reader));
            }
        }

        // Group by (Title, Artist)
        var byKey = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTracks)
        {
            string key = $"{t.Title.Trim()}||{t.ArtistName.Trim()}";
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<Track>();
                byKey[key] = list;
            }
            list.Add(t);
        }

        foreach (var entry in byKey)
        {
            var candidates = entry.Value;
            if (candidates.Count < 2) continue;

            // Sub-group by duration tolerance (1.5 seconds) or identical file size
            var clusters = new List<List<Track>>();
            foreach (var t in candidates)
            {
                bool added = false;
                foreach (var cluster in clusters)
                {
                    var rep = cluster[0];
                    bool durationMatch = Math.Abs(t.DurationSeconds - rep.DurationSeconds) <= 1.5;
                    bool versionMatch = (!HasVersionAnnotation(t.Title) && !HasVersionAnnotation(rep.Title)) || 
                                        string.Equals(t.Title.Trim(), rep.Title.Trim(), StringComparison.OrdinalIgnoreCase);

                    if (durationMatch && versionMatch)
                    {
                        cluster.Add(t);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    clusters.Add(new List<Track> { t });
                }
            }

            foreach (var cluster in clusters)
            {
                if (cluster.Count > 1)
                {
                    // Sort candidates by quality: Lossless (FLAC/WAV/ALAC) > Lossy (AAC/MP3), then by size
                    cluster.Sort((a, b) => GetQualityScore(b).CompareTo(GetQualityScore(a)));
                    groups.Add(new DuplicateGroup(cluster[0].Title, cluster[0].ArtistName, cluster));
                }
            }
        }
        return groups;
    }

    private static bool HasVersionAnnotation(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        string lower = title.ToLowerInvariant();
        return lower.Contains("live") || lower.Contains("remaster") || lower.Contains("acoustic") ||
               lower.Contains("demo") || lower.Contains("mix") || lower.Contains("edit") ||
               lower.Contains("version") || lower.Contains("instrumental") || lower.Contains("cover");
    }

    private static int GetQualityScore(Track t)
    {
        string ext = System.IO.Path.GetExtension(t.SourceUri)?.ToLowerInvariant() ?? "";
        int fmtScore = ext switch
        {
            ".flac" or ".alac" or ".wav" or ".dsf" or ".dff" or ".ape" => 30,
            ".m4a" or ".aac" or ".opus" or ".ogg" => 20,
            _ => 10
        };
        long size = 0;
        try { if (System.IO.File.Exists(t.SourceUri)) size = new System.IO.FileInfo(t.SourceUri).Length; } catch {}
        return fmtScore * 1000000 + (int)Math.Clamp(size / 1024, 0, 999999);
    }

    // ---- Home dashboards & favorites --------------------------------------

    // Standard 15-column Track projection shared by the dashboard queries
    // (ordinal order must match ReadTrack; Disc is last so pre-SCAN-11 column
    // indexes stay stable).
    private const string TrackColumns =
        "Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, TrackNumber, Year, DateAdded, Genre, ReplayGain, Disc";

    private static Track ReadTrack(SqliteDataReader reader)
    {
        var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(10)).UtcDateTime;
        int disc = reader.IsDBNull(13) ? 1 : reader.GetInt32(13);
        return new Track(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
            reader.GetInt32(8), reader.GetInt32(9), dateAdded,
            reader.IsDBNull(11) ? "" : reader.GetString(11), (float)reader.GetDouble(12),
            Math.Max(1, disc));
    }

    public async Task<List<Track>> GetRecentlyPlayedAsync(int limit)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {PrefixColumns("t")}
            FROM Tracks t
            JOIN (SELECT TrackId, MAX(PlayedAt) AS LastPlayed FROM PlaybackHistory GROUP BY TrackId) h
              ON h.TrackId = t.Id
            ORDER BY h.LastPlayed DESC
            LIMIT @limit;";
        cmd.Parameters.Add(new SqliteParameter("@limit", limit));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tracks.Add(ReadTrack(reader));
        return tracks;
    }

    public async Task<List<Track>> GetMostPlayedAsync(int limit)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {PrefixColumns("t")}
            FROM Tracks t
            JOIN PlaybackHistory ph ON ph.TrackId = t.Id
            GROUP BY t.Id
            ORDER BY COUNT(ph.Id) DESC
            LIMIT @limit;";
        cmd.Parameters.Add(new SqliteParameter("@limit", limit));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tracks.Add(ReadTrack(reader));
        return tracks;
    }

    public async Task<List<Track>> GetLastAddedAsync(int limit)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {TrackColumns} FROM Tracks ORDER BY DateAdded DESC LIMIT @limit;";
        cmd.Parameters.Add(new SqliteParameter("@limit", limit));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tracks.Add(ReadTrack(reader));
        return tracks;
    }

    public async Task<List<Track>> GetFavoritesAsync()
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {PrefixColumns("t")}
            FROM Tracks t
            JOIN Favorites f ON f.TrackId = t.Id
            ORDER BY f.AddedAt DESC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tracks.Add(ReadTrack(reader));
        return tracks;
    }

    public async Task<HashSet<string>> GetFavoriteTrackIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TrackId FROM Favorites;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<bool> IsFavoriteAsync(string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Favorites WHERE TrackId = @id LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    public async Task AddFavoriteAsync(string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Favorites (TrackId, AddedAt) VALUES (@id, @at);";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        cmd.Parameters.Add(new SqliteParameter("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveFavoriteAsync(string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE TrackId = @id;";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> ToggleFavoriteAsync(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return false;

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT 1 FROM Favorites WHERE TrackId = @id LIMIT 1;";
            checkCmd.Parameters.Add(new SqliteParameter("@id", trackId));
            bool isFav = (await checkCmd.ExecuteScalarAsync()) != null;

            using var modCmd = conn.CreateCommand();
            modCmd.Transaction = tx;
            if (isFav)
            {
                modCmd.CommandText = "DELETE FROM Favorites WHERE TrackId = @id;";
                modCmd.Parameters.Add(new SqliteParameter("@id", trackId));
            }
            else
            {
                modCmd.CommandText = "INSERT OR IGNORE INTO Favorites (TrackId, AddedAt) VALUES (@id, @at);";
                modCmd.Parameters.Add(new SqliteParameter("@id", trackId));
                modCmd.Parameters.Add(new SqliteParameter("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            }
            await modCmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return !isFav;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // Qualifies the shared column list with a table alias (e.g. "t.Id, t.Title, ...").
    private static string PrefixColumns(string alias) =>
        string.Join(", ", System.Array.ConvertAll(TrackColumns.Split(", "), c => $"{alias}.{c}"));

    // ---- Playlists --------------------------------------------------------

    public async Task<Playlist> CreatePlaylistAsync(string title, string? description)
    {
        string id = Guid.NewGuid().ToString();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Playlists (Id, Title, Description, CreatedAt) VALUES (@id, @title, @desc, @created);";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@title", title));
        cmd.Parameters.Add(new SqliteParameter("@desc", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@created", created));
        await cmd.ExecuteNonQueryAsync();

        return new Playlist(id, title, description, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime, 0);
    }

    public async Task DeletePlaylistAsync(string id)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Playlists WHERE Id = @id;"; // PlaylistTracks cascade
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenamePlaylistAsync(string id, string title)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Playlists SET Title = @title WHERE Id = @id;";
        cmd.Parameters.Add(new SqliteParameter("@title", title));
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Playlist>> GetPlaylistsAsync()
    {
        var playlists = new List<Playlist>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.Id, p.Title, p.Description, p.CreatedAt, COUNT(pt.TrackId)
            FROM Playlists p
            LEFT JOIN PlaylistTracks pt ON pt.PlaylistId = p.Id
            GROUP BY p.Id
            ORDER BY p.Title COLLATE NOCASE ASC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            playlists.Add(new Playlist(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).UtcDateTime,
                reader.GetInt32(4)));
        }
        return playlists;
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(string id)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.Id, p.Title, p.Description, p.CreatedAt, COUNT(pt.TrackId)
            FROM Playlists p
            LEFT JOIN PlaylistTracks pt ON pt.PlaylistId = p.Id
            WHERE p.Id = @id
            GROUP BY p.Id;";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new Playlist(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).UtcDateTime,
                reader.GetInt32(4));
        }
        return null;
    }

    public async Task<List<Track>> GetPlaylistTracksAsync(string playlistId)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {PrefixColumns("t")}
            FROM PlaylistTracks pt
            JOIN Tracks t ON t.Id = pt.TrackId
            WHERE pt.PlaylistId = @id
            ORDER BY pt.SortOrder ASC;";
        cmd.Parameters.Add(new SqliteParameter("@id", playlistId));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(ReadTrack(reader));
        }
        return tracks;
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        string entryId = Guid.NewGuid().ToString();
        cmd.CommandText = @"
            INSERT INTO PlaylistTracks (Id, PlaylistId, TrackId, SortOrder)
            VALUES (@id, @pid, @tid, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM PlaylistTracks WHERE PlaylistId = @pid));";
        cmd.Parameters.Add(new SqliteParameter("@id", entryId));
        cmd.Parameters.Add(new SqliteParameter("@pid", playlistId));
        cmd.Parameters.Add(new SqliteParameter("@tid", trackId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, IEnumerable<string> trackIds)
    {
        var idList = trackIds?.ToList();
        if (idList == null || idList.Count == 0) return;

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            long nextSortOrder = 0;
            using (var maxCmd = conn.CreateCommand())
            {
                maxCmd.Transaction = tx;
                maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM PlaylistTracks WHERE PlaylistId = @pid;";
                maxCmd.Parameters.Add(new SqliteParameter("@pid", playlistId));
                var result = await maxCmd.ExecuteScalarAsync();
                if (result is long val) nextSortOrder = val;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO PlaylistTracks (Id, PlaylistId, TrackId, SortOrder)
                VALUES (@id, @pid, @tid, @sort);";
            var pId = cmd.Parameters.Add("@id", SqliteType.Text);
            var pPid = cmd.Parameters.Add("@pid", SqliteType.Text);
            var pTid = cmd.Parameters.Add("@tid", SqliteType.Text);
            var pSort = cmd.Parameters.Add("@sort", SqliteType.Integer);

            pPid.Value = playlistId;

            foreach (var tid in idList)
            {
                pId.Value = Guid.NewGuid().ToString();
                pTid.Value = tid;
                pSort.Value = nextSortOrder++;
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        // Deletes by surrogate Id if matching, or removes exactly ONE occurrence if trackId is a Track ID
        cmd.CommandText = @"
            DELETE FROM PlaylistTracks 
            WHERE Id = @tid 
               OR rowid IN (SELECT rowid FROM PlaylistTracks WHERE PlaylistId = @pid AND TrackId = @tid LIMIT 1);";
        cmd.Parameters.Add(new SqliteParameter("@pid", playlistId));
        cmd.Parameters.Add(new SqliteParameter("@tid", trackId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<PlaylistTrackEntry>> GetPlaylistTrackEntriesAsync(string playlistId)
    {
        var entries = new List<PlaylistTrackEntry>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pt.Id, pt.PlaylistId, pt.SortOrder,
                   t.Id, t.Title, t.ArtistId, t.ArtistName, t.AlbumId, t.AlbumTitle, t.DurationSeconds, t.SourceUri, t.TrackNumber, t.Year, t.DateAdded, t.Genre, t.ReplayGain, t.Disc
            FROM PlaylistTracks pt
            JOIN Tracks t ON t.Id = pt.TrackId
            WHERE pt.PlaylistId = @id
            ORDER BY pt.SortOrder ASC;";
        cmd.Parameters.Add(new SqliteParameter("@id", playlistId));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string entryId = reader.GetString(0);
            string pid = reader.GetString(1);
            int sortOrder = reader.GetInt32(2);
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)).UtcDateTime;
            int disc = reader.IsDBNull(16) ? 1 : reader.GetInt32(16);
            var track = new Track(
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetDouble(9), reader.GetString(10),
                reader.GetInt32(11), reader.GetInt32(12), dateAdded,
                reader.IsDBNull(14) ? "" : reader.GetString(14), (float)reader.GetDouble(15),
                Math.Max(1, disc));
            entries.Add(new PlaylistTrackEntry(entryId, pid, track, sortOrder));
        }
        return entries;
    }

    public async Task RemoveTrackEntryFromPlaylistAsync(string entryId)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistTracks WHERE Id = @id;";
        cmd.Parameters.Add(new SqliteParameter("@id", entryId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetPlaylistOrderAsync(string playlistId, IReadOnlyList<string> orderedTrackOrEntryIds)
    {
        if (orderedTrackOrEntryIds == null || orderedTrackOrEntryIds.Count == 0) return;

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            // Load current entries once. Keys may be surrogate entry Ids OR TrackIds
            // (the API deliberately accepts both), so match by entry Id first, then
            // consume matching TrackId entries in their current order. One position
            // per entry keeps a track that appears multiple times in the playlist on
            // distinct SortOrders — the old bulk `OR TrackId = @key` update stamped
            // every copy with the same position (DB-08).
            var entryOrder = new List<string>();
            var trackIdByEntry = new Dictionary<string, string>();
            using (var load = conn.CreateCommand())
            {
                load.Transaction = tx;
                load.CommandText = "SELECT Id, TrackId FROM PlaylistTracks WHERE PlaylistId = @pid ORDER BY SortOrder ASC;";
                load.Parameters.Add(new SqliteParameter("@pid", playlistId));
                using var reader = await load.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string entryId = reader.GetString(0);
                    entryOrder.Add(entryId);
                    trackIdByEntry[entryId] = reader.GetString(1);
                }
            }

            var positioned = new HashSet<string>();
            var updates = new List<(string EntryId, int Order)>();
            int nextOrder = 0;
            foreach (string key in orderedTrackOrEntryIds)
            {
                string? matchedEntry;
                if (trackIdByEntry.ContainsKey(key))
                {
                    matchedEntry = key; // surrogate entry Id
                }
                else
                {
                    matchedEntry = null; // TrackId fallback: first not-yet-positioned copy
                    foreach (var entryId in entryOrder)
                    {
                        if (!positioned.Contains(entryId) && trackIdByEntry[entryId] == key)
                        {
                            matchedEntry = entryId;
                            break;
                        }
                    }
                }

                if (matchedEntry != null && positioned.Add(matchedEntry))
                {
                    updates.Add((matchedEntry, nextOrder++));
                }
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE PlaylistTracks SET SortOrder = @order WHERE Id = @entry AND PlaylistId = @pid;";
            var pOrder = cmd.Parameters.Add(new SqliteParameter("@order", 0));
            var pEntry = cmd.Parameters.Add(new SqliteParameter("@entry", ""));
            cmd.Parameters.Add(new SqliteParameter("@pid", playlistId));
            foreach (var (entryId, order) in updates)
            {
                pOrder.Value = order;
                pEntry.Value = entryId;
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task RelocateTrackAsync(string oldTrackId, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldTrackId) || string.IsNullOrWhiteSpace(newPath)) return;
        string newTrackId = Octave.Core.Helpers.IdGenerator.FromTrackUri(newPath);
        if (oldTrackId == newTrackId) return;

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            var oldTrack = await GetTrackByIdUnlockedAsync(conn, tx, oldTrackId);
            if (oldTrack == null) return;

            var newTrack = oldTrack with { Id = newTrackId, SourceUri = newPath };
            await UpsertTrackAsync(newTrack, tx);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE PlaylistTracks SET TrackId = @newId WHERE TrackId = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@newId", newTrackId));
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Favorites SET TrackId = @newId WHERE TrackId = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@newId", newTrackId));
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE PlaybackHistory SET TrackId = @newId WHERE TrackId = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@newId", newTrackId));
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE SavedQueue SET TrackId = @newId WHERE TrackId = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@newId", newTrackId));
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE SavedUnshuffledQueue SET TrackId = @newId WHERE TrackId = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@newId", newTrackId));
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Tracks WHERE Id = @oldId;";
                cmd.Parameters.Add(new SqliteParameter("@oldId", oldTrackId));
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task<Track?> GetTrackByIdUnlockedAsync(SqliteConnection conn, SqliteTransaction tx, string trackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {TrackColumns} FROM Tracks WHERE Id = @trackId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTrack(reader);
        }
        return null;
    }

    public async Task<List<string>> GetGenresAsync()
    {
        var genres = new List<string>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Genre FROM Tracks WHERE Genre <> '' ORDER BY Genre COLLATE NOCASE ASC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            genres.Add(reader.GetString(0));
        }
        return genres;
    }

    public async Task<List<Track>> GetTracksByGenreAsync(string genre)
    {
        var tracks = new List<Track>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        // SCAN-11: disc-aware ordering within each album.
        cmd.CommandText = $@"
            SELECT {TrackColumns}
            FROM Tracks
            WHERE Genre = @genre COLLATE NOCASE
            ORDER BY ArtistName COLLATE NOCASE ASC, AlbumTitle COLLATE NOCASE ASC, Disc ASC, TrackNumber ASC;";
        cmd.Parameters.Add(new SqliteParameter("@genre", genre));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(ReadTrack(reader));
        }
        return tracks;
    }

    public async Task<List<string>> GetMonitoredFoldersAsync()
    {
        var folders = new List<string>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path FROM MonitoredFolders ORDER BY Path ASC;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            folders.Add(reader.GetString(0));
        }
        return folders;
    }

    public async Task AddMonitoredFolderAsync(string path)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO MonitoredFolders (Path) VALUES (@path);";
        cmd.Parameters.Add(new SqliteParameter("@path", path));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveMonitoredFolderAsync(string path)
    {
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MonitoredFolders WHERE Path = @path;";
        cmd.Parameters.Add(new SqliteParameter("@path", path));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Self-healing fallback: if MonitoredFolders is empty but Tracks already contains records
    /// (e.g. after unpacking the app or initial crawl), infer the common root directories
    /// from the stored tracks so real-time watching and reconciliation are active.
    /// </summary>
    public async Task<List<string>> InferMonitoredFoldersFromTracksAsync()
    {
        var inferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT SourceUri FROM Tracks;";
        using var reader = await cmd.ExecuteReaderAsync();

        string specialMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var sampleUris = new List<string>();
        while (await reader.ReadAsync())
        {
            string uri = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(uri))
            {
                sampleUris.Add(uri);
            }
        }

        if (sampleUris.Count == 0) return inferred.ToList();

        // 1. Check if files reside inside the user's standard Music folder
        if (!string.IsNullOrWhiteSpace(specialMusic) && Directory.Exists(specialMusic))
        {
            string normMusic = Path.GetFullPath(specialMusic).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (sampleUris.Any(u =>
            {
                try { return Path.GetFullPath(u).StartsWith(normMusic, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }))
            {
                inferred.Add(Path.GetFullPath(specialMusic));
            }
        }

        // 2. For remaining files outside standard Music, extract their common top directory
        foreach (var uri in sampleUris)
        {
            try
            {
                string? dir = Path.GetDirectoryName(uri);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    string normDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    bool covered = inferred.Any(parent => normDir.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    if (!covered)
                    {
                        inferred.Add(Path.GetFullPath(dir));
                    }
                }
            }
            catch { }
        }

        return inferred.ToList();
    }

    // Deletes all tracks whose file lives under the given folder, then purges the
    // albums/artists left orphaned. Used when a monitored folder is removed.
    public async Task DeleteTracksUnderPathAsync(string folderPath)
    {
        string normalized = System.IO.Path.GetFullPath(folderPath)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;

        // Escape LIKE wildcards in the prefix so odd folder names can't broaden the match.
        string escaped = normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        string likePrefix = escaped + "%";

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Tracks WHERE SourceUri LIKE @prefix ESCAPE '\\';";
                cmd.Parameters.Add(new SqliteParameter("@prefix", likePrefix));
                await cmd.ExecuteNonQueryAsync();

                cmd.Parameters.Clear();
                cmd.CommandText = "DELETE FROM Albums WHERE Id NOT IN (SELECT DISTINCT AlbumId FROM Tracks);";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums UNION SELECT DISTINCT ArtistId FROM Tracks);";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteTrackAsync(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;

        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM Tracks WHERE Id = @id;";
                cmd.Parameters.Add(new SqliteParameter("@id", trackId));
                await cmd.ExecuteNonQueryAsync();

                cmd.Parameters.Clear();
                cmd.CommandText = "DELETE FROM PlaylistTracks WHERE TrackId = @id;";
                cmd.Parameters.Add(new SqliteParameter("@id", trackId));
                await cmd.ExecuteNonQueryAsync();

                cmd.Parameters.Clear();
                cmd.CommandText = "DELETE FROM Albums WHERE Id NOT IN (SELECT DISTINCT AlbumId FROM Tracks);";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "DELETE FROM Artists WHERE Id NOT IN (SELECT DISTINCT ArtistId FROM Albums UNION SELECT DISTINCT ArtistId FROM Tracks);";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<Track>> GetTracksByIdsAsync(IReadOnlyList<string> ids)
    {
        var result = new List<Track>();
        if (ids == null || ids.Count == 0) return result;

        var byId = new Dictionary<string, Track>(StringComparer.Ordinal);
        const int BatchSize = 500;

        using var conn = await CreateConnectionAsync();

        for (int offset = 0; offset < ids.Count; offset += BatchSize)
        {
            int count = Math.Min(BatchSize, ids.Count - offset);
            using var cmd = conn.CreateCommand();

            var paramNames = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string p = "@p" + i;
                paramNames.Add(p);
                cmd.Parameters.Add(new SqliteParameter(p, ids[offset + i]));
            }

            cmd.CommandText =
                $"SELECT {TrackColumns} " +
                $"FROM Tracks WHERE Id IN ({string.Join(",", paramNames)});";

            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var track = ReadTrack(reader);
                    byId[track.Id] = track;
                }
            }
        }

        // Preserve the requested order (and drop any ids no longer in the library).
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var t))
                result.Add(t);
        }
        return result;
    }

    public async Task SavePlayerStateAsync(
        IReadOnlyList<string> orderedTrackIds, IReadOnlyList<string>? unshuffledTrackIds, int currentIndex, double positionSeconds,
        float volume, bool isShuffle, RepeatMode repeatMode)
    {
        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM SavedQueue; DELETE FROM SavedUnshuffledQueue;";
                await clear.ExecuteNonQueryAsync();
            }

            if (orderedTrackIds != null && orderedTrackIds.Count > 0)
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO SavedQueue (SortOrder, TrackId) VALUES (@order, @trackId);";
                var pOrder = insert.Parameters.Add(new SqliteParameter("@order", 0));
                var pTrack = insert.Parameters.Add(new SqliteParameter("@trackId", ""));
                for (int i = 0; i < orderedTrackIds.Count; i++)
                {
                    pOrder.Value = i;
                    pTrack.Value = orderedTrackIds[i];
                    await insert.ExecuteNonQueryAsync();
                }
            }

            if (unshuffledTrackIds != null && unshuffledTrackIds.Count > 0)
            {
                using var insertU = conn.CreateCommand();
                insertU.Transaction = tx;
                insertU.CommandText = "INSERT INTO SavedUnshuffledQueue (SortOrder, TrackId) VALUES (@order, @trackId);";
                var pOrderU = insertU.Parameters.Add(new SqliteParameter("@order", 0));
                var pTrackU = insertU.Parameters.Add(new SqliteParameter("@trackId", ""));
                for (int i = 0; i < unshuffledTrackIds.Count; i++)
                {
                    pOrderU.Value = i;
                    pTrackU.Value = unshuffledTrackIds[i];
                    await insertU.ExecuteNonQueryAsync();
                }
            }

            await UpsertPlayerStateRowAsync(conn, tx, currentIndex, positionSeconds, volume, isShuffle, repeatMode);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // Lightweight progress update - only touches the PlayerState row, not the queue.
    public async Task UpdatePlaybackProgressAsync(
        int currentIndex, double positionSeconds, float volume, bool isShuffle, RepeatMode repeatMode)
    {
        using var conn = await CreateConnectionAsync();
        await UpsertPlayerStateRowAsync(conn, null, currentIndex, positionSeconds, volume, isShuffle, repeatMode);
    }

    private static async Task UpsertPlayerStateRowAsync(
        SqliteConnection conn, SqliteTransaction? tx, int currentIndex, double positionSeconds,
        float volume, bool isShuffle, RepeatMode repeatMode)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO PlayerState (Id, CurrentIndex, PositionSeconds, Volume, IsShuffle, RepeatMode, UpdatedAt)
            VALUES (1, @index, @pos, @vol, @shuffle, @repeat, @updated)
            ON CONFLICT(Id) DO UPDATE SET
                CurrentIndex = excluded.CurrentIndex,
                PositionSeconds = excluded.PositionSeconds,
                Volume = excluded.Volume,
                IsShuffle = excluded.IsShuffle,
                RepeatMode = excluded.RepeatMode,
                UpdatedAt = excluded.UpdatedAt;";
        cmd.Parameters.Add(new SqliteParameter("@index", currentIndex));
        cmd.Parameters.Add(new SqliteParameter("@pos", positionSeconds));
        cmd.Parameters.Add(new SqliteParameter("@vol", (double)volume));
        cmd.Parameters.Add(new SqliteParameter("@shuffle", isShuffle ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@repeat", (int)repeatMode));
        cmd.Parameters.Add(new SqliteParameter("@updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<PersistedPlayerState?> LoadPlayerStateAsync()
    {
        using var conn = await CreateConnectionAsync();

        int currentIndex;
        double position;
        float volume;
        bool isShuffle;
        RepeatMode repeatMode;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT CurrentIndex, PositionSeconds, Volume, IsShuffle, RepeatMode FROM PlayerState WHERE Id = 1 LIMIT 1;";
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            currentIndex = reader.GetInt32(0);
            position = reader.GetDouble(1);
            volume = (float)reader.GetDouble(2);
            isShuffle = reader.GetInt32(3) != 0;
            repeatMode = (RepeatMode)reader.GetInt32(4);
        }

        var trackIds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TrackId FROM SavedQueue ORDER BY SortOrder ASC;";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                trackIds.Add(reader.GetString(0));
            }
        }

        var unshuffledTrackIds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TrackId FROM SavedUnshuffledQueue ORDER BY SortOrder ASC;";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                unshuffledTrackIds.Add(reader.GetString(0));
            }
        }

        if (trackIds.Count == 0)
            return null;

        return new PersistedPlayerState(trackIds, currentIndex, position, volume, isShuffle, repeatMode, unshuffledTrackIds.Count > 0 ? unshuffledTrackIds : null);
    }

    public async Task<SearchResults> SearchLibraryAsync(string query, int? limit = null)
    {
        var tracks = new List<Track>();
        var albums = new List<Album>();
        var artists = new List<Artist>();
        var playlists = new List<Playlist>();

        // DB-07: an empty/whitespace query used to build '%%' and (with FTS skipped for
        // empty input) LIKE-match every row — returning the entire library when no limit
        // was set. Short-circuit to an explicitly empty result instead.
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResults(tracks, albums, artists, playlists);
        }

        // DB-07: never materialize an unbounded result set — callers that pass no
        // explicit limit get a sane per-category cap rather than a whole-table scan.
        limit ??= 200;

        using var conn = await CreateConnectionAsync();
        string wildQuery = $"%{query}%";
        string prefixQuery = $"{query}%";
        string exactQuery = query;

        // Query 1: Tracks (Optimized with FTS5 when available, falling back to prefix/LIKE)
        string ftsQuery = BuildFtsQuery(query);
        bool ftsExecuted = false;
        int tracksBeforeFts = tracks.Count;
        if (!string.IsNullOrWhiteSpace(ftsQuery))
        {
            try
            {
                using var ftsCmd = conn.CreateCommand();
                // PrefixColumns keeps this projection in lockstep with ReadTrack's
                // ordinals as the Track schema grows (SCAN-11 added Disc).
                string ftsSql = $@"
                    SELECT {PrefixColumns("t")}
                    FROM TracksFts f
                    JOIN Tracks t ON t.Id = f.TrackId
                    WHERE TracksFts MATCH @fts
                    ORDER BY bm25(TracksFts), t.Title ASC";
                if (limit.HasValue)
                {
                    ftsSql += " LIMIT @limit";
                    ftsCmd.Parameters.Add(new SqliteParameter("@limit", limit.Value));
                }
                ftsSql += ";";
                ftsCmd.CommandText = ftsSql;
                ftsCmd.Parameters.Add(new SqliteParameter("@fts", ftsQuery));

                using var ftsReader = await ftsCmd.ExecuteReaderAsync();
                while (await ftsReader.ReadAsync())
                {
                    tracks.Add(ReadTrack(ftsReader));
                }
                // DB-03: record that FTS ran successfully — separate from how many rows
                // it returned, so a legitimate no-match doesn't ALSO pay for a full
                // leading-wildcard LIKE table scan.
                ftsExecuted = true;
            }
            // DB-06: only SQLite-level failures (missing/corrupt FTS table, malformed
            // MATCH) may silently fall back to LIKE; any other exception is a genuine
            // bug and must surface instead of being swallowed into a Debug.WriteLine
            // that no-op's in Release builds.
            catch (SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SqliteDbContext] FTS5 search unavailable, falling back to LIKE: {ex.Message}");
                // If rows already streamed out before the failure (the exception can hit
                // mid-ReadAsync), keep the partial result and skip the LIKE fallback —
                // re-querying would append duplicates. The pre-DB-03 guard
                // (`tracks.Count == 0`) used to cover this incidentally.
                ftsExecuted = tracks.Count > tracksBeforeFts;
            }
        }

        if (!ftsExecuted)
        {
            using var cmd = conn.CreateCommand();
            string sql = $@"
                SELECT {TrackColumns}
                FROM Tracks
                WHERE Title LIKE @q OR ArtistName LIKE @q OR AlbumTitle LIKE @q
                ORDER BY
                    CASE
                        WHEN Title = @exactQuery THEN 0
                        WHEN Title LIKE @prefixQuery THEN 1
                        ELSE 2
                    END, Title ASC";
            if (limit.HasValue)
            {
                sql += " LIMIT @limit";
                cmd.Parameters.Add(new SqliteParameter("@limit", limit.Value));
            }
            sql += ";";
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("@q", wildQuery));
            cmd.Parameters.Add(new SqliteParameter("@prefixQuery", prefixQuery));
            cmd.Parameters.Add(new SqliteParameter("@exactQuery", exactQuery));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tracks.Add(ReadTrack(reader));
            }
        }

        // Query 2: Albums
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl 
                FROM Albums 
                WHERE Title LIKE @q OR ArtistName LIKE @q 
                ORDER BY 
                    CASE 
                        WHEN Title = @exactQuery THEN 0 
                        WHEN Title LIKE @prefixQuery THEN 1 
                        ELSE 2 
                    END, Title ASC";
            if (limit.HasValue)
            {
                sql += " LIMIT @limit";
                cmd.Parameters.Add(new SqliteParameter("@limit", limit.Value));
            }
            sql += ";";
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("@q", wildQuery));
            cmd.Parameters.Add(new SqliteParameter("@prefixQuery", prefixQuery));
            cmd.Parameters.Add(new SqliteParameter("@exactQuery", exactQuery));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var artistId = reader.GetString(2);
                var artistName = reader.GetString(3);
                var year = reader.GetInt32(4);
                var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);

                albums.Add(new Album(id, title, artistId, artistName, year, artworkUrl));
            }
        }

        // Query 3: Artists
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT Id, Name, Bio, ArtworkUrl 
                FROM Artists 
                WHERE Name LIKE @q 
                ORDER BY 
                    CASE 
                        WHEN Name = @exactQuery THEN 0 
                        WHEN Name LIKE @prefixQuery THEN 1 
                        ELSE 2 
                    END, Name ASC";
            if (limit.HasValue)
            {
                sql += " LIMIT @limit";
                cmd.Parameters.Add(new SqliteParameter("@limit", limit.Value));
            }
            sql += ";";
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("@q", wildQuery));
            cmd.Parameters.Add(new SqliteParameter("@prefixQuery", prefixQuery));
            cmd.Parameters.Add(new SqliteParameter("@exactQuery", exactQuery));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
                var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);

                artists.Add(new Artist(id, name, bio, artworkUrl));
            }
        }

        // Query 4: Playlists
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT p.Id, p.Title, p.Description, p.CreatedAt, COUNT(pt.TrackId) AS TrackCount
                FROM Playlists p
                LEFT JOIN PlaylistTracks pt ON p.Id = pt.PlaylistId
                WHERE p.Title LIKE @q
                GROUP BY p.Id
                ORDER BY 
                    CASE 
                        WHEN p.Title = @exactQuery THEN 0 
                        WHEN p.Title LIKE @prefixQuery THEN 1 
                        ELSE 2 
                    END, p.Title ASC";
            if (limit.HasValue)
            {
                sql += " LIMIT @limit";
                cmd.Parameters.Add(new SqliteParameter("@limit", limit.Value));
            }
            sql += ";";
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("@q", wildQuery));
            cmd.Parameters.Add(new SqliteParameter("@prefixQuery", prefixQuery));
            cmd.Parameters.Add(new SqliteParameter("@exactQuery", exactQuery));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var desc = reader.IsDBNull(2) ? null : reader.GetString(2);
                var createdAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)).UtcDateTime;
                var trackCount = reader.GetInt32(4);

                playlists.Add(new Playlist(id, title, desc, createdAt, trackCount));
            }
        }

        return new SearchResults(tracks, albums, artists, playlists);
    }

    private static string BuildFtsQuery(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return string.Empty;
        var terms = new List<string>(words.Length);
        foreach (var w in words)
        {
            string clean = w.Replace("\"", "").Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                terms.Add($"\"{clean}\"*");
            }
        }
        return string.Join(" AND ", terms);
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = @key;";
        cmd.Parameters.Add(new SqliteParameter("@key", key));

        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? result.ToString() : null;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AppSettings (Key, Value) VALUES (@key, @val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.Add(new SqliteParameter("@key", key));
        cmd.Parameters.Add(new SqliteParameter("@val", value ?? string.Empty));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        var settings = new Dictionary<string, string>();
        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM AppSettings;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string key = reader.GetString(0);
            string val = reader.GetString(1);
            settings[key] = val;
        }

        return settings;
    }

    public async Task<CachedLyricsEntity?> GetCachedLyricsAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TrackId, PlainLyrics, SyncedLyrics, HasPlainLyrics, HasSyncedLyrics, IsNotFound, CachedAt, LastCheckedAt, Source, LrclibRecordId, SyncedSource, StaticSource
            FROM LyricsCache
            WHERE TrackId = @trackId
            LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            string id = reader.GetString(0);
            string? plain = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? synced = reader.IsDBNull(2) ? null : reader.GetString(2);
            bool hasPlain = reader.GetInt32(3) == 1;
            bool hasSynced = reader.GetInt32(4) == 1;
            bool isNotFound = reader.GetInt32(5) == 1;
            var cachedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6));
            var lastCheckedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7));
            string? source = reader.IsDBNull(8) ? null : reader.GetString(8);
            long? lrclibRecordId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            string? syncedSource = reader.IsDBNull(10) ? null : reader.GetString(10);
            string? staticSource = reader.IsDBNull(11) ? null : reader.GetString(11);

            return new CachedLyricsEntity(id, plain, synced, hasPlain, hasSynced, isNotFound, cachedAt, lastCheckedAt, source, lrclibRecordId, syncedSource, staticSource);
        }
        return null;
    }

    public async Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source,
        long? lrclibRecordId,
        string? syncedSource,
        string? staticSource,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return;

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();

        long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        cmd.CommandText = @"
            INSERT INTO LyricsCache (TrackId, PlainLyrics, SyncedLyrics, HasPlainLyrics, HasSyncedLyrics, IsNotFound, CachedAt, LastCheckedAt, Source, LrclibRecordId, SyncedSource, StaticSource)
            VALUES (@trackId, @plainLyrics, @syncedLyrics, @hasPlainLyrics, @hasSyncedLyrics, @isNotFound, @now, @now, @source, @lrclibRecordId, @syncedSource, @staticSource)
            ON CONFLICT(TrackId) DO UPDATE SET
                PlainLyrics = excluded.PlainLyrics,
                SyncedLyrics = excluded.SyncedLyrics,
                HasPlainLyrics = excluded.HasPlainLyrics,
                HasSyncedLyrics = excluded.HasSyncedLyrics,
                IsNotFound = excluded.IsNotFound,
                LastCheckedAt = excluded.LastCheckedAt,
                Source = excluded.Source,
                LrclibRecordId = COALESCE(excluded.LrclibRecordId, LyricsCache.LrclibRecordId),
                SyncedSource = excluded.SyncedSource,
                StaticSource = excluded.StaticSource;";

        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));
        cmd.Parameters.Add(new SqliteParameter("@plainLyrics", (object?)plainLyrics ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@syncedLyrics", (object?)syncedLyrics ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hasPlainLyrics", hasPlainLyrics ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@hasSyncedLyrics", hasSyncedLyrics ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@isNotFound", isNotFound ? 1 : 0));
        cmd.Parameters.Add(new SqliteParameter("@now", nowEpoch));
        cmd.Parameters.Add(new SqliteParameter("@source", (object?)source ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@lrclibRecordId", (object?)lrclibRecordId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@syncedSource", (object?)syncedSource ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@staticSource", (object?)staticSource ?? DBNull.Value));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source,
        long? lrclibRecordId,
        CancellationToken cancellationToken = default)
        => UpsertCachedLyricsAsync(trackId, plainLyrics, syncedLyrics, hasPlainLyrics, hasSyncedLyrics, isNotFound, source, lrclibRecordId, null, null, cancellationToken);

    public Task UpsertCachedLyricsAsync(
        string trackId,
        string? plainLyrics,
        string? syncedLyrics,
        bool hasPlainLyrics,
        bool hasSyncedLyrics,
        bool isNotFound,
        string? source = null,
        CancellationToken cancellationToken = default)
        => UpsertCachedLyricsAsync(trackId, plainLyrics, syncedLyrics, hasPlainLyrics, hasSyncedLyrics, isNotFound, source, null, null, null, cancellationToken);

    public async Task DeleteCachedLyricsAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return;

        using var conn = await CreateConnectionAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LyricsCache WHERE TrackId = @trackId;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearDatabaseAsync(bool preserveSettings = false)
    {
        using var conn = await CreateConnectionAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    DELETE FROM LyricsCache;
                    DELETE FROM PlaylistTracks;
                    DELETE FROM Playlists;
                    DELETE FROM PlaybackHistory;
                    DELETE FROM Favorites;
                    DELETE FROM SavedQueue;
                    DELETE FROM SavedUnshuffledQueue;
                    DELETE FROM PlayerState;
                    DELETE FROM Tracks;
                    DELETE FROM Albums;
                    DELETE FROM Artists;
                    DELETE FROM MonitoredFolders;
                    DELETE FROM TracksFts;";
                await cmd.ExecuteNonQueryAsync();

                if (!preserveSettings)
                {
                    cmd.CommandText = "DELETE FROM AppSettings;";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        try
        {
            using var vacCmd = conn.CreateCommand();
            vacCmd.CommandText = "VACUUM;";
            await vacCmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort vacuum
        }
    }
}