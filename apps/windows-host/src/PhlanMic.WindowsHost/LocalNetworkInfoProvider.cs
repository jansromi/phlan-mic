using System.Net;
using System.Net.NetworkInformation;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal static class LocalNetworkInfoProvider
{
    public static ManualConnectSnapshot CreateSnapshot(HostRuntimeConfig config)
    {
        var addresses = GetIpv4Addresses(config.Receiver.BindAddress);
        return new ManualConnectSnapshot(
            config.SessionName,
            Environment.MachineName,
            config.Receiver.BindAddress,
            addresses.Count > 0 ? addresses[0] : null,
            addresses,
            config.Receiver.Port,
            string.Equals(config.Receiver.TransportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase)
                ? config.Receiver.GetResolvedAudioPort()
                : null,
            config.Receiver.TransportMode,
            config.Receiver.PayloadCodec);
    }

    private static IReadOnlyList<string> GetIpv4Addresses(string bindAddress)
    {
        var preferred = new List<string>();
        var fallback = new List<string>();

        if (TryGetSpecificBindAddress(bindAddress, out var specificBindAddress))
        {
            preferred.Add(specificBindAddress.ToString());
        }

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicastAddress.Address))
                {
                    continue;
                }

                var address = unicastAddress.Address.ToString();
                if (preferred.Contains(address, StringComparer.OrdinalIgnoreCase) ||
                    fallback.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsApipa(unicastAddress.Address))
                {
                    fallback.Add(address);
                }
                else
                {
                    preferred.Add(address);
                }
            }
        }

        return preferred.Concat(fallback).ToArray();
    }

    private static bool TryGetSpecificBindAddress(string bindAddress, out IPAddress address)
    {
        if (IPAddress.TryParse(bindAddress, out var parsedAddress) &&
            parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(parsedAddress) &&
            !parsedAddress.Equals(IPAddress.Any))
        {
            address = parsedAddress;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
