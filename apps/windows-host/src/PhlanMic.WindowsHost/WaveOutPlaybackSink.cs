using System.Runtime.InteropServices;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class WaveOutPlaybackSink : IAudioOutputSink
{
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan ActivePollDelay = TimeSpan.FromMilliseconds(2);
    private readonly StructuredConsoleLogger logger;
    private readonly AudioStreamPipeline pipeline;
    private readonly OutputConfig config;
    private readonly object gate = new();
    private readonly uint deviceHandleId;
    private readonly WaveOutDeviceInfo? selectedDevice;
    private readonly AudioFormat outputFormat;
    private readonly bool formatConversionActive;
    private readonly int bufferCount;
    private IntPtr waveOutHandle;
    private WaveOutBuffer[] buffers = Array.Empty<WaveOutBuffer>();
    private bool started;
    private bool disposed;
    private long submittedFrames;
    private long completedFrames;
    private long completedBytes;
    private long silenceFramesInserted;
    private long underrunCount;
    private long? lastSequenceNumber;
    private DateTimeOffset? startedAtUtc;
    private DateTimeOffset? lastFrameCapturedAtUtc;
    private DateTimeOffset? lastSubmittedAtUtc;
    private DateTimeOffset? lastCompletedAtUtc;

    public WaveOutPlaybackSink(
        StructuredConsoleLogger logger,
        AudioStreamPipeline pipeline,
        AudioFormat inputFormat,
        OutputConfig config)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(inputFormat);
        ArgumentNullException.ThrowIfNull(config);

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("WaveOut playback requires Windows.");
        }

        this.logger = logger;
        this.pipeline = pipeline;
        this.config = config;

        var devices = WaveOutDeviceEnumerator.Enumerate();
        if (config.LogAvailableDevices)
        {
            logger.Info("audio_output_devices", "Enumerated waveOut render devices.", new Dictionary<string, object?>
            {
                ["deviceCount"] = devices.Count,
                ["devices"] = devices.Select(device => new Dictionary<string, object?>
                {
                    ["deviceId"] = device.DeviceId,
                    ["name"] = device.Name,
                    ["channels"] = device.Channels,
                    ["driverVersion"] = device.DriverVersion,
                    ["supportedFormatsMask"] = device.SupportedFormatsMask
                }).ToArray()
            });
        }

        deviceHandleId = ResolveDeviceHandleId(devices, config.DeviceId);
        selectedDevice = config.DeviceId >= 0 ? devices.Single(device => device.DeviceId == config.DeviceId) : null;
        outputFormat = NegotiateOutputFormat(deviceHandleId, inputFormat, config.DeviceId);
        formatConversionActive = outputFormat != inputFormat;
        bufferCount = Math.Max(2, (int)Math.Ceiling(config.TargetLatencyMs / (double)outputFormat.FrameDurationMs));

        logger.Info("audio_output_selected", "Configured waveOut playback output.", new Dictionary<string, object?>
        {
            ["deviceId"] = config.DeviceId,
            ["deviceName"] = selectedDevice?.Name ?? "System Default (WAVE_MAPPER)",
            ["inputFormat"] = DescribeFormat(inputFormat),
            ["outputFormat"] = DescribeFormat(outputFormat),
            ["formatConversionActive"] = formatConversionActive,
            ["bufferCount"] = bufferCount,
            ["targetLatencyMs"] = config.TargetLatencyMs
        });
    }

    public AudioOutputSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new AudioOutputSnapshot(
                SinkKind: "WaveOut",
                DeviceId: config.DeviceId,
                DeviceName: selectedDevice?.Name ?? "System Default (WAVE_MAPPER)",
                EndpointId: null,
                PairedCaptureEndpointId: null,
                PairedCaptureEndpointName: null,
                OutputFormat: DescribeFormat(outputFormat),
                FormatConversionActive: formatConversionActive,
                BufferCount: bufferCount,
                BufferedFrames: GetInFlightBufferCount() + pipeline.BufferedFrameCount,
                SubmittedFrames: submittedFrames,
                CompletedFrames: completedFrames,
                CompletedBytes: completedBytes,
                SilenceFramesInserted: silenceFramesInserted,
                UnderrunCount: underrunCount,
                EstimatedLatencyMs: (GetInFlightBufferCount() + pipeline.BufferedFrameCount) * outputFormat.FrameDurationMs,
                GlitchRatePerMinute: CalculateGlitchRatePerMinute(),
                LastSequenceNumber: lastSequenceNumber,
                StartedAtUtc: startedAtUtc,
                LastFrameCapturedAtUtc: lastFrameCapturedAtUtc,
                LastSubmittedAtUtc: lastSubmittedAtUtc,
                LastCompletedAtUtc: lastCompletedAtUtc);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var completedAny = CompleteFinishedBuffers();
                var queuedAny = QueueAvailableBuffers();

                if (!started && !queuedAny)
                {
                    await Task.Delay(IdlePollDelay, cancellationToken);
                    continue;
                }

                if (!completedAny && !queuedAny)
                {
                    await Task.Delay(ActivePollDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Dispose();
            logger.Info("audio_output_stopped", "waveOut playback stopped.", new Dictionary<string, object?>
            {
                ["deviceId"] = config.DeviceId,
                ["deviceName"] = selectedDevice?.Name ?? "System Default (WAVE_MAPPER)",
                ["submittedFrames"] = submittedFrames,
                ["completedFrames"] = completedFrames,
                ["completedBytes"] = completedBytes,
                ["silenceFramesInserted"] = silenceFramesInserted,
                ["underrunCount"] = underrunCount
            });
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (waveOutHandle != IntPtr.Zero)
        {
            try
            {
                WinMmInterop.ThrowIfError(WinMmInterop.waveOutReset(waveOutHandle), "waveOutReset", config.DeviceId);
            }
            catch
            {
            }
        }

        foreach (var buffer in buffers)
        {
            buffer.Dispose(waveOutHandle, config.DeviceId);
        }

        buffers = Array.Empty<WaveOutBuffer>();

        if (waveOutHandle != IntPtr.Zero)
        {
            try
            {
                WinMmInterop.ThrowIfError(WinMmInterop.waveOutClose(waveOutHandle), "waveOutClose", config.DeviceId);
            }
            finally
            {
                waveOutHandle = IntPtr.Zero;
            }
        }
    }

    private bool QueueAvailableBuffers()
    {
        if (!started)
        {
            if (!TryCreatePayload(allowSilence: false, out var firstPayload, out var firstSequenceNumber, out var firstCapturedAtUtc, out _))
            {
                return false;
            }

            EnsureWaveOutStarted();
            QueuePayload(firstPayload, firstSequenceNumber, firstCapturedAtUtc, isSilence: false);
        }

        var queuedAny = false;

        foreach (var buffer in buffers)
        {
            if (buffer.InFlight)
            {
                continue;
            }

            if (!TryCreatePayload(allowSilence: true, out var payload, out var sequenceNumber, out var capturedAtUtc, out var isSilence))
            {
                break;
            }

            buffer.Submit(waveOutHandle, payload, config.DeviceId, isSilence);
            RecordSubmission(sequenceNumber, capturedAtUtc, isSilence);
            queuedAny = true;
        }

        return queuedAny;
    }

    private void EnsureWaveOutStarted()
    {
        if (started)
        {
            return;
        }

        var waveFormat = WinMmInterop.CreateWaveFormat(outputFormat);
        WinMmInterop.ThrowIfError(
            WinMmInterop.waveOutOpen(
                out waveOutHandle,
                deviceHandleId,
                ref waveFormat,
                IntPtr.Zero,
                IntPtr.Zero,
                WinMmInterop.CallbackNull),
            "waveOutOpen",
            config.DeviceId);

        buffers = Enumerable.Range(0, bufferCount)
            .Select(_ => new WaveOutBuffer(outputFormat.BytesPerFrame))
            .ToArray();

        started = true;
        startedAtUtc = DateTimeOffset.UtcNow;
        var robustness = pipeline.GetRobustnessSnapshot();

        logger.Info("audio_output_started", "waveOut playback started.", new Dictionary<string, object?>
        {
            ["deviceId"] = config.DeviceId,
            ["deviceName"] = selectedDevice?.Name ?? "System Default (WAVE_MAPPER)",
            ["outputFormat"] = DescribeFormat(outputFormat),
            ["bufferCount"] = bufferCount,
            ["streamRobustnessState"] = robustness.State.ToString(),
            ["currentPrebufferDepth"] = robustness.CurrentPrebufferDepth,
            ["startupPrebufferFrames"] = robustness.StartupPrebufferFrames,
            ["targetBufferedFrames"] = robustness.TargetBufferedFrames,
            ["missingFrameGraceMs"] = robustness.MissingFrameGraceMs
        });
    }

    private void QueuePayload(byte[] payload, long? sequenceNumber, DateTimeOffset? capturedAtUtc, bool isSilence)
    {
        var buffer = buffers.First(candidate => !candidate.InFlight);
        buffer.Submit(waveOutHandle, payload, config.DeviceId, isSilence);
        RecordSubmission(sequenceNumber, capturedAtUtc, isSilence);
    }

    private bool CompleteFinishedBuffers()
    {
        if (!started)
        {
            return false;
        }

        var completedAny = false;

        foreach (var buffer in buffers)
        {
            if (!buffer.TryComplete(waveOutHandle, config.DeviceId, out var bytesCompleted))
            {
                continue;
            }

            lock (gate)
            {
                completedFrames++;
                completedBytes += bytesCompleted;
                lastCompletedAtUtc = DateTimeOffset.UtcNow;
            }

            completedAny = true;
        }

        return completedAny;
    }

    private void RecordSubmission(long? sequenceNumber, DateTimeOffset? capturedAtUtc, bool isSilence)
    {
        lock (gate)
        {
            submittedFrames++;
            if (isSilence)
            {
                silenceFramesInserted++;
                underrunCount++;
            }
            else
            {
                lastSequenceNumber = sequenceNumber;
                lastFrameCapturedAtUtc = capturedAtUtc;
            }

            lastSubmittedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private bool TryCreatePayload(
        bool allowSilence,
        out byte[] payload,
        out long? sequenceNumber,
        out DateTimeOffset? capturedAtUtc,
        out bool isSilence)
    {
        var readResult = pipeline.Read(allowConcealment: true);
        if (readResult.Status is AudioReadStatus.FrameAvailable)
        {
            var frame = readResult.Frame;
            var outputFrame = formatConversionActive
                ? Pcm16AudioFrameConverter.ConvertFrame(frame!, outputFormat)
                : frame!;

            payload = outputFrame.Payload;
            sequenceNumber = outputFrame.SequenceNumber;
            capturedAtUtc = outputFrame.CapturedAtUtc;
            isSilence = false;
            return true;
        }

        if (allowSilence && readResult.Status is AudioReadStatus.WaitingForFrame)
        {
            payload = Array.Empty<byte>();
            sequenceNumber = null;
            capturedAtUtc = null;
            isSilence = false;
            return false;
        }

        if (allowSilence)
        {
            payload = new byte[outputFormat.BytesPerFrame];
            sequenceNumber = null;
            capturedAtUtc = null;
            isSilence = true;
            return true;
        }

        payload = Array.Empty<byte>();
        sequenceNumber = null;
        capturedAtUtc = null;
        isSilence = false;
        return false;
    }

    private static AudioFormat NegotiateOutputFormat(uint deviceHandleId, AudioFormat inputFormat, int configuredDeviceId)
    {
        foreach (var candidate in GetCandidateFormats(inputFormat))
        {
            var waveFormat = WinMmInterop.CreateWaveFormat(candidate);
            var result = WinMmInterop.waveOutOpen(
                out _,
                deviceHandleId,
                ref waveFormat,
                IntPtr.Zero,
                IntPtr.Zero,
                WinMmInterop.WaveFormatQuery);

            if (result == WinMmInterop.MmSysErrNoError)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No supported waveOut PCM format was found for device {configuredDeviceId}. " +
            $"Tried {string.Join(", ", GetCandidateFormats(inputFormat).Select(DescribeFormat))}.");
    }

    private static IEnumerable<AudioFormat> GetCandidateFormats(AudioFormat inputFormat)
    {
        var orderedCandidates = new[]
        {
            inputFormat,
            inputFormat with { Channels = 2 },
            inputFormat with { SampleRate = 44_100 },
            inputFormat with { SampleRate = 44_100, Channels = 2 },
            inputFormat with { SampleRate = 32_000 },
            inputFormat with { SampleRate = 32_000, Channels = 2 }
        };

        return orderedCandidates.Distinct();
    }

    private static uint ResolveDeviceHandleId(IReadOnlyList<WaveOutDeviceInfo> devices, int configuredDeviceId)
    {
        if (configuredDeviceId == -1)
        {
            return WinMmInterop.WaveMapper;
        }

        if (!devices.Any(device => device.DeviceId == configuredDeviceId))
        {
            throw new InvalidOperationException(
                $"Configured output device id {configuredDeviceId} was not found. " +
                $"Available waveOut devices: {string.Join(", ", devices.Select(device => $"{device.DeviceId}:{device.Name}"))}");
        }

        return checked((uint)configuredDeviceId);
    }

    private static string DescribeFormat(AudioFormat format) =>
        $"{format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit/{format.FrameDurationMs}ms";

    private int GetInFlightBufferCount() => buffers.Count(buffer => buffer.InFlight);

    private double CalculateGlitchRatePerMinute()
    {
        if (startedAtUtc is null)
        {
            return 0;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAtUtc.Value;
        if (elapsed.TotalMinutes <= 0)
        {
            return underrunCount;
        }

        return underrunCount / elapsed.TotalMinutes;
    }

    private sealed class WaveOutBuffer
    {
        private readonly byte[] buffer;
        private readonly GCHandle bufferHandle;
        private readonly IntPtr headerPointer;
        private bool prepared;

        public WaveOutBuffer(int bufferSize)
        {
            buffer = new byte[bufferSize];
            bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinMmInterop.WAVEHDR>());
        }

        public bool InFlight { get; private set; }

        public void Submit(IntPtr waveOutHandle, byte[] payload, int deviceId, bool isSilence)
        {
            if (InFlight)
            {
                throw new InvalidOperationException("Cannot submit an in-flight waveOut buffer.");
            }

            if (payload.Length > buffer.Length)
            {
                throw new InvalidOperationException(
                    $"Payload length {payload.Length} exceeds waveOut buffer capacity {buffer.Length}.");
            }

            Array.Clear(buffer);
            Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);

            var header = new WinMmInterop.WAVEHDR
            {
                lpData = bufferHandle.AddrOfPinnedObject(),
                dwBufferLength = checked((uint)payload.Length),
                dwBytesRecorded = checked((uint)payload.Length),
                dwUser = isSilence ? new IntPtr(1) : IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
                lpNext = IntPtr.Zero,
                reserved = IntPtr.Zero
            };

            Marshal.StructureToPtr(header, headerPointer, fDeleteOld: false);
            WinMmInterop.ThrowIfError(
                WinMmInterop.waveOutPrepareHeader(
                    waveOutHandle,
                    headerPointer,
                    checked((uint)Marshal.SizeOf<WinMmInterop.WAVEHDR>())),
                "waveOutPrepareHeader",
                deviceId);
            prepared = true;

            WinMmInterop.ThrowIfError(
                WinMmInterop.waveOutWrite(
                    waveOutHandle,
                    headerPointer,
                    checked((uint)Marshal.SizeOf<WinMmInterop.WAVEHDR>())),
                "waveOutWrite",
                deviceId);

            InFlight = true;
        }

        public bool TryComplete(IntPtr waveOutHandle, int deviceId, out int bytesCompleted)
        {
            if (!InFlight)
            {
                bytesCompleted = 0;
                return false;
            }

            var header = Marshal.PtrToStructure<WinMmInterop.WAVEHDR>(headerPointer);
            if ((header.dwFlags & WinMmInterop.WaveHdrDone) == 0)
            {
                bytesCompleted = 0;
                return false;
            }

            bytesCompleted = checked((int)header.dwBufferLength);

            if (prepared)
            {
                WinMmInterop.ThrowIfError(
                    WinMmInterop.waveOutUnprepareHeader(
                        waveOutHandle,
                        headerPointer,
                        checked((uint)Marshal.SizeOf<WinMmInterop.WAVEHDR>())),
                    "waveOutUnprepareHeader",
                    deviceId);
                prepared = false;
            }

            InFlight = false;
            return true;
        }

        public void Dispose(IntPtr waveOutHandle, int deviceId)
        {
            if (prepared && waveOutHandle != IntPtr.Zero)
            {
                try
                {
                    WinMmInterop.waveOutUnprepareHeader(
                        waveOutHandle,
                        headerPointer,
                        checked((uint)Marshal.SizeOf<WinMmInterop.WAVEHDR>()));
                }
                catch
                {
                }
            }

            if (headerPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(headerPointer);
            }

            if (bufferHandle.IsAllocated)
            {
                bufferHandle.Free();
            }
        }
    }
}
