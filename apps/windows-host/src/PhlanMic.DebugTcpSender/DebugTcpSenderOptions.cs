using PhlanMic.Host.Core;

namespace PhlanMic.DebugTcpSender;

internal sealed record DebugTcpSenderOptions
{
    public string TransportMode { get; init; } = ReceiverConfig.DebugTcpRawPcmTransportMode;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 42_100;

    public int FrameCount { get; init; } = 250;

    public string SignalMode { get; init; } = "sine";

    public int SignalFrequencyHz { get; init; } = 1_000;

    public int DelayMs { get; init; } = AudioFormat.CreateMvpDefault().FrameDurationMs;

    public IReadOnlyList<int> DelayPatternMs { get; init; } = Array.Empty<int>();

    public int PauseAfterFrames { get; init; }

    public int PauseDurationMs { get; init; }

    public AudioFormat Format { get; init; } = AudioFormat.CreateMvpDefault();

    public int KeepAliveIntervalMs { get; init; } = 1_000;

    public int SessionTimeoutMs { get; init; } = 5_000;

    public void Validate()
    {
        if (!string.Equals(TransportMode, ReceiverConfig.DebugTcpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(TransportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Transport mode must be '{ReceiverConfig.DebugTcpRawPcmTransportMode}' or '{ReceiverConfig.UdpRawPcmTransportMode}'.");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("Host must be provided.");
        }

        if (Port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        if (FrameCount < 0)
        {
            throw new InvalidOperationException("Frame count must be zero or greater.");
        }

        if (!string.Equals(SignalMode, "sine", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(SignalMode, "silence", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Signal mode must be either 'sine' or 'silence'.");
        }

        if (SignalFrequencyHz <= 0)
        {
            throw new InvalidOperationException("Signal frequency must be greater than zero.");
        }

        if (DelayMs < 0)
        {
            throw new InvalidOperationException("Delay must be zero or greater.");
        }

        if (DelayPatternMs.Any(delay => delay < 0))
        {
            throw new InvalidOperationException("Delay pattern values must be zero or greater.");
        }

        if (PauseAfterFrames < 0)
        {
            throw new InvalidOperationException("Pause-after-frames must be zero or greater.");
        }

        if (PauseDurationMs < 0)
        {
            throw new InvalidOperationException("Pause duration must be zero or greater.");
        }

        if (KeepAliveIntervalMs <= 0)
        {
            throw new InvalidOperationException("Keep-alive interval must be greater than zero.");
        }

        if (SessionTimeoutMs <= KeepAliveIntervalMs)
        {
            throw new InvalidOperationException("Session timeout must be greater than the keep-alive interval.");
        }

        Format.Validate();
    }

    public static DebugTcpSenderOptions Parse(string[] args)
    {
        var options = new DebugTcpSenderOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                throw new DebugTcpSenderUsageException(GetUsageText());
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new DebugTcpSenderUsageException($"Unrecognized argument '{argument}'.");
            }

            if (index == args.Length - 1)
            {
                throw new DebugTcpSenderUsageException($"Missing value for argument '{argument}'.");
            }

            var value = args[++index];
            options = argument switch
            {
                "--transport" => options with { TransportMode = value },
                "--host" => options with { Host = value },
                "--port" => options with { Port = ParseInt(argument, value) },
                "--frames" => options with { FrameCount = ParseInt(argument, value) },
                "--mode" => options with { SignalMode = value },
                "--frequency-hz" => options with { SignalFrequencyHz = ParseInt(argument, value) },
                "--delay-ms" => options with { DelayMs = ParseInt(argument, value) },
                "--delay-pattern-ms" => options with { DelayPatternMs = ParseDelayPattern(argument, value) },
                "--pause-after-frames" => options with { PauseAfterFrames = ParseInt(argument, value) },
                "--pause-duration-ms" => options with { PauseDurationMs = ParseInt(argument, value) },
                "--keepalive-ms" => options with { KeepAliveIntervalMs = ParseInt(argument, value) },
                "--session-timeout-ms" => options with { SessionTimeoutMs = ParseInt(argument, value) },
                "--sample-rate" => options with { Format = options.Format with { SampleRate = ParseInt(argument, value) } },
                "--channels" => options with { Format = options.Format with { Channels = ParseInt(argument, value) } },
                "--frame-duration-ms" => options with { Format = options.Format with { FrameDurationMs = ParseInt(argument, value) } },
                _ => throw new DebugTcpSenderUsageException($"Unrecognized argument '{argument}'.")
            };
        }

        options.Validate();
        return options;
    }

    public static string GetUsageText() =>
        """
        Usage:
          dotnet run --project .\src\PhlanMic.DebugTcpSender -- [options]

        Options:
          --transport <mode>          Transport mode. DebugTcpRawPcm or UdpRawPcm. Default: DebugTcpRawPcm
          --host <name>               Target host. Default: 127.0.0.1
          --port <number>             Target TCP port. Debug mode uses it for PCM. UDP mode uses it for control. Default: 42100
          --frames <number>           Number of frames to send. 0 means until Ctrl+C. Default: 250
          --mode <sine|silence>       Signal type. Default: sine
          --frequency-hz <number>     Sine frequency. Default: 1000
          --delay-ms <number>         Delay between frames. Default: 20
          --delay-pattern-ms <list>   Comma-separated per-frame delays in ms, repeated cyclically
          --pause-after-frames <num>  Pause once after this many frames. Default: 0
          --pause-duration-ms <num>   Pause length in ms. Default: 0
          --keepalive-ms <number>     UDP control keep-alive interval. Default: 1000
          --session-timeout-ms <num>  UDP control session timeout. Default: 5000
          --sample-rate <number>      PCM sample rate. Default: 48000
          --channels <number>         PCM channel count. Default: 1
          --frame-duration-ms <num>   Frame duration. Default: 20
        """;

    private static int ParseInt(string argument, string value) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new DebugTcpSenderUsageException($"Argument '{argument}' expects an integer value.");

    private static IReadOnlyList<int> ParseDelayPattern(string argument, string value)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new DebugTcpSenderUsageException($"Argument '{argument}' expects at least one integer delay value.");
        }

        return parts.Select(part => ParseInt(argument, part)).ToArray();
    }
}
