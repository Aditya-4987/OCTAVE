using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using System;
using System.Threading.Tasks;

namespace Octave_Desktop.Views;

public sealed partial class SearchResultsPage : Page
{
    public SearchViewModel ViewModel { get; }
    private readonly System.Collections.Generic.List<Button> _playButtons = new();

    public SearchResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        this.Unloaded += SearchResultsPage_Unloaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void SearchResultsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Cleanup();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(SearchViewModel.IsCurrentlyPlaying))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _playButtons.RemoveAll(btn => btn.XamlRoot == null);
                foreach (var btn in _playButtons)
                {
                    UpdatePlayButtonIcon(btn);
                }
            });
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query)
        {
            SearchQueryText.Text = $"Results for \"{query}\"";
            await ViewModel.ExecuteSearchAsync(query);
        }
    }

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
        {
            ViewModel.PlayTrackCommand.Execute(track);
        }
    }

    private void PlayButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && !_playButtons.Contains(btn))
        {
            _playButtons.Add(btn);
            UpdatePlayButtonIcon(btn);
        }
    }

    private void PlayButton_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button btn)
        {
            UpdatePlayButtonIcon(btn);
        }
    }

    private void UpdatePlayButtonIcon(Button btn)
    {
        if (btn.Content is FontIcon fontIcon && btn.DataContext is Track track)
        {
            bool isCurrent = track.Id == ViewModel.CurrentPlayingTrackId;
            bool isPlaying = ViewModel.IsCurrentlyPlaying;
            fontIcon.Glyph = (isCurrent && isPlaying) ? "\uE769" : "\uE768";
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Track track)
        {
            if (track.Id == ViewModel.CurrentPlayingTrackId)
            {
                if (ViewModel.IsCurrentlyPlaying)
                {
                    ViewModel.PausePlayback();
                }
                else
                {
                    ViewModel.ResumePlayback();
                }
            }
            else
            {
                ViewModel.PlayTrackCommand.Execute(track);
            }
        }
    }

    private void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Album album)
        {
            Frame.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Album, album.Id));
        }
    }

    private void ArtistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Artist artist)
        {
            Frame.Navigate(typeof(EntityDetailPage), new EntityNavigationParameter(EntityType.Artist, artist.Id));
        }
    }

    public Visibility VisibilityFromCount(int count)
    {
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility NoResultsVisibility(int trackCount, int albumCount, int artistCount)
    {
        return (trackCount == 0 && albumCount == 0 && artistCount == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string GetProviderGlyph(string provider)
    {
        return provider.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE774";
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.ToString(@"m\:ss");
    }
}
