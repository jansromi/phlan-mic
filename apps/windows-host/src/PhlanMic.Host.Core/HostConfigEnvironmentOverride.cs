namespace PhlanMic.Host.Core;

public sealed record HostConfigEnvironmentOverride(
    string Name,
    string Suffix,
    string Value);
