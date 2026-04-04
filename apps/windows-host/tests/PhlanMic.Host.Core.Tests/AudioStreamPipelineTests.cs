using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class AudioStreamPipelineTests
{
    [Fact]
    public void WriteRejectsFramesWithUnexpectedFormat()
    {
        var expectedFormat = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(expectedFormat, maxBufferedFrames: 2);
        var incompatibleFormat = expectedFormat with { Channels = 2 };
        var frame = CreateFrame(incompatibleFormat, 1);

        var result = pipeline.Write(frame);

        Assert.Equal(AudioEnqueueStatus.RejectedFormatMismatch, result.Status);
        Assert.Equal(0, pipeline.BufferedFrameCount);
        Assert.Equal(1, pipeline.RejectedFrames);
    }

    [Fact]
    public void WriteDropsOldestFrameWhenConfiguredAndBufferIsFull()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 2, dropOldestWhenFull: true, startupPrebufferFrames: 1, targetBufferedFrames: 1);

        var first = CreateFrame(format, 1);
        var second = CreateFrame(format, 2);
        var third = CreateFrame(format, 3);

        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(first).Status);
        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(second).Status);

        var result = pipeline.Write(third);

        Assert.Equal(AudioEnqueueStatus.AcceptedAfterDroppingOldest, result.Status);
        Assert.Equal(1, pipeline.DroppedFrames);
        Assert.True(pipeline.TryRead(out var dequeued));
        Assert.NotNull(dequeued);
        Assert.Equal(2, dequeued!.SequenceNumber);
    }

    [Fact]
    public void WriteRejectsFrameWhenBufferIsFullAndDroppingIsDisabled()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 1, dropOldestWhenFull: false);

        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(CreateFrame(format, 1)).Status);

        var result = pipeline.Write(CreateFrame(format, 2));

        Assert.Equal(AudioEnqueueStatus.RejectedBufferFull, result.Status);
        Assert.Equal(1, pipeline.RejectedFrames);
        Assert.Equal(1, pipeline.DroppedFrames);
    }

    [Fact]
    public void TryReadWaitsForStartupPrebufferBeforeStartingPlayback()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 4, startupPrebufferFrames: 2, targetBufferedFrames: 2);

        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(CreateFrame(format, 1)).Status);
        Assert.False(pipeline.TryRead(out _, allowConcealment: true));

        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(CreateFrame(format, 2)).Status);
        Assert.True(pipeline.TryRead(out var frame, allowConcealment: true));
        Assert.Equal(1, frame!.SequenceNumber);
        Assert.Equal(StreamRobustnessState.Streaming, pipeline.GetRobustnessSnapshot().State);
    }

    [Fact]
    public void TryReadReordersLateArrivalWithinTolerance()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 4, startupPrebufferFrames: 2, targetBufferedFrames: 2);

        pipeline.Write(CreateFrame(format, 1));
        pipeline.Write(CreateFrame(format, 3));

        Assert.True(pipeline.TryRead(out var first, allowConcealment: true));
        Assert.Equal(1, first!.SequenceNumber);
        Assert.False(pipeline.TryRead(out _, allowConcealment: true));

        pipeline.Write(CreateFrame(format, 2));

        Assert.True(pipeline.TryRead(out var second, allowConcealment: true));
        Assert.Equal(2, second!.SequenceNumber);
        Assert.True(pipeline.TryRead(out var third, allowConcealment: true));
        Assert.Equal(3, third!.SequenceNumber);
        Assert.Equal(1, pipeline.GetRobustnessSnapshot().LateFramesArrived);
    }

    [Fact]
    public void TryReadConcealsMissingFramesWhenGapPersists()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 5, startupPrebufferFrames: 2, targetBufferedFrames: 2);

        pipeline.Write(CreateFrame(format, 1));
        pipeline.Write(CreateFrame(format, 3));
        pipeline.Write(CreateFrame(format, 4));

        Assert.True(pipeline.TryRead(out var first, allowConcealment: true));
        Assert.Equal(1, first!.SequenceNumber);
        Assert.True(pipeline.TryRead(out var concealed, allowConcealment: true));
        Assert.Equal(2, concealed!.SequenceNumber);
        Assert.All(concealed.Payload, sample => Assert.Equal(0, sample));
        Assert.True(pipeline.TryRead(out var next, allowConcealment: true));
        Assert.Equal(3, next!.SequenceNumber);

        var snapshot = pipeline.GetRobustnessSnapshot();
        Assert.Equal(StreamRobustnessState.Streaming, snapshot.State);
        Assert.Equal(1, snapshot.SequenceGapsObserved);
        Assert.Equal(1, snapshot.MissingFramesDetected);
        Assert.Equal(1, snapshot.SilenceFramesInserted);
        Assert.Equal(1, snapshot.LargestObservedGap);
    }

    [Fact]
    public void WriteDropsFramesThatArriveAfterPlaybackHasAlreadyMovedPastThem()
    {
        var format = AudioFormat.CreateMvpDefault();
        var pipeline = CreatePipeline(format, maxBufferedFrames: 4, startupPrebufferFrames: 1, targetBufferedFrames: 1);

        pipeline.Write(CreateFrame(format, 1));
        Assert.True(pipeline.TryRead(out _, allowConcealment: true));

        var result = pipeline.Write(CreateFrame(format, 1));

        Assert.Equal(AudioEnqueueStatus.RejectedLateFrame, result.Status);
        Assert.Equal(1, pipeline.GetRobustnessSnapshot().LateFramesArrived);
        Assert.Equal(1, pipeline.GetRobustnessSnapshot().LateFramesDropped);
    }

    private static AudioFrame CreateFrame(AudioFormat format, int sequenceNumber)
    {
        var payload = new byte[format.BytesPerFrame];
        return new AudioFrame(sequenceNumber, format, payload, DateTimeOffset.UtcNow);
    }

    private static AudioStreamPipeline CreatePipeline(
        AudioFormat format,
        int maxBufferedFrames,
        bool dropOldestWhenFull = true,
        int startupPrebufferFrames = 2,
        int targetBufferedFrames = 2,
        int maxLateFrameToleranceFrames = 2)
    {
        return new AudioStreamPipeline(
            format,
            new StreamBufferConfig
            {
                MaxBufferedFrames = maxBufferedFrames,
                DropOldestWhenFull = dropOldestWhenFull
            },
            new StreamRobustnessConfig
            {
                StartupPrebufferFrames = startupPrebufferFrames,
                TargetBufferedFrames = targetBufferedFrames,
                MaxLateFrameToleranceFrames = maxLateFrameToleranceFrames,
                ConcealMissingFramesWithSilence = true
            });
    }
}
