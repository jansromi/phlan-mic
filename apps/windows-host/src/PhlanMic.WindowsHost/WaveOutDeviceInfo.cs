namespace PhlanMic.WindowsHost;

internal sealed record WaveOutDeviceInfo(
    int DeviceId,
    string Name,
    int Channels,
    string DriverVersion,
    string SupportedFormatsMask);
