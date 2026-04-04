using System.Buffers.Binary;

namespace PhlanMic.Host.Core;

public sealed class GeneratedSignalTestSource : IAudioInputSource
{
    private readonly AudioFormat format;
    private readonly GeneratedSignalTestModeConfig config;
    private readonly AudioStreamPipeline pipeline;
    private readonly AudioInputSessionTracker tracker;
    private long nextSequenceNumber = 1;
    private double samplePosition;

    public GeneratedSignalTestSource(
        AudioFormat format,
        GeneratedSignalTestModeConfig config,
        AudioStreamPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pipeline);

        format.Validate();
        config.Validate();

        this.format = format;
        this.config = config;
        this.pipeline = pipeline;
        tracker = new AudioInputSessionTracker("GeneratedSignalTestMode");
        tracker.SessionChanged += (_, snapshot) => SessionChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<StreamSessionSnapshot>? SessionChanged;

    public StreamSessionSnapshot GetSessionSnapshot() => tracker.GetSessionSnapshot();

    public StreamStatisticsSnapshot GetStatisticsSnapshot() => tracker.GetStatisticsSnapshot(pipeline);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        tracker.MarkConnected("generated-signal", startedAtUtc, "Generated signal test mode active.");
        tracker.MarkStreaming(startedAtUtc, "Generating local PCM frames.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = CreateFrame(DateTimeOffset.UtcNow);
                tracker.RecordTraffic(frame.Payload.Length, packets: 1, frames: 1, frame.CapturedAtUtc);
                pipeline.Write(frame);
                await Task.Delay(format.FrameDuration, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            tracker.MarkStopped(DateTimeOffset.UtcNow, "Generated signal test mode stopped.");
        }
    }

    private AudioFrame CreateFrame(DateTimeOffset capturedAtUtc)
    {
        var payload = new byte[format.BytesPerFrame];
        var amplitude = short.MaxValue / 4.0;
        var twoPi = Math.PI * 2;

        for (var sampleIndex = 0; sampleIndex < format.SamplesPerFrame; sampleIndex++)
        {
            var value = Math.Sin(twoPi * config.SignalFrequencyHz * samplePosition / format.SampleRate) * amplitude;
            var pcmSample = (short)Math.Round(value);

            for (var channelIndex = 0; channelIndex < format.Channels; channelIndex++)
            {
                var offset = ((sampleIndex * format.Channels) + channelIndex) * format.BytesPerSample;
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset, sizeof(short)), pcmSample);
            }

            samplePosition++;
        }

        return new AudioFrame(nextSequenceNumber++, format, payload, capturedAtUtc);
    }
}
