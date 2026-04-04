using System.Runtime.InteropServices;

namespace PhlanMic.WindowsHost;

internal static class WaveOutDeviceEnumerator
{
    public static IReadOnlyList<WaveOutDeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<WaveOutDeviceInfo>();
        }

        var deviceCount = checked((int)WinMmInterop.waveOutGetNumDevs());
        var devices = new List<WaveOutDeviceInfo>(deviceCount);

        for (var deviceId = 0; deviceId < deviceCount; deviceId++)
        {
            WinMmInterop.ThrowIfError(
                WinMmInterop.waveOutGetDevCaps(
                    new UIntPtr((uint)deviceId),
                    out var caps,
                    checked((uint)Marshal.SizeOf<WinMmInterop.WAVEOUTCAPS>())),
                "waveOutGetDevCaps",
                deviceId);

            devices.Add(new WaveOutDeviceInfo(
                deviceId,
                caps.szPname,
                caps.wChannels,
                FormatDriverVersion(caps.vDriverVersion),
                $"0x{caps.dwFormats:X8}"));
        }

        return devices;
    }

    private static string FormatDriverVersion(uint version) =>
        $"{(version >> 8) & 0xFF}.{version & 0xFF}";
}
