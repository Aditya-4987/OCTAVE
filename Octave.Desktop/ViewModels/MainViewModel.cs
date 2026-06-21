using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Services.Audio;

namespace Octave_Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAudioPlayerService _audioPlayer;

    [ObservableProperty]
    private string currentTrackTitle = "No Track Loaded";

    [ObservableProperty]
    private bool isPlaying;

    public MainViewModel(IAudioPlayerService audioPlayer)
    {
        _audioPlayer = audioPlayer;
    }

    [RelayCommand]
    private void Play()
    {
        IsPlaying = true;
    }

    [RelayCommand]
    private void Pause()
    {
        IsPlaying = false;
    }

    [RelayCommand]
    private void Stop()
    {
        IsPlaying = false;
    }
}