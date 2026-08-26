using CommunityToolkit.Mvvm.ComponentModel;
using Octave.Core.Models;

namespace Octave_Desktop.ViewModels;

// NF-37: the song-library list shows a per-row album thumbnail immediately
// right of the play/pause button. The Track record deliberately carries no
// artwork (see DomainModels: "the track record itself carries no artwork"),
// so — exactly as the Up-Next queue rows do (NP-19 / QueueItem) — each row is
// wrapped in this lightweight item whose ArtworkUrl is resolved from the
// track's album by the view model and pushed to the already-rendered row via
// PropertyChanged. Binding Track.SourceUri (the audio file path) would only
// ever render the placeholder.
public partial class LibraryTrackItem : ObservableObject
{
    public Track Track { get; }

    [ObservableProperty]
    public partial string? ArtworkUrl { get; set; }

    public LibraryTrackItem(Track track) => Track = track;

    // Passthroughs so the row DataTemplate's existing {x:Bind}s bind against the
    // wrapper unchanged. Track fields are immutable, so these need no change
    // notification; only ArtworkUrl (resolved after the row renders) does.
    public string Id => Track.Id;
    public string Title => Track.Title;
    public string ArtistName => Track.ArtistName;
    public double DurationSeconds => Track.DurationSeconds;
}
