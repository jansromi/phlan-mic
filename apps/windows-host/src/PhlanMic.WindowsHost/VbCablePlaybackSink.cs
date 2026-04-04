using System.Runtime.InteropServices;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class VbCablePlaybackSink : IAudioOutputSink
{
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan ActivePollDelay = TimeSpan.FromMilliseconds(2);
    private readonly StructuredConsoleLogger logger;
    private readonly AudioStreamPipeline pipeline;
    private readonly OutputConfig config;
    private readonly VbCableEndpointPair selectedPair;
    private readonly object gate = new();
    private CoreAudioInterop.IAudioClient? audioClient;
    private CoreAudioInterop.IAudioRenderClient? renderClient;
    private PreparedOutputFrame? currentFrame;
    private AudioFormat outputFormat = AudioFormat.CreateMvpDefault();
    private bool formatConversionActive;
    private int bytesPerDeviceFrame;
    private int bufferCount;
    private uint bufferFrameCapacity;
    private bool started;
    private bool disposed;
    private long submittedFrames;
    private long silenceFramesInserted;
    private long underrunCount;
    private long completedFrames;
    private long completedBytes;
    private long? lastSequenceNumber;
    private DateTimeOffset? startedAtUtc;
    private DateTimeOffset? lastFrameCapturedAtUtc;
    private DateTimeOffset? lastSubmittedAtUtc;
    private DateTimeOffset? lastCompletedAtUtc;
    private uint lastPaddingFrames;

    public VbCablePlaybackSink(
        StructuredConsoleLogger logger,
        AudioStreamPipeline pipeline,
        AudioFormat inputFormat,
        OutputConfig config,
        VbCableEndpointPair selectedPair)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(inputFormat);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selectedPair);

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("VB-CABLE playback requires Windows.");
        }

        this.logger = logger;
        this.pipeline = pipeline;
        this.config = config;
        this.selectedPair = selectedPair;

        InitializeAudioClient(inputFormat);

        logger.Info("audio_output_selected", "Configured VB-CABLE playback output.", new Dictionary<string, object?>
        {
            ["endpointId"] = selectedPair.RenderEndpoint.Id,
            ["deviceName"] = selectedPair.RenderEndpoint.FriendlyName,
            ["captureEndpointId"] = selectedPair.CaptureEndpoint.Id,
            ["captureEndpointName"] = selectedPair.CaptureEndpoint.FriendlyName,
            ["inputFormat"] = DescribeFormat(inputFormat),
            ["outputFormat"] = DescribeFormat(outputFormat),
            ["formatConversionActive"] = formatConversionActive,
            ["bufferCount"] = bufferCount,
            ["targetLatencyMs"] = config.TargetLatencyMs,
            ["matchReason"] = selectedPair.MatchReason
        });
    }

    public AudioOutputSnapshot GetSnapshot()
    {
        var paddingFrames = GetCurrentPaddingOrLastKnown();
        UpdateCompletedMetrics(paddingFrames);

        lock (gate)
        {
            var outstandingBytes = (long)paddingFrames * bytesPerDeviceFrame;
            var bufferedDeviceFrames = outputFormat.BytesPerFrame > 0
                ? (int)Math.Ceiling(outstandingBytes / (double)outputFormat.BytesPerFrame)
                : 0;
            var partialFrameCount = currentFrame is null ? 0 : 1;
            var pendingBytes = currentFrame?.RemainingBytes ?? 0;
            var estimatedLatencyMs =
                (pipeline.BufferedFrameCount * outputFormat.FrameDurationMs) +
                ((outstandingBytes + pendingBytes) * 1000d / Math.Max(1, outputFormat.SampleRate * bytesPerDeviceFrame));

            return new AudioOutputSnapshot(
                SinkKind: "VbCable",
                DeviceId: null,
                DeviceName: selectedPair.RenderEndpoint.FriendlyName,
                EndpointId: selectedPair.RenderEndpoint.Id,
                PairedCaptureEndpointId: selectedPair.CaptureEndpoint.Id,
                PairedCaptureEndpointName: selectedPair.CaptureEndpoint.FriendlyName,
                OutputFormat: DescribeFormat(outputFormat),
                FormatConversionActive: formatConversionActive,
                BufferCount: bufferCount,
                BufferedFrames: pipeline.BufferedFrameCount + bufferedDeviceFrames + partialFrameCount,
                SubmittedFrames: submittedFrames,
                CompletedFrames: completedFrames,
                CompletedBytes: completedBytes,
                SilenceFramesInserted: silenceFramesInserted,
                UnderrunCount: underrunCount,
                EstimatedLatencyMs: estimatedLatencyMs,
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
                if (!started)
                {
                    if (!TryStartPlayback())
                    {
                        await Task.Delay(IdlePollDelay, cancellationToken);
                        continue;
                    }

                    continue;
                }

                if (!TryWriteAvailableAudio())
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
            var snapshot = GetSnapshot();
            Dispose();
            logger.Info("audio_output_stopped", "VB-CABLE playback stopped.", new Dictionary<string, object?>
            {
                ["endpointId"] = selectedPair.RenderEndpoint.Id,
                ["deviceName"] = selectedPair.RenderEndpoint.FriendlyName,
                ["captureEndpointId"] = selectedPair.CaptureEndpoint.Id,
                ["captureEndpointName"] = selectedPair.CaptureEndpoint.FriendlyName,
                ["submittedFrames"] = snapshot.SubmittedFrames,
                ["completedFrames"] = snapshot.CompletedFrames,
                ["completedBytes"] = snapshot.CompletedBytes,
                ["silenceFramesInserted"] = snapshot.SilenceFramesInserted,
                ["underrunCount"] = snapshot.UnderrunCount
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

        CoreAudioInterop.ReleaseComObject(renderClient);
        CoreAudioInterop.ReleaseComObject(audioClient);
        renderClient = null;
        audioClient = null;
    }

    private void InitializeAudioClient(AudioFormat inputFormat)
    {
        CoreAudioInterop.IMMDeviceEnumerator? deviceEnumerator = null;
        CoreAudioInterop.IMMDevice? device = null;

        try
        {
            deviceEnumerator = CoreAudioInterop.CreateDeviceEnumerator();
            CoreAudioInterop.ThrowIfFailed(
                deviceEnumerator.GetDevice(selectedPair.RenderEndpoint.Id, out device),
                "IMMDeviceEnumerator.GetDevice");

            var audioClientInterfaceId = CoreAudioInterop.IAudioClientIid;
            CoreAudioInterop.ThrowIfFailed(
                device.Activate(ref audioClientInterfaceId, CoreAudioInterop.ClsCtxAll, IntPtr.Zero, out var audioClientObject),
                "IMMDevice.Activate(IAudioClient)");
            audioClient = (CoreAudioInterop.IAudioClient)audioClientObject;

            outputFormat = NegotiateOutputFormat(audioClient, inputFormat, selectedPair.RenderEndpoint);
            formatConversionActive = outputFormat != inputFormat;
            bytesPerDeviceFrame = outputFormat.Channels * outputFormat.BytesPerSample;

            var waveFormat = WinMmInterop.CreateWaveFormat(outputFormat);
            var bufferDuration = checked((long)config.TargetLatencyMs * 10_000);
            CoreAudioInterop.ThrowIfFailed(
                audioClient.Initialize(CoreAudioInterop.AudclntSharemodeShared, 0, bufferDuration, 0, ref waveFormat, IntPtr.Zero),
                "IAudioClient.Initialize");
            CoreAudioInterop.ThrowIfFailed(audioClient.GetBufferSize(out bufferFrameCapacity), "IAudioClient.GetBufferSize");

            var renderClientInterfaceId = CoreAudioInterop.IAudioRenderClientIid;
            CoreAudioInterop.ThrowIfFailed(
                audioClient.GetService(ref renderClientInterfaceId, out var renderClientObject),
                "IAudioClient.GetService(IAudioRenderClient)");
            renderClient = (CoreAudioInterop.IAudioRenderClient)renderClientObject;

            var negotiatedBufferDurationMs = bufferFrameCapacity * 1000d / outputFormat.SampleRate;
            bufferCount = Math.Max(1, (int)Math.Ceiling(negotiatedBufferDurationMs / outputFormat.FrameDurationMs));
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

    private bool TryStartPlayback()
    {
        lock (gate)
        {
            if (currentFrame is null && !TryCreateOutputFrameLocked(allowSilence: false))
            {
                return false;
            }
        }

        WriteFramesToDevice(bufferFrameCapacity, allowSilence: true);
        CoreAudioInterop.ThrowIfFailed(audioClient!.Start(), "IAudioClient.Start");

        lock (gate)
        {
            started = true;
            startedAtUtc = DateTimeOffset.UtcNow;
            lastPaddingFrames = bufferFrameCapacity;
        }
        var robustness = pipeline.GetRobustnessSnapshot();

        logger.Info("audio_output_started", "VB-CABLE playback started.", new Dictionary<string, object?>
        {
            ["endpointId"] = selectedPair.RenderEndpoint.Id,
            ["deviceName"] = selectedPair.RenderEndpoint.FriendlyName,
            ["captureEndpointId"] = selectedPair.CaptureEndpoint.Id,
            ["captureEndpointName"] = selectedPair.CaptureEndpoint.FriendlyName,
            ["outputFormat"] = DescribeFormat(outputFormat),
            ["bufferCount"] = bufferCount,
            ["streamRobustnessState"] = robustness.State.ToString(),
            ["currentPrebufferDepth"] = robustness.CurrentPrebufferDepth,
            ["startupPrebufferFrames"] = robustness.StartupPrebufferFrames,
            ["targetBufferedFrames"] = robustness.TargetBufferedFrames,
            ["missingFrameGraceMs"] = robustness.MissingFrameGraceMs
        });

        return true;
    }

    private bool TryWriteAvailableAudio()
    {
        var paddingFrames = GetCurrentPaddingOrLastKnown();
        UpdateCompletedMetrics(paddingFrames);

        if (paddingFrames >= bufferFrameCapacity)
        {
            return false;
        }

        WriteFramesToDevice(bufferFrameCapacity - paddingFrames, allowSilence: true);
        return true;
    }

    private void WriteFramesToDevice(uint framesToWrite, bool allowSilence)
    {
        if (framesToWrite == 0)
        {
            return;
        }

        var bytesToWrite = checked((int)(framesToWrite * (uint)bytesPerDeviceFrame));
        var payload = new byte[bytesToWrite];
        if (!FillPayload(payload, allowSilence))
        {
            return;
        }

        var acquiredBuffer = false;

        try
        {
            CoreAudioInterop.ThrowIfFailed(renderClient!.GetBuffer(framesToWrite, out var bufferPointer), "IAudioRenderClient.GetBuffer");
            acquiredBuffer = true;
            Marshal.Copy(payload, 0, bufferPointer, payload.Length);
            CoreAudioInterop.ThrowIfFailed(renderClient.ReleaseBuffer(framesToWrite, 0), "IAudioRenderClient.ReleaseBuffer");
            acquiredBuffer = false;

            lock (gate)
            {
                lastPaddingFrames = bufferFrameCapacity;
            }
        }
        finally
        {
            if (acquiredBuffer)
            {
                try
                {
                    renderClient!.ReleaseBuffer(0, CoreAudioInterop.AudclntBufferflagsSilent);
                }
                catch
                {
                }
            }
        }
    }

    private bool FillPayload(byte[] destination, bool allowSilence)
    {
        var destinationOffset = 0;

        while (destinationOffset < destination.Length)
        {
            lock (gate)
            {
                if (currentFrame is null && !TryCreateOutputFrameLocked(allowSilence))
                {
                    return false;
                }

                var bytesToCopy = Math.Min(destination.Length - destinationOffset, currentFrame!.RemainingBytes);
                Buffer.BlockCopy(currentFrame.Payload, currentFrame.Offset, destination, destinationOffset, bytesToCopy);
                currentFrame.Offset += bytesToCopy;
                destinationOffset += bytesToCopy;

                if (currentFrame.Offset == currentFrame.Payload.Length)
                {
                    submittedFrames++;

                    if (currentFrame.IsSilence)
                    {
                        silenceFramesInserted++;
                        underrunCount++;
                    }
                    else
                    {
                        lastSequenceNumber = currentFrame.SequenceNumber;
                        lastFrameCapturedAtUtc = currentFrame.CapturedAtUtc;
                    }

                    lastSubmittedAtUtc = DateTimeOffset.UtcNow;
                    currentFrame = null;
                }
            }
        }

        return true;
    }

    private bool TryCreateOutputFrameLocked(bool allowSilence)
    {
        var readResult = pipeline.Read(allowConcealment: true);
        if (readResult.Status is AudioReadStatus.FrameAvailable)
        {
            var frame = readResult.Frame;
            var outputFrame = formatConversionActive
                ? Pcm16AudioFrameConverter.ConvertFrame(frame!, outputFormat)
                : frame!;

            currentFrame = new PreparedOutputFrame(
                outputFrame.Payload,
                outputFrame.SequenceNumber,
                outputFrame.CapturedAtUtc,
                isSilence: false);
            return true;
        }

        if (allowSilence && readResult.Status is AudioReadStatus.WaitingForFrame)
        {
            return false;
        }

        if (!allowSilence)
        {
            return false;
        }

        currentFrame = new PreparedOutputFrame(new byte[outputFormat.BytesPerFrame], null, null, isSilence: true);
        return true;
    }

    private uint GetCurrentPaddingOrLastKnown()
    {
        if (audioClient is null)
        {
            return 0;
        }

        try
        {
            CoreAudioInterop.ThrowIfFailed(audioClient.GetCurrentPadding(out var paddingFrames), "IAudioClient.GetCurrentPadding");
            lock (gate)
            {
                lastPaddingFrames = paddingFrames;
            }

            return paddingFrames;
        }
        catch when (disposed)
        {
            lock (gate)
            {
                return lastPaddingFrames;
            }
        }
    }

    private void UpdateCompletedMetrics(uint paddingFrames)
    {
        lock (gate)
        {
            var submittedPayloadBytes = submittedFrames * (long)outputFormat.BytesPerFrame;
            var outstandingBytes = (long)paddingFrames * bytesPerDeviceFrame;
            var latestCompletedBytes = Math.Max(0, submittedPayloadBytes - outstandingBytes);
            var latestCompletedFrames = latestCompletedBytes / outputFormat.BytesPerFrame;

            if (latestCompletedFrames > completedFrames)
            {
                completedFrames = latestCompletedFrames;
                lastCompletedAtUtc = DateTimeOffset.UtcNow;
            }

            if (latestCompletedBytes > completedBytes)
            {
                completedBytes = latestCompletedBytes;
            }

            lastPaddingFrames = paddingFrames;
        }
    }

    private static AudioFormat NegotiateOutputFormat(
        CoreAudioInterop.IAudioClient audioClient,
        AudioFormat inputFormat,
        AudioEndpointInfo endpoint)
    {
        foreach (var candidate in GetCandidateFormats(inputFormat))
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
            $"No supported WASAPI PCM format was found for endpoint '{endpoint.FriendlyName}' ({endpoint.Id}). " +
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

    private static string DescribeFormat(AudioFormat format) =>
        $"{format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit/{format.FrameDurationMs}ms";

    private double CalculateGlitchRatePerMinute()
    {
        lock (gate)
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
    }

    private sealed class PreparedOutputFrame
    {
        public PreparedOutputFrame(byte[] payload, long? sequenceNumber, DateTimeOffset? capturedAtUtc, bool isSilence)
        {
            Payload = payload;
            SequenceNumber = sequenceNumber;
            CapturedAtUtc = capturedAtUtc;
            IsSilence = isSilence;
        }

        public byte[] Payload { get; }

        public int Offset { get; set; }

        public int RemainingBytes => Payload.Length - Offset;

        public long? SequenceNumber { get; }

        public DateTimeOffset? CapturedAtUtc { get; }

        public bool IsSilence { get; }
    }
}
