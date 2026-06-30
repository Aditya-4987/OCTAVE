using CommunityToolkit.Mvvm.ComponentModel;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Octave_Desktop.ViewModels;

public partial class AlbumsViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IQueueService _queueService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public ObservableCollection<Album> Items { get; } = new();

    private readonly EventHandler _libraryUpdatedHandler;

    public AlbumsViewModel(ILibraryService libraryService, IQueueService queueService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _libraryUpdatedHandler = (s, e) =>
        {
            _ = LoadAsync();
        };
        _libraryService.LibraryUpdated += _libraryUpdatedHandler;
    }

    public void Cleanup()
    {
        _libraryService.LibraryUpdated -= _libraryUpdatedHandler;
    }

    public async Task LoadAsync()
    {
        var albums = await _libraryService.GetAllAlbumsAsync();
        _dispatcher.TryEnqueue(() =>
        {
            Items.Clear();
            foreach (var album in albums)
            {
                Items.Add(album);
            }
        });
    }
}
