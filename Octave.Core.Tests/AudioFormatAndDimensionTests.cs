using System;
using System.IO;
using Octave.Core.Helpers;
using Xunit;

namespace Octave.Core.Tests;

public class AudioFormatAndDimensionTests
{
    [Theory]
    [InlineData(".mp3", true)]
    [InlineData("mp3", true)]
    [InlineData(".FLAC", true)]
    [InlineData(".m4a", true)]
    [InlineData(".wav", true)]
    [InlineData(".ogg", true)]
    [InlineData(".opus", true)]
    [InlineData(".ape", true)]
    [InlineData(".wv", true)]
    [InlineData(".dsf", true)]
    [InlineData(".dff", true)]
    [InlineData(".aiff", true)]
    [InlineData(".aif", true)]
    [InlineData(".alac", true)]
    [InlineData(".exe", false)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AudioFormatRegistry_Validation(string? ext, bool expected)
    {
        bool actual = AudioFormatRegistry.IsSupported(ext);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ImageDimensionReader_ValidPng_ReadsExactDimensions()
    {
        // Minimal valid PNG header: 8 byte magic + 4 byte IHDR length + 4 byte chunk type + 4 byte width (500px) + 4 byte height (300px)
        byte[] pngBytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D,                         // IHDR chunk length (13)
            0x49, 0x48, 0x44, 0x52,                         // "IHDR"
            0x00, 0x00, 0x01, 0xF4,                         // Width: 500 (0x01F4)
            0x00, 0x00, 0x01, 0x2C,                         // Height: 300 (0x012C)
            0x08, 0x02, 0x00, 0x00, 0x00                    // Bit depth, color type, compression, filter, interlace
        };

        string tempPath = Path.Combine(Path.GetTempPath(), $"test_png_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tempPath, pngBytes);
            int? width = ImageDimensionReader.TryReadWidth(tempPath);
            Assert.NotNull(width);
            Assert.Equal(500, width.Value);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
