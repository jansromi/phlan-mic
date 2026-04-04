using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace PhlanMic.WindowsHost;

internal static class CoreAudioEndpointEnumerator
{
    private const DEVICE_STATE AllDeviceStates =
        DEVICE_STATE.DEVICE_STATE_ACTIVE |
        DEVICE_STATE.DEVICE_STATE_DISABLED |
        DEVICE_STATE.DEVICE_STATE_NOTPRESENT |
        DEVICE_STATE.DEVICE_STATE_UNPLUGGED;

    public static IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<AudioEndpointInfo>();
        }

        var deviceEnumerator =
            new MMDeviceEnumerator() as IMMDeviceEnumerator
            ?? throw new InvalidOperationException("Failed to create MMDeviceEnumerator.");

        try
        {
            var endpoints = new List<AudioEndpointInfo>();
            var defaultEndpoints = GetDefaultEndpointIds(deviceEnumerator);

            EnumerateFlow(
                deviceEnumerator,
                EDataFlow.eRender,
                AudioEndpointFlow.Render,
                defaultEndpoints,
                endpoints);
            EnumerateFlow(
                deviceEnumerator,
                EDataFlow.eCapture,
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

    private static Dictionary<(AudioEndpointFlow Flow, ERole Role), string> GetDefaultEndpointIds(
        IMMDeviceEnumerator deviceEnumerator)
    {
        var defaults = new Dictionary<(AudioEndpointFlow, ERole), string>();

        foreach (var flow in new[]
                 {
                     (EDataFlow.eRender, AudioEndpointFlow.Render),
                     (EDataFlow.eCapture, AudioEndpointFlow.Capture)
                 })
        {
            foreach (var role in new[]
                     {
                         ERole.eConsole,
                         ERole.eMultimedia,
                         ERole.eCommunications
                     })
            {
                IMMDevice? device = null;

                try
                {
                    deviceEnumerator.GetDefaultAudioEndpoint(flow.Item1, role, out device);
                    var endpointId = ReadEndpointId(device);

                    if (!string.IsNullOrWhiteSpace(endpointId))
                    {
                        defaults[(flow.Item2, role)] = endpointId;
                    }
                }
                catch
                {
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
        IMMDeviceEnumerator deviceEnumerator,
        EDataFlow dataFlow,
        AudioEndpointFlow flow,
        IReadOnlyDictionary<(AudioEndpointFlow Flow, ERole Role), string> defaultEndpoints,
        ICollection<AudioEndpointInfo> endpoints)
    {
        IMMDeviceCollection? devices = null;

        try
        {
            deviceEnumerator.EnumAudioEndpoints(dataFlow, AllDeviceStates, out devices);
            devices.GetCount(out var deviceCount);

            for (uint index = 0; index < deviceCount; index++)
            {
                IMMDevice? device = null;

                try
                {
                    devices.Item(index, out device);

                    var endpointId = ReadEndpointId(device);
                    device.GetState(out var endpointState);
                    var friendlyName = ReadFriendlyName(device) ?? endpointId;

                    endpoints.Add(
                        new AudioEndpointInfo(
                            endpointId,
                            friendlyName,
                            friendlyName,
                            flow,
                            (AudioEndpointState)(uint)endpointState,
                            IsDefault(defaultEndpoints, flow, ERole.eConsole, endpointId),
                            IsDefault(defaultEndpoints, flow, ERole.eMultimedia, endpointId),
                            IsDefault(defaultEndpoints, flow, ERole.eCommunications, endpointId)));
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
        IReadOnlyDictionary<(AudioEndpointFlow Flow, ERole Role), string> defaultEndpoints,
        AudioEndpointFlow flow,
        ERole role,
        string endpointId) =>
        defaultEndpoints.TryGetValue((flow, role), out var defaultEndpointId) &&
        string.Equals(defaultEndpointId, endpointId, StringComparison.Ordinal);

    private static unsafe string ReadEndpointId(IMMDevice device)
    {
        device.GetId(out var endpointId);
        return endpointId.Value is null ? string.Empty : endpointId.ToString();
    }

    private static unsafe string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? propertyStore = null;
        PROPVARIANT propertyValue = default;

        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out propertyStore);

            var key = PInvoke.PKEY_Device_FriendlyName;
            propertyStore.GetValue(&key, out propertyValue);

            if (propertyValue.Anonymous.Anonymous.vt != VARENUM.VT_LPWSTR)
            {
                return null;
            }

            var pointer = propertyValue.Anonymous.Anonymous.Anonymous.pwszVal;
            return pointer.Value is null ? null : pointer.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                PInvoke.PropVariantClear(ref propertyValue);
            }
            catch
            {
            }

            CoreAudioInterop.ReleaseComObject(propertyStore);
        }
    }
}
