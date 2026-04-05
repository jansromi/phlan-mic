namespace PhlanMic.WindowsHost;

public sealed record HostDiagnosticItem(
    HostDiagnosticSeverity Severity,
    string Title,
    string Message);
