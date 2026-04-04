namespace PhlanMic.Host.Core;

public sealed class AudioStreamPipeline
{
    private readonly AudioFrameQueue queue;

    public AudioStreamPipeline(AudioFormat expectedFormat, StreamBufferConfig bufferConfig)
    {
        ArgumentNullException.ThrowIfNull(expectedFormat);
        ArgumentNullException.ThrowIfNull(bufferConfig);

        expectedFormat.Validate();
        bufferConfig.Validate();

        ExpectedFormat = expectedFormat;
        BufferConfig = bufferConfig;
        queue = new AudioFrameQueue(bufferConfig.MaxBufferedFrames);
    }

    public AudioFormat ExpectedFormat { get; }

    public StreamBufferConfig BufferConfig { get; }

    public long AcceptedFrames { get; private set; }

    public long RejectedFrames { get; private set; }

    public long DroppedFrames => queue.DroppedFrames;

    public int BufferedFrameCount => queue.Count;

    public AudioEnqueueResult Write(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format != ExpectedFormat)
        {
            RejectedFrames++;
            return new AudioEnqueueResult(AudioEnqueueStatus.RejectedFormatMismatch, queue.Count);
        }

        var accepted = queue.TryEnqueue(frame, BufferConfig.DropOldestWhenFull, out var droppedOldest);
        if (!accepted)
        {
            RejectedFrames++;
            return new AudioEnqueueResult(AudioEnqueueStatus.RejectedBufferFull, queue.Count);
        }

        AcceptedFrames++;
        return new AudioEnqueueResult(
            droppedOldest ? AudioEnqueueStatus.AcceptedAfterDroppingOldest : AudioEnqueueStatus.Accepted,
            queue.Count);
    }

    public bool TryRead(out AudioFrame? frame) => queue.TryDequeue(out frame);
}
