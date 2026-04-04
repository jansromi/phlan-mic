namespace PhlanMic.Host.Core;

public sealed class AudioStreamPipeline
{
    private readonly AudioJitterBuffer jitterBuffer;

    public AudioStreamPipeline(
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

        ExpectedFormat = expectedFormat;
        BufferConfig = bufferConfig;
        RobustnessConfig = robustnessConfig;
        jitterBuffer = new AudioJitterBuffer(expectedFormat, bufferConfig, robustnessConfig, timeProvider);
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

    public AudioReadResult Read(bool allowConcealment = false) =>
        jitterBuffer.Read(allowConcealment);

    public bool TryRead(out AudioFrame? frame, bool allowConcealment = false)
    {
        var result = Read(allowConcealment);
        frame = result.Frame;
        return result.Status is AudioReadStatus.FrameAvailable;
    }

    public StreamRobustnessSnapshot GetRobustnessSnapshot() => jitterBuffer.GetSnapshot();

    public void ResetForNewStream() => jitterBuffer.ResetForNewStream();
}
