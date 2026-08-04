using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octave.Core.Services.Audio;

namespace Octave_Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAudioPlayerService _audioPlayer;

    [ObservableProperty]
    public partial string CurrentTrackTitle { get; set; } = "No Track Loaded";

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

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