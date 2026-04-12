using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace PhlanMic.Host.Core;

public sealed class HostConfigStore
{
    private const string EnvironmentPrefix = "PHLANMIC__";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly Func<string, string?> getEnvironmentValue;

    public HostConfigStore(Func<string, string?>? getEnvironmentValue = null)
    {
        this.getEnvironmentValue = getEnvironmentValue ?? Environment.GetEnvironmentVariable;
    }

    public HostRuntimeConfig LoadRaw(string configPath)
    {
        ValidateConfigPath(configPath);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Config file was not found: {configPath}", configPath);
        }

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<HostRuntimeConfig>(json, JsonOptions) ?? new HostRuntimeConfig();
        config.Validate();
        return config;
    }

    public HostRuntimeConfig LoadEffective(string configPath)
    {
        var config = ApplyEnvironmentOverrides(LoadRaw(configPath));
        config.Validate();
        return config;
    }

    public void SaveRaw(string configPath, HostRuntimeConfig config)
    {
        ValidateConfigPath(configPath);
        ArgumentNullException.ThrowIfNull(config);

        var normalizedConfig = NormalizeForPersistence(config);
        normalizedConfig.Validate();

        var directoryPath = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var json = JsonSerializer.Serialize(normalizedConfig, JsonOptions);
        File.WriteAllText(configPath, json + Environment.NewLine);
    }

    public IReadOnlyList<HostConfigEnvironmentOverride> GetEnvironmentOverrides()
    {
        var overrides = new List<HostConfigEnvironmentOverride>();

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name ||
                entry.Value is not string value ||
                !name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            overrides.Add(new HostConfigEnvironmentOverride(
                name,
                name[EnvironmentPrefix.Length..],
                value));
        }

        return overrides
            .OrderBy(overrideValue => overrideValue.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static HostRuntimeConfig NormalizeForPersistence(HostRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var outputMode = config.Output.Mode;
        var isWaveOut = OutputConfig.UsesWaveOutDevice(outputMode);
        var isVbCable = OutputConfig.UsesVbCableEndpoint(outputMode);

        return config with
        {
            Output = config.Output with
            {
                DeviceId = isWaveOut ? config.Output.DeviceId : -1,
                EndpointId = isVbCable ? config.Output.EndpointId : null
            }
        };
    }

    private HostRuntimeConfig ApplyEnvironmentOverrides(HostRuntimeConfig config)
    {
        var sessionName = GetEnvironmentValue("SESSIONNAME") ?? config.SessionName;
        var logLevel = GetEnvironmentValue("LOGLEVEL") ?? config.LogLevel;
        var logFormat = GetEnvironmentValue("LOGFORMAT") ?? config.LogFormat;
        var bindAddress = GetEnvironmentValue("RECEIVER__BINDADDRESS") ?? config.Receiver.BindAddress;
        var transportMode = GetEnvironmentValue("RECEIVER__TRANSPORTMODE") ?? config.Receiver.TransportMode;
        var port = ParseInt("RECEIVER__PORT") ?? config.Receiver.Port;
        var audioPort = ParseInt("RECEIVER__AUDIOPORT") ?? config.Receiver.AudioPort;
        var payloadCodec = GetEnvironmentValue("RECEIVER__PAYLOADCODEC") ?? config.Receiver.PayloadCodec;
        var keepAliveIntervalMs = ParseInt("RECEIVER__KEEPALIVEINTERVALMS") ?? config.Receiver.KeepAliveIntervalMs;
        var sessionTimeoutMs = ParseInt("RECEIVER__SESSIONTIMEOUTMS") ?? config.Receiver.SessionTimeoutMs;
        var maxBufferedFrames = ParseInt("BUFFER__MAXBUFFEREDFRAMES") ?? config.Buffer.MaxBufferedFrames;
        var dropOldestWhenFull = ParseBool("BUFFER__DROPOLDESTWHENFULL") ?? config.Buffer.DropOldestWhenFull;
        var startupPrebufferFrames = ParseInt("ROBUSTNESS__STARTUPPREBUFFERFRAMES") ?? config.Robustness.StartupPrebufferFrames;
        var targetBufferedFrames = ParseInt("ROBUSTNESS__TARGETBUFFEREDFRAMES") ?? config.Robustness.TargetBufferedFrames;
        var maxLateFrameToleranceFrames = ParseInt("ROBUSTNESS__MAXLATEFRAMETOLERANCEFRAMES") ?? config.Robustness.MaxLateFrameToleranceFrames;
        var missingFrameGraceMs = ParseInt("ROBUSTNESS__MISSINGFRAMEGRACEMS") ?? config.Robustness.MissingFrameGraceMs;
        var concealMissingFramesWithSilence = ParseBool("ROBUSTNESS__CONCEALMISSINGFRAMESWITHSILENCE") ?? config.Robustness.ConcealMissingFramesWithSilence;
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
            LogFormat = logFormat,
            Receiver = config.Receiver with
            {
                BindAddress = bindAddress,
                Port = port,
                TransportMode = transportMode,
                AudioPort = audioPort,
                PayloadCodec = payloadCodec,
                KeepAliveIntervalMs = keepAliveIntervalMs,
                SessionTimeoutMs = sessionTimeoutMs
            },
            Buffer = config.Buffer with
            {
                MaxBufferedFrames = maxBufferedFrames,
                DropOldestWhenFull = dropOldestWhenFull
            },
            Robustness = config.Robustness with
            {
                StartupPrebufferFrames = startupPrebufferFrames,
                TargetBufferedFrames = targetBufferedFrames,
                MaxLateFrameToleranceFrames = maxLateFrameToleranceFrames,
                MissingFrameGraceMs = missingFrameGraceMs,
                ConcealMissingFramesWithSilence = concealMissingFramesWithSilence
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

    private static void ValidateConfigPath(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Config path must be provided.", nameof(configPath));
        }
    }

    private string? GetEnvironmentValue(string suffix) =>
        getEnvironmentValue(EnvironmentPrefix + suffix);

    private int? ParseInt(string suffix)
    {
        var raw = GetEnvironmentValue(suffix);
        return raw is not null ? int.Parse(raw, CultureInfo.InvariantCulture) : null;
    }

    private bool? ParseBool(string suffix)
    {
        var raw = GetEnvironmentValue(suffix);
        return raw is not null ? bool.Parse(raw) : null;
    }
}
