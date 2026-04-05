namespace PhlanMic.WindowsHost;

public enum HostReadinessState
{
    Stopped,
    Starting,
    Ready,
    Streaming,
    Stopping,
    Faulted
}
