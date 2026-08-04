using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Octave_Desktop.ViewModels;

// One equalizer band. Setting Gain applies it to the audio engine.
public partial class EqBandViewModel : ObservableObject
{
    private readonly Action<int, float> _apply;

    public int Index { get; }
    public int Frequency { get; }
    public string Label { get; }

    [ObservableProperty]
    public partial double Gain { get; set; }

    public EqBandViewModel(int index, int frequency, double gain, Action<int, float> apply)
    {
        Index = index;
        Frequency = frequency;
        Label = frequency >= 1000 ? $"{frequency / 1000}k" : frequency.ToString();
        _apply = apply;
        Gain = gain;
    }

    partial void OnGainChanged(double value)
    {
        _apply(Index, (float)value);
    }
}
