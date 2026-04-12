using System.Runtime.InteropServices;

namespace PhlanMic.WindowsHost;

internal static class CoreAudioInterop
{
    public const uint DeviceStateMaskAll = 0x0000000F;
    public const uint StgmRead = 0;
    public const uint ClsCtxAll = 23;
    public const int AudclntSharemodeShared = 0;
    public const uint AudclntBufferflagsSilent = 0x00000002;
    public const int SOk = 0;
    public const int SFalse = 1;

    public static Guid MmDeviceEnumeratorClsid { get; } = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static Guid IAudioClientIid { get; } = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public static Guid IAudioRenderClientIid { get; } = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    public static Guid IAudioCaptureClientIid { get; } = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public static PROPERTYKEY DeviceFriendlyNamePropertyKey { get; } =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static PROPERTYKEY DeviceDescriptionPropertyKey { get; } =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

    public static PROPERTYKEY DeviceInterfaceFriendlyNamePropertyKey { get; } =
        new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);

    public static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var enumeratorType = Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid, throwOnError: true);
        var instance = Activator.CreateInstance(enumeratorType!) ?? throw new InvalidOperationException("Failed to create MMDeviceEnumerator.");
        return (IMMDeviceEnumerator)instance;
    }

    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed with HRESULT 0x{unchecked((uint)hresult):X8}.");
    }

    public static string? PropVariantToString(PROPVARIANT value) =>
        value.vt == 31 && value.pointerValue != IntPtr.Zero
            ? Marshal.PtrToStringUni(value.pointerValue)
            : null;

    public static string? TryConvertPropVariantToString(ref PROPVARIANT value)
    {
        var hresult = PropVariantToStringAlloc(ref value, out var stringPointer);
        if (hresult < 0 || stringPointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(stringPointer);
        }
        finally
        {
            CoTaskMemFree(stringPointer);
        }
    }

    public static void ClearPropVariant(ref PROPVARIANT value)
    {
        if (value.vt != 0)
        {
            PropVariantClear(ref value);
        }
    }

    public static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pointer);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PropVariantToStringAlloc(ref PROPVARIANT value, out IntPtr stringPointer);

    internal enum EDataFlow
    {
        eRender = 0,
        eCapture = 1
    }

    internal enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PROPERTYKEY
    {
        public PROPERTYKEY(Guid formatId, uint propertyId)
        {
            fmtid = formatId;
            pid = propertyId;
        }

        public readonly Guid fmtid;

        public readonly uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string endpointId, out IMMDevice device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string endpointId);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            ref WinMmInterop.WAVEFORMATEX format,
            IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrameCount);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrameCount);

        [PreserveSig]
        int IsFormatSupported(
            int shareMode,
            ref WinMmInterop.WAVEFORMATEX format,
            out IntPtr closestMatchFormat);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormatPointer);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint bufferFrameCount, out IntPtr dataPointer);

        [PreserveSig]
        int ReleaseBuffer(uint writtenFrameCount, uint flags);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr dataPointer,
            out uint frameCount,
            out uint flags,
            out ulong devicePosition,
            out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frameCount);

        [PreserveSig]
        int GetNextPacketSize(out uint nextPacketFrameCount);
    }
}
