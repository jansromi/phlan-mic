namespace PhlanMic.WindowsHost;

internal enum AudioEndpointFlow
{
    Render,
    Capture
}

[Flags]
internal enum AudioEndpointState
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8
}

internal sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    AudioEndpointFlow Flow,
    AudioEndpointState State,
    bool IsDefaultConsole,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications)
{
    public bool IsActive => (State & AudioEndpointState.Active) == AudioEndpointState.Active;
}
