namespace PhlanMic.Host.Core;

public sealed class TransportProtocolException : Exception
{
    public TransportProtocolException(string message)
        : base(message)
    {
    }
}
