namespace PhlanMic.Host.Core;

public sealed record GeneratedSignalTestModeConfig
{
    public bool Enabled { get; init; }

    public int SignalFrequencyHz { get; init; } = 1_000;

    public void Validate()
    {
        if (SignalFrequencyHz <= 0)
        {
            throw new InvalidOperationException("Signal frequency must be greater than zero.");
        }
    }
}

