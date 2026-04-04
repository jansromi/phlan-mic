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
        inputSource.SessionChanged += (_, snapshot) => LogSessionSnapshot(snapshot, inputSource, outputSink);

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
            ["outputEndpointId"] = outputSnapshot.EndpointId,
            ["outputCaptureEndpointId"] = outputSnapshot.PairedCaptureEndpointId,
            ["outputCaptureEndpointName"] = outputSnapshot.PairedCaptureEndpointName,
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
        LogOutputSummary("shutdown", inputSource, outputSink);
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

        if (string.Equals(config.Output.Mode, OutputConfig.VbCableMode, StringComparison.OrdinalIgnoreCase))
        {
            return CreateVbCableOutputSink(pipeline);
        }

        return new WaveOutPlaybackSink(logger, pipeline, config.AudioFormat, config.Output);
    }

    private IAudioOutputSink CreateVbCableOutputSink(AudioStreamPipeline pipeline)
    {
        var endpoints = CoreAudioEndpointEnumerator.Enumerate();

        if (config.Output.LogEndpointInventory)
        {
            LogEndpointInventory(endpoints);
        }

        var match = VbCableEndpointMatcher.Match(endpoints, config.Output.EndpointId);
        if (match.Status is not VbCableEndpointMatchStatus.Matched || match.SelectedPair is null)
        {
            throw new InvalidOperationException(BuildVbCableStartupError(match));
        }

        logger.Info("vb_cable_endpoint_selected", "Selected VB-CABLE render/capture pair.", new Dictionary<string, object?>
        {
            ["renderEndpointId"] = match.SelectedPair.RenderEndpoint.Id,
            ["renderEndpointName"] = match.SelectedPair.RenderEndpoint.FriendlyName,
            ["captureEndpointId"] = match.SelectedPair.CaptureEndpoint.Id,
            ["captureEndpointName"] = match.SelectedPair.CaptureEndpoint.FriendlyName,
            ["matchReason"] = match.SelectedPair.MatchReason,
            ["manualOverrideActive"] = config.Output.EndpointId is not null
        });

        return new VbCablePlaybackSink(logger, pipeline, config.AudioFormat, config.Output, match.SelectedPair);
    }

    private void LogEndpointInventory(IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        logger.Info("audio_endpoint_inventory", "Enumerated Windows Core Audio endpoints.", new Dictionary<string, object?>
        {
            ["endpointCount"] = endpoints.Count,
            ["endpoints"] = endpoints.Select(endpoint => new Dictionary<string, object?>
            {
                ["endpointId"] = endpoint.Id,
                ["friendlyName"] = endpoint.FriendlyName,
                ["flow"] = endpoint.Flow.ToString(),
                ["state"] = endpoint.State.ToString(),
                ["isDefaultConsole"] = endpoint.IsDefaultConsole,
                ["isDefaultMultimedia"] = endpoint.IsDefaultMultimedia,
                ["isDefaultCommunications"] = endpoint.IsDefaultCommunications
            }).ToArray()
        });
    }

    private string BuildVbCableStartupError(VbCableEndpointMatchResult match)
    {
        var renderCandidates = FormatEndpoints(match.RenderCandidates);
        var captureCandidates = FormatEndpoints(match.CaptureCandidates);
        var candidatePairs = FormatPairs(match.CandidatePairs);

        return match.Status switch
        {
            VbCableEndpointMatchStatus.NoEndpointsFound =>
                "VB-CABLE was not detected on this machine. Install VB-CABLE, then restart the host. " +
                "Expected a render endpoint such as 'CABLE Input' and a capture endpoint such as 'CABLE Output'.",
            VbCableEndpointMatchStatus.NoUsablePairFound =>
                "VB-CABLE-related endpoints were found, but no active render/capture pair was usable. " +
                "Enable the endpoints in Windows Sound settings, then restart the host. " +
                $"Render candidates: {renderCandidates}. Capture candidates: {captureCandidates}.",
            VbCableEndpointMatchStatus.MultipleUsablePairsFound =>
                "Multiple active VB-CABLE render/capture pairs were found. Set Output.EndpointId to the desired render endpoint id. " +
                $"Candidate pairs: {candidatePairs}.",
            VbCableEndpointMatchStatus.PreferredRenderEndpointNotFound =>
                $"Configured Output.EndpointId '{match.PreferredRenderEndpointId}' was not found among VB-CABLE render endpoints. " +
                $"Render candidates: {renderCandidates}.",
            VbCableEndpointMatchStatus.PreferredRenderEndpointNotUsable =>
                $"Configured Output.EndpointId '{match.PreferredRenderEndpointId}' matches a VB-CABLE render endpoint, but it is not active. " +
                $"Render candidates: {renderCandidates}.",
            VbCableEndpointMatchStatus.PreferredRenderEndpointMissingCapturePair =>
                $"Configured Output.EndpointId '{match.PreferredRenderEndpointId}' matches a VB-CABLE render endpoint, but no active paired capture endpoint was found. " +
                $"Capture candidates: {captureCandidates}.",
            VbCableEndpointMatchStatus.PreferredRenderEndpointAmbiguous =>
                $"Configured Output.EndpointId '{match.PreferredRenderEndpointId}' matched multiple VB-CABLE capture pair candidates. " +
                $"Candidate pairs: {candidatePairs}.",
            _ => "VB-CABLE startup validation failed."
        };
    }

    private static string FormatEndpoints(IReadOnlyList<AudioEndpointInfo> endpoints) =>
        endpoints.Count == 0
            ? "none"
            : string.Join(
                "; ",
                endpoints.Select(endpoint =>
                    $"{endpoint.Flow}:{endpoint.FriendlyName} [{endpoint.State}] ({endpoint.Id})"));

    private static string FormatPairs(IReadOnlyList<VbCableEndpointPair> pairs) =>
        pairs.Count == 0
            ? "none"
            : string.Join(
                "; ",
                pairs.Select(pair =>
                    $"render='{pair.RenderEndpoint.FriendlyName}' ({pair.RenderEndpoint.Id}) -> capture='{pair.CaptureEndpoint.FriendlyName}' ({pair.CaptureEndpoint.Id}) [{pair.MatchReason}]"));

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
                    ["outputEndpointId"] = outputSnapshot.EndpointId,
                    ["outputCaptureEndpointId"] = outputSnapshot.PairedCaptureEndpointId,
                    ["outputCaptureEndpointName"] = outputSnapshot.PairedCaptureEndpointName,
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

    private void LogSessionSnapshot(
        StreamSessionSnapshot snapshot,
        IAudioInputSource inputSource,
        IAudioOutputSink outputSink)
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
            LogOutputSummary("faulted", inputSource, outputSink);
            return;
        }

        logger.Info("stream_session_changed", "Stream session updated.", properties);

        if (snapshot.State is StreamSessionState.Disconnected)
        {
            LogOutputSummary("disconnected", inputSource, outputSink);
        }
    }

    private void LogOutputSummary(
        string scope,
        IAudioInputSource inputSource,
        IAudioOutputSink outputSink)
    {
        var sessionSnapshot = inputSource.GetSessionSnapshot();
        var inputStats = inputSource.GetStatisticsSnapshot();
        var outputStats = outputSink.GetSnapshot();

        logger.Info("audio_output_summary", "Audio output summary captured.", new Dictionary<string, object?>
        {
            ["summaryScope"] = scope,
            ["sessionState"] = sessionSnapshot.State.ToString(),
            ["transportMode"] = sessionSnapshot.TransportMode,
            ["connectionId"] = sessionSnapshot.ConnectionId,
            ["connectionCount"] = sessionSnapshot.ConnectionCount,
            ["disconnectCount"] = sessionSnapshot.DisconnectCount,
            ["startedAtUtc"] = sessionSnapshot.StartedAtUtc,
            ["connectedAtUtc"] = sessionSnapshot.ConnectedAtUtc,
            ["lastActivityUtc"] = sessionSnapshot.LastActivityUtc,
            ["lastDisconnectedAtUtc"] = sessionSnapshot.LastDisconnectedAtUtc,
            ["inputBytesReceived"] = inputStats.BytesReceived,
            ["inputPacketsReceived"] = inputStats.PacketsReceived,
            ["inputFramesReceived"] = inputStats.FramesReceived,
            ["acceptedFrames"] = inputStats.AcceptedFrames,
            ["rejectedFrames"] = inputStats.RejectedFrames,
            ["droppedFrames"] = inputStats.DroppedFrames,
            ["bufferedFrames"] = inputStats.BufferedFrameCount,
            ["outputSink"] = outputStats.SinkKind,
            ["outputDeviceId"] = outputStats.DeviceId,
            ["outputDeviceName"] = outputStats.DeviceName,
            ["outputEndpointId"] = outputStats.EndpointId,
            ["outputCaptureEndpointId"] = outputStats.PairedCaptureEndpointId,
            ["outputCaptureEndpointName"] = outputStats.PairedCaptureEndpointName,
            ["outputFormat"] = outputStats.OutputFormat,
            ["formatConversionActive"] = outputStats.FormatConversionActive,
            ["outputBufferCount"] = outputStats.BufferCount,
            ["outputBufferedFrames"] = outputStats.BufferedFrames,
            ["outputSubmittedFrames"] = outputStats.SubmittedFrames,
            ["outputCompletedFrames"] = outputStats.CompletedFrames,
            ["outputCompletedBytes"] = outputStats.CompletedBytes,
            ["silenceFramesInserted"] = outputStats.SilenceFramesInserted,
            ["underrunCount"] = outputStats.UnderrunCount,
            ["estimatedLatencyMs"] = outputStats.EstimatedLatencyMs,
            ["glitchRatePerMinute"] = outputStats.GlitchRatePerMinute,
            ["lastOutputSequenceNumber"] = outputStats.LastSequenceNumber,
            ["outputStartedAtUtc"] = outputStats.StartedAtUtc,
            ["lastFrameCapturedAtUtc"] = outputStats.LastFrameCapturedAtUtc,
            ["lastFrameSubmittedAtUtc"] = outputStats.LastSubmittedAtUtc,
            ["lastFrameCompletedAtUtc"] = outputStats.LastCompletedAtUtc
        });
    }
}
