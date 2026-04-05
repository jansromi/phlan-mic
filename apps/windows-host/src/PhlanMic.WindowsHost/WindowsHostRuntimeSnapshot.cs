using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

public sealed record WindowsHostRuntimeSnapshot(
    HostReadinessSnapshot Readiness,
    ManualConnectSnapshot ManualConnect,
    OutputReadinessSnapshot Output,
    StreamSessionSnapshot Session,
    StreamStatisticsSnapshot Statistics,
    AudioOutputSnapshot AudioOutput,
    HostFaultSnapshot? Fault,
    IReadOnlyList<HostDiagnosticItem> Diagnostics);
