using System.Text;

namespace PhlanMic.WindowsHost;

public sealed record ManualConnectSnapshot(
    string SessionName,
    string HostName,
    string BindAddress,
    string? RecommendedIpv4Address,
    IReadOnlyList<string> AvailableIpv4Addresses,
    int ControlPort,
    int? AudioPort,
    string TransportMode,
    string PayloadCodec)
{
    public string ConnectionHost => RecommendedIpv4Address ?? BindAddress;

    public string GetSummaryText()
    {
        var builder = new StringBuilder()
            .AppendLine($"Session: {SessionName}")
            .AppendLine($"Host: {HostName}")
            .AppendLine($"Recommended IP: {RecommendedIpv4Address ?? "No LAN IPv4 address detected"}")
            .AppendLine($"Bind Address: {BindAddress}")
            .AppendLine($"Control Port: {ControlPort}");

        if (AudioPort.HasValue)
        {
            builder.AppendLine($"Audio Port: {AudioPort.Value}");
        }

        builder
            .AppendLine($"Transport: {TransportMode}")
            .AppendLine($"Payload Codec: {PayloadCodec}");

        if (AvailableIpv4Addresses.Count > 0)
        {
            builder.AppendLine($"Other IPv4 Addresses: {string.Join(", ", AvailableIpv4Addresses)}");
        }

        return builder.ToString().TrimEnd();
    }

    public string GetCopyText() => GetSummaryText();
}
