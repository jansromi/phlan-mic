namespace PhlanMic.Host.Core;

public interface IAudioPayloadDecoder
{
    AudioFrame Decode(
        long sequenceNumber,
        DateTimeOffset capturedAtUtc,
        AudioPayloadCodec payloadCodec,
        ReadOnlyMemory<byte> payload);
}
