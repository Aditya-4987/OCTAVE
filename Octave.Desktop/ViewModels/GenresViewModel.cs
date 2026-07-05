using CommunityToolkit.Mvvm.ComponentModel;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class GenresViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public ObservableCollection<string> Items { get; } = new();

    private readonly EventHandler _libraryUpdatedHandler;

    public GenresViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _libraryUpdatedHandler = (s, e) => { _ = LoadAsync(); };
        _libraryService.LibraryUpdated += _libraryUpdatedHandler;
    }

    public void Cleanup()
    {
        _libraryService.LibraryUpdated -= _libraryUpdatedHandler;
    }

    public async Task LoadAsync()
    {
        var genres = await _libraryService.GetGenresAsync();
        _dispatcher.TryEnqueue(() =>
        {
            Items.Clear();
            foreach (var genre in genres)
            {
                Items.Add(genre);
            }
        });
    }
}
