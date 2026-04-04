namespace PhlanMic.Host.Core;

public sealed record ReceiverConfig
{
    public string BindAddress { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 42_100;

    public string TransportMode { get; init; } = "DebugTcpRawPcm";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
        {
            throw new InvalidOperationException("Receiver bind address must be provided.");
        }

        if (Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("Receiver port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(TransportMode))
        {
            throw new InvalidOperationException("Receiver transport mode must be provided.");
        }
    }
}

