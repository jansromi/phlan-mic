using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class WindowsHostApp
{
    private readonly StructuredConsoleLogger logger;
    private readonly HostRuntimeConfig config;

    public WindowsHostApp(StructuredConsoleLogger logger, HostRuntimeConfig config)
    {
        this.logger = logger;
        this.config = config;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.Warning("platform_check", "The host targets Windows and should be executed on a Windows machine.");
        }

        var pipeline = new AudioStreamPipeline(config.AudioFormat, config.Buffer);
        logger.Info("host_ready", "Windows host foundation is ready.", new Dictionary<string, object?>
        {
            ["sessionName"] = config.SessionName,
            ["bindAddress"] = config.Receiver.BindAddress,
            ["port"] = config.Receiver.Port,
            ["transportMode"] = config.Receiver.TransportMode,
            ["audioFormat"] = $"{config.AudioFormat.SampleRate}Hz/{config.AudioFormat.Channels}ch/{config.AudioFormat.BitsPerSample}bit/{config.AudioFormat.FrameDurationMs}ms",
            ["bufferedFrames"] = pipeline.BufferedFrameCount,
            ["bufferCapacity"] = config.Buffer.MaxBufferedFrames,
            ["generatedSignalTestMode"] = config.TestMode.Enabled
        });

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.Info("host_shutdown", "Host shutdown requested.");
        }
    }
}

