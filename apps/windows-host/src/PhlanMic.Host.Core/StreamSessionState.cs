namespace PhlanMic.Host.Core;

public enum StreamSessionState
{
    Stopped,
    Listening,
    Connected,
    Streaming,
    Disconnected,
    Faulted
}
