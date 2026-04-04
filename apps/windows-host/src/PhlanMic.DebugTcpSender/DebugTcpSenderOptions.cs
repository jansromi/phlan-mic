using PhlanMic.Host.Core;

namespace PhlanMic.DebugTcpSender;

internal sealed record DebugTcpSenderOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 42_100;

    public int FrameCount { get; init; } = 250;

    public string SignalMode { get; init; } = "sine";

    public int SignalFrequencyHz { get; init; } = 1_000;

    public int DelayMs { get; init; } = AudioFormat.CreateMvpDefault().FrameDurationMs;

    public AudioFormat Format { get; init; } = AudioFormat.CreateMvpDefault();

    public void Validate()
    {
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
                "--host" => options with { Host = value },
                "--port" => options with { Port = ParseInt(argument, value) },
                "--frames" => options with { FrameCount = ParseInt(argument, value) },
                "--mode" => options with { SignalMode = value },
                "--frequency-hz" => options with { SignalFrequencyHz = ParseInt(argument, value) },
                "--delay-ms" => options with { DelayMs = ParseInt(argument, value) },
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
          --host <name>               Target host. Default: 127.0.0.1
          --port <number>             Target port. Default: 42100
          --frames <number>           Number of frames to send. 0 means until Ctrl+C. Default: 250
          --mode <sine|silence>       Signal type. Default: sine
          --frequency-hz <number>     Sine frequency. Default: 1000
          --delay-ms <number>         Delay between frames. Default: 20
          --sample-rate <number>      PCM sample rate. Default: 48000
          --channels <number>         PCM channel count. Default: 1
          --frame-duration-ms <num>   Frame duration. Default: 20
        """;

    private static int ParseInt(string argument, string value) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new DebugTcpSenderUsageException($"Argument '{argument}' expects an integer value.");
}
