using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class AudioFormatTests
{
    [Fact]
    public void MvpFormatProducesExpectedFrameShape()
    {
        var format = AudioFormat.CreateMvpDefault();

        format.Validate();

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(20, format.FrameDurationMs);
        Assert.Equal(960, format.SamplesPerFrame);
        Assert.Equal(1_920, format.BytesPerFrame);
    }

    [Fact]
    public void ValidateRejectsUnsupportedBitDepth()
    {
        var format = AudioFormat.CreateMvpDefault() with
        {
            BitsPerSample = 32
        };

        var exception = Assert.Throws<InvalidOperationException>(format.Validate);
        Assert.Contains("16-bit", exception.Message);
    }
}

