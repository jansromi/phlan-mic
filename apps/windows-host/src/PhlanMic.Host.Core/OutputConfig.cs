namespace PhlanMic.Host.Core;

public sealed record OutputConfig
{
    public const string WaveOutMode = "WaveOut";
    public const string DebugDrainMode = "DebugDrain";
    public const string VbCableMode = "VbCable";
    public const string VbCableToneProbeMode = "VbCableToneProbe";

    public string Mode { get; init; } = VbCableMode;

    public int DeviceId { get; init; } = -1;

    public string? EndpointId { get; init; }

    public int TargetLatencyMs { get; init; } = 80;

    public bool LogAvailableDevices { get; init; } = true;

    public bool LogEndpointInventory { get; init; } = true;

    public static bool UsesWaveOutDevice(string? mode) =>
        string.Equals(mode, WaveOutMode, StringComparison.OrdinalIgnoreCase);

    public static bool UsesVbCableEndpoint(string? mode) =>
        string.Equals(mode, VbCableMode, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, VbCableToneProbeMode, StringComparison.OrdinalIgnoreCase);

    public static bool UsesLocalToneProbe(string? mode) =>
        string.Equals(mode, VbCableToneProbeMode, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Mode))
        {
            throw new InvalidOperationException("Output mode must be provided.");
        }

        if (!string.Equals(Mode, WaveOutMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, DebugDrainMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, VbCableMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, VbCableToneProbeMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Output mode '{Mode}' is not supported in Phase 3.");
        }

        if (DeviceId < -1)
        {
            throw new InvalidOperationException("Output device id must be -1 for the system default or a non-negative waveOut device id.");
        }

        if (EndpointId is not null && string.IsNullOrWhiteSpace(EndpointId))
        {
            throw new InvalidOperationException("Output endpoint id cannot be empty when provided.");
        }

        if (TargetLatencyMs <= 0)
        {
            throw new InvalidOperationException("Output target latency must be greater than zero.");
        }

        if (!UsesVbCableEndpoint(Mode) && EndpointId is not null)
        {
            throw new InvalidOperationException("Output endpoint id can only be used with VB-CABLE output modes.");
        }

        if (!UsesWaveOutDevice(Mode) && DeviceId != -1)
        {
            throw new InvalidOperationException("Output device id can only be used with WaveOut mode.");
        }
    }
}
