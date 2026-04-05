namespace PhlanMic.Host.Core;

public sealed record TransportAudioPacket(
    Guid SessionId,
    AudioPayloadCodec PayloadCodec,
    long SequenceNumber,
    DateTimeOffset CapturedAtUtc,
    byte[] Payload)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty)
        {
            throw new TransportProtocolException("Audio packet session id must be provided.");
        }

        if (SequenceNumber <= 0)
        {
            throw new TransportProtocolException("Audio packet sequence number must be greater than zero.");
        }

        if (Payload.Length == 0)
        {
            throw new TransportProtocolException("Audio packet payload cannot be empty.");
        }

        if (Payload.Length > TransportProtocolConstants.MaxPayloadLengthBytes)
        {
            throw new TransportProtocolException(
                $"Audio packet payload length {Payload.Length} exceeds the maximum supported size of {TransportProtocolConstants.MaxPayloadLengthBytes} bytes.");
        }
    }
}
