namespace PhlanMic.Host.Core;

public sealed record HostRuntimeConfig
{
    public string SessionName { get; init; } = "phlan-mic";

    public string LogLevel { get; init; } = "Information";

    public ReceiverConfig Receiver { get; init; } = new();

    public StreamBufferConfig Buffer { get; init; } = new();

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

        Receiver.Validate();
        Buffer.Validate();
        AudioFormat.Validate();
        TestMode.Validate();
        Output.Validate();
    }
}
