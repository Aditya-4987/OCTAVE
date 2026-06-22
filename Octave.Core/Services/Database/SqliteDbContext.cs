using Microsoft.Data.Sqlite;
using Octave.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Octave.Core.Services.Database;

public class SqliteDbContext
{
    private readonly string _connectionString;

    public SqliteDbContext(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;";
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
                PlaylistId TEXT NOT NULL,
                TrackId TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                PRIMARY KEY(PlaylistId, TrackId),
                FOREIGN KEY(PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT, WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS PlaybackHistory (
                Id TEXT PRIMARY KEY,
                TrackId TEXT NOT NULL,
                PlayedAt INTEGER NOT NULL, -- Stored explicitly as Unix Epoch Seconds
                FOREIGN KEY(TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_tracks_artist ON Tracks(ArtistId);
            CREATE INDEX IF NOT EXISTS idx_tracks_album ON Tracks(AlbumId);
            CREATE INDEX IF NOT EXISTS idx_history_time ON PlaybackHistory(PlayedAt DESC);";

        await cmd.ExecuteNonQueryAsync();
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
                    ArtworkUrl = excluded.ArtworkUrl,
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
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;

            // DateAdded converted to Epoch Seconds to match strictly typed schema
            long epochSeconds = ((DateTimeOffset)track.DateAdded).ToUnixTimeSeconds();

            cmd.CommandText = @"
                INSERT INTO Tracks (Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded)
                VALUES (@id, @title, @artistId, @artistName, @albumId, @albumTitle, @durationSeconds, @sourceUri, @provider, @trackNumber, @year, @dateAdded)
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
                    DateAdded = excluded.DateAdded;";

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
        cmd.CommandText = "SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded FROM Tracks ORDER BY Title ASC;";

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

            // Rehydrate DateTime from Epoch Seconds
            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;

            tracks.Add(new Track(
                id,
                title,
                artistId,
                artistName,
                albumId,
                albumTitle,
                durationSeconds,
                sourceUri,
                provider,
                trackNumber,
                year,
                dateAdded
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
            SELECT Id, Title, ArtistId, ArtistName, AlbumId, AlbumTitle, DurationSeconds, SourceUri, Provider, TrackNumber, Year, DateAdded 
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

            tracks.Add(new Track(
                id,
                title,
                artistId,
                artistName,
                albId,
                albumTitle,
                durationSeconds,
                sourceUri,
                provider,
                trackNumber,
                year,
                dateAdded
            ));
        }
        return tracks;
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
        var conn = CreateConnection();
        await conn.OpenAsync();
        return (SqliteTransaction)await conn.BeginTransactionAsync();
    }
}