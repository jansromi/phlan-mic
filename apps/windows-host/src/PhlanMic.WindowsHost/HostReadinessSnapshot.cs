namespace PhlanMic.WindowsHost;

public sealed record HostReadinessSnapshot(
    HostReadinessState State,
    bool IsReady,
    bool IsStreaming,
    string Summary,
    string? Detail,
    DateTimeOffset UpdatedAtUtc);
