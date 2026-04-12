using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class DebugPipelineDrain
    : IAudioOutputSink
{
    private static readonly TimeSpan EmptyPollDelay = TimeSpan.FromMilliseconds(5);
    private readonly AudioStreamPipeline pipeline;
    private readonly object gate = new();
    private readonly Pcm16AudioLevelMeter signalMeter = new();
    private long drainedFrames;
    private long drainedBytes;
    private long? lastSequenceNumber;
    private DateTimeOffset? lastFrameCapturedAtUtc;
    private DateTimeOffset? lastDrainedAtUtc;

    public DebugPipelineDrain(AudioStreamPipeline pipeline)
    {
        this.pipeline = pipeline;
    }

    public AudioOutputSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            return new AudioOutputSnapshot(
                SinkKind: "DebugDrain",
                DeviceId: null,
                DeviceName: null,
                EndpointId: null,
                PairedCaptureEndpointId: null,
                PairedCaptureEndpointName: null,
                OutputFormat: "DrainOnly",
                FormatConversionActive: false,
                BufferCount: 0,
                BufferedFrames: pipeline.BufferedFrameCount,
                SubmittedFrames: drainedFrames,
                CompletedFrames: drainedFrames,
                CompletedBytes: drainedBytes,
                SilenceFramesInserted: 0,
                UnderrunCount: 0,
                EstimatedLatencyMs: 0,
                GlitchRatePerMinute: 0,
                LastSequenceNumber: lastSequenceNumber,
                StartedAtUtc: null,
                LastFrameCapturedAtUtc: lastFrameCapturedAtUtc,
                LastSubmittedAtUtc: lastDrainedAtUtc,
                LastCompletedAtUtc: lastDrainedAtUtc,
                SignalMeter: signalMeter.GetSnapshot(observedAtUtc),
                CaptureProbeState: null,
                CaptureProbeFormat: null,
                CaptureProbeObservedBytes: 0,
                CaptureProbeLastObservedAtUtc: null,
                CaptureProbeSignalMeter: AudioLevelMeterSnapshot.Empty);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var drainedAny = false;

                while (pipeline.TryRead(out var frame, allowConcealment: false))
                {
                    drainedAny = true;
                    RecordFrame(frame!);
                }

                if (!drainedAny)
                {
                    await Task.Delay(EmptyPollDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RecordFrame(AudioFrame frame)
    {
        lock (gate)
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            drainedFrames++;
            drainedBytes += frame.Payload.Length;
            lastSequenceNumber = frame.SequenceNumber;
            lastFrameCapturedAtUtc = frame.CapturedAtUtc;
            lastDrainedAtUtc = observedAtUtc;
            signalMeter.ObserveFrame(frame.Format, frame.Payload, observedAtUtc);
        }
    }

    public void Dispose()
    {
    }
}
