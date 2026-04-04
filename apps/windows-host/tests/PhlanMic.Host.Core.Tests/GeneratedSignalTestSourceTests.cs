using PhlanMic.Host.Core;

namespace PhlanMic.Host.Core.Tests;

public sealed class GeneratedSignalTestSourceTests
{
    [Fact]
    public async Task RunAsyncProducesFramesAndUpdatesStats()
    {
        var format = new AudioFormat
        {
            SampleRate = 1_000,
            Channels = 1,
            BitsPerSample = 16,
            FrameDurationMs = 10
        };
        var pipeline = new AudioStreamPipeline(
            format,
            new StreamBufferConfig { MaxBufferedFrames = 8 },
            new StreamRobustnessConfig
            {
                StartupPrebufferFrames = 2,
                TargetBufferedFrames = 2,
                MaxLateFrameToleranceFrames = 2,
                ConcealMissingFramesWithSilence = true
            });
        var source = new GeneratedSignalTestSource(
            format,
            new GeneratedSignalTestModeConfig
            {
                Enabled = true,
                SignalFrequencyHz = 100
            },
            pipeline);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(35));

        await source.RunAsync(cancellation.Token);

        var session = source.GetSessionSnapshot();
        var stats = source.GetStatisticsSnapshot();
        Assert.Equal(StreamSessionState.Stopped, session.State);
        Assert.Equal(1, session.ConnectionCount);
        Assert.True(stats.FramesReceived > 0);
        Assert.Equal(stats.FramesReceived, stats.AcceptedFrames);
        Assert.Equal(stats.FramesReceived * format.BytesPerFrame, stats.BytesReceived);
    }
}
