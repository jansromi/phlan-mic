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
        using var outputSink = CreateOutputSink(pipeline);
        var outputSnapshot = outputSink.GetSnapshot();
        inputSource.SessionChanged += (_, snapshot) => LogSessionSnapshot(snapshot);

        logger.Info("host_ready", "Windows host foundation is ready.", new Dictionary<string, object?>
        {
            ["sessionName"] = config.SessionName,
            ["bindAddress"] = config.Receiver.BindAddress,
            ["port"] = config.Receiver.Port,
            ["transportMode"] = config.Receiver.TransportMode,
            ["inputSource"] = inputSource.GetType().Name,
            ["outputMode"] = config.Output.Mode,
            ["outputSink"] = outputSnapshot.SinkKind,
            ["outputDeviceId"] = outputSnapshot.DeviceId,
            ["outputDeviceName"] = outputSnapshot.DeviceName,
            ["outputFormat"] = outputSnapshot.OutputFormat,
            ["formatConversionActive"] = outputSnapshot.FormatConversionActive,
            ["outputBufferCount"] = outputSnapshot.BufferCount,
            ["outputTargetLatencyMs"] = config.Output.TargetLatencyMs,
            ["audioFormat"] = $"{config.AudioFormat.SampleRate}Hz/{config.AudioFormat.Channels}ch/{config.AudioFormat.BitsPerSample}bit/{config.AudioFormat.FrameDurationMs}ms",
            ["bufferedFrames"] = pipeline.BufferedFrameCount,
            ["bufferCapacity"] = config.Buffer.MaxBufferedFrames,
            ["generatedSignalTestMode"] = config.TestMode.Enabled
        });

        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputTask = inputSource.RunAsync(runtimeCancellation.Token);
        var outputTask = outputSink.RunAsync(runtimeCancellation.Token);
        var statsTask = LogStatsLoopAsync(inputSource, outputSink, runtimeCancellation.Token);

        try
        {
            var completedTask = await Task.WhenAny(inputTask, outputTask, statsTask);
            await completedTask;
        }
        finally
        {
            if (!runtimeCancellation.IsCancellationRequested)
            {
                runtimeCancellation.Cancel();
            }
        }

        await Task.WhenAll(inputTask, outputTask, statsTask);
        logger.Info("host_shutdown", "Host shutdown requested.");
    }

    private IAudioInputSource CreateInputSource(AudioStreamPipeline pipeline) =>
        config.TestMode.Enabled
            ? new GeneratedSignalTestSource(config.AudioFormat, config.TestMode, pipeline)
            : new DebugTcpRawPcmReceiver(config.Receiver, config.AudioFormat, pipeline);

    private IAudioOutputSink CreateOutputSink(AudioStreamPipeline pipeline)
    {
        if (string.Equals(config.Output.Mode, OutputConfig.DebugDrainMode, StringComparison.OrdinalIgnoreCase))
        {
            return new DebugPipelineDrain(pipeline);
        }

        return new WaveOutPlaybackSink(logger, pipeline, config.AudioFormat, config.Output);
    }

    private async Task LogStatsLoopAsync(
        IAudioInputSource inputSource,
        IAudioOutputSink outputSink,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StatsLogInterval);
        long lastDroppedFrames = 0;
        long lastRejectedFrames = 0;
        long lastUnderrunCount = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var stats = inputSource.GetStatisticsSnapshot();
                var outputSnapshot = outputSink.GetSnapshot();
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
                    ["outputSink"] = outputSnapshot.SinkKind,
                    ["outputDeviceId"] = outputSnapshot.DeviceId,
                    ["outputDeviceName"] = outputSnapshot.DeviceName,
                    ["outputFormat"] = outputSnapshot.OutputFormat,
                    ["outputBufferCount"] = outputSnapshot.BufferCount,
                    ["outputBufferedFrames"] = outputSnapshot.BufferedFrames,
                    ["outputSubmittedFrames"] = outputSnapshot.SubmittedFrames,
                    ["outputCompletedFrames"] = outputSnapshot.CompletedFrames,
                    ["outputCompletedBytes"] = outputSnapshot.CompletedBytes,
                    ["silenceFramesInserted"] = outputSnapshot.SilenceFramesInserted,
                    ["underrunCount"] = outputSnapshot.UnderrunCount,
                    ["estimatedLatencyMs"] = outputSnapshot.EstimatedLatencyMs,
                    ["glitchRatePerMinute"] = outputSnapshot.GlitchRatePerMinute,
                    ["lastOutputSequenceNumber"] = outputSnapshot.LastSequenceNumber,
                    ["lastFrameCapturedAtUtc"] = outputSnapshot.LastFrameCapturedAtUtc,
                    ["lastFrameSubmittedAtUtc"] = outputSnapshot.LastSubmittedAtUtc,
                    ["lastFrameCompletedAtUtc"] = outputSnapshot.LastCompletedAtUtc
                });

                if (stats.DroppedFrames > lastDroppedFrames || stats.RejectedFrames > lastRejectedFrames)
                {
                    logger.Warning("audio_output_overrun", "Input buffering dropped or rejected frames before playback.", new Dictionary<string, object?>
                    {
                        ["droppedFrames"] = stats.DroppedFrames,
                        ["rejectedFrames"] = stats.RejectedFrames,
                        ["bufferedFrames"] = stats.BufferedFrameCount,
                        ["bufferCapacity"] = config.Buffer.MaxBufferedFrames
                    });
                }

                if (outputSnapshot.UnderrunCount > lastUnderrunCount)
                {
                    logger.Warning("audio_output_underrun", "Playback inserted silence because audio frames were not available in time.", new Dictionary<string, object?>
                    {
                        ["underrunCount"] = outputSnapshot.UnderrunCount,
                        ["silenceFramesInserted"] = outputSnapshot.SilenceFramesInserted,
                        ["estimatedLatencyMs"] = outputSnapshot.EstimatedLatencyMs,
                        ["glitchRatePerMinute"] = outputSnapshot.GlitchRatePerMinute
                    });
                }

                lastDroppedFrames = stats.DroppedFrames;
                lastRejectedFrames = stats.RejectedFrames;
                lastUnderrunCount = outputSnapshot.UnderrunCount;
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
