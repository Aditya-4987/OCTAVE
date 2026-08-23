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
    public void CrossfadeSkip_SkippedTrackNeverReports_IncomingEndsOnceWithOwnIdentity()
    {
        // AUDIO-07 regression: skipping (crossfade) used to leave the outgoing
        // stream's End-sync registered against GLOBAL session/uri state, so a
        // late sync fired reporting the INCOMING track's identity - which made
        // QueueService believe the incoming track had already ended and skip
        // it again. Post-fix contract pinned here: a skipped stream has its
        // sync detached at skip time and must NEVER raise TrackEnded - not
        // even when skipped inside its final stretch, where the old
        // global-identity bug was likeliest to misfire - while the incoming
        // track ends exactly once and reports its OWN (sessionId, uri).
        //
        // (NF-02: an earlier draft expected the SKIPPED track to deliver its
        // own natural end "during the fade", but the fade always completes
        // before that end and the detach makes silence the intended behavior -
        // the old expectation only passed when the position poll overshot past
        // the track's natural end before the skip landed.)
        using var service = new ManagedBassAudioService { CrossfadeDurationMs = 250 };

        string fileA = Path.Combine(_dir, "a.wav"); // long enough that skipping in its final stretch is unambiguous
        string fileB = Path.Combine(_dir, "b.wav"); // short so its own end arrives quickly after the skip
        WriteWavFile(fileA, seconds: 8.0);
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

        // Land inside A's final stretch but comfortably clear of BOTH
        // boundaries: deep enough that the old bug would have fired here, yet
        // far from A's natural end that the fade (complete by ~5.9s) cannot be
        // mistaken for A ending on its own. The 500ms-wide window dwarfs the
        // 20ms poll step.
        Assert.True(
            WaitFor(() =>
            {
                double p = service.PositionSeconds;
                return p >= 5.0 && p < 5.5;
            }, TimeSpan.FromSeconds(15)),
            $"track A never reached the skip window (position: {service.PositionSeconds:0.00}s)");

        sessionB = service.Play(fileB); // manual crossfade-skip inside A's final stretch

        Assert.True(
            WaitFor(() =>
            {
                lock (endedLock) { return ended.Count >= 1; }
            }, TimeSpan.FromSeconds(10)),
            $"incoming track never reported its own end (deliveries: {ended.Count})");

        // Straggler window: any additional delivery would mean the skipped
        // stream's End-sync survived the detach (the exact AUDIO-07 bug).
        Thread.Sleep(400);

        List<(long SessionId, string SourceUri)> snapshot;
        lock (endedLock)
        {
            snapshot = new List<(long, string)>(ended);
        }

        Assert.Single(snapshot);                      // the SKIPPED track stays silent…
        Assert.Equal((sessionB, fileB), snapshot[0]); // …and the incoming track ends exactly once, as itself.
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
