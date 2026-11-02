using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Octave.Core.Helpers;
using Octave.Core.Models;
using Xunit;

namespace Octave.Core.Tests;

public class PackagingAndPublishingIntegrityTests
{
    [Theory]
    [InlineData("bass.dll")]
    [InlineData("bass_fx.dll")]
    public void NativeBinary_ExistsAndIsValidX64PE(string binaryName)
    {
        string binPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        Assert.True(File.Exists(binPath), $"Native binary '{binaryName}' is missing from runtime directory: {binPath}");

        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        // 1. DOS Header MZ signature
        ushort mz = reader.ReadUInt16();
        Assert.Equal(0x5A4D, mz); // 'MZ' in little-endian

        // 2. PE header offset at 0x3C
        fs.Seek(0x3C, SeekOrigin.Begin);
        int peOffset = reader.ReadInt32();
        Assert.True(peOffset > 0 && peOffset < fs.Length - 4);

        // 3. PE signature ("PE\0\0")
        fs.Seek(peOffset, SeekOrigin.Begin);
        uint peSig = reader.ReadUInt32();
        Assert.Equal(0x00004550u, peSig); // "PE\0\0" in little-endian

        // 4. Machine type: IMAGE_FILE_MACHINE_AMD64 = 0x8664
        ushort machine = reader.ReadUInt16();
        Assert.Equal(0x8664, machine);
    }

    [Fact]
    public void AudioFormatRegistry_ContainsAllSupportedExtensions()
    {
        var supported = AudioFormatRegistry.AllExtensions;
        Assert.NotEmpty(supported);

        // Expected formats across core and plugin decoders
        string[] expected = [
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav",
            ".wma", ".ape", ".wv", ".dsf", ".dff", ".aiff", ".aif",
            ".alac", ".ac3", ".dts", ".mpc", ".spx", ".tta", ".ofr",
            ".mp2", ".mp1"
        ];

        foreach (var ext in expected)
        {
            Assert.True(AudioFormatRegistry.IsSupported(ext), $"Expected format {ext} was not supported.");
            Assert.True(AudioFormatRegistry.IsSupported(ext.ToUpperInvariant()), $"Uppercase format {ext.ToUpperInvariant()} was not supported.");
        }

        // Non-audio formats must not be supported
        Assert.False(AudioFormatRegistry.IsSupported(".exe"));
        Assert.False(AudioFormatRegistry.IsSupported(".dll"));
        Assert.False(AudioFormatRegistry.IsSupported(".pdf"));
        Assert.False(AudioFormatRegistry.IsSupported(".txt"));
        Assert.False(AudioFormatRegistry.IsSupported(""));
        Assert.False(AudioFormatRegistry.IsSupported(null!));
    }

    [Fact]
    public void Sqlite_InitializesWithFts5VirtualTableSupport()
    {
        // Tests that SQLite FTS5 (Full Text Search 5) virtual tables function properly.
        // Used in LibraryService for instant search indexing.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE VIRTUAL TABLE test_fts USING fts5(title, artist, album);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO test_fts(title, artist, album) VALUES ('Stairway to Heaven', 'Led Zeppelin', 'Led Zeppelin IV');";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT title, artist FROM test_fts WHERE test_fts MATCH 'Zeppelin';";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Stairway to Heaven", reader.GetString(0));
            Assert.Equal("Led Zeppelin", reader.GetString(1));
        }
    }

    [Fact]
    public void LrclibResponse_JsonSerializationAndDeserialization_FunctionsWithoutReflectionErrors()
    {
        // Simulates JSON payloads returned by LRCLIB API to ensure System.Text.Json
        // contracts remain stable without trimming or reflection exceptions.
        string json = """
        {
            "id": 12345,
            "name": "Octave Symphony",
            "trackName": "Octave Symphony",
            "artistName": "Antigravity",
            "albumName": "Debut",
            "duration": 245.5,
            "instrumental": false,
            "plainLyrics": "Play the notes\nFeel the sound",
            "syncedLyrics": "[00:12.34] Play the notes\n[00:15.67] Feel the sound"
        }
        """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var response = JsonSerializer.Deserialize<LrclibResponse>(json, options);

        Assert.NotNull(response);
        Assert.Equal("Octave Symphony", response.TrackName);
        Assert.Equal("Antigravity", response.ArtistName);
        Assert.Equal("Debut", response.AlbumName);
        Assert.Equal(245.5, response.Duration);
        Assert.False(response.Instrumental);
        Assert.Equal("Play the notes\nFeel the sound", response.PlainLyrics);
        Assert.Contains("[00:12.34]", response.SyncedLyrics);

        string serialized = JsonSerializer.Serialize(response, options);
        Assert.Contains("Octave Symphony", serialized);
    }

    [Fact]
    public void TagLibSharp_ParsesWavFile_WithoutReflectionOrMissingMemberExceptions()
    {
        // Creates a valid minimal PCM WAV in memory and validates TagLib# can instantiate
        // its internal format parsers without trimming stripping codec implementations.
        string tempWav = Path.Combine(Path.GetTempPath(), $"taglib_integrity_{Guid.NewGuid():N}.wav");
        try
        {
            CreateMinimalWavFile(tempWav, durationSeconds: 0.5);

            using var file = TagLib.File.Create(tempWav);
            Assert.NotNull(file);
            Assert.True(file.Properties.Duration.TotalMilliseconds > 0);
            Assert.Equal(44100, file.Properties.AudioSampleRate);
            Assert.Equal(2, file.Properties.AudioChannels);
        }
        finally
        {
            try { File.Delete(tempWav); } catch { }
        }
    }

    private static void CreateMinimalWavFile(string filePath, double durationSeconds)
    {
        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int subchunk2Size = (int)(durationSeconds * sampleRate * channels * (bitsPerSample / 8));
        int chunkSize = 36 + subchunk2Size;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(chunkSize);
        writer.Write("WAVE"u8.ToArray());

        // fmt chunk
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // subchunk1 size
        writer.Write((short)1); // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
        writer.Write((short)(channels * (bitsPerSample / 8))); // block align
        writer.Write(bitsPerSample);

        // data chunk
        writer.Write("data"u8.ToArray());
        writer.Write(subchunk2Size);

        // Silent samples
        byte[] silence = new byte[subchunk2Size];
        writer.Write(silence);
    }

    [Fact]
    public void OctavePackage_WhenPresent_ContainsAllRequiredComponents()
    {
        // Locates repo root from test base directory
        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(current);
        string? repoRoot = null;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Octave.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);
        string packageDir = Path.Combine(repoRoot, "Octave_Package");
        if (!Directory.Exists(packageDir))
        {
            return;
        }

        // 1. Primary Executable & Core DLLs
        Assert.True(File.Exists(Path.Combine(packageDir, "Octave.Desktop.exe")), "Octave.Desktop.exe is missing");
        Assert.True(File.Exists(Path.Combine(packageDir, "Octave.Desktop.dll")), "Octave.Desktop.dll is missing");
        Assert.True(File.Exists(Path.Combine(packageDir, "Octave.Core.dll")), "Octave.Core.dll is missing");

        // 2. WinUI PRI resource indexes
        Assert.True(File.Exists(Path.Combine(packageDir, "Octave.Desktop.pri")), "Octave.Desktop.pri is missing");
        Assert.True(File.Exists(Path.Combine(packageDir, "resources.pri")), "resources.pri is missing");

        // 3. Root XBFs
        string[] rootXbfs = ["App.xbf", "MainWindow.xbf", "MiniPlayerWindow.xbf"];
        foreach (var xbf in rootXbfs)
        {
            Assert.True(File.Exists(Path.Combine(packageDir, xbf)), $"Root XBF {xbf} is missing");
        }

        // 4. Views XBFs
        string[] viewXbfs = [
            "AlbumsPage.xbf", "ArtistsPage.xbf", "EntityDetailPage.xbf", "HomePage.xbf",
            "LibraryPage.xbf", "NowPlayingPage.xbf", "PlaylistDetailPage.xbf", "PlaylistsPage.xbf",
            "SearchResultsPage.xbf", "SettingsPage.xbf"
        ];
        foreach (var xbf in viewXbfs)
        {
            string path = Path.Combine(packageDir, "Views", xbf);
            Assert.True(File.Exists(path), $"View XBF {xbf} is missing at {path}");
        }

        // 5. Controls XBFs
        string spectrumPath = Path.Combine(packageDir, "Controls", "SpectrumVisualizerControl.xbf");
        Assert.True(File.Exists(spectrumPath), $"Spectrum visualizer XBF is missing at {spectrumPath}");

        string[] nowPlayingXbfs = ["AlbumArtPanel.xbf", "CreditsPanel.xbf", "LyricsPanel.xbf", "QueuePanel.xbf"];
        foreach (var xbf in nowPlayingXbfs)
        {
            string path = Path.Combine(packageDir, "Controls", "NowPlaying", xbf);
            Assert.True(File.Exists(path), $"NowPlaying panel XBF {xbf} is missing at {path}");
        }

        // 6. Assets
        string[] assets = ["AppIcon.ico", "PlaceholderAlbum.png", "PlaceholderArtist.png", "SplashScreen.scale-200.png"];
        foreach (var asset in assets)
        {
            string path = Path.Combine(packageDir, "Assets", asset);
            Assert.True(File.Exists(path), $"Asset {asset} is missing at {path}");
        }

        // 7. Native BASS and Decoder Addons
        string[] bassBinaries = [
            "bass.dll", "bass_fx.dll", "bassflac.dll", "bassopus.dll", "bass_aac.dll",
            "bassalac.dll", "basswma.dll", "bassdsd.dll", "bassape.dll", "basswv.dll"
        ];
        foreach (var bin in bassBinaries)
        {
            string path = Path.Combine(packageDir, bin);
            Assert.True(File.Exists(path), $"BASS binary {bin} is missing at {path}");
        }

        // 8. Native SQLite
        Assert.True(File.Exists(Path.Combine(packageDir, "e_sqlite3.dll")), "e_sqlite3.dll is missing");

        // 9. WindowsAppSDK & .NET Self-Contained Runtime
        Assert.True(File.Exists(Path.Combine(packageDir, "Microsoft.ui.xaml.dll")), "Microsoft.ui.xaml.dll is missing");
        Assert.True(File.Exists(Path.Combine(packageDir, "coreclr.dll")), "coreclr.dll is missing");
    }
}
