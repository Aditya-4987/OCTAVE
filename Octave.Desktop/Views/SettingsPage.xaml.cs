using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Generic;

namespace Octave_Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public ShellViewModel ViewModel { get; }
    private bool _themeInitialized;

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Reflect the saved theme in the selector without triggering a re-apply.
        _themeInitialized = false;
        ThemeSelector.SelectedIndex = ThemeHelper.GetSavedTheme() switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        _themeInitialized = true;

        await ViewModel.LoadFoldersAsync();
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeInitialized) return;
        if (ThemeSelector.SelectedItem is not ComboBoxItem item) return;

        var theme = item.Tag?.ToString() switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        ThemeHelper.SaveTheme(theme);
        ThemeHelper.Apply(this.XamlRoot?.Content as FrameworkElement, theme);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        // Packaged WinUI 3 pickers must be associated with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            await ViewModel.AddFolderAsync(folder.Path);
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string path)
        {
            ViewModel.RemoveFolderCommand.Execute(path);
        }
    }

    // "N copies · mp3, flac" summary for a duplicate group.
    public static string DuplicateSummary(List<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return "";
        var formats = new List<string>();
        foreach (var t in tracks)
        {
            var ext = System.IO.Path.GetExtension(t.SourceUri)?.TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && !formats.Contains(ext)) formats.Add(ext);
        }
        return $"{tracks.Count} copies · {string.Join(", ", formats)}";
    }

    private async void MoveDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Track track)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);

            var destFolder = await picker.PickSingleFolderAsync();
            if (destFolder != null)
            {
                try
                {
                    string fileName = System.IO.Path.GetFileName(track.SourceUri);
                    string destPath = System.IO.Path.Combine(destFolder.Path, fileName);
                    System.IO.File.Move(track.SourceUri, destPath);

                    // Re-run the duplicates scan to update the UI
                    if (ViewModel.FindDuplicatesCommand.CanExecute(null))
                    {
                        ViewModel.FindDuplicatesCommand.Execute(null);
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to move file: {ex.Message}");
                }
            }
        }
    }

    private async void DeleteDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Track track)
        {
            var dialog = new ContentDialog
            {
                Title = "Permanently Delete File?",
                Content = $"Are you sure you want to permanently delete:\n{track.SourceUri}\nThis action cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    if (System.IO.File.Exists(track.SourceUri))
                    {
                        System.IO.File.Delete(track.SourceUri);
                    }

                    // Re-run the duplicates scan to update the UI
                    if (ViewModel.FindDuplicatesCommand.CanExecute(null))
                    {
                        ViewModel.FindDuplicatesCommand.Execute(null);
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete file: {ex.Message}");
                }
            }
        }
    }
}
