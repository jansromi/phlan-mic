namespace PhlanMic.WindowsHost;

public sealed record HostFaultSnapshot(
    string Summary,
    string? Detail,
    string ExceptionType,
    DateTimeOffset OccurredAtUtc);
