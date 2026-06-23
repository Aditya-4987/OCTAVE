using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using System;

namespace Octave_Desktop.Views;

public sealed partial class EntityDetailPage : Page
{
    public EntityDetailViewModel ViewModel { get; }

    private readonly System.Collections.Generic.List<Button> _playButtons = new();

    public EntityDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<EntityDetailViewModel>();
        InitializeComponent();
        this.Unloaded += EntityDetailPage_Unloaded;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void EntityDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EntityDetailViewModel.CurrentPlayingTrackId) ||
            e.PropertyName == nameof(EntityDetailViewModel.IsCurrentlyPlaying))
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

        if (e.Parameter is EntityNavigationParameter param)
        {
            await ViewModel.LoadEntityAsync(param);
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

    public static string GetProviderGlyph(string provider)
    {
        return provider.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "\uE770" : "\uE774";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.ToString(@"m\:ss");
    }
}
