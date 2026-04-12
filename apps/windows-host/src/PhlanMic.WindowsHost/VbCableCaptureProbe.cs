using System.Runtime.InteropServices;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class VbCableCaptureProbe : IDisposable
{
    private readonly StructuredConsoleLogger logger;
    private readonly AudioEndpointInfo captureEndpoint;
    private readonly object gate = new();
    private readonly Pcm16AudioLevelMeter signalMeter = new();
    private CoreAudioInterop.IAudioClient? audioClient;
    private CoreAudioInterop.IAudioCaptureClient? captureClient;
    private AudioFormat captureFormat = AudioFormat.CreateMvpDefault();
    private bool started;
    private bool disposed;
    private long observedBytes;
    private DateTimeOffset? lastObservedAtUtc;
    private string state = "Unavailable";
    private string? faultDetail;

    public VbCableCaptureProbe(
        StructuredConsoleLogger logger,
        AudioEndpointInfo captureEndpoint,
        AudioFormat preferredFormat,
        int targetLatencyMs)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(captureEndpoint);
        ArgumentNullException.ThrowIfNull(preferredFormat);

        this.logger = logger;
        this.captureEndpoint = captureEndpoint;

        try
        {
            InitializeAudioClient(preferredFormat, targetLatencyMs);
            state = "Ready";

            logger.Info("vb_cable_capture_probe_ready", "Prepared a shared capture probe for the VB-CABLE paired capture endpoint.", new Dictionary<string, object?>
            {
                ["captureEndpointId"] = captureEndpoint.Id,
                ["captureEndpointName"] = captureEndpoint.FriendlyName,
                ["captureProbeFormat"] = DescribeFormat(captureFormat)
            });
        }
        catch (Exception exception)
        {
            faultDetail = exception.Message;
            state = "Faulted";
            Dispose();

            logger.Warning("vb_cable_capture_probe_faulted", "Failed to initialize the VB-CABLE paired capture probe.", new Dictionary<string, object?>
            {
                ["captureEndpointId"] = captureEndpoint.Id,
                ["captureEndpointName"] = captureEndpoint.FriendlyName,
                ["detail"] = faultDetail
            });
        }
    }

    public string State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public string? FaultDetail
    {
        get
        {
            lock (gate)
            {
                return faultDetail;
            }
        }
    }

    public string? CaptureFormat
    {
        get
        {
            lock (gate)
            {
                return state is "Ready" or "Active" ? DescribeFormat(captureFormat) : null;
            }
        }
    }

    public void StartIfNeeded()
    {
        try
        {
            lock (gate)
            {
                if (disposed || started || state != "Ready" || audioClient is null)
                {
                    return;
                }

                CoreAudioInterop.ThrowIfFailed(audioClient.Start(), "IAudioClient.Start");
                started = true;
                state = "Active";
            }

            logger.Info("vb_cable_capture_probe_started", "Started shared capture probing on the VB-CABLE paired capture endpoint.", new Dictionary<string, object?>
            {
                ["captureEndpointId"] = captureEndpoint.Id,
                ["captureEndpointName"] = captureEndpoint.FriendlyName,
                ["captureProbeFormat"] = CaptureFormat
            });
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (disposed || state == "Faulted")
                {
                    return;
                }

                faultDetail = exception.Message;
                state = "Faulted";
            }

            logger.Warning("vb_cable_capture_probe_faulted", "Failed to start shared capture probing on the VB-CABLE paired capture endpoint.", new Dictionary<string, object?>
            {
                ["captureEndpointId"] = captureEndpoint.Id,
                ["captureEndpointName"] = captureEndpoint.FriendlyName,
                ["detail"] = exception.Message
            });

            Dispose();
        }
    }

    public void Poll()
    {
        lock (gate)
        {
            if (disposed || !started || captureClient is null)
            {
                return;
            }
        }

        try
        {
            while (true)
            {
                CoreAudioInterop.ThrowIfFailed(captureClient!.GetNextPacketSize(out var nextPacketFrameCount), "IAudioCaptureClient.GetNextPacketSize");
                if (nextPacketFrameCount == 0)
                {
                    return;
                }

                CoreAudioInterop.ThrowIfFailed(
                    captureClient.GetBuffer(out var dataPointer, out var frameCount, out var flags, out _, out _),
                    "IAudioCaptureClient.GetBuffer");

                try
                {
                    ObservePacket(dataPointer, frameCount, flags);
                }
                finally
                {
                    CoreAudioInterop.ThrowIfFailed(captureClient.ReleaseBuffer(frameCount), "IAudioCaptureClient.ReleaseBuffer");
                }
            }
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (disposed || state == "Faulted")
                {
                    return;
                }

                faultDetail = exception.Message;
                state = "Faulted";
            }

            logger.Warning("vb_cable_capture_probe_faulted", "VB-CABLE paired capture probing stopped after a capture failure.", new Dictionary<string, object?>
            {
                ["captureEndpointId"] = captureEndpoint.Id,
                ["captureEndpointName"] = captureEndpoint.FriendlyName,
                ["detail"] = exception.Message
            });

            Dispose();
        }
    }

    public CaptureProbeSnapshot GetSnapshot(DateTimeOffset observedAtUtc)
    {
        lock (gate)
        {
            return new CaptureProbeSnapshot(
                state,
                state is "Ready" or "Active" ? DescribeFormat(captureFormat) : null,
                observedBytes,
                lastObservedAtUtc,
                signalMeter.GetSnapshot(observedAtUtc),
                faultDetail);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (audioClient is not null)
        {
            try
            {
                audioClient.Stop();
            }
            catch
            {
            }

            try
            {
                audioClient.Reset();
            }
            catch
            {
            }
        }

        CoreAudioInterop.ReleaseComObject(captureClient);
        CoreAudioInterop.ReleaseComObject(audioClient);
        captureClient = null;
        audioClient = null;
    }

    private void InitializeAudioClient(AudioFormat preferredFormat, int targetLatencyMs)
    {
        CoreAudioInterop.IMMDeviceEnumerator? deviceEnumerator = null;
        CoreAudioInterop.IMMDevice? device = null;

        try
        {
            deviceEnumerator = CoreAudioInterop.CreateDeviceEnumerator();
            CoreAudioInterop.ThrowIfFailed(
                deviceEnumerator.GetDevice(captureEndpoint.Id, out device),
                "IMMDeviceEnumerator.GetDevice");

            var audioClientInterfaceId = CoreAudioInterop.IAudioClientIid;
            CoreAudioInterop.ThrowIfFailed(
                device.Activate(ref audioClientInterfaceId, CoreAudioInterop.ClsCtxAll, IntPtr.Zero, out var audioClientObject),
                "IMMDevice.Activate(IAudioClient)");
            audioClient = (CoreAudioInterop.IAudioClient)audioClientObject;

            captureFormat = NegotiateCaptureFormat(audioClient, preferredFormat, captureEndpoint);

            var waveFormat = WinMmInterop.CreateWaveFormat(captureFormat);
            var bufferDuration = checked((long)targetLatencyMs * 10_000);
            CoreAudioInterop.ThrowIfFailed(
                audioClient.Initialize(CoreAudioInterop.AudclntSharemodeShared, 0, bufferDuration, 0, ref waveFormat, IntPtr.Zero),
                "IAudioClient.Initialize");

            var captureClientInterfaceId = CoreAudioInterop.IAudioCaptureClientIid;
            CoreAudioInterop.ThrowIfFailed(
                audioClient.GetService(ref captureClientInterfaceId, out var captureClientObject),
                "IAudioClient.GetService(IAudioCaptureClient)");
            captureClient = (CoreAudioInterop.IAudioCaptureClient)captureClientObject;
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            CoreAudioInterop.ReleaseComObject(device);
            CoreAudioInterop.ReleaseComObject(deviceEnumerator);
        }
    }

    private void ObservePacket(IntPtr dataPointer, uint frameCount, uint flags)
    {
        var packetBytes = checked((int)(frameCount * (uint)captureFormat.BytesPerSample * (uint)captureFormat.Channels));
        if (packetBytes == 0)
        {
            return;
        }

        var observedAtUtc = DateTimeOffset.UtcNow;

        if ((flags & CoreAudioInterop.AudclntBufferflagsSilent) != 0)
        {
            signalMeter.ObserveSamples(captureFormat, new byte[packetBytes], observedAtUtc);
        }
        else
        {
            var payload = new byte[packetBytes];
            Marshal.Copy(dataPointer, payload, 0, payload.Length);
            signalMeter.ObserveSamples(captureFormat, payload, observedAtUtc);
        }

        lock (gate)
        {
            observedBytes += packetBytes;
            lastObservedAtUtc = observedAtUtc;
        }
    }

    private static AudioFormat NegotiateCaptureFormat(
        CoreAudioInterop.IAudioClient audioClient,
        AudioFormat preferredFormat,
        AudioEndpointInfo endpoint)
    {
        foreach (var candidate in GetCandidateFormats(preferredFormat))
        {
            var waveFormat = WinMmInterop.CreateWaveFormat(candidate);
            IntPtr closestMatch = IntPtr.Zero;

            try
            {
                var result = audioClient.IsFormatSupported(CoreAudioInterop.AudclntSharemodeShared, ref waveFormat, out closestMatch);
                if (result == CoreAudioInterop.SOk)
                {
                    return candidate;
                }
            }
            finally
            {
                if (closestMatch != IntPtr.Zero)
                {
                    CoreAudioInterop.CoTaskMemFree(closestMatch);
                }
            }
        }

        throw new InvalidOperationException(
            $"No supported shared-mode PCM format was found for capture endpoint '{endpoint.FriendlyName}' ({endpoint.Id}). " +
            $"Tried {string.Join(", ", GetCandidateFormats(preferredFormat).Select(DescribeFormat))}.");
    }

    private static IEnumerable<AudioFormat> GetCandidateFormats(AudioFormat preferredFormat)
    {
        var orderedCandidates = new[]
        {
            preferredFormat,
            preferredFormat with { Channels = 2 },
            preferredFormat with { SampleRate = 44_100, Channels = 2 },
            preferredFormat with { SampleRate = 48_000, Channels = 2 },
            preferredFormat with { SampleRate = 44_100 },
            preferredFormat with { SampleRate = 32_000, Channels = 2 }
        };

        return orderedCandidates.Distinct();
    }

    private static string DescribeFormat(AudioFormat format) =>
        $"{format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit/{format.FrameDurationMs}ms";

    internal sealed record CaptureProbeSnapshot(
        string State,
        string? Format,
        long ObservedBytes,
        DateTimeOffset? LastObservedAtUtc,
        AudioLevelMeterSnapshot SignalMeter,
        string? FaultDetail);
}
