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
    public void GetFftData_ReturnsExpectedBinCount()
    {
        var player = new ManagedBassAudioService();
        var fft = player.GetFftData(36);
        Assert.NotNull(fft);
        Assert.Equal(36, fft.Length);
    }
}
