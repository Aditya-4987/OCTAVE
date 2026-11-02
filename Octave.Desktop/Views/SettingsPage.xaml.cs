using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Octave_Desktop.ViewModels;
using Octave.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;

namespace Octave_Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public ShellViewModel ViewModel { get; }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BoolToVisibilityInverted(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public bool BoolToInverted(bool value) => !value;
    public Visibility NotEmptyToVisibility(string? text) => !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    // UI-ST-03: check glyph on the EQ preset button whose curve matches the
    // current band gains.
    public Visibility PresetCheckVisible(string preset, string activePreset)
        => string.Equals(preset, activePreset, System.StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // The sleep-timer status line only carries information while a timer runs.
    public Visibility StatusVisibleWhenActive(string status)
        => string.Equals(status, "Off", System.StringComparison.Ordinal)
            ? Visibility.Collapsed
            : Visibility.Visible;

    private bool _isSyncingSleepTimerCombo;
    private bool _isSyncingActionCombo;

    public int GetActionIndex(SleepTimerCompletionAction action) => action switch
    {
        SleepTimerCompletionAction.StopPlayback => 0,
        SleepTimerCompletionAction.SleepPC => 1,
        SleepTimerCompletionAction.ShutdownPC => 2,
        SleepTimerCompletionAction.SignOut => 3,
        _ => 0
    };

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshAudioQuality();
        ViewModel.RefreshOutputDevices();
        await ViewModel.LoadFoldersAsync();
        UpdateSettingsSleepArc();
        SyncSleepTimerComboWithViewModel();
        SyncActionComboWithViewModel();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SleepTimerProgressFraction) || e.PropertyName == nameof(ShellViewModel.IsSleepTimerActive))
        {
            UpdateSettingsSleepArc();
        }

        if (e.PropertyName == nameof(ShellViewModel.ActiveSleepPresetTag) || e.PropertyName == nameof(ShellViewModel.IsSleepTimerActive))
        {
            SyncSleepTimerComboWithViewModel();
        }

        if (e.PropertyName == nameof(ShellViewModel.SelectedSleepTimerAction))
        {
            SyncActionComboWithViewModel();
        }
    }

    private void SyncSleepTimerComboWithViewModel()
    {
        if (SleepTimerCombo == null || ViewModel == null) return;
        _isSyncingSleepTimerCombo = true;
        try
        {
            if (!ViewModel.IsSleepTimerActive)
            {
                SleepTimerCombo.SelectedIndex = 0;
                return;
            }

            string activeTag = ViewModel.ActiveSleepPresetTag;
            for (int i = 0; i < SleepTimerCombo.Items.Count; i++)
            {
                if (SleepTimerCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, activeTag, StringComparison.OrdinalIgnoreCase))
                {
                    SleepTimerCombo.SelectedIndex = i;
                    return;
                }
            }

            // If custom duration or extended
            SleepTimerCombo.SelectedIndex = -1;
        }
        finally
        {
            _isSyncingSleepTimerCombo = false;
        }
    }

    private void UpdateSettingsSleepArc()
    {
        if (SettingsSleepArcPath == null) return;
        if (ViewModel.IsSleepTimerActive && ViewModel.SleepTimerProgressFraction > 0.001)
        {
            SettingsSleepArcPath.Data = Octave_Desktop.Helpers.SleepTimerHelper.CreateCountdownArcGeometry(ViewModel.SleepTimerProgressFraction, 22.0, 9.0);
        }
        else
        {
            SettingsSleepArcPath.Data = null;
        }
    }

    // Remade sleep timer: supports minutes, hours, EndOfTrack, and Custom dialog with live countdown
    private async void SleepTimerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSleepTimerCombo || ViewModel == null || sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        string? mode = item.Tag as string ?? item.Content?.ToString();

        if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (this.Content?.XamlRoot == null) return;
            var minutes = await Octave_Desktop.Helpers.SleepTimerHelper.ShowCustomSleepTimerDialogAsync(this.Content.XamlRoot);
            if (minutes.HasValue)
            {
                ViewModel.SetSleepTimerCustom(minutes.Value);
            }
            else
            {
                _isSyncingSleepTimerCombo = true;
                try
                {
                    cb.SelectedIndex = ViewModel.IsSleepTimerActive ? -1 : 0;
                }
                finally
                {
                    _isSyncingSleepTimerCombo = false;
                }
            }
            return;
        }

        ViewModel.SetSleepTimerCommand.Execute(mode);
    }

    private void SyncActionComboWithViewModel()
    {
        if (SleepTimerActionCombo == null || ViewModel == null) return;
        _isSyncingActionCombo = true;
        try
        {
            SleepTimerActionCombo.SelectedIndex = GetActionIndex(ViewModel.SelectedSleepTimerAction);
        }
        finally
        {
            _isSyncingActionCombo = false;
        }
    }

    private async void SleepTimerActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingActionCombo || ViewModel == null || sender is not ComboBox cb) return;

        int newIndex = cb.SelectedIndex;
        SleepTimerCompletionAction newAction = newIndex switch
        {
            0 => SleepTimerCompletionAction.StopPlayback,
            1 => SleepTimerCompletionAction.SleepPC,
            2 => SleepTimerCompletionAction.ShutdownPC,
            3 => SleepTimerCompletionAction.SignOut,
            _ => SleepTimerCompletionAction.StopPlayback
        };

        if (newAction == ViewModel.SelectedSleepTimerAction) return;

        // If user chose a power action, ask for confirmation
        if (newAction != SleepTimerCompletionAction.StopPlayback)
        {
            if (this.Content?.XamlRoot == null) return;

            bool confirmed = await Octave_Desktop.Helpers.SleepTimerHelper.ShowPowerActionWarningDialogAsync(this.Content.XamlRoot, newAction);
            if (!confirmed)
            {
                _isSyncingActionCombo = true;
                try
                {
                    cb.SelectedIndex = GetActionIndex(ViewModel.SelectedSleepTimerAction);
                }
                finally
                {
                    _isSyncingActionCombo = false;
                }
                return;
            }
        }

        ViewModel.SelectedSleepTimerAction = newAction;
    }

    private void EqBand_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is EqBandViewModel band)
        {
            band.ResetGain();
            e.Handled = true;
        }
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

    private async void ClearDatabase_Click(object sender, RoutedEventArgs e)
    {
        var confirmDialog = new ContentDialog
        {
            Title = "Clear Library Database & Cache?",
            Content = "Are you sure you want to completely clear the local music database and cache?\n\nThis will remove all indexed tracks, albums, artists, playlists, favorites, playback history, monitored folders, and cached artwork from OCTAVE.\n\nYour actual audio files on disk will NOT be deleted or modified.",
            PrimaryButtonText = "Clear Database",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            await ViewModel.ClearDatabaseCommand.ExecuteAsync(null);

            var doneDialog = new ContentDialog
            {
                Title = "Database Cleared",
                Content = "The music library database and cache have been successfully cleared.\n\nYou can add music folders at any time to re-index your collection.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await doneDialog.ShowAsync();
        }
        catch (System.Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Clear Failed",
                Content = $"Failed to clear database: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }
}
