namespace PhlanMic.Host.Core;

public sealed record StreamSessionSnapshot(
    StreamSessionState State,
    string TransportMode,
    string? LocalEndpoint,
    string? RemoteEndpoint,
    long ConnectionId,
    long ConnectionCount,
    long DisconnectCount,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastActivityUtc,
    DateTimeOffset? LastDisconnectedAtUtc,
    string? StatusDetail);
