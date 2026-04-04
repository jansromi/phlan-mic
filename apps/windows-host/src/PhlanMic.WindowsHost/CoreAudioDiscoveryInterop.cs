using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhlanMic.WindowsHost;

internal static partial class CoreAudioDiscoveryInterop
{
    public const uint DeviceStateMaskAll = 0x0000000F;
    public const uint StgmRead = 0;
    public const uint ClsCtxAll = 23;

    public static Guid MmDeviceEnumeratorClsid { get; } = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static Guid MmDeviceEnumeratorIid { get; } = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    public static PROPERTYKEY DeviceFriendlyNamePropertyKey { get; } =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static PROPERTYKEY DeviceDescriptionPropertyKey { get; } =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

    public static PROPERTYKEY DeviceInterfaceFriendlyNamePropertyKey { get; } =
        new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);

    public static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        ThrowIfFailed(
            CoCreateInstance(MmDeviceEnumeratorClsid, IntPtr.Zero, ClsCtxAll, MmDeviceEnumeratorIid, out IMMDeviceEnumerator enumerator),
            "CoCreateInstance(MMDeviceEnumerator)");
        return enumerator;
    }

    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed with HRESULT 0x{unchecked((uint)hresult):X8}.");
    }

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

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(IntPtr pointer);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PROPVARIANT value);

    [LibraryImport("propsys.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PropVariantToStringAlloc(ref PROPVARIANT value, out IntPtr stringPointer);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid,
        IntPtr outer,
        uint classContext,
        in Guid interfaceId,
        out IMMDeviceEnumerator instance);

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

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice(string endpointId, out IMMDevice device);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal partial interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IMMDevice
    {
        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out string endpointId);

        [PreserveSig]
        int GetState(out uint state);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
}
