namespace PhlanMic.WindowsHost;

internal static class CoreAudioEndpointEnumerator
{
    public static IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AudioEndpointInfo>();
        }

        CoreAudioInterop.IMMDeviceEnumerator? deviceEnumerator = null;

        try
        {
            deviceEnumerator = CoreAudioInterop.CreateDeviceEnumerator();
            var endpoints = new List<AudioEndpointInfo>();
            var defaultEndpoints = GetDefaultEndpointIds(deviceEnumerator);

            EnumerateFlow(
                deviceEnumerator,
                CoreAudioInterop.EDataFlow.eRender,
                AudioEndpointFlow.Render,
                defaultEndpoints,
                endpoints);
            EnumerateFlow(
                deviceEnumerator,
                CoreAudioInterop.EDataFlow.eCapture,
                AudioEndpointFlow.Capture,
                defaultEndpoints,
                endpoints);

            return endpoints
                .OrderBy(endpoint => endpoint.Flow)
                .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(deviceEnumerator);
        }
    }

    private static Dictionary<(AudioEndpointFlow Flow, CoreAudioInterop.ERole Role), string> GetDefaultEndpointIds(
        CoreAudioInterop.IMMDeviceEnumerator deviceEnumerator)
    {
        var defaults = new Dictionary<(AudioEndpointFlow, CoreAudioInterop.ERole), string>();

        foreach (var flow in new[]
                 {
                     (CoreAudioInterop.EDataFlow.eRender, AudioEndpointFlow.Render),
                     (CoreAudioInterop.EDataFlow.eCapture, AudioEndpointFlow.Capture)
                 })
        {
            foreach (var role in new[]
                     {
                         CoreAudioInterop.ERole.eConsole,
                         CoreAudioInterop.ERole.eMultimedia,
                         CoreAudioInterop.ERole.eCommunications
                     })
            {
                CoreAudioInterop.IMMDevice? device = null;

                try
                {
                    var hresult = deviceEnumerator.GetDefaultAudioEndpoint(flow.Item1, role, out device);
                    if (hresult < 0 || device is null)
                    {
                        continue;
                    }

                    CoreAudioInterop.ThrowIfFailed(device.GetId(out var endpointId), "IMMDevice.GetId");
                    defaults[(flow.Item2, role)] = endpointId;
                }
                finally
                {
                    CoreAudioInterop.ReleaseComObject(device);
                }
            }
        }

        return defaults;
    }

    private static void EnumerateFlow(
        CoreAudioInterop.IMMDeviceEnumerator deviceEnumerator,
        CoreAudioInterop.EDataFlow dataFlow,
        AudioEndpointFlow flow,
        IReadOnlyDictionary<(AudioEndpointFlow Flow, CoreAudioInterop.ERole Role), string> defaultEndpoints,
        ICollection<AudioEndpointInfo> endpoints)
    {
        CoreAudioInterop.IMMDeviceCollection? devices = null;

        try
        {
            CoreAudioInterop.ThrowIfFailed(
                deviceEnumerator.EnumAudioEndpoints(dataFlow, CoreAudioInterop.DeviceStateMaskAll, out devices),
                "IMMDeviceEnumerator.EnumAudioEndpoints");
            CoreAudioInterop.ThrowIfFailed(devices.GetCount(out var deviceCount), "IMMDeviceCollection.GetCount");

            for (uint index = 0; index < deviceCount; index++)
            {
                CoreAudioInterop.IMMDevice? device = null;

                try
                {
                    CoreAudioInterop.ThrowIfFailed(devices.Item(index, out device), "IMMDeviceCollection.Item");
                    CoreAudioInterop.ThrowIfFailed(device.GetId(out var endpointId), "IMMDevice.GetId");
                    CoreAudioInterop.ThrowIfFailed(device.GetState(out var endpointState), "IMMDevice.GetState");

                    endpoints.Add(
                        new AudioEndpointInfo(
                            endpointId,
                            ReadFriendlyName(device) ?? endpointId,
                            flow,
                            (AudioEndpointState)endpointState,
                            IsDefault(defaultEndpoints, flow, CoreAudioInterop.ERole.eConsole, endpointId),
                            IsDefault(defaultEndpoints, flow, CoreAudioInterop.ERole.eMultimedia, endpointId),
                            IsDefault(defaultEndpoints, flow, CoreAudioInterop.ERole.eCommunications, endpointId)));
                }
                finally
                {
                    CoreAudioInterop.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(devices);
        }
    }

    private static bool IsDefault(
        IReadOnlyDictionary<(AudioEndpointFlow Flow, CoreAudioInterop.ERole Role), string> defaultEndpoints,
        AudioEndpointFlow flow,
        CoreAudioInterop.ERole role,
        string endpointId) =>
        defaultEndpoints.TryGetValue((flow, role), out var defaultEndpointId) &&
        string.Equals(defaultEndpointId, endpointId, StringComparison.Ordinal);

    private static string? ReadFriendlyName(CoreAudioInterop.IMMDevice device)
    {
        CoreAudioInterop.IPropertyStore? propertyStore = null;

        try
        {
            CoreAudioInterop.ThrowIfFailed(
                device.OpenPropertyStore(CoreAudioInterop.StgmRead, out propertyStore),
                "IMMDevice.OpenPropertyStore");

            return
                TryReadPropertyString(propertyStore, CoreAudioInterop.DeviceFriendlyNamePropertyKey) ??
                TryReadPropertyString(propertyStore, CoreAudioInterop.DeviceDescriptionPropertyKey) ??
                TryReadPropertyString(propertyStore, CoreAudioInterop.DeviceInterfaceFriendlyNamePropertyKey);
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(propertyStore);
        }
    }

    private static string? TryReadPropertyString(
        CoreAudioInterop.IPropertyStore propertyStore,
        CoreAudioInterop.PROPERTYKEY propertyKey)
    {
        CoreAudioInterop.PROPVARIANT propertyValue = default;
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
            return CoreAudioInterop.TryConvertPropVariantToString(ref propertyValue);
        }
        finally
        {
            if (gotValue)
            {
                CoreAudioInterop.ClearPropVariant(ref propertyValue);
            }
        }
    }
}
