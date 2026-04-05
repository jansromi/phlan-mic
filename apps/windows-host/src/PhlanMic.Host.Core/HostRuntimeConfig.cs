namespace PhlanMic.Host.Core;

public sealed record HostRuntimeConfig
{
    public string SessionName { get; init; } = "phlan-mic";

    public string LogLevel { get; init; } = "Information";

    public string LogFormat { get; init; } = "Text";

    public ReceiverConfig Receiver { get; init; } = new();

    public StreamBufferConfig Buffer { get; init; } = new();

    public StreamRobustnessConfig Robustness { get; init; } = new();

    public AudioFormat AudioFormat { get; init; } = AudioFormat.CreateMvpDefault();

    public GeneratedSignalTestModeConfig TestMode { get; init; } = new();

    public OutputConfig Output { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SessionName))
        {
            throw new InvalidOperationException("Session name must be provided.");
        }

        if (string.IsNullOrWhiteSpace(LogLevel))
        {
            throw new InvalidOperationException("Log level must be provided.");
        }

        if (string.IsNullOrWhiteSpace(LogFormat))
        {
            throw new InvalidOperationException("Log format must be provided.");
        }

        if (!string.Equals(LogFormat, "Text", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(LogFormat, "Json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Log format '{LogFormat}' is not supported.");
        }

        Receiver.Validate();
        Buffer.Validate();
        Robustness.Validate(Buffer);
        AudioFormat.Validate();
        TestMode.Validate();
        Output.Validate();
    }
}
