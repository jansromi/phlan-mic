namespace PhlanMic.Host.Core;

public sealed class AudioJitterBuffer
{
    private readonly AudioFormat expectedFormat;
    private readonly StreamBufferConfig bufferConfig;
    private readonly StreamRobustnessConfig robustnessConfig;
    private readonly SortedDictionary<long, AudioFrame> bufferedFrames = new();
    private readonly object gate = new();
    private long acceptedFrames;
    private long rejectedFrames;
    private long droppedFrames;
    private long sequenceGapsObserved;
    private long lateFramesArrived;
    private long lateFramesDropped;
    private long missingFramesDetected;
    private long silenceFramesInserted;
    private int largestObservedGap;
    private long? expectedNextSequence;
    private long? highestReceivedSequence;
    private long? activeGapEndSequenceExclusive;
    private StreamRobustnessState state = StreamRobustnessState.Buffering;

    public AudioJitterBuffer(
        AudioFormat expectedFormat,
        StreamBufferConfig bufferConfig,
        StreamRobustnessConfig robustnessConfig)
    {
        ArgumentNullException.ThrowIfNull(expectedFormat);
        ArgumentNullException.ThrowIfNull(bufferConfig);
        ArgumentNullException.ThrowIfNull(robustnessConfig);

        expectedFormat.Validate();
        bufferConfig.Validate();
        robustnessConfig.Validate(bufferConfig);

        this.expectedFormat = expectedFormat;
        this.bufferConfig = bufferConfig;
        this.robustnessConfig = robustnessConfig;
    }

    public long AcceptedFrames
    {
        get
        {
            lock (gate)
            {
                return acceptedFrames;
            }
        }
    }

    public long RejectedFrames
    {
        get
        {
            lock (gate)
            {
                return rejectedFrames;
            }
        }
    }

    public long DroppedFrames
    {
        get
        {
            lock (gate)
            {
                return droppedFrames;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return bufferedFrames.Count;
            }
        }
    }

    public AudioEnqueueResult Write(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (gate)
        {
            if (frame.Format != expectedFormat)
            {
                rejectedFrames++;
                return new AudioEnqueueResult(AudioEnqueueStatus.RejectedFormatMismatch, bufferedFrames.Count);
            }

            if (expectedNextSequence is long expected && frame.SequenceNumber < expected)
            {
                lateFramesArrived++;
                lateFramesDropped++;
                rejectedFrames++;
                state = StreamRobustnessState.Degraded;
                return new AudioEnqueueResult(AudioEnqueueStatus.RejectedLateFrame, bufferedFrames.Count);
            }

            if (highestReceivedSequence is long highest && frame.SequenceNumber < highest)
            {
                lateFramesArrived++;
            }

            if (bufferedFrames.ContainsKey(frame.SequenceNumber))
            {
                rejectedFrames++;
                return new AudioEnqueueResult(AudioEnqueueStatus.RejectedDuplicateSequence, bufferedFrames.Count);
            }

            var status = AudioEnqueueStatus.Accepted;
            if (bufferedFrames.Count >= bufferConfig.MaxBufferedFrames)
            {
                if (!bufferConfig.DropOldestWhenFull)
                {
                    droppedFrames++;
                    rejectedFrames++;
                    state = StreamRobustnessState.Degraded;
                    return new AudioEnqueueResult(AudioEnqueueStatus.RejectedBufferFull, bufferedFrames.Count);
                }

                bufferedFrames.Remove(bufferedFrames.First().Key);
                droppedFrames++;
                state = StreamRobustnessState.Degraded;
                status = AudioEnqueueStatus.AcceptedAfterDroppingOldest;
            }

            bufferedFrames.Add(frame.SequenceNumber, frame);
            highestReceivedSequence = highestReceivedSequence.HasValue
                ? Math.Max(highestReceivedSequence.Value, frame.SequenceNumber)
                : frame.SequenceNumber;
            acceptedFrames++;

            if (state is StreamRobustnessState.Buffering &&
                expectedNextSequence is not null &&
                bufferedFrames.Count >= robustnessConfig.TargetBufferedFrames)
            {
                state = StreamRobustnessState.Streaming;
            }

            return new AudioEnqueueResult(status, bufferedFrames.Count);
        }
    }

    public bool TryRead(out AudioFrame? frame, bool allowConcealment)
    {
        lock (gate)
        {
            frame = null;

            if (expectedNextSequence is null)
            {
                if (bufferedFrames.Count < robustnessConfig.StartupPrebufferFrames)
                {
                    state = StreamRobustnessState.Buffering;
                    return false;
                }

                expectedNextSequence = bufferedFrames.First().Key;
                state = StreamRobustnessState.Streaming;
            }

            if (bufferedFrames.TryGetValue(expectedNextSequence.Value, out var bufferedFrame))
            {
                bufferedFrames.Remove(expectedNextSequence.Value);
                expectedNextSequence++;

                if (activeGapEndSequenceExclusive is not null &&
                    expectedNextSequence >= activeGapEndSequenceExclusive)
                {
                    activeGapEndSequenceExclusive = null;
                }

                state = StreamRobustnessState.Streaming;
                frame = bufferedFrame;
                return true;
            }

            if (!allowConcealment || !robustnessConfig.ConcealMissingFramesWithSilence)
            {
                state = StreamRobustnessState.Buffering;
                return false;
            }

            if (bufferedFrames.Count > 0)
            {
                var nextAvailableSequence = bufferedFrames.First().Key;
                var gapSize = checked((int)Math.Min(int.MaxValue, nextAvailableSequence - expectedNextSequence.Value));

                if (gapSize <= robustnessConfig.MaxLateFrameToleranceFrames &&
                    bufferedFrames.Count < robustnessConfig.TargetBufferedFrames)
                {
                    state = StreamRobustnessState.Buffering;
                    return false;
                }

                RegisterGap(nextAvailableSequence, gapSize);
                frame = CreateConcealedFrameLocked();
                return true;
            }

            state = StreamRobustnessState.Buffering;
            return false;
        }
    }

    public StreamRobustnessSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new StreamRobustnessSnapshot(
                state,
                expectedNextSequence,
                highestReceivedSequence,
                sequenceGapsObserved,
                lateFramesArrived,
                lateFramesDropped,
                missingFramesDetected,
                silenceFramesInserted,
                bufferedFrames.Count,
                largestObservedGap,
                robustnessConfig.StartupPrebufferFrames,
                robustnessConfig.TargetBufferedFrames,
                robustnessConfig.MaxLateFrameToleranceFrames,
                bufferedFrames.Count * expectedFormat.FrameDurationMs);
        }
    }

    public void ResetForNewStream()
    {
        lock (gate)
        {
            bufferedFrames.Clear();
            expectedNextSequence = null;
            highestReceivedSequence = null;
            activeGapEndSequenceExclusive = null;
            state = StreamRobustnessState.Buffering;
        }
    }

    private void RegisterGap(long nextAvailableSequence, int gapSize)
    {
        if (activeGapEndSequenceExclusive is not null &&
            expectedNextSequence < activeGapEndSequenceExclusive &&
            nextAvailableSequence == activeGapEndSequenceExclusive)
        {
            return;
        }

        sequenceGapsObserved++;
        largestObservedGap = Math.Max(largestObservedGap, gapSize);
        activeGapEndSequenceExclusive = nextAvailableSequence;
    }

    private AudioFrame CreateConcealedFrameLocked()
    {
        var concealedFrame = new AudioFrame(
            expectedNextSequence!.Value,
            expectedFormat,
            new byte[expectedFormat.BytesPerFrame],
            DateTimeOffset.UtcNow);

        expectedNextSequence++;
        missingFramesDetected++;
        silenceFramesInserted++;
        state = StreamRobustnessState.Degraded;
        return concealedFrame;
    }
}
