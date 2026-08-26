using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Interfaces;
using Octave.Core.Models;
using Octave.Core.Services.Library;
using System;
using System.Threading.Tasks;

namespace Octave_Desktop.Helpers;

public static class TrackContextMenu
{
    public static async Task ShowAsync(
        FrameworkElement anchor, 
        Track track, 
        ILibraryService libraryService, 
        IPlaylistService playlistService, 
        IQueueService queueService,
        XamlRoot xamlRoot,
        Action<Track>? onRemoveFromPlaylist = null)
    {
        var flyout = new MenuFlyout();

        // Play Next
        var playNextItem = new MenuFlyoutItem { Text = "Play Next", Icon = new SymbolIcon(Symbol.Next) };
        playNextItem.Click += (s, e) => queueService.EnqueueNext(track);
        flyout.Items.Add(playNextItem);

        // Add to Queue
        var addQueueItem = new MenuFlyoutItem { Text = "Add to Queue", Icon = new SymbolIcon(Symbol.Add) };
        addQueueItem.Click += (s, e) => queueService.Enqueue(track);
        flyout.Items.Add(addQueueItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Favorite toggle
        bool isFavorite = await libraryService.IsFavoriteAsync(track.Id);
        var favItem = new MenuFlyoutItem { Text = isFavorite ? "Remove from favorites" : "Add to favorites", Icon = new SymbolIcon(Symbol.OutlineStar) };
        if (isFavorite) favItem.Icon = new SymbolIcon(Symbol.SolidStar);
        
        favItem.Click += async (s, e) =>
        {
            try { await libraryService.ToggleFavoriteAsync(track.Id); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Favorite toggle failed: {ex}"); }
        };
        flyout.Items.Add(favItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Playlists SubMenu
        var addToPlaylistSub = new MenuFlyoutSubItem { Text = "Add to Playlist...", Icon = new SymbolIcon(Symbol.List) };
        
        var playlists = await playlistService.GetPlaylistsAsync();
        foreach (var playlist in playlists)
        {
            string playlistId = playlist.Id;
            var item = new MenuFlyoutItem { Text = playlist.Title };
            item.Click += async (s, e) =>
            {
                try { await playlistService.AddTrackAsync(playlistId, track.Id); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Add to playlist failed: {ex}"); }
            };
            addToPlaylistSub.Items.Add(item);
        }

        if (playlists.Count > 0) addToPlaylistSub.Items.Add(new MenuFlyoutSeparator());

        var newItem = new MenuFlyoutItem { Text = "New playlist..." };
        newItem.Click += async (s, e) => await CreatePlaylistWithTrackAsync(track.Id, playlistService, xamlRoot);
        addToPlaylistSub.Items.Add(newItem);

        flyout.Items.Add(addToPlaylistSub);

        // Optional: Remove from current playlist
        if (onRemoveFromPlaylist != null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var removeItem = new MenuFlyoutItem { Text = "Remove from this Playlist", Icon = new SymbolIcon(Symbol.Remove) };
            removeItem.Click += (s, e) => onRemoveFromPlaylist(track);
            flyout.Items.Add(removeItem);
        }

        flyout.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private static async Task CreatePlaylistWithTrackAsync(string trackId, IPlaylistService playlistService, XamlRoot root)
    {
        try
        {
            if (root is null) return;
            var input = new TextBox { PlaceholderText = "Playlist name" };
            var dialog = new ContentDialog
            {
                Title = "New Playlist",
                Content = input,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var playlist = await playlistService.CreatePlaylistAsync(input.Text.Trim());
                await playlistService.AddTrackAsync(playlist.Id, trackId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Create playlist dialog failed: {ex}");
        }
    }
}
