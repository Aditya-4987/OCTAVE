using CommunityToolkit.Mvvm.ComponentModel;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public record TrackDisplayItem(
    Track Track,
    string? ArtworkUrl
)
{
    public string Id => Track.Id;
    public string Title => Track.Title;
    public string ArtistName => Track.ArtistName;
    public string AlbumTitle => Track.AlbumTitle;
}

public partial class HomeViewModel : ObservableObject
{
    private const int SectionLimit = 8;

    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome back";

    [ObservableProperty]
    public partial Track? HeroTrack { get; set; }

    [ObservableProperty]
    public partial string HeroTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeroArtistName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? HeroArtworkUrl { get; set; }

    [ObservableProperty]
    public partial bool IsHeroFavorite { get; set; }

    // VM-06: last load failure, so a page can surface it; the primary fix is
    // that fire-and-forget loads no longer let exceptions vanish silently.
    [ObservableProperty]
    public partial string? LastError { get; set; }

    public ObservableCollection<TrackDisplayItem> QuickPlayItems { get; } = new();
    public ObservableCollection<TrackDisplayItem> RecentlyPlayed { get; } = new();
    public ObservableCollection<TrackDisplayItem> MostPlayed { get; } = new();
    public ObservableCollection<TrackDisplayItem> LastAdded { get; } = new();
    public ObservableCollection<TrackDisplayItem> Favorites { get; } = new();

    private readonly EventHandler _libraryUpdatedHandler;
    private readonly EventHandler _favoritesChangedHandler;

    public HomeViewModel(ILibraryService libraryService, IQueueService queueService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _libraryUpdatedHandler = (s, e) => { _ = LoadAsync(); };
        _favoritesChangedHandler = (s, e) => { _ = LoadFavoritesAsync(); };
        _libraryService.LibraryUpdated += _libraryUpdatedHandler;
        _libraryService.FavoritesChanged += _favoritesChangedHandler;
    }

    public void Cleanup()
    {
        _libraryService.LibraryUpdated -= _libraryUpdatedHandler;
        _libraryService.FavoritesChanged -= _favoritesChangedHandler;
    }

    public async Task LoadAsync()
    {
        try
        {
            var recent = await _libraryService.GetRecentlyPlayedAsync(SectionLimit);
            var most = await _libraryService.GetMostPlayedAsync(SectionLimit);
            var lastAdded = await _libraryService.GetLastAddedAsync(SectionLimit);
            var favorites = await _libraryService.GetFavoritesAsync();

            int hour = DateTime.Now.Hour;
            string greeting = hour switch
            {
                >= 5 and < 12 => "Good morning",
                >= 12 and < 18 => "Good afternoon",
                _ => "Good evening"
            };

            // Hydrate album artwork URLs for all tracks
            var allTracks = recent.Concat(most).Concat(lastAdded).Concat(favorites);
            var albumIds = allTracks.Select(t => t.AlbumId).Distinct();
            var albums = await _libraryService.GetAlbumsByIdsAsync(albumIds);
            var artMap = albums.ToDictionary(a => a.Id, a => a.ArtworkUrl);

            TrackDisplayItem ToDisplayItem(Track t) => new TrackDisplayItem(t, artMap.TryGetValue(t.AlbumId, out var url) ? url : null);

            var recentItems = recent.Select(ToDisplayItem).ToList();
            var mostItems = most.Select(ToDisplayItem).ToList();
            var lastAddedItems = lastAdded.Select(ToDisplayItem).ToList();
            var favoritesItems = favorites.Select(ToDisplayItem).ToList();

            // Combine unique tracks for the 6-tile Quick Play grid
            var quickItems = new System.Collections.Generic.List<TrackDisplayItem>();
            var seenIds = new System.Collections.Generic.HashSet<string>();

            void AddCandidates(System.Collections.Generic.List<TrackDisplayItem> list)
            {
                foreach (var item in list)
                {
                    if (quickItems.Count >= 6) break;
                    if (seenIds.Add(item.Id)) quickItems.Add(item);
                }
            }

            AddCandidates(recentItems);
            AddCandidates(favoritesItems);
            AddCandidates(mostItems);
            AddCandidates(lastAddedItems);

            var hero = recent.Count > 0 ? recent[0] : (most.Count > 0 ? most[0] : (lastAdded.Count > 0 ? lastAdded[0] : null));
            var heroArt = hero != null && artMap.TryGetValue(hero.AlbumId, out var art) ? art : null;
            bool isHeroFav = hero != null && await _libraryService.IsFavoriteAsync(hero.Id);

            _dispatcher.TryEnqueue(() =>
            {
                Greeting = greeting;
                HeroTrack = hero;
                HeroTitle = hero?.Title ?? string.Empty;
                HeroArtistName = hero?.ArtistName ?? string.Empty;
                HeroArtworkUrl = heroArt;
                IsHeroFavorite = isHeroFav;

                Fill(QuickPlayItems, quickItems);
                Fill(RecentlyPlayed, recentItems);
                Fill(MostPlayed, mostItems);
                Fill(LastAdded, lastAddedItems);
                Fill(Favorites, favoritesItems);
            });
        }
        catch (Exception ex)
        {
            // VM-06: this runs fire-and-forget off LibraryUpdated - an unhandled
            // throw used to vanish without a trace.
            LastError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[HomeViewModel] LoadAsync failed: {ex.Message}");
        }
    }

    public async Task<bool> ToggleHeroFavoriteAsync()
    {
        if (HeroTrack == null) return false;
        bool newFav = await _libraryService.ToggleFavoriteAsync(HeroTrack.Id);
        IsHeroFavorite = newFav;
        return newFav;
    }

    private async Task LoadFavoritesAsync()
    {
        try
        {
            var favorites = await _libraryService.GetFavoritesAsync();
            var albumIds = favorites.Select(t => t.AlbumId).Distinct().ToList();
            var artMap = new System.Collections.Generic.Dictionary<string, string?>();
            foreach (var albumId in albumIds)
            {
                var album = await _libraryService.GetAlbumByIdAsync(albumId);
                if (album != null) artMap[albumId] = album.ArtworkUrl;
            }

            var favoriteItems = favorites.Select(t => new TrackDisplayItem(t, artMap.TryGetValue(t.AlbumId, out var url) ? url : null)).ToList();
            bool isHeroFav = HeroTrack != null && await _libraryService.IsFavoriteAsync(HeroTrack.Id);
            _dispatcher.TryEnqueue(() =>
            {
                IsHeroFavorite = isHeroFav;
                Fill(Favorites, favoriteItems);
            });
        }
        catch (Exception ex)
        {
            // VM-06: same fire-and-forget exposure as LoadAsync.
            LastError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[HomeViewModel] LoadFavoritesAsync failed: {ex.Message}");
        }
    }

    private static void Fill(ObservableCollection<TrackDisplayItem> target, System.Collections.Generic.List<TrackDisplayItem> source)
    {
        // VM-10: skip the rebuild when the id sequence is unchanged - every
        // LibraryUpdated wave used to clear-and-refill all five sections.
        if (Helpers.CollectionDiff.SameIdSequence(target, source, i => i.Id)) return;

        target.Clear();
        foreach (var t in source) target.Add(t);
    }

    // Plays the clicked track in the context of its section.
    public void PlaySection(ObservableCollection<TrackDisplayItem> section, TrackDisplayItem item)
    {
        if (item == null || section == null) return;
        var tracks = section.Select(i => i.Track).ToList();
        _queueService.Clear();
        _queueService.EnqueueRange(tracks);

        int index = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id == item.Id) { index = i; break; }
        }
        if (index >= 0) _queueService.PlayIndex(index);
    }
}
