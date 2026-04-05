using System.Buffers.Binary;
using System.Text;

namespace PhlanMic.Host.Core;

public static class TransportAudioPacketSerializer
{
    public static byte[] Serialize(TransportAudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        packet.Validate();

        var buffer = new byte[TransportProtocolConstants.AudioPacketHeaderSize + packet.Payload.Length];
        var span = buffer.AsSpan();
        Encoding.ASCII.GetBytes(TransportProtocolConstants.AudioPacketMagic.AsSpan(), span[..4]);
        span[4] = TransportProtocolConstants.ProtocolVersion;
        span[5] = checked((byte)packet.PayloadCodec);
        span[6] = 0;
        span[7] = 0;
        packet.SessionId.TryWriteBytes(span.Slice(8, 16));
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(24, 8), packet.SequenceNumber);
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(32, 8), packet.CapturedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(40, 4), packet.Payload.Length);
        packet.Payload.CopyTo(span.Slice(TransportProtocolConstants.AudioPacketHeaderSize));
        return buffer;
    }

    public static TransportAudioPacket Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < TransportProtocolConstants.AudioPacketHeaderSize)
        {
            throw new TransportProtocolException("Audio packet is smaller than the fixed protocol header.");
        }

        if (!bytes[..4].SequenceEqual(Encoding.ASCII.GetBytes(TransportProtocolConstants.AudioPacketMagic)))
        {
            throw new TransportProtocolException("Audio packet magic header is invalid.");
        }

        var protocolVersion = bytes[4];
        if (protocolVersion != TransportProtocolConstants.ProtocolVersion)
        {
            throw new TransportProtocolException(
                $"Audio packet protocol version {protocolVersion} is not supported. Expected {TransportProtocolConstants.ProtocolVersion}.");
        }

        if (!Enum.IsDefined(typeof(AudioPayloadCodec), (int)bytes[5]))
        {
            throw new TransportProtocolException($"Audio packet codec value {bytes[5]} is not supported.");
        }

        var payloadCodec = (AudioPayloadCodec)bytes[5];
        var sessionId = new Guid(bytes.Slice(8, 16));
        var sequenceNumber = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(24, 8));
        var capturedAtUnixMs = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(32, 8));
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(40, 4));

        if (payloadLength <= 0)
        {
            throw new TransportProtocolException("Audio packet payload length must be greater than zero.");
        }

        if (payloadLength > TransportProtocolConstants.MaxPayloadLengthBytes)
        {
            throw new TransportProtocolException(
                $"Audio packet payload length {payloadLength} exceeds the maximum supported size of {TransportProtocolConstants.MaxPayloadLengthBytes} bytes.");
        }

        if (bytes.Length != TransportProtocolConstants.AudioPacketHeaderSize + payloadLength)
        {
            throw new TransportProtocolException("Audio packet payload length does not match the datagram size.");
        }

        var packet = new TransportAudioPacket(
            sessionId,
            payloadCodec,
            sequenceNumber,
            DateTimeOffset.FromUnixTimeMilliseconds(capturedAtUnixMs),
            bytes.Slice(TransportProtocolConstants.AudioPacketHeaderSize, payloadLength).ToArray());

        packet.Validate();
        return packet;
    }
}
