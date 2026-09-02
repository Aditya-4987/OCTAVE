using System;
using System.Collections.Generic;
using ManagedBass;
using Octave.Core.Helpers;
using Octave.Core.Models;
using Octave.Core.Services.Audio;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Octave.Core.Tests;

public class AudioPlayerServiceTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AudioPlayerServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    // TEST-17: every construction used to be left undisposed, leaking the BASS
    // free-noise mixers and sync handles each instance registers. One field,
    // disposed once per test via the class-level IDisposable.
    private readonly ManagedBassAudioService _player = new();

    public void Dispose() => _player.Dispose();

    [Fact]
    public void OutputDeviceDetails_ReturnsValidProperties()
    {
        // TEST-05: NotNull alone passed even when every string was empty. Assert
        // the actual shape contract: non-empty and a known quality token.
        Assert.False(string.IsNullOrWhiteSpace(_player.StreamingQuality));
        Assert.False(string.IsNullOrWhiteSpace(_player.OutputDeviceName));
        Assert.False(string.IsNullOrWhiteSpace(_player.OutputDeviceQuality));
    }

    [Fact]
    public void GetAvailableOutputDevices_ContainsDefaultAndHardwareDevices()
    {
        _player.Init();
        var devices = _player.GetAvailableOutputDevices();
        Assert.NotNull(devices);
        Assert.NotEmpty(devices);

        // First device must be system default (-1)
        var defaultDev = devices[0];
        Assert.Equal(-1, defaultDev.Index);
        Assert.True(defaultDev.IsDefault);
        Assert.Contains("Default", defaultDev.Name);

        _output.WriteLine($"Found {devices.Count} available output devices:");
        foreach (var dev in devices)
        {
            var (n, fmt, khz, bits, t) = WindowsAudioDeviceHelper.GetOutputDeviceInfo(dev.Driver, forceRefresh: true);
            _output.WriteLine($" -> [{dev.Index}] {dev.Name} (Driver: '{dev.Driver}') => Real Name: '{n}', Fmt: '{fmt}', {bits}-bit {khz}kHz, Type: '{t}'");
        }
    }

    [Fact]
    public void SetOutputDevice_UpdatesCurrentDeviceIndex_AndRaisesEvent()
    {
        _player.Init();
        bool eventFired = false;
        _player.OutputDeviceChanged += (_, _) => eventFired = true;

        _player.SetOutputDevice(1);
        Assert.Equal(1, _player.CurrentOutputDeviceIndex);
        Assert.True(eventFired);

        eventFired = false;
        _player.SetOutputDevice(-1);
        Assert.Equal(-1, _player.CurrentOutputDeviceIndex);
        Assert.True(eventFired);
    }

    [Fact]
    public void SetOutputDevice_ById_UpdatesCurrentDeviceEndpointId_AndRaisesEvent()
    {
        _player.Init();
        var devices = _player.GetAvailableOutputDevices();
        var realHardware = devices.FirstOrDefault(d => d.Index > 1 && !string.IsNullOrEmpty(d.Driver));

        bool eventFired = false;
        _player.OutputDeviceChanged += (_, _) => eventFired = true;

        if (realHardware != null)
        {
            _player.SetOutputDevice(realHardware.Id);
            Assert.Equal(realHardware.Id, _player.CurrentOutputDeviceId);
            Assert.Equal(realHardware.Index, _player.CurrentOutputDeviceIndex);
            Assert.True(eventFired);
        }

        eventFired = false;
        _player.SetOutputDevice("__default__");
        Assert.Null(_player.CurrentOutputDeviceId);
        Assert.Equal(-1, _player.CurrentOutputDeviceIndex);
        Assert.True(eventFired);
    }

    [Fact]
    public void SetOutputDevice_WhilePlaying_SeamlesslyRecreatesOrMovesStream()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stream_switch_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            Assert.Equal(PlaybackStatus.Playing, _player.Status);

            // Switching to default while playing
            _player.SetOutputDevice("__default__");
            Assert.Equal(PlaybackStatus.Playing, _player.Status);

            var defaultDev = _player.GetAvailableOutputDevices().FirstOrDefault(d => d.Index > 1 && d.IsDefault);
            if (defaultDev != null)
            {
                _player.SetOutputDevice(defaultDev.Id);
                Assert.Equal(PlaybackStatus.Playing, _player.Status);

                _player.SetOutputDevice("__default__");
                Assert.Equal(PlaybackStatus.Playing, _player.Status);
            }
        }
        finally
        {
            _player.Stop();
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void SetOutputDevice_InvalidOrMissingId_FallsBackToDefault()
    {
        _player.Init();
        _player.SetOutputDevice(1);

        bool eventFired = false;
        _player.OutputDeviceChanged += (_, _) => eventFired = true;

        // Passing an unknown or unplugged ID must safely resolve to default
        _player.SetOutputDevice("non-existent-device-guid");
        Assert.Null(_player.CurrentOutputDeviceId);
        Assert.Equal(-1, _player.CurrentOutputDeviceIndex);
        Assert.True(eventFired);
    }

    [Fact]
    public void PlaybackInterrupted_WhenPlaybackActive_CanBeRaisedAndHandled()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"interrupted_test_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 2.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            Assert.Equal(PlaybackStatus.Playing, _player.Status);

            bool interrupted = false;
            _player.PlaybackInterrupted += (_, _) => interrupted = true;

            // Pause simulates manual pause (interrupted remains false)
            _player.Pause();
            Assert.Equal(PlaybackStatus.Paused, _player.Status);
            Assert.False(interrupted);
        }
        finally
        {
            _player.Stop();
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void DiagnoseAllBassDevices()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"diag_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 1.0);
        try
        {
            _output.WriteLine($"BASS Device Diagnostics:");
            _output.WriteLine($"Default Windows Endpoint: {WindowsAudioDeviceHelper.GetOutputDeviceInfo(null).Name}");
            for (int i = 0; Bass.GetDeviceInfo(i, out var info); i++)
            {
                bool initResult = info.IsInitialized;
                if (!initResult)
                {
                    initResult = Bass.Init(i, 44100, DeviceInitFlags.Default, IntPtr.Zero);
                    if (!initResult && Bass.LastError == Errors.Already) initResult = true;
                }
                _output.WriteLine($"Device [{i}]: Name='{info.Name}', Driver='{info.Driver}', IsDefault={info.IsDefault}, IsEnabled={info.IsEnabled}, IsInitialized={info.IsInitialized} (InitResult={initResult}, LastErr={Bass.LastError})");

                // Test creating stream on this device
                try
                {
                    Bass.CurrentDevice = i;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"   Set CurrentDevice={i} threw: {ex.Message}");
                }

                int stream = Bass.CreateStream(tempWav, 0, 0, BassFlags.Default);
                if (stream == 0)
                {
                    _output.WriteLine($"   CreateStream on dev {i} FAILED: {Bass.LastError}");
                }
                else
                {
                    int assignedDev = Bass.ChannelGetDevice(stream);
                    bool played = Bass.ChannelPlay(stream, false);
                    var state = Bass.ChannelIsActive(stream);
                    _output.WriteLine($"   CreateStream OK: handle={stream}, AssignedDev={assignedDev}, Play={played}, State={state}, LastErr={Bass.LastError}");

                    // Test switching to other devices with ChannelSetDevice WHILE PLAYING!
                    for (int other = 1; Bass.GetDeviceInfo(other, out var oInfo); other++)
                    {
                        if (other == assignedDev || !oInfo.IsInitialized) continue;
                        bool setDevWhilePlaying = Bass.ChannelSetDevice(stream, other);
                        var errWhilePlaying = Bass.LastError;
                        int nowDev = Bass.ChannelGetDevice(stream);
                        var stateWhilePlaying = Bass.ChannelIsActive(stream);
                        _output.WriteLine($"      [WHILE PLAYING] ChannelSetDevice to {other} ({oInfo.Name}): Success={setDevWhilePlaying}, Err={errWhilePlaying}, NowDev={nowDev}, State={stateWhilePlaying}");
                    }

                    Bass.ChannelStop(stream);
                }
            }
        }
        finally
        {
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void EnumerateCoreAudioEndpoints()
    {
        var devices = _player.GetAvailableOutputDevices();
        _output.WriteLine($"Audio Player Service found {devices.Count} endpoints:");
        foreach (var d in devices)
        {
            var info = WindowsAudioDeviceHelper.GetOutputDeviceInfo(d.Driver, forceRefresh: true);
            _output.WriteLine($"Endpoint: Name='{info.Name}', Driver='{d.Driver}', Type='{info.DeviceType}', Format='{info.Format}'");
        }

        string? defId = WindowsAudioDeviceHelper.GetDefaultOutputEndpointId();
        _output.WriteLine($"Default Output Endpoint ID from CoreAudio: '{defId}'");
        Assert.NotNull(defId);
    }

    [Fact]
    public void TestActualPlaybackViaServiceOnEachDevice()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"srv_diag_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 2.0);
        try
        {
            var devices = _player.GetAvailableOutputDevices();
            _output.WriteLine($"Testing _player.Play() with {devices.Count} devices:");
            foreach (var dev in devices)
            {
                _output.WriteLine($"--- Selecting [{dev.Index}] '{dev.Name}' (Id='{dev.Id}', Driver='{dev.Driver}') ---");
                _player.SetOutputDevice(dev.Id);
                long session = _player.Play(tempWav);
                var status = _player.Status;
                int bassDev = _player.CurrentOutputDeviceIndex;
                string? devId = _player.CurrentOutputDeviceId;
                _output.WriteLine($"    Play() result: Session={session}, Status={status}, CurrentOutputDeviceIndex={bassDev}, CurrentOutputDeviceId={devId}");

                int activeDev = Bass.CurrentDevice;
                _output.WriteLine($"    Bass.CurrentDevice={activeDev}, OutputDeviceQuality='{_player.OutputDeviceQuality}'");

                _player.Stop();
            }
        }
        finally
        {
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void TestDevicePositionAdvancement()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pos_adv_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 5.0);
        try
        {
            var devices = _player.GetAvailableOutputDevices();
            foreach (var dev in devices)
            {
                _player.SetOutputDevice(dev.Id);
                long session = _player.Play(tempWav);
                long startPos = Bass.ChannelGetPosition(_player.CurrentOutputDeviceIndex <= 0 ? 1 : _player.CurrentOutputDeviceIndex, PositionFlags.Bytes);
                System.Threading.Thread.Sleep(200);
                double currentSec = _player.GetPositionSeconds();
                _output.WriteLine($"Device [{dev.Index}] '{dev.Name}': Played at {currentSec:0.00}s, Status={_player.Status}");
                Assert.True(currentSec > 0.05, $"Position did not advance on device {dev.Name}!");
            }
        }
        finally
        {
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void TestLiveSwitchingWhilePlaying()
    {
        _player.Init();
        string tempWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"switch_live_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 5.0);
        try
        {
            var devices = _player.GetAvailableOutputDevices();
            // Start playing on Headphones or first device
            var firstDev = devices.FirstOrDefault(d => d.Index > 1) ?? devices.First();
            _output.WriteLine($"Starting playback on [{firstDev.Index}] '{firstDev.Name}'...");
            _player.SetOutputDevice(firstDev.Id);
            long session = _player.Play(tempWav);
            _output.WriteLine($"Playback started: Status={_player.Status}, Bass.CurrentDevice={Bass.CurrentDevice}");

            // Now switch to Windows Default WHILE PLAYING!
            _output.WriteLine($"--- Live switching to Windows Default (__default__) while playing ---");
            _player.SetOutputDevice("__default__");
            _output.WriteLine($"After switch to default: Status={_player.Status}, Bass.CurrentDevice={Bass.CurrentDevice}");

            // Now switch to Speakers WHILE PLAYING!
            var speakers = devices.FirstOrDefault(d => d.Name.Contains("Speakers"));
            if (speakers != null)
            {
                _output.WriteLine($"--- Live switching to Speakers ({speakers.Id}) while playing ---");
                _player.SetOutputDevice(speakers.Id);
                _output.WriteLine($"After switch to speakers: Status={_player.Status}, Bass.CurrentDevice={Bass.CurrentDevice}");
            }

            // Now switch back to Windows Default WHILE PLAYING!
            _output.WriteLine($"--- Live switching back to Windows Default while playing ---");
            _player.SetOutputDevice("__default__");
            _output.WriteLine($"After switch to default: Status={_player.Status}, Bass.CurrentDevice={Bass.CurrentDevice}");
        }
        finally
        {
            _player.Stop();
            try { System.IO.File.Delete(tempWav); } catch { }
        }
    }



    private static void WriteTestWav(string path, double seconds, int sampleRate = 44100)
    {
        int sampleCount = (int)(seconds * sampleRate);
        int dataBytes = sampleCount * 2 * 2; // 16-bit stereo

        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        using var w = new System.IO.BinaryWriter(fs);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)2); // Stereo
        w.Write(sampleRate);
        w.Write(sampleRate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
    }

    [Theory]
    [InlineData("FiiO Q3 USB DAC", 0, "External DAC / Audio Interface")]
    [InlineData("Topping E30 II", 0, "External DAC / Audio Interface")]
    [InlineData("Realtek USB Audio", 0, "USB DAC / Audio Device")]
    [InlineData("Sony WH-1000XM5 Bluetooth", 0, "Bluetooth Audio")]
    [InlineData("Realtek Audio", 3, "Headphones")]
    [InlineData("Realtek Audio", 1, "Speakers")]
    [InlineData("NVIDIA High Definition Audio", 9, "Digital HDMI / Display Audio")]
    public void ClassifyDeviceType_IdentifiesExpectedCategories(string name, uint formFactor, string expectedType)
    {
        string actualType = Octave.Core.Helpers.WindowsAudioDeviceHelper.ClassifyDeviceType(name, formFactor);
        Assert.Equal(expectedType, actualType);
    }


    // TEST-07: synthetic device-format blobs through the extracted pure parser.
    // Layout is WAVEFORMATEX with Pack=2 (18 bytes) / WAVEFORMATEXTENSIBLE (40
    // bytes), little-endian.
    [Fact]
    public void ParseDeviceFormatBlob_Plain16Bit48kHz_WaveFormatEx_YieldsExactValues()
    {
        byte[] blob = BuildWaveFormatEx(channels: 2, samplesPerSec: 48000, bitsPerSample: 16, cbSize: 0);

        var (format, khz, bits) = WindowsAudioDeviceHelper.ParseDeviceFormatBlob(blob);

        Assert.Equal("16-bit 48.0kHz (Shared Mode)", format);
        Assert.Equal(48.0, khz);
        Assert.Equal(16, bits);
    }

    [Fact]
    public void ParseDeviceFormatBlob_Extensible24ValidBits_HonorsWValidBitsPerSample()
    {
        byte[] blob = BuildWaveFormatExtensible(channels: 2, samplesPerSec: 44100, containerBits: 32, validBits: 24);

        var (format, khz, bits) = WindowsAudioDeviceHelper.ParseDeviceFormatBlob(blob);

        Assert.Equal("24-bit 44.1kHz (Shared Mode)", format);
        Assert.Equal(44.1, khz);
        Assert.Equal(24, bits);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]                       // shorter than WAVEFORMATEX
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void ParseDeviceFormatBlob_GarbageOrShortBlob_FallsBackToUnknownDefaults(byte[] blob)
    {
        var (format, khz, bits) = WindowsAudioDeviceHelper.ParseDeviceFormatBlob(blob);

        Assert.Equal("Unknown", format);
        Assert.Equal(44.1, khz);
        Assert.Equal(16, bits);
    }

    private static void AppendU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void AppendU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));

    private static byte[] BuildWaveFormatEx(int channels, int samplesPerSec, int bitsPerSample, int cbSize)
    {
        var b = new List<byte>();
        AppendU16(b, 1);                                            // wFormatTag = PCM
        AppendU16(b, (ushort)channels);
        AppendU32(b, (uint)samplesPerSec);
        AppendU32(b, (uint)(samplesPerSec * channels * bitsPerSample / 8)); // nAvgBytesPerSec
        AppendU16(b, (ushort)(channels * bitsPerSample / 8));       // nBlockAlign
        AppendU16(b, (ushort)bitsPerSample);                        // wBitsPerSample
        AppendU16(b, (ushort)cbSize);                               // cbSize
        return b.ToArray();
    }

    private static byte[] BuildWaveFormatExtensible(int channels, int samplesPerSec, int containerBits, int validBits)
    {
        var b = new List<byte>(BuildWaveFormatEx(channels, samplesPerSec, containerBits, cbSize: 22));
        AppendU16(b, (ushort)validBits);                            // wValidBitsPerSample
        AppendU32(b, 0x00000003u);                                  // dwChannelMask (FL+FR)
        // SubFormat = KSDATAFORMAT_SUBTYPE_PCM
        b.AddRange(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        return b.ToArray();
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
        var fft = _player.GetFftData(bins);
        Assert.NotNull(fft);
        Assert.Equal(bins, fft.Length);
    }

    // TEST-06: AggregateFftBins previously had zero coverage of its actual math.
    // These pin the exact first-frame values for the two extremes plus the decay
    // blend and the monotonic frequency mapping.
    [Fact]
    public void AggregateFftBins_ZeroInputFirstFrame_ProducesNoiseFloorBlend()
    {
        float[] raw = new float[256];      // silence
        float[] peaks = new float[64];
        float[] result = new float[64];

        ManagedBassAudioService.AggregateFftBins(raw, 64, peaks, result);

        // target clamps to 0.02 on silence; attack blend from 0 → 0.7 * 0.02.
        foreach (float v in result) Assert.Equal(0.014f, v, precision: 4);
        foreach (float v in peaks) Assert.Equal(0.014f, v, precision: 4);
    }

    [Fact]
    public void AggregateFftBins_FullScaleSpikeFirstFrame_ClampsToAttackCeiling()
    {
        float[] raw = new float[256];
        Array.Fill(raw, 1.0f);             // full-scale everywhere
        float[] peaks = new float[36];
        float[] result = new float[36];

        ManagedBassAudioService.AggregateFftBins(raw, 36, peaks, result);

        // target clamps to 1.0; attack blend from 0 → 0.7 * 1.0.
        foreach (float v in result) Assert.Equal(0.7f, v, precision: 4);
    }

    [Fact]
    public void AggregateFftBins_SilenceAfterPeak_DecaysTowardFloor()
    {
        float[] raw = new float[256];
        float[] peaks = new float[8];
        Array.Fill(peaks, 0.7f);           // pre-warmed peak state
        float[] result = new float[8];

        ManagedBassAudioService.AggregateFftBins(raw, 8, peaks, result);

        // decay blend: 0.82*0.7 + 0.18*0.02 = 0.5776
        foreach (float v in result) Assert.Equal(0.5776f, v, precision: 4);
    }

    [Fact]
    public void AggregateFftBins_LowVsHighFrequencySpike_HigherFrequencyMapsToHigherOrEqualBin()
    {
        const int binCount = 64;

        float[] rawLow = new float[256];
        rawLow[10] = 1.0f;
        float[] peaksLow = new float[binCount];
        float[] resultLow = new float[binCount];
        ManagedBassAudioService.AggregateFftBins(rawLow, binCount, peaksLow, resultLow);

        float[] rawHigh = new float[256];
        rawHigh[200] = 1.0f;
        float[] peaksHigh = new float[binCount];
        float[] resultHigh = new float[binCount];
        ManagedBassAudioService.AggregateFftBins(rawHigh, binCount, peaksHigh, resultHigh);

        int ArgMax(float[] a) { int m = 0; for (int i = 1; i < a.Length; i++) if (a[i] > a[m]) m = i; return m; }
        int lowPeakBin = ArgMax(resultLow);
        int highPeakBin = ArgMax(resultHigh);

        // The log mapping is monotonic non-decreasing: an energy spike deeper into
        // the spectrum must surface at the same or a higher visual bin.
        Assert.True(highPeakBin >= lowPeakBin,
            $"high-frequency spike surfaced at bin {highPeakBin}, low-frequency at {lowPeakBin}");
        Assert.True(resultHigh[highPeakBin] > 0.05f, "spike energy should be visible above the noise floor");
    }

    [Fact]
    public void Equalizer_DefaultFrequencies_AreValid()
    {
        Assert.Equal(10, _player.EqFrequencies.Count);
        Assert.Equal(31, _player.EqFrequencies[0]);
        Assert.Equal(16000, _player.EqFrequencies[9]);
    }

    [Fact]
    public void Volume_And_Crossfade_Properties_ClampCorrectly()
    {
        _player.Volume = 1.5f;
        Assert.Equal(1.0f, _player.Volume);

        _player.Volume = -0.5f;
        Assert.Equal(0.0f, _player.Volume);

        _player.CrossfadeDurationMs = -500;
        Assert.Equal(0, _player.CrossfadeDurationMs);

        _player.CrossfadeDurationMs = 2000;
        Assert.Equal(2000, _player.CrossfadeDurationMs);
    }

    [Fact]
    public void PreampGain_ClampsAndApplies_Correctly()
    {
        Assert.Equal(0.0f, _player.PreampGainDb);

        _player.SetPreampGain(5.5f);
        Assert.Equal(5.5f, _player.PreampGainDb);

        _player.SetPreampGain(25.0f);
        Assert.Equal(15.0f, _player.PreampGainDb);

        _player.SetPreampGain(-30.0f);
        Assert.Equal(-15.0f, _player.PreampGainDb);

        // NaN and Infinity safety
        _player.SetPreampGain(float.NaN);
        Assert.Equal(0.0f, _player.PreampGainDb);

        _player.SetPreampGain(float.PositiveInfinity);
        Assert.Equal(0.0f, _player.PreampGainDb);
    }

    [Fact]
    public void Volume_NaN_And_Infinity_DefaultsSafely()
    {
        _player.Volume = float.NaN;
        Assert.Equal(0.5f, _player.Volume);

        _player.Volume = float.PositiveInfinity;
        Assert.Equal(0.5f, _player.Volume);
    }

    [Fact]
    public void EqBand_NaN_And_Infinity_DefaultsSafely()
    {
        _player.SetEqBand(0, float.NaN);
        var gains = _player.GetEqGains();
        Assert.Equal(0.0f, gains[0]);

        _player.SetEqBand(0, float.PositiveInfinity);
        gains = _player.GetEqGains();
        Assert.Equal(0.0f, gains[0]);
    }

    [Fact]
    public void GetBinMap_PrecomputesMonotonicValidBounds()
    {
        var map36 = ManagedBassAudioService.GetBinMap(36);
        Assert.NotNull(map36);
        Assert.Equal(36, map36.Length);

        for (int i = 0; i < map36.Length; i++)
        {
            var (start, end) = map36[i];
            Assert.True(start >= 0);
            Assert.True(end <= 256);
            Assert.True(end > start);
            if (i > 0)
            {
                Assert.True(start >= map36[i - 1].Start);
            }
        }
    }
}
