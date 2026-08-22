using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Octave.Core.Models;
using Octave_Desktop.ViewModels;
using Windows.Storage.Pickers;

namespace Octave_Desktop.Controls;

public sealed partial class MetadataEnrichmentDialog : ContentDialog
{
    public MetadataEnrichmentViewModel ViewModel { get; }

    public MetadataEnrichmentDialog(Track track)
    {
        ViewModel = App.Services.GetRequiredService<MetadataEnrichmentViewModel>();
        this.InitializeComponent();
        _ = ViewModel.InitializeAsync(track);
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            bool success = await ViewModel.ApplyChangesAsync();
            if (!success)
            {
                // Keep dialog open to show error on InfoBar
                args.Cancel = true;
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SearchAsync();
    }

    private async void BrowseArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");

            var hwnd = App.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                using var stream = await file.OpenReadAsync();
                using var mem = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(mem);
                byte[] bytes = mem.ToArray();

                ViewModel.SetCustomArtwork(file.Path, bytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MetadataEnrichmentDialog] Image pick failed: {ex.Message}");
        }
    }

    public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisibleWhenOr(bool v1, bool v2) => (v1 || v2) ? Visibility.Visible : Visibility.Collapsed;

    public string FormatNullableInt(int? value) => value.HasValue && value.Value > 0 ? value.Value.ToString() : "-";

    public SolidColorBrush GetConfidenceBadgeBackground(MatchConfidenceTier tier)
    {
        return tier switch
        {
            MatchConfidenceTier.ExactMatch => new SolidColorBrush(ColorHelper.FromArgb(220, 16, 124, 65)),     // Green
            MatchConfidenceTier.ProbableMatch => new SolidColorBrush(ColorHelper.FromArgb(220, 194, 114, 0)),   // Amber
            MatchConfidenceTier.AmbiguousMatch => new SolidColorBrush(ColorHelper.FromArgb(220, 100, 100, 100)), // Gray
            _ => new SolidColorBrush(ColorHelper.FromArgb(220, 180, 40, 40))                                    // Red / Dim
        };
    }

    public string GetArtworkStatusText(CandidatePreview? candidate, string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            return $"Selected Local File: {Path.GetFileName(customPath)}";
        }

        if (candidate?.ProposedArtworkBytes != null && candidate.ProposedArtworkBytes.Length > 0)
        {
            return $"Online cover found on Cover Art Archive ({candidate.ProposedArtworkBytes.Length / 1024} KB)";
        }

        if (!string.IsNullOrWhiteSpace(candidate?.ArtworkUrl.ProposedValue))
        {
            return $"Cover Art URL available: {candidate.ArtworkUrl.ProposedValue}";
        }

        return "No new online cover found for this release.";
    }
}
