namespace PhlanMic.Host.Core;

public sealed record ReceiverConfig
{
    public const string DebugTcpRawPcmTransportMode = "DebugTcpRawPcm";
    public const string UdpRawPcmTransportMode = "UdpRawPcm";

    public string BindAddress { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 42_100;

    public string TransportMode { get; init; } = "DebugTcpRawPcm";

    public int? AudioPort { get; init; }

    public string PayloadCodec { get; init; } = "RawPcm16";

    public int KeepAliveIntervalMs { get; init; } = 1_000;

    public int SessionTimeoutMs { get; init; } = 5_000;

    public int GetResolvedAudioPort()
    {
        if (AudioPort.HasValue)
        {
            return AudioPort.Value;
        }

        if (Port == 65_535)
        {
            throw new InvalidOperationException("Receiver audio port must be configured explicitly when the control port is 65535.");
        }

        return Port + 1;
    }

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

        if (!AudioPayloadCodecExtensions.TryParseProtocolValue(PayloadCodec, out var payloadCodec))
        {
            throw new InvalidOperationException(
                $"Receiver payload codec '{PayloadCodec}' is not supported.");
        }

        if (KeepAliveIntervalMs <= 0)
        {
            throw new InvalidOperationException("Receiver keep-alive interval must be greater than zero.");
        }

        if (SessionTimeoutMs <= 0)
        {
            throw new InvalidOperationException("Receiver session timeout must be greater than zero.");
        }

        if (SessionTimeoutMs <= KeepAliveIntervalMs)
        {
            throw new InvalidOperationException("Receiver session timeout must be greater than the keep-alive interval.");
        }

        if (string.Equals(TransportMode, DebugTcpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            if (payloadCodec is not AudioPayloadCodec.RawPcm16)
            {
                throw new InvalidOperationException("Debug TCP raw PCM mode only supports the RawPcm16 payload codec.");
            }

            return;
        }

        if (string.Equals(TransportMode, UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            var audioPort = GetResolvedAudioPort();
            if (audioPort is < 1 or > 65_535)
            {
                throw new InvalidOperationException("Receiver audio port must be between 1 and 65535.");
            }

            if (audioPort == Port)
            {
                throw new InvalidOperationException("Receiver audio port must be different from the control port.");
            }

            if (payloadCodec is not AudioPayloadCodec.RawPcm16)
            {
                throw new InvalidOperationException("UdpRawPcm mode only supports the RawPcm16 payload codec.");
            }

            return;
        }

        throw new InvalidOperationException(
            $"Receiver transport mode '{TransportMode}' is not supported.");
    }
}
