using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

internal sealed class WindowsHostApp
{
    private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(5);
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
        var inputSource = CreateInputSource(pipeline);
        var drain = new DebugPipelineDrain(pipeline);
        inputSource.SessionChanged += (_, snapshot) => LogSessionSnapshot(snapshot);

        logger.Info("host_ready", "Windows host foundation is ready.", new Dictionary<string, object?>
        {
            ["sessionName"] = config.SessionName,
            ["bindAddress"] = config.Receiver.BindAddress,
            ["port"] = config.Receiver.Port,
            ["transportMode"] = config.Receiver.TransportMode,
            ["inputSource"] = inputSource.GetType().Name,
            ["audioFormat"] = $"{config.AudioFormat.SampleRate}Hz/{config.AudioFormat.Channels}ch/{config.AudioFormat.BitsPerSample}bit/{config.AudioFormat.FrameDurationMs}ms",
            ["bufferedFrames"] = pipeline.BufferedFrameCount,
            ["bufferCapacity"] = config.Buffer.MaxBufferedFrames,
            ["generatedSignalTestMode"] = config.TestMode.Enabled
        });

        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = inputSource.RunAsync(runtimeCancellation.Token);
        var drainTask = drain.RunAsync(runtimeCancellation.Token);
        var statsTask = LogStatsLoopAsync(inputSource, drain, runtimeCancellation.Token);

        try
        {
            await inputTask;
        }
        finally
        {
            if (!runtimeCancellation.IsCancellationRequested)
            {
                runtimeCancellation.Cancel();
            }
        }

        await Task.WhenAll(drainTask, statsTask);
        logger.Info("host_shutdown", "Host shutdown requested.");
    }

    private IAudioInputSource CreateInputSource(AudioStreamPipeline pipeline) =>
        config.TestMode.Enabled
            ? new GeneratedSignalTestSource(config.AudioFormat, config.TestMode, pipeline)
            : new DebugTcpRawPcmReceiver(config.Receiver, config.AudioFormat, pipeline);

    private async Task LogStatsLoopAsync(
        IAudioInputSource inputSource,
        DebugPipelineDrain drain,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StatsLogInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var stats = inputSource.GetStatisticsSnapshot();
                var drainSnapshot = drain.GetSnapshot();
                logger.Info("stream_stats", "Stream statistics updated.", new Dictionary<string, object?>
                {
                    ["bytesReceived"] = stats.BytesReceived,
                    ["packetsReceived"] = stats.PacketsReceived,
                    ["framesReceived"] = stats.FramesReceived,
                    ["acceptedFrames"] = stats.AcceptedFrames,
                    ["rejectedFrames"] = stats.RejectedFrames,
                    ["droppedFrames"] = stats.DroppedFrames,
                    ["bufferedFrames"] = stats.BufferedFrameCount,
                    ["lastActivityUtc"] = stats.LastActivityUtc,
                    ["drainedFrames"] = drainSnapshot.DrainedFrames,
                    ["drainedBytes"] = drainSnapshot.DrainedBytes,
                    ["lastDrainedSequenceNumber"] = drainSnapshot.LastSequenceNumber,
                    ["lastFrameCapturedAtUtc"] = drainSnapshot.LastFrameCapturedAtUtc,
                    ["lastFrameDrainedAtUtc"] = drainSnapshot.LastDrainedAtUtc
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void LogSessionSnapshot(StreamSessionSnapshot snapshot)
    {
        var properties = new Dictionary<string, object?>
        {
            ["state"] = snapshot.State.ToString(),
            ["transportMode"] = snapshot.TransportMode,
            ["localEndpoint"] = snapshot.LocalEndpoint,
            ["remoteEndpoint"] = snapshot.RemoteEndpoint,
            ["connectionId"] = snapshot.ConnectionId,
            ["connectionCount"] = snapshot.ConnectionCount,
            ["disconnectCount"] = snapshot.DisconnectCount,
            ["startedAtUtc"] = snapshot.StartedAtUtc,
            ["connectedAtUtc"] = snapshot.ConnectedAtUtc,
            ["lastActivityUtc"] = snapshot.LastActivityUtc,
            ["lastDisconnectedAtUtc"] = snapshot.LastDisconnectedAtUtc,
            ["detail"] = snapshot.StatusDetail
        };

        if (snapshot.State is StreamSessionState.Faulted)
        {
            logger.Warning("stream_session_faulted", "Stream session faulted.", properties);
            return;
        }

        logger.Info("stream_session_changed", "Stream session updated.", properties);
    }
}
