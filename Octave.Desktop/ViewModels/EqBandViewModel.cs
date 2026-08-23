using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Octave_Desktop.ViewModels;

// One equalizer band. Setting Gain applies it to the audio engine.
public partial class EqBandViewModel : ObservableObject
{
    private readonly Action<int, float> _apply;

    // VM-12: the constructor seeds Gain from the engine's persisted value, so
    // OnGainChanged must NOT push it straight back - that both mutated the
    // engine during construction and relied on the setter's equality check to
    // skip 0-gain bands (which silently never applied a restored non-zero gain
    // if the property ever started at its default).
    private bool _suppressEngineApply = true;

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

    // VM-12: called by ShellViewModel AFTER the whole band list is built -
    // pushes the restored gain to the engine exactly once, explicitly.
    public void PushInitialGainToEngine()
    {
        _apply(Index, (float)Gain);
        _suppressEngineApply = false;
    }

    partial void OnGainChanged(double value)
    {
        if (_suppressEngineApply) return;
        _apply(Index, (float)value);
    }
}
