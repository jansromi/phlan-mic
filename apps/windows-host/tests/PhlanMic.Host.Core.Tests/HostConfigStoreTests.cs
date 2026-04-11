using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class HostConfigStoreTests : IDisposable
{
    private readonly string tempDirectory;

    public HostConfigStoreTests()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "phlanmic-host-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void SaveRawAndLoadRawRoundTripPreservesConfiguration()
    {
        var configPath = CreateConfigPath();
        var store = new HostConfigStore();
        var config = new HostRuntimeConfig
        {
            SessionName = "round-trip",
            LogLevel = "Debug",
            LogFormat = "Json",
            Receiver = new ReceiverConfig
            {
                BindAddress = "127.0.0.1",
                TransportMode = ReceiverConfig.UdpRawPcmTransportMode,
                Port = 42_110,
                AudioPort = 42_111,
                PayloadCodec = "RawPcm16",
                KeepAliveIntervalMs = 1_500,
                SessionTimeoutMs = 5_500
            },
            Buffer = new StreamBufferConfig
            {
                MaxBufferedFrames = 20,
                DropOldestWhenFull = false
            },
            Robustness = new StreamRobustnessConfig
            {
                StartupPrebufferFrames = 8,
                TargetBufferedFrames = 6,
                MaxLateFrameToleranceFrames = 3,
                MissingFrameGraceMs = 50,
                ConcealMissingFramesWithSilence = false
            },
            AudioFormat = new AudioFormat
            {
                SampleRate = 48_000,
                Channels = 1,
                BitsPerSample = 16,
                FrameDurationMs = 20
            },
            TestMode = new GeneratedSignalTestModeConfig
            {
                Enabled = true,
                SignalFrequencyHz = 880
            },
            Output = new OutputConfig
            {
                Mode = OutputConfig.VbCableMode,
                EndpointId = "{vb-cable-endpoint}",
                TargetLatencyMs = 90,
                LogAvailableDevices = false,
                LogEndpointInventory = false
            }
        };

        store.SaveRaw(configPath, config);
        var roundTripped = store.LoadRaw(configPath);

        roundTripped.Validate();
        Assert.Equal(config, roundTripped);
    }

    [Fact]
    public void LoadEffectiveAppliesEnvironmentOverridesAfterSave()
    {
        var configPath = CreateConfigPath();
        var store = new HostConfigStore();
        store.SaveRaw(configPath, new HostRuntimeConfig());

        var overrideStore = new HostConfigStore(name =>
            name switch
            {
                "PHLANMIC__OUTPUT__MODE" => OutputConfig.DebugDrainMode,
                "PHLANMIC__RECEIVER__TRANSPORTMODE" => ReceiverConfig.UdpRawPcmTransportMode,
                "PHLANMIC__RECEIVER__AUDIOPORT" => "42101",
                _ => null
            });

        var effective = overrideStore.LoadEffective(configPath);

        Assert.Equal(OutputConfig.DebugDrainMode, effective.Output.Mode);
        Assert.Equal(ReceiverConfig.UdpRawPcmTransportMode, effective.Receiver.TransportMode);
        Assert.Equal(42_101, effective.Receiver.AudioPort);
    }

    [Fact]
    public void SaveRawNormalizesOutputFieldsForNonWaveOutModes()
    {
        var configPath = CreateConfigPath();
        var store = new HostConfigStore();
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = OutputConfig.VbCableMode,
                DeviceId = 7,
                EndpointId = "{vb-cable-endpoint}"
            }
        };

        store.SaveRaw(configPath, config);
        var saved = store.LoadRaw(configPath);

        Assert.Equal(-1, saved.Output.DeviceId);
        Assert.Equal("{vb-cable-endpoint}", saved.Output.EndpointId);
    }

    [Fact]
    public void SaveRawNormalizesOutputFieldsForNonVbCableModes()
    {
        var configPath = CreateConfigPath();
        var store = new HostConfigStore();
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = OutputConfig.WaveOutMode,
                DeviceId = 3,
                EndpointId = "{should-be-cleared}"
            }
        };

        store.SaveRaw(configPath, config);
        var saved = store.LoadRaw(configPath);

        Assert.Equal(3, saved.Output.DeviceId);
        Assert.Null(saved.Output.EndpointId);
    }

    private string CreateConfigPath() => Path.Combine(tempDirectory, "appsettings.json");
}
