using System;
using Octave.Core.Models;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Batch 13 / NP-19+NP-20+NP-21: the Now Playing queue rows bind
/// {x:Bind ..., Mode=OneWay} against QueueItem.IsPlaying and QueueItem.ArtworkUrl.
/// That only updates live if the class raises PropertyChanged on real changes -
/// and must NOT raise on no-op assignments (which would re-render rows for nothing).
/// </summary>
public class QueueItemTests
{
    private static Track MakeTrack(string id) => new(
        id, $"Track {id}", "ar1", "Artist", "al1", "Album",
        180, $"C:\\music\\{id}.mp3", 1, 2024, DateTime.UtcNow);

    [Fact]
    public void IsPlaying_RaisesPropertyChanged_OnRealChange()
    {
        var item = new QueueItem { Id = "q1", Track = MakeTrack("t1") };
        string? observed = null;
        item.PropertyChanged += (_, e) => observed = e.PropertyName;

        item.IsPlaying = true;

        Assert.True(item.IsPlaying);
        Assert.Equal(nameof(QueueItem.IsPlaying), observed);
    }

    [Fact]
    public void IsPlaying_SuppressesPropertyChanged_OnSameValue()
    {
        var item = new QueueItem { Id = "q1", Track = MakeTrack("t1"), IsPlaying = true };
        int raiseCount = 0;
        item.PropertyChanged += (_, _) => raiseCount++;

        item.IsPlaying = true; // no-op

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void ArtworkUrl_RaisesPropertyChanged_OnRealChange_AndOnNullOut()
    {
        var item = new QueueItem { Id = "q1", Track = MakeTrack("t1") };
        int raiseCount = 0;
        item.PropertyChanged += (_, _) => raiseCount++;

        item.ArtworkUrl = "ArtworkCache/abc.jpg";
        Assert.Equal(1, raiseCount);

        // Distinct value raises again...
        item.ArtworkUrl = "";
        Assert.Equal(2, raiseCount);

        // ...but assigning the identical reference does not (hydration guard).
        item.ArtworkUrl = "";
        Assert.Equal(2, raiseCount);
    }
}
