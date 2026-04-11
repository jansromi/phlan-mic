namespace PhlanMic.WindowsHost;

public sealed record WaveOutDeviceCatalogEntry(
    int DeviceId,
    string Name,
    int Channels,
    string DriverVersion,
    string SupportedFormatsMask);

public sealed record VbCableRenderEndpointCatalogEntry(
    string EndpointId,
    string FriendlyName,
    bool IsActive,
    bool IsDefaultConsole,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications);

public sealed class HostOutputDeviceCatalog
{
    public IReadOnlyList<WaveOutDeviceCatalogEntry> GetWaveOutDevices() =>
        WaveOutDeviceEnumerator.Enumerate()
            .Select(device => new WaveOutDeviceCatalogEntry(
                device.DeviceId,
                device.Name,
                device.Channels,
                device.DriverVersion,
                device.SupportedFormatsMask))
            .ToArray();

    public IReadOnlyList<VbCableRenderEndpointCatalogEntry> GetVbCableRenderEndpoints()
    {
        var endpoints = CoreAudioEndpointEnumerator.Enumerate();
        return VbCableEndpointMatcher.GetRenderCandidates(endpoints)
            .Select(endpoint => new VbCableRenderEndpointCatalogEntry(
                endpoint.Id,
                endpoint.FriendlyName,
                endpoint.IsActive,
                endpoint.IsDefaultConsole,
                endpoint.IsDefaultMultimedia,
                endpoint.IsDefaultCommunications))
            .ToArray();
    }
}
