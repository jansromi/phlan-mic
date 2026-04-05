namespace PhlanMic.Host.Core;

public enum TransportControlMessageType
{
    Hello,
    HelloAccepted,
    StartStream,
    StartAccepted,
    StopStream,
    KeepAlive,
    Error
}

public static class TransportControlMessageTypeExtensions
{
    public static string ToProtocolValue(this TransportControlMessageType messageType) =>
        messageType switch
        {
            TransportControlMessageType.Hello => "hello",
            TransportControlMessageType.HelloAccepted => "helloAccepted",
            TransportControlMessageType.StartStream => "startStream",
            TransportControlMessageType.StartAccepted => "startAccepted",
            TransportControlMessageType.StopStream => "stopStream",
            TransportControlMessageType.KeepAlive => "keepAlive",
            TransportControlMessageType.Error => "error",
            _ => throw new InvalidOperationException($"Unsupported control message type '{messageType}'.")
        };

    public static bool TryParseProtocolValue(string? value, out TransportControlMessageType messageType)
    {
        if (string.Equals(value, "hello", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.Hello;
            return true;
        }

        if (string.Equals(value, "helloAccepted", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.HelloAccepted;
            return true;
        }

        if (string.Equals(value, "startStream", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.StartStream;
            return true;
        }

        if (string.Equals(value, "startAccepted", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.StartAccepted;
            return true;
        }

        if (string.Equals(value, "stopStream", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.StopStream;
            return true;
        }

        if (string.Equals(value, "keepAlive", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.KeepAlive;
            return true;
        }

        if (string.Equals(value, "error", StringComparison.OrdinalIgnoreCase))
        {
            messageType = TransportControlMessageType.Error;
            return true;
        }

        messageType = default;
        return false;
    }
}
