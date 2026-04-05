namespace PhlanMic.Host.Core;

public sealed class AudioJitterBuffer
{
    private readonly AudioFormat expectedFormat;
    private readonly StreamBufferConfig bufferConfig;
    private readonly StreamRobustnessConfig robustnessConfig;
    private readonly TimeProvider timeProvider;
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
    private DateTimeOffset? nextPlayoutDueAtUtc;
    private StreamRobustnessState state = StreamRobustnessState.Buffering;

    public AudioJitterBuffer(
        AudioFormat expectedFormat,
        StreamBufferConfig bufferConfig,
        StreamRobustnessConfig robustnessConfig,
        TimeProvider? timeProvider = null)
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
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

    public AudioReadResult Read(bool allowConcealment)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();

            if (expectedNextSequence is null)
            {
                if (bufferedFrames.Count < robustnessConfig.StartupPrebufferFrames)
                {
                    state = StreamRobustnessState.Buffering;
                    return new AudioReadResult(AudioReadStatus.WaitingForFrame, null, bufferedFrames.Count);
                }

                expectedNextSequence = bufferedFrames.First().Key;
                state = StreamRobustnessState.Streaming;
            }

            if (bufferedFrames.TryGetValue(expectedNextSequence.Value, out var bufferedFrame))
            {
                bufferedFrames.Remove(expectedNextSequence.Value);
                expectedNextSequence++;
                AdvancePlayoutScheduleLocked(now);

                if (activeGapEndSequenceExclusive is not null &&
                    expectedNextSequence >= activeGapEndSequenceExclusive)
                {
                    activeGapEndSequenceExclusive = null;
                }

                state = StreamRobustnessState.Streaming;
                return new AudioReadResult(AudioReadStatus.FrameAvailable, bufferedFrame, bufferedFrames.Count);
            }

            if (!allowConcealment || !robustnessConfig.ConcealMissingFramesWithSilence)
            {
                state = StreamRobustnessState.Buffering;
                return new AudioReadResult(AudioReadStatus.WaitingForFrame, null, bufferedFrames.Count);
            }

            if (bufferedFrames.Count > 0)
            {
                var nextAvailableSequence = bufferedFrames.First().Key;
                var gapSize = checked((int)Math.Min(int.MaxValue, nextAvailableSequence - expectedNextSequence.Value));

                if (gapSize <= robustnessConfig.MaxLateFrameToleranceFrames &&
                    bufferedFrames.Count < robustnessConfig.TargetBufferedFrames &&
                    !HasMissedFrameDeadlineLocked(now))
                {
                    state = StreamRobustnessState.Buffering;
                    return new AudioReadResult(AudioReadStatus.WaitingForFrame, null, bufferedFrames.Count);
                }

                RegisterGap(nextAvailableSequence, gapSize);
                return new AudioReadResult(AudioReadStatus.FrameAvailable, CreateConcealedFrameLocked(now), bufferedFrames.Count);
            }

            if (HasMissedFrameDeadlineLocked(now))
            {
                return new AudioReadResult(AudioReadStatus.FrameAvailable, CreateConcealedFrameLocked(now), bufferedFrames.Count);
            }

            state = StreamRobustnessState.Buffering;
            return new AudioReadResult(AudioReadStatus.WaitingForFrame, null, bufferedFrames.Count);
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
                robustnessConfig.MissingFrameGraceMs,
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
            nextPlayoutDueAtUtc = null;
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

    private bool HasMissedFrameDeadlineLocked(DateTimeOffset now) =>
        nextPlayoutDueAtUtc is not null &&
        now >= nextPlayoutDueAtUtc.Value.AddMilliseconds(robustnessConfig.MissingFrameGraceMs);

    private void AdvancePlayoutScheduleLocked(DateTimeOffset now)
    {
        nextPlayoutDueAtUtc = nextPlayoutDueAtUtc is null
            ? now + expectedFormat.FrameDuration
            : nextPlayoutDueAtUtc.Value + expectedFormat.FrameDuration;
    }

    private AudioFrame CreateConcealedFrameLocked(DateTimeOffset now)
    {
        var concealedFrame = new AudioFrame(
            expectedNextSequence!.Value,
            expectedFormat,
            new byte[expectedFormat.BytesPerFrame],
            now);

        expectedNextSequence++;
        AdvancePlayoutScheduleLocked(now);
        missingFramesDetected++;
        silenceFramesInserted++;
        state = StreamRobustnessState.Degraded;
        return concealedFrame;
    }
}
