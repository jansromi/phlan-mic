namespace PhlanMic.WindowsHost;

internal static class CoreAudioEndpointEnumerator
{
    public static IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AudioEndpointInfo>();
        }

        CoreAudioDiscoveryInterop.IMMDeviceEnumerator? deviceEnumerator = null;

        deviceEnumerator = CoreAudioDiscoveryInterop.CreateDeviceEnumerator();
        var endpoints = new List<AudioEndpointInfo>();
        var defaultEndpoints = GetDefaultEndpointIds(deviceEnumerator);

        EnumerateFlow(
            deviceEnumerator,
            CoreAudioDiscoveryInterop.EDataFlow.eRender,
            AudioEndpointFlow.Render,
            defaultEndpoints,
            endpoints);
        EnumerateFlow(
            deviceEnumerator,
            CoreAudioDiscoveryInterop.EDataFlow.eCapture,
            AudioEndpointFlow.Capture,
            defaultEndpoints,
            endpoints);

        return endpoints
            .OrderBy(endpoint => endpoint.Flow)
            .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<(AudioEndpointFlow Flow, CoreAudioDiscoveryInterop.ERole Role), string> GetDefaultEndpointIds(
        CoreAudioDiscoveryInterop.IMMDeviceEnumerator deviceEnumerator)
    {
        var defaults = new Dictionary<(AudioEndpointFlow, CoreAudioDiscoveryInterop.ERole), string>();

        foreach (var flow in new[]
                 {
                     (CoreAudioDiscoveryInterop.EDataFlow.eRender, AudioEndpointFlow.Render),
                     (CoreAudioDiscoveryInterop.EDataFlow.eCapture, AudioEndpointFlow.Capture)
                 })
        {
            foreach (var role in new[]
                     {
                         CoreAudioDiscoveryInterop.ERole.eConsole,
                         CoreAudioDiscoveryInterop.ERole.eMultimedia,
                         CoreAudioDiscoveryInterop.ERole.eCommunications
                     })
            {
                var hresult = deviceEnumerator.GetDefaultAudioEndpoint(flow.Item1, role, out var device);
                if (hresult < 0 || device is null)
                {
                    continue;
                }

                CoreAudioDiscoveryInterop.ThrowIfFailed(device.GetId(out var endpointId), "IMMDevice.GetId");
                defaults[(flow.Item2, role)] = endpointId;
            }
        }

        return defaults;
    }

    private static void EnumerateFlow(
        CoreAudioDiscoveryInterop.IMMDeviceEnumerator deviceEnumerator,
        CoreAudioDiscoveryInterop.EDataFlow dataFlow,
        AudioEndpointFlow flow,
        IReadOnlyDictionary<(AudioEndpointFlow Flow, CoreAudioDiscoveryInterop.ERole Role), string> defaultEndpoints,
        ICollection<AudioEndpointInfo> endpoints)
    {
        CoreAudioDiscoveryInterop.ThrowIfFailed(
            deviceEnumerator.EnumAudioEndpoints(dataFlow, CoreAudioDiscoveryInterop.DeviceStateMaskAll, out var devices),
            "IMMDeviceEnumerator.EnumAudioEndpoints");
        CoreAudioDiscoveryInterop.ThrowIfFailed(devices.GetCount(out var deviceCount), "IMMDeviceCollection.GetCount");

        for (uint index = 0; index < deviceCount; index++)
        {
            CoreAudioDiscoveryInterop.ThrowIfFailed(devices.Item(index, out var device), "IMMDeviceCollection.Item");
            CoreAudioDiscoveryInterop.ThrowIfFailed(device.GetId(out var endpointId), "IMMDevice.GetId");
            CoreAudioDiscoveryInterop.ThrowIfFailed(device.GetState(out var endpointState), "IMMDevice.GetState");

            var friendlyName = ReadFriendlyName(device) ?? endpointId;

            endpoints.Add(
                new AudioEndpointInfo(
                    endpointId,
                    friendlyName,
                    friendlyName,
                    flow,
                    (AudioEndpointState)endpointState,
                    IsDefault(defaultEndpoints, flow, CoreAudioDiscoveryInterop.ERole.eConsole, endpointId),
                    IsDefault(defaultEndpoints, flow, CoreAudioDiscoveryInterop.ERole.eMultimedia, endpointId),
                    IsDefault(defaultEndpoints, flow, CoreAudioDiscoveryInterop.ERole.eCommunications, endpointId)));
        }
    }

    private static bool IsDefault(
        IReadOnlyDictionary<(AudioEndpointFlow Flow, CoreAudioDiscoveryInterop.ERole Role), string> defaultEndpoints,
        AudioEndpointFlow flow,
        CoreAudioDiscoveryInterop.ERole role,
        string endpointId) =>
        defaultEndpoints.TryGetValue((flow, role), out var defaultEndpointId) &&
        string.Equals(defaultEndpointId, endpointId, StringComparison.Ordinal);

    private static string? ReadFriendlyName(CoreAudioDiscoveryInterop.IMMDevice device)
    {
        var hresult = device.OpenPropertyStore(CoreAudioDiscoveryInterop.StgmRead, out var propertyStore);
        if (hresult < 0 || propertyStore is null)
        {
            return null;
        }

        return
            TryReadPropertyString(propertyStore, CoreAudioDiscoveryInterop.DeviceFriendlyNamePropertyKey) ??
            TryReadPropertyString(propertyStore, CoreAudioDiscoveryInterop.DeviceDescriptionPropertyKey) ??
            TryReadPropertyString(propertyStore, CoreAudioDiscoveryInterop.DeviceInterfaceFriendlyNamePropertyKey);
    }

    private static string? TryReadPropertyString(
        CoreAudioDiscoveryInterop.IPropertyStore propertyStore,
        CoreAudioDiscoveryInterop.PROPERTYKEY propertyKey)
    {
        CoreAudioDiscoveryInterop.PROPVARIANT propertyValue = default;
        var gotValue = false;

        try
        {
            var lookupKey = propertyKey;
            var hresult = propertyStore.GetValue(ref lookupKey, out propertyValue);
            if (hresult < 0)
            {
                return null;
            }

            gotValue = true;
            return CoreAudioDiscoveryInterop.TryConvertPropVariantToString(ref propertyValue);
        }
        finally
        {
            if (gotValue)
            {
                CoreAudioDiscoveryInterop.ClearPropVariant(ref propertyValue);
            }
        }
    }
}
