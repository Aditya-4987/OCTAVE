using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Octave_Desktop.ViewModels;

namespace Octave_Desktop.Controls;

// ENR-06: inline body of the Settings "Smart Library Enrichment" card. Replaces
// LibraryEnrichmentDialog (a fixed 720x620 ContentDialog that truncated its own
// action labels and clipped review-card buttons); the settings card expands
// vertically around this panel instead.
public sealed partial class LibraryEnrichmentPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(LibraryEnrichmentViewModel), typeof(LibraryEnrichmentPanel), new PropertyMetadata(null));

    // Set by SettingsPage when the card expands (fresh transient instance) and
    // nulled when it collapses - Cleanup() detaches the singleton service's
    // events so the panel-scoped VM can be collected (VM-02).
    public LibraryEnrichmentViewModel? ViewModel
    {
        get => (LibraryEnrichmentViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public LibraryEnrichmentPanel()
    {
        InitializeComponent();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityInverted(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool BoolToInverted(bool value) => !value;
    public string FormatProgressPercent(double pct) => $"{pct:0.#}%";
    public string FormatQueueCount(int count) => count > 0 ? $"({count} items)" : "";
}
