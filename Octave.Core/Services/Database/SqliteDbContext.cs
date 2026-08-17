using Microsoft.Data.Sqlite;
using Octave.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Octave.Core.Services.Database;

public class SqliteDbContext
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

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    public async Task InitializeAsync()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        
        // Fully restored STRICT mode DDL exactly matching the frozen architecture
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Artists (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE,
                Bio TEXT,
                ArtworkUrl TEXT,
                IsLocal INTEGER NOT NULL CHECK(IsLocal IN (0, 1))
            ) STRICT;

            CREATE TABLE IF NOT EXISTS Albums (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL COLLATE NOCASE,
                ArtistId TEXT NOT NULL,
                ArtistName TEXT NOT NULL,
                Year INTEGER NOT NULL,
                ArtworkUrl TEXT,
                Provider TEXT NOT NULL,
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
                Provider TEXT NOT NULL,
                TrackNumber INTEGER NOT NULL DEFAULT 1,
                Year INTEGER NOT NULL,
                DateAdded INTEGER NOT NULL, -- Stored explicitly as Unix Epoch Seconds
                Genre TEXT NOT NULL DEFAULT '',
                ReplayGain REAL NOT NULL DEFAULT 0.0,
                FOREIGN KEY(ArtistId) REFERENCES Artists(Id) ON DELETE CASCADE,
                FOREIGN KEY(AlbumId) REFERENCES Albums(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS Playlists (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL COLLATE NOCASE,
                Description TEXT,
                CreatedAt INTEGER NOT NULL, -- Stored explicitly as Unix Epoch Seconds
                IsLocalOnly INTEGER NOT NULL CHECK(IsLocalOnly IN (0, 1))
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

            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER PRIMARY KEY,
                AppliedAt INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_tracks_artist ON Tracks(ArtistId);
            CREATE INDEX IF NOT EXISTS idx_tracks_album ON Tracks(AlbumId);
            CREATE INDEX IF NOT EXISTS idx_tracks_title ON Tracks(Title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_albums_title ON Albums(Title COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_artists_name ON Artists(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_playlisttracks_playlist ON PlaylistTracks(PlaylistId, SortOrder);
            CREATE INDEX IF NOT EXISTS idx_history_time ON PlaybackHistory(PlayedAt DESC);";

        await cmd.ExecuteNonQueryAsync();

        using (var vCmd = conn.CreateCommand())
        {
            vCmd.CommandText = "INSERT OR IGNORE INTO SchemaVersion (Version, AppliedAt) VALUES (1, @now);";
            vCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            await vCmd.ExecuteNonQueryAsync();
        }

        // Lightweight migrations for databases created before a column existed.
        await TryAddColumnAsync(conn, "Tracks", "Genre", "TEXT NOT NULL DEFAULT ''");
        await TryAddColumnAsync(conn, "Tracks", "ReplayGain", "REAL NOT NULL DEFAULT 0.0");

        // Migration for PlaylistTracks surrogate key (Id)
        bool hasPlaylistTrackId = await CheckColumnExistsAsync(conn, "PlaylistTracks", "Id");
        if (!hasPlaylistTrackId)
        {
            using var migrateCmd = conn.CreateCommand();
            migrateCmd.CommandText = @"
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
                ALTER TABLE PlaylistTracks_Temp RENAME TO PlaylistTracks;";
            await migrateCmd.ExecuteNonQueryAsync();
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
            localConn = CreateConnection();
            conn = localConn;
        }

        try
        {
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            cmd.CommandText = @"
                INSERT INTO Artists (Id, Name, Bio, ArtworkUrl, IsLocal)
                VALUES (@id, @name, @bio, @artworkUrl, @isLocal)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Bio = excluded.Bio,
                    ArtworkUrl = excluded.ArtworkUrl,
                    IsLocal = excluded.IsLocal;";

            cmd.Parameters.Add(new SqliteParameter("@id", artist.Id));
            cmd.Parameters.Add(new SqliteParameter("@name", artist.Name));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)artist.Bio ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@artworkUrl", (object?)artist.ArtworkUrl ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@isLocal", artist.IsLocal ? 1 : 0));

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
            localConn = CreateConnection();
            conn = localConn;
        }

        try
        {
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            cmd.CommandText = @"
                INSERT INTO Albums (Id, Title, ArtistId, ArtistName, Year, ArtworkUrl, Provider)
                VALUES (@id, @title, @artistId, @artistName, @year, @artworkUrl, @provider)
                ON CONFLICT(Id) DO UPDATE SET
                    Title = excluded.Title,
                    ArtistId = excluded.ArtistId,
                    ArtistName = excluded.ArtistName,
                    Year = excluded.Year,
                    ArtworkUrl = COALESCE(excluded.ArtworkUrl, Albums.ArtworkUrl),
                    Provider = excluded.Provider;";

            cmd.Parameters.Add(new SqliteParameter("@id", album.Id));
            cmd.Parameters.Add(new SqliteParameter("@title", album.Title));
            cmd.Parameters.Add(new SqliteParameter("@artistId", album.ArtistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", album.ArtistName));
            cmd.Parameters.Add(new SqliteParameter("@year", album.Year));
            cmd.Parameters.Add(new SqliteParameter("@artworkUrl", (object?)album.ArtworkUrl ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@provider", album.Provider));

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
        SqliteConnection conn;
        if (tx != null)
        {
            conn = tx.Connection ?? throw new InvalidOperationException("Transaction has no associated connection.");
        }
        else
        {
            localConn = CreateConnection();
            conn = localConn;
        }

        try
        {
            using (var insertArtist = conn.CreateCommand())
            {
                if (tx != null) insertArtist.Transaction = tx;
                insertArtist.CommandText = "INSERT OR IGNORE INTO Artists (Id, Name, IsLocal) VALUES (@id, @name, 1);";
                insertArtist.Parameters.Add(new SqliteParameter("@id", track.ArtistId));
                insertArtist.Parameters.Add(new SqliteParameter("@name", track.ArtistName));
                await insertArtist.ExecuteNonQueryAsync();
            }

            using (var insertAlbum = conn.CreateCommand())
            {
                if (tx != null) insertAlbum.Transaction = tx;
                insertAlbum.CommandText = "INSERT OR IGNORE INTO Albums (Id, Title, ArtistId, ArtistName, Year, Provider) VALUES (@id, @title, @artistId, @artistName, @year, @provider);";
                insertAlbum.Parameters.Add(new SqliteParameter("@id", track.AlbumId));
                insertAlbum.Parameters.Add(new SqliteParameter("@title", track.AlbumTitle));
                insertAlbum.Parameters.Add(new SqliteParameter("@artistId", track.ArtistId));
                insertAlbum.Parameters.Add(new SqliteParameter("@artistName", track.ArtistName));
                insertAlbum.Parameters.Add(new SqliteParameter("@year", track.Year));
                insertAlbum.Parameters.Add(new SqliteParameter("@provider", track.Provider));
                await insertAlbum.ExecuteNonQueryAsync();
            }

            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            // DateAdded converted to Epoch Seconds to match strictly typed schema
            long epochSeconds = ((DateTimeOffset)track.DateAdded).ToUnixTimeSeconds();

            cmd.CommandText = @"
                INSERT INTO Tracks (Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain)
                VALUES (@id, @title, @artistId, @artistName, @albumId, @albumTitle, @durationSeconds, @sourceUri, @provider, @trackNumber, @year, @dateAdded, @genre, @replayGain)
                ON CONFLICT(Id) DO UPDATE SET
                    Title = excluded.Title,
                    ArtistId = excluded.ArtistId,
                    ArtistName = excluded.ArtistName,
                    AlbumId = excluded.AlbumId,
                    AlbumTitle = excluded.AlbumTitle,
                    DurationSeconds = excluded.DurationSeconds,
                    SourceUri = excluded.SourceUri,
                    Provider = excluded.Provider,
                    TrackNumber = excluded.TrackNumber,
                    Year = excluded.Year,
                    DateAdded = excluded.DateAdded,
                    Genre = excluded.Genre,
                    ReplayGain = excluded.ReplayGain;";

            cmd.Parameters.Add(new SqliteParameter("@id", track.Id));
            cmd.Parameters.Add(new SqliteParameter("@title", track.Title));
            cmd.Parameters.Add(new SqliteParameter("@artistId", track.ArtistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", track.ArtistName));
            cmd.Parameters.Add(new SqliteParameter("@albumId", track.AlbumId));
            cmd.Parameters.Add(new SqliteParameter("@albumTitle", track.AlbumTitle));
            cmd.Parameters.Add(new SqliteParameter("@durationSeconds", track.DurationSeconds));
            cmd.Parameters.Add(new SqliteParameter("@sourceUri", track.SourceUri));
            cmd.Parameters.Add(new SqliteParameter("@provider", track.Provider));
            cmd.Parameters.Add(new SqliteParameter("@trackNumber", track.TrackNumber));
            cmd.Parameters.Add(new SqliteParameter("@year", track.Year));
            cmd.Parameters.Add(new SqliteParameter("@dateAdded", epochSeconds));
            cmd.Parameters.Add(new SqliteParameter("@genre", track.Genre ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@replayGain", track.ReplayGain));

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            localConn?.Dispose();
        }
    }

    public async Task<List<Track>> GetAllTracksAsync()
    {
        var tracks = new List<Track>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain FROM Tracks ORDER BY Title ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var albumId = reader.GetString(4);
            var albumTitle = reader.GetString(5);
            var durationSeconds = reader.GetDouble(6);
            var sourceUri = reader.GetString(7);
            var provider = reader.GetString(8);
            var trackNumber = reader.GetInt32(9);
            var year = reader.GetInt32(10);
            var epochSeconds = reader.GetInt64(11);

            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            var genre = reader.IsDBNull(12) ? "" : reader.GetString(12);
            var replayGain = reader.IsDBNull(13) ? 0.0f : (float)reader.GetDouble(13);

            tracks.Add(new Track(
                id, title, artistId, artistName, albumId, albumTitle, durationSeconds, sourceUri, provider, trackNumber, year, dateAdded, genre, replayGain
            ));
        }
        return tracks;
    }

    public async Task<int> GetTotalTrackCountAsync()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Tracks;";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
    
    public async Task<List<Artist>> GetAllArtistsAsync()
    {
        var artists = new List<Artist>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Bio, ArtworkUrl, IsLocal FROM Artists ORDER BY Name ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isLocal = reader.GetInt32(4) != 0;

            artists.Add(new Artist(id, name, bio, artworkUrl, isLocal));
        }
        return artists;
    }

    public async Task<List<Album>> GetAllAlbumsAsync()
    {
        var albums = new List<Album>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl, Provider FROM Albums ORDER BY Title ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var year = reader.GetInt32(4);
            var artworkUrl = reader.IsDBNull(5) ? null : reader.GetString(5);
            var provider = reader.GetString(6);

            albums.Add(new Album(id, title, artistId, artistName, year, artworkUrl, provider));
        }
        return albums;
    }

    public async Task<List<Track>> GetTracksByAlbumAsync(string albumId)
    {
        var tracks = new List<Track>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain 
            FROM Tracks 
            WHERE AlbumId = @albumId 
            ORDER BY TrackNumber ASC;";
        
        cmd.Parameters.Add(new SqliteParameter("@albumId", albumId));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var albId = reader.GetString(4);
            var albumTitle = reader.GetString(5);
            var durationSeconds = reader.GetDouble(6);
            var sourceUri = reader.GetString(7);
            var provider = reader.GetString(8);
            var trackNumber = reader.GetInt32(9);
            var year = reader.GetInt32(10);
            var epochSeconds = reader.GetInt64(11);

            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            var genre = reader.IsDBNull(12) ? "" : reader.GetString(12);
            var replayGain = reader.IsDBNull(13) ? 0.0f : (float)reader.GetDouble(13);

            tracks.Add(new Track(
                id, title, artistId, artistName, albId, albumTitle, durationSeconds, sourceUri, provider, trackNumber, year, dateAdded, genre, replayGain
            ));
        }
        return tracks;
    }

    public async Task<List<Track>> GetTracksByArtistAsync(string artistId)
    {
        var tracks = new List<Track>();
        using var conn = CreateConnection();
        
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
            cmd.CommandText = @"
                SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain 
                FROM Tracks 
                WHERE ArtistId = @artistId 
                   OR ArtistName LIKE '%' || @artistName || '%'
                ORDER BY Year DESC, AlbumTitle ASC, TrackNumber ASC;";
            cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
            cmd.Parameters.Add(new SqliteParameter("@artistName", artistName));
        }
        else
        {
            cmd.CommandText = @"
                SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain 
                FROM Tracks 
                WHERE ArtistId = @artistId 
                ORDER BY Year DESC, AlbumTitle ASC, TrackNumber ASC;";
            cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artId = reader.GetString(2);
            var rowArtistName = reader.GetString(3);
            var albumIdVal = reader.GetString(4);
            var albumTitle = reader.GetString(5);
            var durationSeconds = reader.GetDouble(6);
            var sourceUri = reader.GetString(7);
            var provider = reader.GetString(8);
            var trackNumber = reader.GetInt32(9);
            var year = reader.GetInt32(10);
            var epochSeconds = reader.GetInt64(11);

            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            var genre = reader.IsDBNull(12) ? "" : reader.GetString(12);
            var replayGain = reader.IsDBNull(13) ? 0.0f : (float)reader.GetDouble(13);

            tracks.Add(new Track(
                id, title, artId, rowArtistName, albumIdVal, albumTitle, durationSeconds, sourceUri, provider, trackNumber, year, dateAdded, genre, replayGain
            ));
        }
        return tracks;
    }

    public async Task<Album?> GetAlbumByIdAsync(string albumId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl, Provider FROM Albums WHERE Id = @albumId LIMIT 1;";
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
            var provider = reader.GetString(6);

            return new Album(id, title, artistId, artistName, year, artworkUrl, provider);
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
        using var conn = CreateConnection();

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
                SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl, Provider 
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
                var provider = reader.GetString(6);

                result.Add(new Album(id, title, artistId, artistName, year, artworkUrl, provider));
            }
        }

        return result;
    }

    public async Task<Artist?> GetArtistByIdAsync(string artistId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Bio, ArtworkUrl, IsLocal FROM Artists WHERE Id = @artistId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@artistId", artistId));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var bio = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artworkUrl = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isLocal = reader.GetInt32(4) != 0;

            return new Artist(id, name, bio, artworkUrl, isLocal);
        }
        return null;
    }
    
    public async Task LogPlaybackHistoryAsync(string trackId)
    {
        using var conn = CreateConnection();
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
        // CreateConnection() already opens the connection; opening it again is
        // redundant (and ADO.NET throws on a second Open of an open connection).
        var conn = CreateConnection();
        return (SqliteTransaction)await conn.BeginTransactionAsync();
    }

    public async Task<Track?> GetTrackByIdAsync(string trackId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain FROM Tracks WHERE Id = @trackId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var artistId = reader.GetString(2);
            var artistName = reader.GetString(3);
            var albumIdVal = reader.GetString(4);
            var albumTitle = reader.GetString(5);
            var durationSeconds = reader.GetDouble(6);
            var sourceUri = reader.GetString(7);
            var provider = reader.GetString(8);
            var trackNumber = reader.GetInt32(9);
            var year = reader.GetInt32(10);
            var epochSeconds = reader.GetInt64(11);
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            var genre = reader.IsDBNull(12) ? "" : reader.GetString(12);
            var replayGain = reader.IsDBNull(13) ? 0.0f : (float)reader.GetDouble(13);

            return new Track(id, title, artistId, artistName, albumIdVal, albumTitle, durationSeconds, sourceUri, provider, trackNumber, year, dateAdded, genre, replayGain);
        }
        return null;
    }

    // ---- Duplicate detection ----------------------------------------------

    // High-confidence duplicate detection: clusters tracks sharing title, artist,
    // duration within ±1.5s tolerance and quality ranking.
    public async Task<List<DuplicateGroup>> GetDuplicatesAsync()
    {
        var groups = new List<DuplicateGroup>();
        using var conn = CreateConnection();
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

    // Standard 14-column Track projection shared by the dashboard queries.
    private const string TrackColumns =
        "Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain";

    private static Track ReadTrack(SqliteDataReader reader)
    {
        var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)).UtcDateTime;
        return new Track(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
            reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), dateAdded, reader.GetString(12), (float)reader.GetDouble(13));
    }

    public async Task<List<Track>> GetRecentlyPlayedAsync(int limit)
    {
        var tracks = new List<Track>();
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TrackId FROM Favorites;";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<bool> IsFavoriteAsync(string trackId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Favorites WHERE TrackId = @id LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    public async Task AddFavoriteAsync(string trackId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Favorites (TrackId, AddedAt) VALUES (@id, @at);";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        cmd.Parameters.Add(new SqliteParameter("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveFavoriteAsync(string trackId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE TrackId = @id;";
        cmd.Parameters.Add(new SqliteParameter("@id", trackId));
        await cmd.ExecuteNonQueryAsync();
    }

    // Qualifies the shared column list with a table alias (e.g. "t.Id, t.Title, ...").
    private static string PrefixColumns(string alias) =>
        string.Join(", ", System.Array.ConvertAll(TrackColumns.Split(", "), c => $"{alias}.{c}"));

    // ---- Playlists --------------------------------------------------------

    public async Task<Playlist> CreatePlaylistAsync(string title, string? description)
    {
        string id = Guid.NewGuid().ToString();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Playlists (Id, Title, Description, CreatedAt, IsLocalOnly) VALUES (@id, @title, @desc, @created, 1);";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@title", title));
        cmd.Parameters.Add(new SqliteParameter("@desc", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@created", created));
        await cmd.ExecuteNonQueryAsync();

        return new Playlist(id, title, description, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime, true, 0);
    }

    public async Task DeletePlaylistAsync(string id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Playlists WHERE Id = @id;"; // PlaylistTracks cascade
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenamePlaylistAsync(string id, string title)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Playlists SET Title = @title WHERE Id = @id;";
        cmd.Parameters.Add(new SqliteParameter("@title", title));
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Playlist>> GetPlaylistsAsync()
    {
        var playlists = new List<Playlist>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.Id, p.Title, p.Description, p.CreatedAt, p.IsLocalOnly, COUNT(pt.TrackId)
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
                reader.GetInt32(4) != 0,
                reader.GetInt32(5)));
        }
        return playlists;
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(string id)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT p.Id, p.Title, p.Description, p.CreatedAt, p.IsLocalOnly, COUNT(pt.TrackId)
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
                reader.GetInt32(4) != 0,
                reader.GetInt32(5));
        }
        return null;
    }

    public async Task<List<Track>> GetPlaylistTracksAsync(string playlistId)
    {
        var tracks = new List<Track>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.Id, t.Title, t.ArtistId, t.ArtistName, t.AlbumId, t.AlbumTitle, t.DurationSeconds, t.SourceUri, t.Provider, t.TrackNumber, t.Year, t.DateAdded, t.Genre, t.ReplayGain
            FROM PlaylistTracks pt
            JOIN Tracks t ON t.Id = pt.TrackId
            WHERE pt.PlaylistId = @id
            ORDER BY pt.SortOrder ASC;";
        cmd.Parameters.Add(new SqliteParameter("@id", playlistId));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)).UtcDateTime;
            tracks.Add(new Track(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), dateAdded,
                reader.GetString(12), (float)reader.GetDouble(13)));
        }
        return tracks;
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, string trackId)
    {
        using var conn = CreateConnection();
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

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId)
    {
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pt.Id, pt.PlaylistId, pt.SortOrder,
                   t.Id, t.Title, t.ArtistId, t.ArtistName, t.AlbumId, t.AlbumTitle, t.DurationSeconds, t.SourceUri, t.Provider, t.TrackNumber, t.Year, t.DateAdded, t.Genre, t.ReplayGain
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
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(14)).UtcDateTime;
            var track = new Track(
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetDouble(9), reader.GetString(10),
                reader.GetString(11), reader.GetInt32(12), reader.GetInt32(13), dateAdded,
                reader.GetString(15), (float)reader.GetDouble(16));
            entries.Add(new PlaylistTrackEntry(entryId, pid, track, sortOrder));
        }
        return entries;
    }

    public async Task RemoveTrackEntryFromPlaylistAsync(string entryId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PlaylistTracks WHERE Id = @id;";
        cmd.Parameters.Add(new SqliteParameter("@id", entryId));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetPlaylistOrderAsync(string playlistId, IReadOnlyList<string> orderedTrackOrEntryIds)
    {
        if (orderedTrackOrEntryIds == null || orderedTrackOrEntryIds.Count == 0) return;

        using var conn = CreateConnection();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE PlaylistTracks SET SortOrder = @order WHERE PlaylistId = @pid AND (Id = @key OR TrackId = @key);";
            var pOrder = cmd.Parameters.Add(new SqliteParameter("@order", 0));
            var pPid = cmd.Parameters.Add(new SqliteParameter("@pid", playlistId));
            var pKey = cmd.Parameters.Add(new SqliteParameter("@key", ""));
            for (int i = 0; i < orderedTrackOrEntryIds.Count; i++)
            {
                pOrder.Value = i;
                pKey.Value = orderedTrackOrEntryIds[i];
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

        using var conn = CreateConnection();
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
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain FROM Tracks WHERE Id = @trackId LIMIT 1;";
        cmd.Parameters.Add(new SqliteParameter("@trackId", trackId));
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)).UtcDateTime;
            return new Track(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), dateAdded,
                reader.GetString(12), (float)reader.GetDouble(13));
        }
        return null;
    }

    public async Task<List<string>> GetGenresAsync()
    {
        var genres = new List<string>();
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain
            FROM Tracks
            WHERE Genre = @genre COLLATE NOCASE
            ORDER BY ArtistName COLLATE NOCASE ASC, AlbumTitle COLLATE NOCASE ASC, TrackNumber ASC;";
        cmd.Parameters.Add(new SqliteParameter("@genre", genre));

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var epochSeconds = reader.GetInt64(11);
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            tracks.Add(new Track(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), dateAdded,
                reader.GetString(12), (float)reader.GetDouble(13)));
        }
        return tracks;
    }

    public async Task<List<string>> GetMonitoredFoldersAsync()
    {
        var folders = new List<string>();
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO MonitoredFolders (Path) VALUES (@path);";
        cmd.Parameters.Add(new SqliteParameter("@path", path));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveMonitoredFolderAsync(string path)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MonitoredFolders WHERE Path = @path;";
        cmd.Parameters.Add(new SqliteParameter("@path", path));
        await cmd.ExecuteNonQueryAsync();
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

        using var conn = CreateConnection();
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

        using var conn = CreateConnection();
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

        using var conn = CreateConnection();

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
                "SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain " +
                $"FROM Tracks WHERE Id IN ({string.Join(",", paramNames)});";

            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var epochSeconds = reader.GetInt64(11);
                    var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                    var track = new Track(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
                        reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), dateAdded,
                        reader.GetString(12), (float)reader.GetDouble(13));
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
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();
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
        using var conn = CreateConnection();

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

        using var conn = CreateConnection();
        string wildQuery = $"%{query}%";
        string prefixQuery = $"{query}%";
        string exactQuery = query;

        // Query 1: Tracks
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded, Genre, ReplayGain 
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
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var artistId = reader.GetString(2);
                var artistName = reader.GetString(3);
                var albumIdVal = reader.GetString(4);
                var albumTitle = reader.GetString(5);
                var durationSeconds = reader.GetDouble(6);
                var sourceUri = reader.GetString(7);
                var provider = reader.GetString(8);
                var trackNumber = reader.GetInt32(9);
                var year = reader.GetInt32(10);
                var epochSeconds = reader.GetInt64(11);
                var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                var genre = reader.IsDBNull(12) ? "" : reader.GetString(12);
                var replayGain = reader.IsDBNull(13) ? 0.0f : (float)reader.GetDouble(13);

                tracks.Add(new Track(
                    id, title, artistId, artistName, albumIdVal, albumTitle,
                    durationSeconds, sourceUri, provider, trackNumber, year, dateAdded,
                    genre, replayGain
                ));
            }
        }

        // Query 2: Albums
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT Id, Title, ArtistId, ArtistName, Year, ArtworkUrl, Provider 
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
                var provider = reader.GetString(6);

                albums.Add(new Album(id, title, artistId, artistName, year, artworkUrl, provider));
            }
        }

        // Query 3: Artists
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT Id, Name, Bio, ArtworkUrl, IsLocal 
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
                var isLocal = reader.GetInt32(4) != 0;

                artists.Add(new Artist(id, name, bio, artworkUrl, isLocal));
            }
        }

        // Query 4: Playlists
        var playlists = new List<Playlist>();
        {
            using var cmd = conn.CreateCommand();
            string sql = @"
                SELECT p.Id, p.Title, p.Description, p.CreatedAt, p.IsLocalOnly, COUNT(pt.TrackId) AS TrackCount
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
                var isLocal = reader.GetInt32(4) != 0;
                var trackCount = reader.GetInt32(5);

                playlists.Add(new Playlist(id, title, desc, createdAt, isLocal, trackCount));
            }
        }

        return new SearchResults(tracks, albums, artists, playlists);
    }
}