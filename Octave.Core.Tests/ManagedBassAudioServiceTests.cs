using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Octave.Core.Services.Audio;
using Xunit;

namespace Octave.Core.Tests;

// =====================================================================
// Batch 5 — audio engine safety (§12.3 / §15 Batch 5)
//
// These tests exercise the REAL ManagedBass engine with generated PCM WAV
// fixtures (BASS core decodes WAV natively — no plugin needed), following
// the real-construction idiom established by the crossfade-clamp test.
// =====================================================================
public class ManagedBassAudioServiceTests : IDisposable
{
    private readonly string _dir;

    public ManagedBassAudioServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "octave_audio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    // Minimal RIFF/WAVE writer: 16-bit mono PCM silence. Content is irrelevant
    // to BASS's End-sync; only the duration matters for the timing scenarios.
    private static void WriteWavFile(string path, double seconds, int sampleRate = 8000)
    {
        int sampleCount = (int)(seconds * sampleRate);
        int dataBytes = sampleCount * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        void Tag(string s) => w.Write(Encoding.ASCII.GetBytes(s));

        Tag("RIFF");
        w.Write(36 + dataBytes);
        Tag("WAVE");
        Tag("fmt ");
        w.Write(16);              // PCM chunk size
        w.Write((short)1);        // PCM format
        w.Write((short)1);        // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);  // byte rate
        w.Write((short)2);        // block align
        w.Write((short)16);       // bits per sample
        Tag("data");
        w.Write(dataBytes);
        for (int i = 0; i < sampleCount; i++)
        {
            w.Write((short)0);
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void CrossfadeSkip_InFinalStretch_OldTrackReportsItsOwnIdentity_NotIncoming()
    {
        // AUDIO-07 regression: skipping (crossfade) inside a track's final
        // stretch used to let the outgoing stream's still-registered End-sync
        // fire and report the INCOMING track's global session/uri - which made
        // QueueService believe the incoming track had already ended and skip it.
        // Each stream must report its own (sessionId, uri): exactly two
        // deliveries, in order - A first (its natural end during the fade),
        // then B (its own natural end).
        using var service = new ManagedBassAudioService { CrossfadeDurationMs = 250 };

        string fileA = Path.Combine(_dir, "a.wav"); // long enough that we can skip "in its final stretch"
        string fileB = Path.Combine(_dir, "b.wav"); // short so its own end arrives quickly after the skip
        WriteWavFile(fileA, seconds: 3.0);
        WriteWavFile(fileB, seconds: 1.0);

        long sessionA = 0;
        long sessionB = 0;
        var ended = new List<(long SessionId, string SourceUri)>();
        var endedLock = new object();
        service.TrackEnded += (_, e) =>
        {
            lock (endedLock)
            {
                ended.Add((e.SessionId, e.SourceUri));
            }
        };

        sessionA = service.Play(fileA);
        Assert.NotEqual(0, sessionA);

        // Wait until A is comfortably inside its final stretch but not over.
        Assert.True(
            WaitFor(() => service.PositionSeconds >= 2.5, TimeSpan.FromSeconds(15)),
            $"track A never approached its natural end (position: {service.PositionSeconds:0.00}s)");

        sessionB = service.Play(fileB); // manual crossfade-skip while A is near/past its end

        Assert.True(
            WaitFor(() =>
            {
                lock (endedLock) { return ended.Count >= 2; }
            }, TimeSpan.FromSeconds(10)),
            $"expected two TrackEnded deliveries, saw {ended.Count}");

        // Let any straggler land before freezing expectations: a third delivery
        // would mean a lingering End-sync survived the detach.
        Thread.Sleep(400);

        List<(long SessionId, string SourceUri)> snapshot;
        lock (endedLock)
        {
            snapshot = new List<(long, string)>(ended);
        }

        Assert.Equal(2, snapshot.Count);
        Assert.Equal((sessionA, fileA), snapshot[0]); // the SKIPPED track reports itself…
        Assert.Equal((sessionB, fileB), snapshot[1]); // …and the incoming track ends exactly once.
    }

    [Fact]
    public void Play_MissingFile_FiresTrackEndedOnceWithLocalSessionIdentity()
    {
        // Pins the failure-path contract the queue relies on (the one case where
        // TrackEnded carries this call's own identity even though nothing played).
        using var service = new ManagedBassAudioService();
        string missing = Path.Combine(_dir, "does_not_exist.wav");

        // Subscribe FIRST: the delivery is marshalled to the thread pool and can
        // land before Play() returns.
        var deliveries = new List<(long SessionId, string SourceUri)>();
        var lockObj = new object();
        service.TrackEnded += (_, e) =>
        {
            lock (lockObj) { deliveries.Add((e.SessionId, e.SourceUri)); }
        };

        long returnedSession = service.Play(missing);
        Assert.NotEqual(0, returnedSession);

        Assert.True(
            WaitFor(() => { lock (lockObj) { return deliveries.Count >= 1; } }, TimeSpan.FromSeconds(5)),
            "load failure never raised TrackEnded");

        Thread.Sleep(200); // straggler window: there must be exactly ONE delivery
        lock (lockObj)
        {
            Assert.Single(deliveries);
            Assert.Equal(returnedSession, deliveries[0].SessionId);
            Assert.Equal(missing, deliveries[0].SourceUri);
        }
    }

    [Fact]
    public void CrossfadeDurationMs_ClampsToTenSecondMaximum()
    {
        using var service = new ManagedBassAudioService();
        service.CrossfadeDurationMs = 999_999;
        Assert.Equal(10000, service.CrossfadeDurationMs);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndSafeWithoutPlayback()
    {
        // AUDIO-04: teardown is deterministic-only (no finalizer). Disposing
        // twice - and without ever playing - must be safe and side-effect free.
        var service = new ManagedBassAudioService();
        Assert.False(service.IsSilentFallback); // AUDIO-02 API surface: default state

        service.Dispose();
        service.Dispose(); // idempotent, no throw
    }
}
