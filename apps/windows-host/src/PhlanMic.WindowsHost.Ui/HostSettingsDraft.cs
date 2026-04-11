using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost.Ui;

internal sealed class HostSettingsDraft
{
    public string SessionName { get; set; } = "phlan-mic";
    public string LogLevel { get; set; } = "Information";
    public string LogFormat { get; set; } = "Text";
    public string ReceiverBindAddress { get; set; } = "0.0.0.0";
    public string ReceiverTransportMode { get; set; } = ReceiverConfig.DebugTcpRawPcmTransportMode;
    public int ReceiverPort { get; set; } = 42_100;
    public bool UseAutomaticAudioPort { get; set; }
    public int ReceiverAudioPort { get; set; } = 42_101;
    public string ReceiverPayloadCodec { get; set; } = "RawPcm16";
    public int ReceiverKeepAliveIntervalMs { get; set; } = 1_000;
    public int ReceiverSessionTimeoutMs { get; set; } = 5_000;
    public int BufferMaxBufferedFrames { get; set; } = 16;
    public bool BufferDropOldestWhenFull { get; set; } = true;
    public int RobustnessStartupPrebufferFrames { get; set; } = 6;
    public int RobustnessTargetBufferedFrames { get; set; } = 5;
    public int RobustnessMaxLateFrameToleranceFrames { get; set; } = 4;
    public int RobustnessMissingFrameGraceMs { get; set; } = 40;
    public bool RobustnessConcealMissingFramesWithSilence { get; set; } = true;
    public int AudioSampleRate { get; set; } = 48_000;
    public int AudioChannels { get; set; } = 1;
    public int AudioBitsPerSample { get; set; } = 16;
    public int AudioFrameDurationMs { get; set; } = 20;
    public bool TestModeEnabled { get; set; }
    public int TestModeSignalFrequencyHz { get; set; } = 1_000;
    public string OutputMode { get; set; } = OutputConfig.VbCableMode;
    public int OutputDeviceId { get; set; } = -1;
    public string? OutputEndpointId { get; set; }
    public int OutputTargetLatencyMs { get; set; } = 120;
    public bool OutputLogAvailableDevices { get; set; } = true;
    public bool OutputLogEndpointInventory { get; set; } = true;

    public static HostSettingsDraft FromConfig(HostRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new HostSettingsDraft
        {
            SessionName = config.SessionName,
            LogLevel = config.LogLevel,
            LogFormat = config.LogFormat,
            ReceiverBindAddress = config.Receiver.BindAddress,
            ReceiverTransportMode = config.Receiver.TransportMode,
            ReceiverPort = config.Receiver.Port,
            UseAutomaticAudioPort = !config.Receiver.AudioPort.HasValue,
            ReceiverAudioPort = config.Receiver.AudioPort ?? config.Receiver.GetResolvedAudioPort(),
            ReceiverPayloadCodec = config.Receiver.PayloadCodec,
            ReceiverKeepAliveIntervalMs = config.Receiver.KeepAliveIntervalMs,
            ReceiverSessionTimeoutMs = config.Receiver.SessionTimeoutMs,
            BufferMaxBufferedFrames = config.Buffer.MaxBufferedFrames,
            BufferDropOldestWhenFull = config.Buffer.DropOldestWhenFull,
            RobustnessStartupPrebufferFrames = config.Robustness.StartupPrebufferFrames,
            RobustnessTargetBufferedFrames = config.Robustness.TargetBufferedFrames,
            RobustnessMaxLateFrameToleranceFrames = config.Robustness.MaxLateFrameToleranceFrames,
            RobustnessMissingFrameGraceMs = config.Robustness.MissingFrameGraceMs,
            RobustnessConcealMissingFramesWithSilence = config.Robustness.ConcealMissingFramesWithSilence,
            AudioSampleRate = config.AudioFormat.SampleRate,
            AudioChannels = config.AudioFormat.Channels,
            AudioBitsPerSample = config.AudioFormat.BitsPerSample,
            AudioFrameDurationMs = config.AudioFormat.FrameDurationMs,
            TestModeEnabled = config.TestMode.Enabled,
            TestModeSignalFrequencyHz = config.TestMode.SignalFrequencyHz,
            OutputMode = config.Output.Mode,
            OutputDeviceId = config.Output.DeviceId,
            OutputEndpointId = config.Output.EndpointId,
            OutputTargetLatencyMs = config.Output.TargetLatencyMs,
            OutputLogAvailableDevices = config.Output.LogAvailableDevices,
            OutputLogEndpointInventory = config.Output.LogEndpointInventory
        };
    }

    public HostRuntimeConfig ToConfig()
    {
        var config = new HostRuntimeConfig
        {
            SessionName = SessionName.Trim(),
            LogLevel = LogLevel,
            LogFormat = LogFormat,
            Receiver = new ReceiverConfig
            {
                BindAddress = ReceiverBindAddress.Trim(),
                TransportMode = ReceiverTransportMode,
                Port = ReceiverPort,
                AudioPort = UseAutomaticAudioPort ? null : ReceiverAudioPort,
                PayloadCodec = ReceiverPayloadCodec,
                KeepAliveIntervalMs = ReceiverKeepAliveIntervalMs,
                SessionTimeoutMs = ReceiverSessionTimeoutMs
            },
            Buffer = new StreamBufferConfig
            {
                MaxBufferedFrames = BufferMaxBufferedFrames,
                DropOldestWhenFull = BufferDropOldestWhenFull
            },
            Robustness = new StreamRobustnessConfig
            {
                StartupPrebufferFrames = RobustnessStartupPrebufferFrames,
                TargetBufferedFrames = RobustnessTargetBufferedFrames,
                MaxLateFrameToleranceFrames = RobustnessMaxLateFrameToleranceFrames,
                MissingFrameGraceMs = RobustnessMissingFrameGraceMs,
                ConcealMissingFramesWithSilence = RobustnessConcealMissingFramesWithSilence
            },
            AudioFormat = new AudioFormat
            {
                SampleRate = AudioSampleRate,
                Channels = AudioChannels,
                BitsPerSample = AudioBitsPerSample,
                FrameDurationMs = AudioFrameDurationMs
            },
            TestMode = new GeneratedSignalTestModeConfig
            {
                Enabled = TestModeEnabled,
                SignalFrequencyHz = TestModeSignalFrequencyHz
            },
            Output = new OutputConfig
            {
                Mode = OutputMode,
                DeviceId = OutputDeviceId,
                EndpointId = string.IsNullOrWhiteSpace(OutputEndpointId) ? null : OutputEndpointId,
                TargetLatencyMs = OutputTargetLatencyMs,
                LogAvailableDevices = OutputLogAvailableDevices,
                LogEndpointInventory = OutputLogEndpointInventory
            }
        };

        return HostConfigStore.NormalizeForPersistence(config);
    }
}
