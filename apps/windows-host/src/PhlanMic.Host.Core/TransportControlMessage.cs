namespace PhlanMic.Host.Core;

public sealed record TransportControlMessage
{
    public int ProtocolVersion { get; init; } = TransportProtocolConstants.ProtocolVersion;

    public string Type { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string? SessionName { get; init; }

    public int? AudioPort { get; init; }

    public string[]? SupportedCodecs { get; init; }

    public string? PayloadCodec { get; init; }

    public int? SampleRate { get; init; }

    public int? Channels { get; init; }

    public int? BitsPerSample { get; init; }

    public int? FrameDurationMs { get; init; }

    public int? KeepAliveIntervalMs { get; init; }

    public int? SessionTimeoutMs { get; init; }

    public string? Detail { get; init; }

    public TransportControlMessageType GetMessageType()
    {
        if (!TransportControlMessageTypeExtensions.TryParseProtocolValue(Type, out var messageType))
        {
            throw new TransportProtocolException($"Unsupported control message type '{Type}'.");
        }

        return messageType;
    }

    public Guid GetRequiredSessionId()
    {
        if (!Guid.TryParse(SessionId, out var sessionId) || sessionId == Guid.Empty)
        {
            throw new TransportProtocolException("Control message session id is missing or invalid.");
        }

        return sessionId;
    }

    public AudioPayloadCodec GetRequiredPayloadCodec()
    {
        if (!AudioPayloadCodecExtensions.TryParseProtocolValue(PayloadCodec, out var codec))
        {
            throw new TransportProtocolException("Control message payload codec is missing or unsupported.");
        }

        return codec;
    }

    public void ValidateProtocolVersion()
    {
        if (ProtocolVersion != TransportProtocolConstants.ProtocolVersion)
        {
            throw new TransportProtocolException(
                $"Protocol version {ProtocolVersion} is not supported. Expected {TransportProtocolConstants.ProtocolVersion}.");
        }
    }

    public void Validate()
    {
        ValidateProtocolVersion();

        _ = GetMessageType();

        if (KeepAliveIntervalMs is <= 0)
        {
            throw new TransportProtocolException("Keep-alive interval must be greater than zero when provided.");
        }

        if (SessionTimeoutMs is <= 0)
        {
            throw new TransportProtocolException("Session timeout must be greater than zero when provided.");
        }

        if (AudioPort is < 1 or > 65_535)
        {
            throw new TransportProtocolException("Audio port must be between 1 and 65535 when provided.");
        }

        if (SampleRate is <= 0)
        {
            throw new TransportProtocolException("Sample rate must be greater than zero when provided.");
        }

        if (Channels is <= 0)
        {
            throw new TransportProtocolException("Channel count must be greater than zero when provided.");
        }

        if (BitsPerSample is <= 0)
        {
            throw new TransportProtocolException("Bits-per-sample must be greater than zero when provided.");
        }

        if (FrameDurationMs is <= 0)
        {
            throw new TransportProtocolException("Frame duration must be greater than zero when provided.");
        }
    }

    public static TransportControlMessage CreateHello(
        string sessionName,
        IReadOnlyList<AudioPayloadCodec> supportedCodecs,
        int keepAliveIntervalMs,
        int sessionTimeoutMs) =>
        new()
        {
            Type = TransportControlMessageType.Hello.ToProtocolValue(),
            SessionName = sessionName,
            SupportedCodecs = supportedCodecs.Select(codec => codec.ToProtocolValue()).ToArray(),
            KeepAliveIntervalMs = keepAliveIntervalMs,
            SessionTimeoutMs = sessionTimeoutMs
        };

    public static TransportControlMessage CreateHelloAccepted(
        Guid sessionId,
        int audioPort,
        AudioPayloadCodec payloadCodec,
        AudioFormat format,
        int keepAliveIntervalMs,
        int sessionTimeoutMs,
        string? detail = null) =>
        new()
        {
            Type = TransportControlMessageType.HelloAccepted.ToProtocolValue(),
            SessionId = sessionId.ToString("D"),
            AudioPort = audioPort,
            PayloadCodec = payloadCodec.ToProtocolValue(),
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            FrameDurationMs = format.FrameDurationMs,
            KeepAliveIntervalMs = keepAliveIntervalMs,
            SessionTimeoutMs = sessionTimeoutMs,
            Detail = detail
        };

    public static TransportControlMessage CreateStartStream(
        Guid sessionId,
        AudioPayloadCodec payloadCodec,
        AudioFormat format) =>
        new()
        {
            Type = TransportControlMessageType.StartStream.ToProtocolValue(),
            SessionId = sessionId.ToString("D"),
            PayloadCodec = payloadCodec.ToProtocolValue(),
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            FrameDurationMs = format.FrameDurationMs
        };

    public static TransportControlMessage CreateStartAccepted(Guid sessionId, string? detail = null) =>
        new()
        {
            Type = TransportControlMessageType.StartAccepted.ToProtocolValue(),
            SessionId = sessionId.ToString("D"),
            Detail = detail
        };

    public static TransportControlMessage CreateStopStream(Guid sessionId, string? detail = null) =>
        new()
        {
            Type = TransportControlMessageType.StopStream.ToProtocolValue(),
            SessionId = sessionId.ToString("D"),
            Detail = detail
        };

    public static TransportControlMessage CreateKeepAlive(Guid sessionId) =>
        new()
        {
            Type = TransportControlMessageType.KeepAlive.ToProtocolValue(),
            SessionId = sessionId.ToString("D")
        };

    public static TransportControlMessage CreateError(string detail, Guid? sessionId = null) =>
        new()
        {
            Type = TransportControlMessageType.Error.ToProtocolValue(),
            SessionId = sessionId?.ToString("D"),
            Detail = detail
        };
}
