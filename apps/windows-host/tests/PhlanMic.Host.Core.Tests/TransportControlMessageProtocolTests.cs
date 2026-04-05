using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class TransportControlMessageProtocolTests
{
    [Fact]
    public void SerializeAndDeserializeRoundTripHelloMessage()
    {
        var message = TransportControlMessage.CreateHello(
            "test-session",
            [AudioPayloadCodec.RawPcm16],
            keepAliveIntervalMs: 1000,
            sessionTimeoutMs: 5000);

        var json = TransportControlMessageProtocol.Serialize(message);
        var decoded = TransportControlMessageProtocol.Deserialize(json);

        Assert.Equal(TransportControlMessageType.Hello, decoded.GetMessageType());
        Assert.Equal("test-session", decoded.SessionName);
        Assert.NotNull(decoded.SupportedCodecs);
        Assert.Single(decoded.SupportedCodecs!);
        Assert.Equal("RawPcm16", decoded.SupportedCodecs[0]);
        Assert.Equal(1000, decoded.KeepAliveIntervalMs);
        Assert.Equal(5000, decoded.SessionTimeoutMs);
    }

    [Fact]
    public void DeserializeRejectsUnsupportedProtocolVersion()
    {
        const string json = """
            {"protocolVersion":999,"type":"hello","sessionName":"test","supportedCodecs":["RawPcm16"],"keepAliveIntervalMs":1000,"sessionTimeoutMs":5000}
            """;

        var exception = Assert.Throws<TransportProtocolException>(() => TransportControlMessageProtocol.Deserialize(json));
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeserializeRejectsUnknownMessageType()
    {
        const string json = """
            {"protocolVersion":1,"type":"wat","sessionName":"test"}
            """;

        var exception = Assert.Throws<TransportProtocolException>(() => TransportControlMessageProtocol.Deserialize(json));
        Assert.Contains("type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
