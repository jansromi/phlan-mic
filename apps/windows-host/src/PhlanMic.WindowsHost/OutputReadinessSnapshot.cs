namespace PhlanMic.WindowsHost;

public sealed record OutputReadinessSnapshot(
    OutputReadinessState State,
    bool IsReady,
    string Summary,
    string? Detail,
    string Mode,
    string? DeviceName,
    string? EndpointId,
    string? PairedCaptureEndpointName);
