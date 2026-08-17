using Octave.Core.Services.Audio;
using Xunit;

namespace Octave.Core.Tests;

public class AudioPlayerServiceTests
{
    [Fact]
    public void OutputDeviceDetails_ReturnsValidProperties()
    {
        var player = new ManagedBassAudioService();
        Assert.NotNull(player.StreamingQuality);
        Assert.NotNull(player.OutputDeviceName);
        Assert.NotNull(player.OutputDeviceQuality);
    }

    [Fact]
    public void WindowsAudioDeviceHelper_GetDefaultOutputDeviceDetails_DoesNotThrow_AndReturnsValidInfo()
    {
        var details = Octave.Core.Helpers.WindowsAudioDeviceHelper.GetDefaultOutputDeviceDetails();
        Assert.NotNull(details.Name);
        Assert.NotNull(details.Format);
        Assert.True(details.SampleRateKhz > 0);
        Assert.True(details.BitDepth > 0);
    }

    [Fact]
    public void ShellRecycleBin_InvalidFile_ReturnsFalseSafely()
    {
        Assert.False(Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin(""));
        Assert.False(Octave.Core.Helpers.ShellRecycleBin.SendToRecycleBin("C:\\nonexistent_octave_test_path_12345.mp3"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(36)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void GetFftData_VariousBinCounts_ReturnsAccurateLengthsWithoutException(int bins)
    {
        var player = new ManagedBassAudioService();
        var fft = player.GetFftData(bins);
        Assert.NotNull(fft);
        Assert.Equal(bins, fft.Length);
    }

    [Fact]
    public void Equalizer_DefaultFrequencies_AreValid()
    {
        var player = new ManagedBassAudioService();
        Assert.Equal(10, player.EqFrequencies.Count);
        Assert.Equal(31, player.EqFrequencies[0]);
        Assert.Equal(16000, player.EqFrequencies[9]);
    }

    [Fact]
    public void Volume_And_Crossfade_Properties_ClampCorrectly()
    {
        var player = new ManagedBassAudioService();
        player.Volume = 1.5f;
        Assert.Equal(1.0f, player.Volume);

        player.Volume = -0.5f;
        Assert.Equal(0.0f, player.Volume);

        player.CrossfadeDurationMs = -500;
        Assert.Equal(0, player.CrossfadeDurationMs);

        player.CrossfadeDurationMs = 2000;
        Assert.Equal(2000, player.CrossfadeDurationMs);
    }

    [Fact]
    public void PreampGain_ClampsAndApplies_Correctly()
    {
        var player = new ManagedBassAudioService();
        Assert.Equal(0.0f, player.PreampGainDb);

        player.SetPreampGain(5.5f);
        Assert.Equal(5.5f, player.PreampGainDb);

        player.SetPreampGain(25.0f);
        Assert.Equal(15.0f, player.PreampGainDb);

        player.SetPreampGain(-30.0f);
        Assert.Equal(-15.0f, player.PreampGainDb);
    }
}
