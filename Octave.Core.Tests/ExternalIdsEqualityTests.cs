using System;
using System.Collections.Generic;
using Octave.Core.Models;
using Xunit;

namespace Octave.Core.Tests;

/// <summary>
/// Value-equality and GetId alignment coverage for <see cref="ExternalIds"/>
/// (AR-03 / ENR-01 — Batch 2). The generated record equality compared the
/// AdditionalIds dictionary BY REFERENCE; the manual implementation must make
/// logically identical snapshots equal regardless of instance or key casing.
/// </summary>
public class ExternalIdsEqualityTests
{
    [Fact]
    public void Equal_SameContentDifferentDictionaryInstances_AreEqual()
    {
        var a = new ExternalIds("mb-1", Isrc: "ISRC1", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzReleaseId"] = "rel-1" });
        var b = new ExternalIds("mb-1", Isrc: "ISRC1", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzReleaseId"] = "rel-1" });

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equal_DictionaryKeyCasingOnly_IsIgnored()
    {
        var a = new ExternalIds("mb-1", AdditionalIds: new Dictionary<string, string> { ["MusicBrainzReleaseId"] = "rel-1" });
        var b = new ExternalIds("mb-1", AdditionalIds: new Dictionary<string, string> { ["musicbrainzreleaseid"] = "rel-1" });

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void NotEqual_DifferingScalarOrValues()
    {
        var baseIds = new ExternalIds("mb-1", SpotifyId: "sp-1", Isrc: "ISRC1");

        Assert.False(baseIds.Equals(new ExternalIds("mb-OTHER", SpotifyId: "sp-1", Isrc: "ISRC1")));
        Assert.False(baseIds.Equals(new ExternalIds("mb-1", SpotifyId: "sp-X", Isrc: "ISRC1")));
        // Identifier VALUES are Ordinal (case-sensitive)
        Assert.False(baseIds.Equals(new ExternalIds("MB-1")));
        Assert.False(baseIds.Equals(new ExternalIds("mb-1", Isrc: "isrc1")));

        var withDict = baseIds with { AdditionalIds = new Dictionary<string, string> { ["k"] = "v" } };
        Assert.False(withDict.Equals(baseIds));
        Assert.False(withDict.Equals(baseIds with { AdditionalIds = new Dictionary<string, string> { ["k"] = "OTHER" } }));
        Assert.False(withDict.Equals(baseIds with { AdditionalIds = new Dictionary<string, string> { ["other"] = "v" } }));

        Assert.False(baseIds.Equals(null));
    }

    [Fact]
    public void Equals_EmptyInstances_AreEqualRegardlessOfInstance()
    {
        var a = ExternalIds.Empty;
        var b = new ExternalIds();

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void GetId_KnownAliases_RouteToScalarProperties()
    {
        var ids = new ExternalIds(MusicBrainzId: "mb-1", SpotifyId: "sp-1", DiscogsId: "dc-1", Isrc: "is-1", AcoustId: "ac-1");

        Assert.Equal("mb-1", ids.GetId("musicbrainz"));
        Assert.Equal("mb-1", ids.GetId("MUSICBRAINZ")); // switch is case-insensitive
        Assert.Equal("mb-1", ids.GetId("mbid"));
        Assert.Equal("sp-1", ids.GetId("Spotify"));
        Assert.Equal("dc-1", ids.GetId("discogs"));
        Assert.Equal("is-1", ids.GetId("ISRC"));
        Assert.Equal("ac-1", ids.GetId("acoustid"));
    }

    [Fact]
    public void GetId_AdditionalKeys_LookupIsCaseInsensitive()
    {
        // AR-03: a stored lowercase key used to be invisible to an exact-cased lookup
        // and vice versa; the fallback scan must be case-insensitive like the switch.
        var ids = new ExternalIds(AdditionalIds: new Dictionary<string, string>
        {
            ["musicbrainzreleaseid"] = "rel-1",
            ["MusicBrainzArtistId"] = "art-1"
        });

        Assert.Equal("rel-1", ids.GetId("MusicBrainzReleaseId"));
        Assert.Equal("rel-1", ids.GetId("MUSICBRAINZRELEASEID"));
        Assert.Equal("art-1", ids.GetId("musicbrainzartistid"));
        Assert.Null(ids.GetId("MusicBrainzReleaseGroupId"));
        Assert.Null(ids.GetId(""));
        Assert.Null(ExternalIds.Empty.GetId("anything"));
    }
}
