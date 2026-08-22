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
    public ExternalDataSettingsViewModel ExternalSettings { get; }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityInverted(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool BoolToInverted(bool value) => !value;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        ExternalSettings = App.Services.GetRequiredService<ExternalDataSettingsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadFoldersAsync();
        await ExternalSettings.InitializeAsync();
    }

    private void SettingToggled(object sender, RoutedEventArgs e)
    {
        _ = ExternalSettings.SaveCommand.ExecuteAsync(null);
    }

    private void SettingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = ExternalSettings.SaveCommand.ExecuteAsync(null);
    }

    private void SettingSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _ = ExternalSettings.SaveCommand.ExecuteAsync(null);
    }

    private void SettingCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        _ = ExternalSettings.SaveCommand.ExecuteAsync(null);
    }

    private void TheAudioDbPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            ExternalSettings.TheAudioDbApiKey = pb.Password;
            _ = ExternalSettings.SaveCommand.ExecuteAsync(null);
        }
    }

    private async void OpenLibraryEnrichment_Click(object sender, RoutedEventArgs e)
    {
        var vm = App.Services.GetRequiredService<LibraryEnrichmentViewModel>();
        var dialog = new Controls.LibraryEnrichmentDialog(vm)
        {
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
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
            if (ViewModel.MonitoredFolders.Contains(folder.Path))
            {
                var dialog = new ContentDialog
                {
                    Title = "Folder Already Added",
                    Content = $"The selected folder is already being monitored in your library:\n{folder.Path}",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

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
                    string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    string ext = System.IO.Path.GetExtension(fileName);
                    string destPath = System.IO.Path.Combine(destFolder.Path, fileName);

                    int collisionCounter = 1;
                    while (System.IO.File.Exists(destPath))
                    {
                        destPath = System.IO.Path.Combine(destFolder.Path, $"{fileNameWithoutExt} ({collisionCounter}){ext}");
                        collisionCounter++;
                    }

                    System.IO.File.Move(track.SourceUri, destPath);

                    var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
                    await libraryService.RelocateTrackAsync(track.Id, destPath);

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
                    bool moved = Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin(track.SourceUri);
                    if (moved)
                    {
                        var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();
                        await libraryService.DeleteTrackAsync(track.Id);

                        if (ViewModel.FindDuplicatesCommand.CanExecute(null))
                        {
                            ViewModel.FindDuplicatesCommand.Execute(null);
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[SettingsPage] Could not recycle track file: {track.SourceUri}");
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete file: {ex.Message}");
                }
            }
        }
    }

    private async void DeleteAllDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = System.Linq.Enumerable.ToList(ViewModel.Duplicates);
        int totalFilesToDelete = 0;
        foreach (var group in snapshot)
        {
            if (group.Tracks.Count > 1)
            {
                totalFilesToDelete += (group.Tracks.Count - 1);
            }
        }

        if (totalFilesToDelete == 0) return;

        var dialog = new ContentDialog
        {
            Title = "Delete All Duplicate Files?",
            Content = $"Are you sure you want to move {totalFilesToDelete} duplicate file(s) across {snapshot.Count} group(s) to the Recycle Bin?\nThe primary highest-quality copy in each group will be retained.",
            PrimaryButtonText = "Move All to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        int successCount = 0;
        int failedCount = 0;
        var libraryService = App.Services.GetRequiredService<Octave.Core.Services.Library.ILibraryService>();

        foreach (var group in snapshot)
        {
            for (int i = 1; i < group.Tracks.Count; i++)
            {
                var track = group.Tracks[i];
                try
                {
                    bool moved = Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin(track.SourceUri);
                    if (moved)
                    {
                        await libraryService.DeleteTrackAsync(track.Id);
                        successCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Bulk delete failed for {track.SourceUri}: {ex.Message}");
                    failedCount++;
                }
            }
        }

        var summaryDialog = new ContentDialog
        {
            Title = "Bulk Deletion Complete",
            Content = $"Successfully moved {successCount} duplicate file(s) to the Recycle Bin." + (failedCount > 0 ? $"\n{failedCount} file(s) could not be removed." : ""),
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };
        await summaryDialog.ShowAsync();

        if (ViewModel.FindDuplicatesCommand.CanExecute(null))
        {
            ViewModel.FindDuplicatesCommand.Execute(null);
        }
    }
}
