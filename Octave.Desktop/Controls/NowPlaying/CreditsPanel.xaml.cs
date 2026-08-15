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

    public string FormatText(AudioQualityDetails? details) => details?.StreamQuality ?? "Lossless Stream";
    public string OutputDeviceNameText(AudioQualityDetails? details) => details?.OutputDeviceName ?? "Default Playback Device";
    public string DecoderEngineText(AudioQualityDetails? details) => details?.DecoderEngine ?? "ManagedBASS Engine";
    public string OutputDeviceQualityText(AudioQualityDetails? details) => details?.OutputDeviceQuality ?? "DirectSound / WASAPI";

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
