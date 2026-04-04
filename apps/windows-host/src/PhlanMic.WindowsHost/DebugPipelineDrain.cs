using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class DebugPipelineDrain
{
    private static readonly TimeSpan EmptyPollDelay = TimeSpan.FromMilliseconds(5);
    private readonly AudioStreamPipeline pipeline;
    private readonly object gate = new();
    private long drainedFrames;
    private long drainedBytes;
    private long? lastSequenceNumber;
    private DateTimeOffset? lastFrameCapturedAtUtc;
    private DateTimeOffset? lastDrainedAtUtc;

    public DebugPipelineDrain(AudioStreamPipeline pipeline)
    {
        this.pipeline = pipeline;
    }

    public DebugPipelineDrainSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new DebugPipelineDrainSnapshot(
                drainedFrames,
                drainedBytes,
                lastSequenceNumber,
                lastFrameCapturedAtUtc,
                lastDrainedAtUtc);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var drainedAny = false;

                while (pipeline.TryRead(out var frame))
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
            drainedFrames++;
            drainedBytes += frame.Payload.Length;
            lastSequenceNumber = frame.SequenceNumber;
            lastFrameCapturedAtUtc = frame.CapturedAtUtc;
            lastDrainedAtUtc = DateTimeOffset.UtcNow;
        }
    }
}
