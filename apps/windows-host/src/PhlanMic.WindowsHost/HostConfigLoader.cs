using System.Globalization;
using System.Text.Json;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class HostConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public HostRuntimeConfig Load(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Config path must be provided.", nameof(configPath));
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file was not found: {configPath}", configPath);
        }

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<HostRuntimeConfig>(json, JsonOptions) ?? new HostRuntimeConfig();
        config = ApplyEnvironmentOverrides(config);
        config.Validate();
        return config;
    }

    private static HostRuntimeConfig ApplyEnvironmentOverrides(HostRuntimeConfig config)
    {
        var sessionName = GetEnvironmentValue("SESSIONNAME") ?? config.SessionName;
        var logLevel = GetEnvironmentValue("LOGLEVEL") ?? config.LogLevel;
        var bindAddress = GetEnvironmentValue("RECEIVER__BINDADDRESS") ?? config.Receiver.BindAddress;
        var transportMode = GetEnvironmentValue("RECEIVER__TRANSPORTMODE") ?? config.Receiver.TransportMode;
        var port = ParseInt("RECEIVER__PORT") ?? config.Receiver.Port;
        var maxBufferedFrames = ParseInt("BUFFER__MAXBUFFEREDFRAMES") ?? config.Buffer.MaxBufferedFrames;
        var dropOldestWhenFull = ParseBool("BUFFER__DROPOLDESTWHENFULL") ?? config.Buffer.DropOldestWhenFull;
        var sampleRate = ParseInt("AUDIOFORMAT__SAMPLERATE") ?? config.AudioFormat.SampleRate;
        var channels = ParseInt("AUDIOFORMAT__CHANNELS") ?? config.AudioFormat.Channels;
        var bitsPerSample = ParseInt("AUDIOFORMAT__BITSPERSAMPLE") ?? config.AudioFormat.BitsPerSample;
        var frameDurationMs = ParseInt("AUDIOFORMAT__FRAMEDURATIONMS") ?? config.AudioFormat.FrameDurationMs;
        var testModeEnabled = ParseBool("TESTMODE__ENABLED") ?? config.TestMode.Enabled;
        var signalFrequencyHz = ParseInt("TESTMODE__SIGNALFREQUENCYHZ") ?? config.TestMode.SignalFrequencyHz;
        var outputMode = GetEnvironmentValue("OUTPUT__MODE") ?? config.Output.Mode;
        var outputDeviceId = ParseInt("OUTPUT__DEVICEID") ?? config.Output.DeviceId;
        var outputEndpointId = GetEnvironmentValue("OUTPUT__ENDPOINTID") ?? config.Output.EndpointId;
        var outputTargetLatencyMs = ParseInt("OUTPUT__TARGETLATENCYMS") ?? config.Output.TargetLatencyMs;
        var logAvailableDevices = ParseBool("OUTPUT__LOGAVAILABLEDEVICES") ?? config.Output.LogAvailableDevices;
        var logEndpointInventory = ParseBool("OUTPUT__LOGENDPOINTINVENTORY") ?? config.Output.LogEndpointInventory;

        return config with
        {
            SessionName = sessionName,
            LogLevel = logLevel,
            Receiver = config.Receiver with
            {
                BindAddress = bindAddress,
                Port = port,
                TransportMode = transportMode
            },
            Buffer = config.Buffer with
            {
                MaxBufferedFrames = maxBufferedFrames,
                DropOldestWhenFull = dropOldestWhenFull
            },
            AudioFormat = config.AudioFormat with
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
                FrameDurationMs = frameDurationMs
            },
            TestMode = config.TestMode with
            {
                Enabled = testModeEnabled,
                SignalFrequencyHz = signalFrequencyHz
            },
            Output = config.Output with
            {
                Mode = outputMode,
                DeviceId = outputDeviceId,
                EndpointId = outputEndpointId,
                TargetLatencyMs = outputTargetLatencyMs,
                LogAvailableDevices = logAvailableDevices,
                LogEndpointInventory = logEndpointInventory
            }
        };
    }

    private static string? GetEnvironmentValue(string suffix) =>
        Environment.GetEnvironmentVariable($"PHLANMIC__{suffix}");

    private static int? ParseInt(string suffix)
    {
        var raw = GetEnvironmentValue(suffix);
        return raw is not null ? int.Parse(raw, CultureInfo.InvariantCulture) : null;
    }

    private static bool? ParseBool(string suffix)
    {
        var raw = GetEnvironmentValue(suffix);
        return raw is not null ? bool.Parse(raw) : null;
    }
}
