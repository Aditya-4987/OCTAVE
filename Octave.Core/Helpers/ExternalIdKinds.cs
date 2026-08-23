namespace Octave.Core.Helpers;

// CAA-01: ExternalIds.MusicBrainzId is entity-ambiguous - the track flow puts
// a RECORDING MBID in it while the album flows put RELEASE / RELEASE-GROUP
// ids in the very same field. Producers stamp this well-known AdditionalIds
// key so consumers (e.g. the Cover Art Archive provider) never have to guess
// what an id points at.
public static class ExternalIdKinds
{
    public const string EntityKind = "MusicBrainzEntityKind";
    public const string KindRelease = "release";
    public const string KindReleaseGroup = "release-group";
    public const string KindRecording = "recording";
}
