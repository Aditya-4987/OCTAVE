using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Octave_Desktop.Controls.NowPlaying;

public sealed partial class AlbumArtPanel : UserControl
{
    public static readonly DependencyProperty TrackTitleProperty =
        DependencyProperty.Register(nameof(TrackTitle), typeof(string), typeof(AlbumArtPanel), new PropertyMetadata("No Track Playing"));

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(AlbumArtPanel), new PropertyMetadata("Octave Core"));

    public static readonly DependencyProperty AlbumTitleProperty =
        DependencyProperty.Register(nameof(AlbumTitle), typeof(string), typeof(AlbumArtPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty ArtworkUrlProperty =
        DependencyProperty.Register(nameof(ArtworkUrl), typeof(string), typeof(AlbumArtPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(AlbumArtPanel), new PropertyMetadata(false));

    public event RoutedEventHandler? PlayPauseRequested;

    public string TrackTitle
    {
        get => (string)GetValue(TrackTitleProperty);
        set => SetValue(TrackTitleProperty, value);
    }

    public string ArtistName
    {
        get => (string)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    public string AlbumTitle
    {
        get => (string)GetValue(AlbumTitleProperty);
        set => SetValue(AlbumTitleProperty, value);
    }

    public string? ArtworkUrl
    {
        get => (string?)GetValue(ArtworkUrlProperty);
        set => SetValue(ArtworkUrlProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public AlbumArtPanel()
    {
        InitializeComponent();
        ArtButton.PointerEntered += (s, e) => HoverOverlay.Opacity = 1.0;
        ArtButton.PointerExited += (s, e) => HoverOverlay.Opacity = 0.0;
    }

    private void ArtButton_Click(object sender, RoutedEventArgs e)
    {
        PlayPauseRequested?.Invoke(this, e);
    }

    public string IsPlayingGlyph(bool isPlaying) => isPlaying ? "\uE769" : "\uE768";

    public Visibility HasTextVisibility(string text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
