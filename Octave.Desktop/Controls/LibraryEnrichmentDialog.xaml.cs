using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave_Desktop.ViewModels;

namespace Octave_Desktop.Controls;

public sealed partial class LibraryEnrichmentDialog : ContentDialog
{
    public LibraryEnrichmentViewModel ViewModel { get; }

    public LibraryEnrichmentDialog(LibraryEnrichmentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        // VM-02: the dialog owns the transient VM's lifetime - detach its
        // singleton-service subscriptions when the dialog closes.
        Closed += (s, e) => ViewModel.Cleanup();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityInverted(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool BoolToInverted(bool value) => !value;
    public bool IsQueueEmpty(int count) => count == 0;
    public string FormatProgressPercent(double pct) => $"{pct:0.#}%";
    public string FormatQueueCount(int count) => count > 0 ? $"({count} items)" : "";
    public bool IsNotResolvedAndNotProcessing(bool isResolved, bool isProcessing) => !isResolved && !isProcessing;

    public static string FormatLocalSummary(string title, string artist, string album, int year)
    {
        string y = year > 0 ? $" ({year})" : "";
        string alb = !string.IsNullOrWhiteSpace(album) ? $" · {album}{y}" : "";
        return $"{title} · {artist}{alb}";
    }

    public static string FormatProposedSummary(string title, string artist, string album, string year)
    {
        string y = year != "—" && !string.IsNullOrWhiteSpace(year) ? $" ({year})" : "";
        string alb = album != "—" && !string.IsNullOrWhiteSpace(album) ? $" · {album}{y}" : "";
        return $"{title} · {artist}{alb}";
    }
}
