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

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadFoldersAsync();
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

                    var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
                    await libraryService.DeleteTrackAsync(track.Id);

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
                Title = "Move File to Recycle Bin?",
                Content = $"Are you sure you want to move this file to the Recycle Bin?\n{track.SourceUri}",
                PrimaryButtonText = "Move to Recycle Bin",
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
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            track.SourceUri,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }

                    var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
                    await libraryService.DeleteTrackAsync(track.Id);

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
