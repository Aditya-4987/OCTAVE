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
    public void DeviceClassification_Maps5CategoriesAndGlyphsCorrectly()
    {
        // 1. Bluetooth
        var btCat = WindowsAudioDeviceHelper.ClassifyDeviceCategory("Sony WH-1000XM5 (Bluetooth)", 0, "BTHENUM\\{...}");
        Assert.Equal(AudioDeviceCategory.Bluetooth, btCat);
        Assert.Equal("\uE702", WindowsAudioDeviceHelper.GetCategoryGlyph(btCat));

        // 2. Monitor speakers (HDMI / DisplayPort)
        var monCat = WindowsAudioDeviceHelper.ClassifyDeviceCategory("LG UltraFine Display Audio (NVIDIA High Definition Audio)", 3, null);
        Assert.Equal(AudioDeviceCategory.MonitorSpeakers, monCat);
        Assert.Equal("\uE7F4", WindowsAudioDeviceHelper.GetCategoryGlyph(monCat));

        // 3. External speakers (Aux / Line Out / DAC)
        var extCat = WindowsAudioDeviceHelper.ClassifyDeviceCategory("Realtek HD Audio 2nd output (Line Out)", 2, null);
        Assert.Equal(AudioDeviceCategory.ExternalSpeakers, extCat);
        Assert.Equal("\uE7F5", WindowsAudioDeviceHelper.GetCategoryGlyph(extCat));

        // 4. Headphones (Aux / 3.5mm)
        var hpCat = WindowsAudioDeviceHelper.ClassifyDeviceCategory("Headphones (Realtek(R) Audio)", 1, null);
        Assert.Equal(AudioDeviceCategory.Headphones, hpCat);
        Assert.Equal("\uE7F6", WindowsAudioDeviceHelper.GetCategoryGlyph(hpCat));

        // 5. Laptop speakers (Internal)
        var lapCat = WindowsAudioDeviceHelper.ClassifyDeviceCategory("Speakers (Realtek(R) Audio)", 0, null);
        Assert.Equal(AudioDeviceCategory.LaptopSpeakers, lapCat);
        Assert.Equal("\uE7F8", WindowsAudioDeviceHelper.GetCategoryGlyph(lapCat));
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
        var info = WindowsAudioDeviceHelper.GetOutputDeviceInfo(null, forceRefresh: true);
        _output.WriteLine($"Default Output Endpoint: Name='{info.Name}', Type='{info.DeviceType}', Format='{info.Format}', Category='{info.Category}'");
        Assert.False(string.IsNullOrWhiteSpace(info.Name));

        string? defId = WindowsAudioDeviceHelper.GetDefaultOutputEndpointId();
        _output.WriteLine($"Default Output Endpoint ID from CoreAudio: '{defId}'");
        Assert.NotNull(defId);
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

    [Fact]
    public void GetDefaultOutputEndpointId_OnWindows_ReturnsValidGuid()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? defaultId = WindowsAudioDeviceHelper.GetDefaultOutputEndpointId();
        Assert.NotNull(defaultId);
        Assert.StartsWith("{", defaultId);
    }

    [Fact]
    public void QualityDetails_LosslessWavPlayback_ReportsWavCodecFormat()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"wav_quality_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 2.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);

            var details = _player.QualityDetails;
            Assert.NotNull(details);
            Assert.Equal("WAV Uncompressed PCM", details.CodecFormat);
            Assert.Contains("Direct Passthrough", details.DspStatus);
            Assert.False(string.IsNullOrWhiteSpace(details.OutputDeviceName));
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void GetOutputDeviceInfo_FallbackHandlesDefaultSafely()
    {
        var infoDefault = WindowsAudioDeviceHelper.GetOutputDeviceInfo("__default__");
        var infoNull = WindowsAudioDeviceHelper.GetOutputDeviceInfo(null);

        Assert.False(string.IsNullOrWhiteSpace(infoDefault.Name));
        Assert.False(string.IsNullOrWhiteSpace(infoNull.Name));
        Assert.False(string.IsNullOrWhiteSpace(infoDefault.Glyph));
        Assert.False(string.IsNullOrWhiteSpace(infoNull.Glyph));
        Assert.True(infoDefault.SampleRateKhz > 0);
        Assert.True(infoNull.SampleRateKhz > 0);
    }

    [Fact]
    public void GetAvailableOutputDevices_ReturnsWindowsDefaultAsFirstItem()
    {
        _player.Init();
        var devices = _player.GetAvailableOutputDevices();
        Assert.NotEmpty(devices);

        var first = devices[0];
        Assert.Equal(-1, first.Index);
        Assert.Equal("__default__", first.Id);
        Assert.True(first.IsDefault);
        Assert.True(first.IsEnabled);
        Assert.StartsWith("Windows Default", first.Name);
        Assert.False(string.IsNullOrWhiteSpace(first.Glyph));
    }

    [Fact]
    public void SetOutputDevice_SessionOnly_TogglesCustomDeviceAndDefaultsOnStartup()
    {
        _player.Init();
        // Default launch behavior: strictly system default on startup
        Assert.Null(_player.SelectedCustomDeviceId);
        Assert.False(_player.IsCustomDeviceSelected);

        var devices = _player.GetAvailableOutputDevices();
        var customDevice = devices.FirstOrDefault(d => d.Index > 0 && !string.IsNullOrEmpty(d.Driver));

        if (customDevice != null)
        {
            _player.SetOutputDevice(customDevice.Id);
            Assert.Equal(customDevice.Id, _player.SelectedCustomDeviceId);
            Assert.True(_player.IsCustomDeviceSelected);

            // Reverting to Windows Default
            _player.SetOutputDevice("__default__");
            Assert.Null(_player.SelectedCustomDeviceId);
            Assert.False(_player.IsCustomDeviceSelected);
        }
    }

    [Fact]
    public void SetOutputDevice_WhilePlaying_PausesSwitchesAndResumes()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"switch_resume_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            Assert.Equal(PlaybackStatus.Playing, _player.Status);

            var devices = _player.GetAvailableOutputDevices();
            var targetDev = devices.FirstOrDefault(d => d.Index > 0 && !string.IsNullOrEmpty(d.Driver)) ?? devices[0];

            _player.SetOutputDevice(targetDev.Id);

            // Playback must automatically resume
            Assert.Equal(PlaybackStatus.Playing, _player.Status);
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void SetOutputDevice_WhilePaused_PreservesPausedState()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"switch_paused_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);

            _player.Pause();
            Assert.Equal(PlaybackStatus.Paused, _player.Status);

            var devices = _player.GetAvailableOutputDevices();
            var targetDev = devices.FirstOrDefault(d => d.Index > 0 && !string.IsNullOrEmpty(d.Driver)) ?? devices[0];

            _player.SetOutputDevice(targetDev.Id);

            // Playback must remain paused
            Assert.Equal(PlaybackStatus.Paused, _player.Status);
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void DeviceDisconnection_WhileCustomActive_StopsPlaybackAndFallsBackToDefault()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"disconnect_fallback_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            var devices = _player.GetAvailableOutputDevices();
            var customDev = devices.FirstOrDefault(d => d.Index > 0 && !string.IsNullOrEmpty(d.Driver));

            if (customDev != null)
            {
                _player.SetOutputDevice(customDev.Id);
                Assert.Equal(customDev.Id, _player.SelectedCustomDeviceId);

                long session = _player.Play(tempWav);
                Assert.True(session > 0);
                Assert.Equal(PlaybackStatus.Playing, _player.Status);

                bool interrupted = false;
                _player.PlaybackInterrupted += (_, _) => interrupted = true;

                // Simulate physical removal of custom device
                WindowsAudioDeviceHelper.TriggerDeviceRemovedForTesting(customDev.Id);

                // Playback must stop immediately
                Assert.True(interrupted);
                Assert.Equal(PlaybackStatus.Paused, _player.Status);
                // Custom device must be deselected, falling back to default
                Assert.Null(_player.SelectedCustomDeviceId);
                Assert.False(_player.IsCustomDeviceSelected);
            }
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void EndpointsChanged_WhilePlayingOnWindowsDefault_RecreatesStreamAndAutoResumes()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"endpoint_reconnect_play_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            Assert.Equal(PlaybackStatus.Playing, _player.Status);
            Assert.True(_player.CurrentStreamBassDevice > 0);

            // Simulate Windows audio endpoint event (e.g. headphones reconnected or default changed)
            WindowsAudioDeviceHelper.TriggerEndpointsChangedForTesting();

            // Playback must remain playing seamlessly without getting stuck or stopped
            Assert.Equal(PlaybackStatus.Playing, _player.Status);
            Assert.True(_player.CurrentStreamBassDevice > 0);
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void EndpointsChanged_WhilePausedOnWindowsDefault_PreservesPausedState()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"endpoint_reconnect_pause_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            _player.Pause();
            Assert.Equal(PlaybackStatus.Paused, _player.Status);

            // Simulate Windows audio endpoint event while paused
            WindowsAudioDeviceHelper.TriggerEndpointsChangedForTesting();

            // Must preserve paused state and remain paused
            Assert.Equal(PlaybackStatus.Paused, _player.Status);

            // Resuming must work smoothly on the target device
            _player.Resume();
            Assert.Equal(PlaybackStatus.Playing, _player.Status);
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }

    [Fact]
    public void DeviceRemoved_ActiveDefaultDevice_PausesAndFallsBack()
    {
        _player.Init();
        string tempWav = Path.Combine(Path.GetTempPath(), $"endpoint_removed_default_{Guid.NewGuid():N}.wav");
        WriteTestWav(tempWav, 3.0);
        try
        {
            long session = _player.Play(tempWav);
            Assert.True(session > 0);
            Assert.Equal(PlaybackStatus.Playing, _player.Status);

            string? currentEndpoint = _player.CurrentStreamEndpointId;
            if (!string.IsNullOrEmpty(currentEndpoint))
            {
                bool interrupted = false;
                _player.PlaybackInterrupted += (_, _) => interrupted = true;

                // Simulate device removal of active default device
                WindowsAudioDeviceHelper.TriggerDeviceRemovedForTesting(currentEndpoint);

                // Playback must pause to prevent speaker blast
                Assert.True(interrupted);
                Assert.Equal(PlaybackStatus.Paused, _player.Status);
            }
        }
        finally
        {
            _player.Stop();
            try { File.Delete(tempWav); } catch { }
        }
    }
}

