namespace PhlanMic.Host.Core;

public sealed class RawPcmAudioPayloadDecoder : IAudioPayloadDecoder
{
    private readonly AudioFormat format;

    public RawPcmAudioPayloadDecoder(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.Validate();
        this.format = format;
    }

    public AudioFrame Decode(
        long sequenceNumber,
        DateTimeOffset capturedAtUtc,
        AudioPayloadCodec payloadCodec,
        ReadOnlyMemory<byte> payload)
    {
        if (payloadCodec is not AudioPayloadCodec.RawPcm16)
        {
            throw new TransportProtocolException(
                $"Payload codec '{payloadCodec.ToProtocolValue()}' is not supported by the raw PCM decoder.");
        }

        if (payload.Length != format.BytesPerFrame)
        {
            throw new TransportProtocolException(
                $"Raw PCM payload length {payload.Length} does not match expected frame size {format.BytesPerFrame}.");
        }

        return new AudioFrame(sequenceNumber, format, payload, capturedAtUtc);
    }
}
