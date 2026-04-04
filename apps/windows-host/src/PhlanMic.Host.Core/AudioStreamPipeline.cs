namespace PhlanMic.Host.Core;

public sealed class AudioStreamPipeline
{
    private readonly AudioJitterBuffer jitterBuffer;

    public AudioStreamPipeline(
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

        ExpectedFormat = expectedFormat;
        BufferConfig = bufferConfig;
        RobustnessConfig = robustnessConfig;
        jitterBuffer = new AudioJitterBuffer(expectedFormat, bufferConfig, robustnessConfig);
    }

    public AudioFormat ExpectedFormat { get; }

    public StreamBufferConfig BufferConfig { get; }

    public StreamRobustnessConfig RobustnessConfig { get; }

    public long AcceptedFrames => jitterBuffer.AcceptedFrames;

    public long RejectedFrames => jitterBuffer.RejectedFrames;

    public long DroppedFrames => jitterBuffer.DroppedFrames;

    public int BufferedFrameCount => jitterBuffer.Count;

    public AudioEnqueueResult Write(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return jitterBuffer.Write(frame);
    }

    public bool TryRead(out AudioFrame? frame, bool allowConcealment = false) =>
        jitterBuffer.TryRead(out frame, allowConcealment);

    public StreamRobustnessSnapshot GetRobustnessSnapshot() => jitterBuffer.GetSnapshot();

    public void ResetForNewStream() => jitterBuffer.ResetForNewStream();
}
