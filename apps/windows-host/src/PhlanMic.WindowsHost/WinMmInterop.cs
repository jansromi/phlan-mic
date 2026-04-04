using System.Runtime.InteropServices;
using System.Text;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal static class WinMmInterop
{
    public const uint CallbackNull = 0x00000000;
    public const uint WaveFormatQuery = 0x00000001;
    public const uint WaveMapper = 0xFFFFFFFF;
    public const uint WaveHdrDone = 0x00000001;
    public const uint MmSysErrNoError = 0;
    public const int MaxProductNameLength = 32;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutGetDevCaps(
        UIntPtr deviceId,
        out WAVEOUTCAPS caps,
        uint capsSize);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutOpen(
        out IntPtr waveOutHandle,
        uint deviceId,
        ref WAVEFORMATEX format,
        IntPtr callback,
        IntPtr instance,
        uint flags);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutPrepareHeader(
        IntPtr waveOutHandle,
        IntPtr waveHeader,
        uint waveHeaderSize);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutUnprepareHeader(
        IntPtr waveOutHandle,
        IntPtr waveHeader,
        uint waveHeaderSize);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutWrite(
        IntPtr waveOutHandle,
        IntPtr waveHeader,
        uint waveHeaderSize);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutReset(IntPtr waveOutHandle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutClose(IntPtr waveOutHandle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveOutGetErrorText(
        uint errorCode,
        StringBuilder errorText,
        uint errorTextCharCount);

    public static WAVEFORMATEX CreateWaveFormat(AudioFormat format) =>
        new()
        {
            wFormatTag = 1,
            nChannels = checked((ushort)format.Channels),
            nSamplesPerSec = checked((uint)format.SampleRate),
            wBitsPerSample = checked((ushort)format.BitsPerSample),
            nBlockAlign = checked((ushort)(format.Channels * format.BytesPerSample)),
            nAvgBytesPerSec = checked((uint)(format.SampleRate * format.Channels * format.BytesPerSample)),
            cbSize = 0
        };

    public static void ThrowIfError(uint result, string operation, int? deviceId = null)
    {
        if (result == MmSysErrNoError)
        {
            return;
        }

        var detail = GetErrorText(result);
        var deviceSuffix = deviceId is null ? string.Empty : $" for device {deviceId.Value}";
        throw new InvalidOperationException($"{operation}{deviceSuffix} failed with winmm error {result}: {detail}");
    }

    public static string GetErrorText(uint result)
    {
        var builder = new StringBuilder(256);
        var status = waveOutGetErrorText(result, builder, checked((uint)builder.Capacity));
        return status == MmSysErrNoError ? builder.ToString() : "Unknown winmm error.";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxProductNameLength)]
        public string szPname;

        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }
}
