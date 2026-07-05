using CommunityToolkit.Mvvm.ComponentModel;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const int SectionLimit = 8;

    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public ObservableCollection<Track> RecentlyPlayed { get; } = new();
    public ObservableCollection<Track> MostPlayed { get; } = new();
    public ObservableCollection<Track> LastAdded { get; } = new();
    public ObservableCollection<Track> Favorites { get; } = new();

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
        var recent = await _libraryService.GetRecentlyPlayedAsync(SectionLimit);
        var most = await _libraryService.GetMostPlayedAsync(SectionLimit);
        var lastAdded = await _libraryService.GetLastAddedAsync(SectionLimit);
        var favorites = await _libraryService.GetFavoritesAsync();

        _dispatcher.TryEnqueue(() =>
        {
            Fill(RecentlyPlayed, recent);
            Fill(MostPlayed, most);
            Fill(LastAdded, lastAdded);
            Fill(Favorites, favorites);
        });
    }

    private async Task LoadFavoritesAsync()
    {
        var favorites = await _libraryService.GetFavoritesAsync();
        _dispatcher.TryEnqueue(() => Fill(Favorites, favorites));
    }

    private static void Fill(ObservableCollection<Track> target, System.Collections.Generic.List<Track> source)
    {
        target.Clear();
        foreach (var t in source) target.Add(t);
    }

    // Plays the clicked track in the context of its section.
    public void PlaySection(ObservableCollection<Track> section, Track track)
    {
        if (track == null || section == null) return;
        _queueService.Clear();
        _queueService.EnqueueRange(section);

        int index = -1;
        for (int i = 0; i < section.Count; i++)
        {
            if (section[i].Id == track.Id) { index = i; break; }
        }
        if (index >= 0) _queueService.PlayIndex(index);
    }
}
