namespace PhlanMic.Host.Core;

public sealed record OutputConfig
{
    public const string WaveOutMode = "WaveOut";
    public const string DebugDrainMode = "DebugDrain";

    public string Mode { get; init; } = WaveOutMode;

    public int DeviceId { get; init; } = -1;

    public int TargetLatencyMs { get; init; } = 80;

    public bool LogAvailableDevices { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Mode))
        {
            throw new InvalidOperationException("Output mode must be provided.");
        }

        if (!string.Equals(Mode, WaveOutMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, DebugDrainMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Output mode '{Mode}' is not supported in Phase 2.");
        }

        if (DeviceId < -1)
        {
            throw new InvalidOperationException("Output device id must be -1 for the system default or a non-negative waveOut device id.");
        }

        if (TargetLatencyMs <= 0)
        {
            throw new InvalidOperationException("Output target latency must be greater than zero.");
        }
    }
}
