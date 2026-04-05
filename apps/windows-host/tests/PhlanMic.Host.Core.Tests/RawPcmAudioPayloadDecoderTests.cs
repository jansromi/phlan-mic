using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class RawPcmAudioPayloadDecoderTests
{
    [Fact]
    public void DecodeCreatesAudioFrameForMatchingPayload()
    {
        var format = AudioFormat.CreateMvpDefault();
        var decoder = new RawPcmAudioPayloadDecoder(format);
        var payload = new byte[format.BytesPerFrame];
        payload[0] = 1;

        var frame = decoder.Decode(7, DateTimeOffset.UtcNow, AudioPayloadCodec.RawPcm16, payload);

        Assert.Equal(7, frame.SequenceNumber);
        Assert.Equal(format, frame.Format);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void DecodeRejectsUnexpectedPayloadLength()
    {
        var decoder = new RawPcmAudioPayloadDecoder(AudioFormat.CreateMvpDefault());

        var exception = Assert.Throws<TransportProtocolException>(() =>
            decoder.Decode(1, DateTimeOffset.UtcNow, AudioPayloadCodec.RawPcm16, [1, 2, 3]));

        Assert.Contains("frame size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
