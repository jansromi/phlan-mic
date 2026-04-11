using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class HostRuntimeConfigTests
{
    [Fact]
    public void ValidateAcceptsDefaultConfiguration()
    {
        var config = new HostRuntimeConfig();

        Assert.Equal("Text", config.LogFormat);
        Assert.Equal(OutputConfig.VbCableMode, config.Output.Mode);
        config.Validate();
    }

    [Fact]
    public void ValidateAcceptsJsonLogFormat()
    {
        var config = new HostRuntimeConfig
        {
            LogFormat = "Json"
        };

        config.Validate();
    }

    [Fact]
    public void ValidateRejectsUnsupportedLogFormat()
    {
        var config = new HostRuntimeConfig
        {
            LogFormat = "Xml"
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("log format", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsOutOfRangePort()
    {
        var config = new HostRuntimeConfig
        {
            Receiver = new ReceiverConfig
            {
                Port = 70_000
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("port", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsUnsupportedTransportMode()
    {
        var config = new HostRuntimeConfig
        {
            Receiver = new ReceiverConfig
            {
                TransportMode = "Udp"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("transport mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAcceptsUdpRawPcmTransportMode()
    {
        var config = new HostRuntimeConfig
        {
            Receiver = new ReceiverConfig
            {
                TransportMode = ReceiverConfig.UdpRawPcmTransportMode,
                Port = 42_100,
                AudioPort = 42_101,
                PayloadCodec = "RawPcm16",
                KeepAliveIntervalMs = 1000,
                SessionTimeoutMs = 5000
            }
        };

        config.Validate();
    }

    [Fact]
    public void ValidateRejectsUdpAudioPortEqualToControlPort()
    {
        var config = new HostRuntimeConfig
        {
            Receiver = new ReceiverConfig
            {
                TransportMode = ReceiverConfig.UdpRawPcmTransportMode,
                Port = 42_100,
                AudioPort = 42_100
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("audio port", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsSessionTimeoutLessThanKeepAliveInterval()
    {
        var config = new HostRuntimeConfig
        {
            Receiver = new ReceiverConfig
            {
                TransportMode = ReceiverConfig.UdpRawPcmTransportMode,
                KeepAliveIntervalMs = 5000,
                SessionTimeoutMs = 5000
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsUnsupportedOutputMode()
    {
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = "Wasapi"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("output mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAcceptsVbCableModeWithEndpointOverride()
    {
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = OutputConfig.VbCableMode,
                EndpointId = "{vb-cable-render-endpoint}"
            }
        };

        config.Validate();
    }

    [Fact]
    public void ValidateRejectsWaveOutDeviceIdOutsideWaveOutMode()
    {
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = OutputConfig.VbCableMode,
                DeviceId = 1
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("device id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsEndpointOverrideOutsideVbCableMode()
    {
        var config = new HostRuntimeConfig
        {
            Output = new OutputConfig
            {
                Mode = OutputConfig.WaveOutMode,
                EndpointId = "{not-valid-for-waveout}"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("endpoint id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsStartupPrebufferLargerThanBufferCapacity()
    {
        var config = new HostRuntimeConfig
        {
            Buffer = new StreamBufferConfig
            {
                MaxBufferedFrames = 2
            },
            Robustness = new StreamRobustnessConfig
            {
                StartupPrebufferFrames = 3
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("prebuffer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsTargetBufferLargerThanBufferCapacity()
    {
        var config = new HostRuntimeConfig
        {
            Buffer = new StreamBufferConfig
            {
                MaxBufferedFrames = 2
            },
            Robustness = new StreamRobustnessConfig
            {
                StartupPrebufferFrames = 2,
                TargetBufferedFrames = 3
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("target buffered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsNegativeMissingFrameGrace()
    {
        var config = new HostRuntimeConfig
        {
            Robustness = new StreamRobustnessConfig
            {
                MissingFrameGraceMs = -1
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("missing frame grace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
