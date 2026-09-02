using System;
using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave.Core.Models;

namespace Octave_Desktop.Controls.NowPlaying;

public sealed partial class CreditsPanel : UserControl
{
    public static readonly DependencyProperty ArtistsListProperty =
        DependencyProperty.Register(nameof(ArtistsList), typeof(IEnumerable), typeof(CreditsPanel), new PropertyMetadata(null, OnArtistsListChanged));

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(CreditsPanel), new PropertyMetadata("Unknown Artist"));

    public static readonly DependencyProperty ArtistArtworkUrlProperty =
        DependencyProperty.Register(nameof(ArtistArtworkUrl), typeof(string), typeof(CreditsPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty AlbumTitleProperty =
        DependencyProperty.Register(nameof(AlbumTitle), typeof(string), typeof(CreditsPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty AlbumArtworkUrlProperty =
        DependencyProperty.Register(nameof(AlbumArtworkUrl), typeof(string), typeof(CreditsPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty AlbumYearProperty =
        DependencyProperty.Register(nameof(AlbumYear), typeof(int), typeof(CreditsPanel), new PropertyMetadata(0));

    public static readonly DependencyProperty QualityDetailsProperty =
        DependencyProperty.Register(nameof(QualityDetails), typeof(AudioQualityDetails), typeof(CreditsPanel), new PropertyMetadata(null, OnQualityDetailsChanged));

    public static readonly DependencyProperty TrackGenreProperty =
        DependencyProperty.Register(nameof(TrackGenre), typeof(string), typeof(CreditsPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty TrackNumberProperty =
        DependencyProperty.Register(nameof(TrackNumber), typeof(int), typeof(CreditsPanel), new PropertyMetadata(0));

    public static readonly DependencyProperty DiscNumberProperty =
        DependencyProperty.Register(nameof(DiscNumber), typeof(int), typeof(CreditsPanel), new PropertyMetadata(0));

    public static readonly DependencyProperty ReplayGainProperty =
        DependencyProperty.Register(nameof(ReplayGain), typeof(object), typeof(CreditsPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty SourceUriProperty =
        DependencyProperty.Register(nameof(SourceUri), typeof(string), typeof(CreditsPanel), new PropertyMetadata(""));

    public event EventHandler<ArtistDisplayItem>? ArtistClicked;
    public event RoutedEventHandler? AlbumClicked;

    public IEnumerable? ArtistsList
    {
        get => (IEnumerable?)GetValue(ArtistsListProperty);
        set => SetValue(ArtistsListProperty, value);
    }

    public string ArtistName
    {
        get => (string)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    public string? ArtistArtworkUrl
    {
        get => (string?)GetValue(ArtistArtworkUrlProperty);
        set => SetValue(ArtistArtworkUrlProperty, value);
    }

    public string AlbumTitle
    {
        get => (string)GetValue(AlbumTitleProperty);
        set => SetValue(AlbumTitleProperty, value);
    }

    public string? AlbumArtworkUrl
    {
        get => (string?)GetValue(AlbumArtworkUrlProperty);
        set => SetValue(AlbumArtworkUrlProperty, value);
    }

    public int AlbumYear
    {
        get => (int)GetValue(AlbumYearProperty);
        set => SetValue(AlbumYearProperty, value);
    }

    public AudioQualityDetails? QualityDetails
    {
        get => (AudioQualityDetails?)GetValue(QualityDetailsProperty);
        set => SetValue(QualityDetailsProperty, value);
    }

    public string TrackGenre
    {
        get => (string)GetValue(TrackGenreProperty);
        set => SetValue(TrackGenreProperty, value);
    }

    public int TrackNumber
    {
        get => (int)GetValue(TrackNumberProperty);
        set => SetValue(TrackNumberProperty, value);
    }

    public int DiscNumber
    {
        get => (int)GetValue(DiscNumberProperty);
        set => SetValue(DiscNumberProperty, value);
    }

    public object? ReplayGain
    {
        get => GetValue(ReplayGainProperty);
        set => SetValue(ReplayGainProperty, value);
    }

    public string SourceUri
    {
        get => (string)GetValue(SourceUriProperty);
        set => SetValue(SourceUriProperty, value);
    }

    public string FormatText(AudioQualityDetails? details) => details?.StreamQuality ?? "Lossless Stream";
    public string OutputDeviceNameText(AudioQualityDetails? details)
    {
        if (details == null) return "Default Playback Device";
        if (!string.IsNullOrWhiteSpace(details.OutputDeviceType) && details.OutputDeviceType != "Audio Endpoint")
        {
            return $"{details.OutputDeviceName} ({details.OutputDeviceType})";
        }
        return details.OutputDeviceName;
    }
    public string DecoderEngineText(AudioQualityDetails? details) => details?.DecoderEngine ?? "ManagedBASS Engine";
    public string OutputDeviceQualityText(AudioQualityDetails? details)
    {
        if (details == null) return "DirectSound / WASAPI";
        if (details.IsBitMatched)
        {
            return $"{details.OutputDeviceQuality} [Bit-Matched]";
        }
        return details.OutputDeviceQuality;
    }

    public string TrackAndDiscText(int trackNum, int discNum)
    {
        if (trackNum > 0 && discNum > 0) return $"Track {trackNum} (Disc {discNum})";
        if (trackNum > 0) return $"Track {trackNum}";
        if (discNum > 0) return $"Disc {discNum}";
        return "-";
    }

    public string ReplayGainDisplay(object? gain)
    {
        if (gain is double d && Math.Abs(d) > 0.001) return $"{d:+0.00;-0.00} dB";
        if (gain is float f && Math.Abs(f) > 0.001f) return $"{f:+0.00;-0.00} dB";
        return "-";
    }
    public string GenreDisplay(string genre) => !string.IsNullOrWhiteSpace(genre) ? genre : "Unknown Genre";
    public string BitDepthAndChannelsText(AudioQualityDetails? details)
    {
        if (details == null) return "Stereo (2.0)";
        string bitStr = details.BitDepth > 0 ? $"{details.BitDepth}-bit" : "";
        string chanStr = !string.IsNullOrWhiteSpace(details.ChannelsText) ? details.ChannelsText : "Stereo";
        return !string.IsNullOrWhiteSpace(bitStr) ? $"{bitStr} / {chanStr}" : chanStr;
    }

    public CreditsPanel()
    {
        InitializeComponent();
    }

    private static void OnArtistsListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CreditsPanel panel)
        {
            panel.ArtistsItemsControl.ItemsSource = panel.ArtistsList;
        }
    }

    private static void OnQualityDetailsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CreditsPanel panel)
        {
            panel.Bindings.Update();
        }
    }

    private void ArtistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ArtistDisplayItem item)
        {
            ArtistClicked?.Invoke(this, item);
        }
    }

    private void AlbumTile_Click(object sender, RoutedEventArgs e)
    {
        AlbumClicked?.Invoke(this, e);
    }

    public Visibility HasTextVisibility(string text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AlbumYearVisibility(int year) => year > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string AlbumYearText(int year) => year > 0 ? $"Released in {year}" : "";
}
