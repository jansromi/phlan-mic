using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class AudioStreamPipelineTests
{
    [Fact]
    public void WriteRejectsFramesWithUnexpectedFormat()
    {
        var expectedFormat = AudioFormat.CreateMvpDefault();
        var pipeline = new AudioStreamPipeline(expectedFormat, new StreamBufferConfig { MaxBufferedFrames = 2 });
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
        var pipeline = new AudioStreamPipeline(format, new StreamBufferConfig
        {
            MaxBufferedFrames = 2,
            DropOldestWhenFull = true
        });

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
        var pipeline = new AudioStreamPipeline(format, new StreamBufferConfig
        {
            MaxBufferedFrames = 1,
            DropOldestWhenFull = false
        });

        Assert.Equal(AudioEnqueueStatus.Accepted, pipeline.Write(CreateFrame(format, 1)).Status);

        var result = pipeline.Write(CreateFrame(format, 2));

        Assert.Equal(AudioEnqueueStatus.RejectedBufferFull, result.Status);
        Assert.Equal(1, pipeline.RejectedFrames);
        Assert.Equal(1, pipeline.DroppedFrames);
    }

    private static AudioFrame CreateFrame(AudioFormat format, int sequenceNumber)
    {
        var payload = new byte[format.BytesPerFrame];
        return new AudioFrame(sequenceNumber, format, payload, DateTimeOffset.UtcNow);
    }
}
