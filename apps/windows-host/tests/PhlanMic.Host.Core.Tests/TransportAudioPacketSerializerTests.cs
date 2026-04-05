using System.Buffers.Binary;
using System.Text;
using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class TransportAudioPacketSerializerTests
{
    [Fact]
    public void SerializeAndDeserializeRoundTripAudioPacket()
    {
        var packet = new TransportAudioPacket(
            Guid.NewGuid(),
            AudioPayloadCodec.RawPcm16,
            42,
            new DateTimeOffset(2026, 04, 05, 12, 0, 0, TimeSpan.Zero),
            [1, 2, 3, 4]);

        var bytes = TransportAudioPacketSerializer.Serialize(packet);
        var decoded = TransportAudioPacketSerializer.Deserialize(bytes);

        Assert.Equal(packet.SessionId, decoded.SessionId);
        Assert.Equal(packet.PayloadCodec, decoded.PayloadCodec);
        Assert.Equal(packet.SequenceNumber, decoded.SequenceNumber);
        Assert.Equal(packet.CapturedAtUtc, decoded.CapturedAtUtc);
        Assert.Equal(packet.Payload, decoded.Payload);
    }

    [Fact]
    public void DeserializeRejectsInvalidMagicHeader()
    {
        var packet = new TransportAudioPacket(
            Guid.NewGuid(),
            AudioPayloadCodec.RawPcm16,
            1,
            DateTimeOffset.UtcNow,
            [1, 2, 3, 4]);

        var bytes = TransportAudioPacketSerializer.Serialize(packet);
        Encoding.ASCII.GetBytes("BAD!".AsSpan(), bytes.AsSpan(0, 4));

        var exception = Assert.Throws<TransportProtocolException>(() => TransportAudioPacketSerializer.Deserialize(bytes));
        Assert.Contains("magic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeRejectsPayloadLengthMismatch()
    {
        var packet = new TransportAudioPacket(
            Guid.NewGuid(),
            AudioPayloadCodec.RawPcm16,
            1,
            DateTimeOffset.UtcNow,
            [1, 2, 3, 4]);

        var bytes = TransportAudioPacketSerializer.Serialize(packet);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(40, 4), 2);

        var exception = Assert.Throws<TransportProtocolException>(() => TransportAudioPacketSerializer.Deserialize(bytes));
        Assert.Contains("length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
