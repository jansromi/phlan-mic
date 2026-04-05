using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost;

public sealed class WindowsHostRuntime : IAsyncDisposable
{
    private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(5);
    private readonly object syncRoot = new();
    private readonly StructuredConsoleLogger logger;
    private readonly HostRuntimeConfig config;
    private readonly ManualConnectSnapshot manualConnect;
    private Task? backgroundTask;
    private CancellationTokenSource? backgroundCancellation;
    private StreamRobustnessState? lastRobustnessState;
    private WindowsHostRuntimeSnapshot snapshot;

    public WindowsHostRuntime(StructuredConsoleLogger logger, HostRuntimeConfig config)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        manualConnect = LocalNetworkInfoProvider.CreateSnapshot(config);
        snapshot = BuildSnapshot(
            new HostReadinessSnapshot(
                HostReadinessState.Stopped,
                IsReady: false,
                IsStreaming: false,
                Summary: "Host is stopped.",
                Detail: "Start the host to validate output devices and begin listening for the iPhone client.",
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            CreateInitialSessionSnapshot(),
            CreateInitialStatisticsSnapshot(),
            AudioOutputSnapshot.Empty(config.Output.Mode),
            fault: null);
    }

    public event EventHandler<WindowsHostRuntimeSnapshot>? SnapshotChanged;

    public WindowsHostRuntimeSnapshot Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return snapshot;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? startupCompletion;
        WindowsHostRuntimeSnapshot? nextSnapshot = null;
        CancellationToken backgroundCancellationToken = cancellationToken;

        lock (syncRoot)
        {
            if (backgroundTask is { IsCompleted: false })
            {
                logger.Debug("host_runtime_start_ignored", "Start was requested while the runtime was already active.", new Dictionary<string, object?>
                {
                    ["readinessState"] = snapshot.Readiness.State.ToString(),
                    ["sessionState"] = snapshot.Session.State.ToString()
                });
                return;
            }

            backgroundCancellation?.Dispose();
            backgroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            backgroundCancellationToken = backgroundCancellation.Token;
            startupCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lastRobustnessState = null;
            logger.Info("host_runtime_start_requested", "Starting the Windows host runtime.", new Dictionary<string, object?>
            {
                ["sessionName"] = config.SessionName,
                ["transportMode"] = config.Receiver.TransportMode,
                ["outputMode"] = config.Output.Mode,
                ["configSessionName"] = config.SessionName
            });
            nextSnapshot = BuildSnapshot(
                new HostReadinessSnapshot(
                    HostReadinessState.Starting,
                    IsReady: false,
                    IsStreaming: false,
                    Summary: "Starting host runtime.",
                    Detail: "Validating transport and output configuration.",
                    UpdatedAtUtc: DateTimeOffset.UtcNow),
                snapshot.Session,
                snapshot.Statistics,
                AudioOutputSnapshot.Empty(config.Output.Mode),
                fault: null);
        }

        PublishSnapshot(nextSnapshot);
        lock (syncRoot)
        {
            if (backgroundTask is null)
            {
                backgroundTask = Task.Run(
                    () => RunHostLoopAsync(backgroundCancellationToken, startupCompletion, rethrowFaults: false),
                    CancellationToken.None);
            }
        }
        _ = await startupCompletion.Task.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? taskToWait;
        WindowsHostRuntimeSnapshot? nextSnapshot = null;

        lock (syncRoot)
        {
            taskToWait = backgroundTask;
            if (taskToWait is null || taskToWait.IsCompleted)
            {
                logger.Debug("host_runtime_stop_noop", "Stop was requested while the runtime was already stopped.", new Dictionary<string, object?>
                {
                    ["readinessState"] = snapshot.Readiness.State.ToString()
                });
                nextSnapshot = BuildSnapshot(
                    new HostReadinessSnapshot(
                        HostReadinessState.Stopped,
                        IsReady: false,
                        IsStreaming: false,
                        Summary: "Host is stopped.",
                        Detail: null,
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
                    CreateInitialSessionSnapshot(),
                    CreateInitialStatisticsSnapshot(),
                    AudioOutputSnapshot.Empty(config.Output.Mode),
                    fault: null);
            }
            else
            {
                logger.Info("host_runtime_stop_requested", "Stopping the Windows host runtime.", new Dictionary<string, object?>
                {
                    ["sessionState"] = snapshot.Session.State.ToString(),
                    ["connectionCount"] = snapshot.Session.ConnectionCount,
                    ["disconnectCount"] = snapshot.Session.DisconnectCount
                });
                nextSnapshot = BuildSnapshot(
                    new HostReadinessSnapshot(
                        HostReadinessState.Stopping,
                        IsReady: false,
                        IsStreaming: false,
                        Summary: "Stopping host runtime.",
                        Detail: "Waiting for the receiver and output pipeline to shut down.",
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
                    snapshot.Session,
                    snapshot.Statistics,
                    snapshot.AudioOutput,
                    snapshot.Fault);

                backgroundCancellation?.Cancel();
            }
        }

        PublishSnapshot(nextSnapshot);

        if (taskToWait is null || taskToWait.IsCompleted)
        {
            return;
        }

        await taskToWait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RunUntilStoppedAsync(CancellationToken cancellationToken) =>
        RunHostLoopAsync(cancellationToken, startupCompletion: null, rethrowFaults: true);

    public async ValueTask DisposeAsync()
    {
        if (backgroundTask is { IsCompleted: false })
        {
            await StopAsync().ConfigureAwait(false);
        }

        backgroundCancellation?.Dispose();
    }

    private async Task RunHostLoopAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? startupCompletion,
        bool rethrowFaults)
    {
        IAudioInputSource? inputSource = null;
        IAudioOutputSink? outputSink = null;
        AudioStreamPipeline? pipeline = null;
        var started = false;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                logger.Warning("platform_check", "The host targets Windows and should be executed on a Windows machine.");
            }

            pipeline = new AudioStreamPipeline(config.AudioFormat, config.Buffer, config.Robustness);
            inputSource = CreateInputSource(pipeline);
            outputSink = CreateOutputSink(pipeline);
            inputSource.SessionChanged += (_, sessionSnapshot) => OnSessionChanged(sessionSnapshot, inputSource, outputSink!);

            var initialOutputSnapshot = outputSink.GetSnapshot();
            var initialStatistics = inputSource.GetStatisticsSnapshot();
            var initialSession = inputSource.GetSessionSnapshot();

            PublishSnapshot(MapRuntimeSnapshot(initialSession, initialStatistics, initialOutputSnapshot, fault: null));
            LogHostReady(inputSource, initialOutputSnapshot, pipeline);

            started = true;
            startupCompletion?.TrySetResult(true);

            using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var inputTask = inputSource.RunAsync(runtimeCancellation.Token);
            var outputTask = outputSink.RunAsync(runtimeCancellation.Token);
            var statsTask = LogStatsLoopAsync(inputSource, outputSink, runtimeCancellation.Token);

            try
            {
                var completedTask = await Task.WhenAny(inputTask, outputTask, statsTask).ConfigureAwait(false);
                await completedTask.ConfigureAwait(false);
            }
            finally
            {
                if (!runtimeCancellation.IsCancellationRequested)
                {
                    runtimeCancellation.Cancel();
                }
            }

            await Task.WhenAll(inputTask, outputTask, statsTask).ConfigureAwait(false);
            LogOutputSummary("shutdown", inputSource, outputSink);
            logger.Info("host_shutdown", "Host shutdown requested.");
            PublishSnapshot(BuildSnapshot(
                new HostReadinessSnapshot(
                    HostReadinessState.Stopped,
                    IsReady: false,
                    IsStreaming: false,
                    Summary: "Host is stopped.",
                    Detail: "The receiver is no longer listening for connections.",
                    UpdatedAtUtc: DateTimeOffset.UtcNow),
                inputSource.GetSessionSnapshot() with { State = StreamSessionState.Stopped },
                inputSource.GetStatisticsSnapshot(),
                outputSink.GetSnapshot(),
                fault: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startupCompletion?.TrySetCanceled(cancellationToken);
            PublishSnapshot(BuildSnapshot(
                new HostReadinessSnapshot(
                    HostReadinessState.Stopped,
                    IsReady: false,
                    IsStreaming: false,
                    Summary: "Host is stopped.",
                    Detail: "Cancellation was requested.",
                    UpdatedAtUtc: DateTimeOffset.UtcNow),
                inputSource?.GetSessionSnapshot() ?? CreateInitialSessionSnapshot(),
                inputSource?.GetStatisticsSnapshot() ?? CreateInitialStatisticsSnapshot(),
                outputSink?.GetSnapshot() ?? AudioOutputSnapshot.Empty(config.Output.Mode),
                fault: null));
        }
        catch (Exception exception)
        {
            var fault = new HostFaultSnapshot(
                started ? "Host faulted while running." : "Host failed during startup.",
                exception.Message,
                exception.GetType().Name,
                DateTimeOffset.UtcNow);

            logger.Error(
                started ? "host_runtime_faulted" : "host_startup_faulted",
                fault.Summary,
                exception,
                new Dictionary<string, object?>
                {
                    ["outputMode"] = config.Output.Mode,
                    ["transportMode"] = config.Receiver.TransportMode,
                    ["sessionName"] = config.SessionName
                });

            if (started && inputSource is not null && outputSink is not null)
            {
                LogOutputSummary("faulted", inputSource, outputSink);
            }

            PublishSnapshot(BuildSnapshot(
                new HostReadinessSnapshot(
                    HostReadinessState.Faulted,
                    IsReady: false,
                    IsStreaming: false,
                    Summary: started ? "Host faulted." : "Host could not start.",
                    Detail: exception.Message,
                    UpdatedAtUtc: DateTimeOffset.UtcNow),
                inputSource?.GetSessionSnapshot() ?? CreateInitialSessionSnapshot(),
                inputSource?.GetStatisticsSnapshot() ?? CreateInitialStatisticsSnapshot(),
                outputSink?.GetSnapshot() ?? AudioOutputSnapshot.Empty(config.Output.Mode),
                fault));

            startupCompletion?.TrySetResult(false);

            if (rethrowFaults)
            {
                throw;
            }
        }
        finally
        {
            outputSink?.Dispose();

            if (!rethrowFaults)
            {
                lock (syncRoot)
                {
                    backgroundTask = null;
                }
            }
        }
    }

    private IAudioInputSource CreateInputSource(AudioStreamPipeline pipeline) =>
        config.TestMode.Enabled
            ? new GeneratedSignalTestSource(config.AudioFormat, config.TestMode, pipeline)
            : CreateReceiverInputSource(pipeline);

    private IAudioInputSource CreateReceiverInputSource(AudioStreamPipeline pipeline)
    {
        if (string.Equals(config.Receiver.TransportMode, ReceiverConfig.DebugTcpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            return new DebugTcpRawPcmReceiver(config.Receiver, config.AudioFormat, pipeline);
        }

        if (string.Equals(config.Receiver.TransportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            return new UdpRawPcmReceiver(config.Receiver, config.AudioFormat, pipeline);
        }

        throw new InvalidOperationException(
            $"Receiver transport mode '{config.Receiver.TransportMode}' is not supported by WindowsHostRuntime.");
    }

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
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var stats = inputSource.GetStatisticsSnapshot();
                var outputSnapshot = outputSink.GetSnapshot();
                var robustness = stats.Robustness;
                logger.Info("stream_stats", "Stream statistics updated.", new Dictionary<string, object?>
                {
                    ["bytesReceived"] = stats.BytesReceived,
                    ["packetsReceived"] = stats.PacketsReceived,
                    ["framesReceived"] = stats.FramesReceived,
                    ["acceptedFrames"] = stats.AcceptedFrames,
                    ["rejectedFrames"] = stats.RejectedFrames,
                    ["droppedFrames"] = stats.DroppedFrames,
                    ["controlMessagesReceived"] = stats.Transport.ControlMessagesReceived,
                    ["controlMessagesSent"] = stats.Transport.ControlMessagesSent,
                    ["controlTimeoutCount"] = stats.Transport.ControlTimeoutCount,
                    ["protocolErrorCount"] = stats.Transport.ProtocolErrorCount,
                    ["audioPacketsRejected"] = stats.Transport.AudioPacketsRejected,
                    ["duplicatePackets"] = stats.Transport.DuplicatePackets,
                    ["outOfOrderPackets"] = stats.Transport.OutOfOrderPackets,
                    ["decodeFailureCount"] = stats.Transport.DecodeFailureCount,
                    ["bufferedFrames"] = stats.BufferedFrameCount,
                    ["streamRobustnessState"] = robustness.State.ToString(),
                    ["expectedNextSequence"] = robustness.ExpectedNextSequence,
                    ["highestReceivedSequence"] = robustness.HighestReceivedSequence,
                    ["sequenceGapsObserved"] = robustness.SequenceGapsObserved,
                    ["lateFramesArrived"] = robustness.LateFramesArrived,
                    ["lateFramesDropped"] = robustness.LateFramesDropped,
                    ["missingFramesDetected"] = robustness.MissingFramesDetected,
                    ["hostSilenceFramesInserted"] = robustness.SilenceFramesInserted,
                    ["currentPrebufferDepth"] = robustness.CurrentPrebufferDepth,
                    ["largestObservedGap"] = robustness.LargestObservedGap,
                    ["startupPrebufferFrames"] = robustness.StartupPrebufferFrames,
                    ["targetBufferedFrames"] = robustness.TargetBufferedFrames,
                    ["maxLateFrameToleranceFrames"] = robustness.MaxLateFrameToleranceFrames,
                    ["missingFrameGraceMs"] = robustness.MissingFrameGraceMs,
                    ["hostEstimatedBufferLatencyMs"] = robustness.EstimatedBufferLatencyMs,
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

                PublishSnapshot(MapRuntimeSnapshot(inputSource.GetSessionSnapshot(), stats, outputSnapshot, fault: snapshot.Fault));

                if (lastRobustnessState != robustness.State)
                {
                    var properties = new Dictionary<string, object?>
                    {
                        ["previousState"] = lastRobustnessState?.ToString(),
                        ["state"] = robustness.State.ToString(),
                        ["bufferedFrames"] = stats.BufferedFrameCount,
                        ["expectedNextSequence"] = robustness.ExpectedNextSequence,
                        ["sequenceGapsObserved"] = robustness.SequenceGapsObserved,
                        ["lateFramesDropped"] = robustness.LateFramesDropped,
                        ["missingFramesDetected"] = robustness.MissingFramesDetected,
                        ["hostSilenceFramesInserted"] = robustness.SilenceFramesInserted
                    };

                    if (robustness.State is StreamRobustnessState.Degraded)
                    {
                        logger.Warning("stream_buffer_degraded", "Host stream robustness entered a degraded state.", properties);
                    }
                    else
                    {
                        logger.Info("stream_buffer_state_changed", "Host stream robustness state changed.", properties);
                    }

                    lastRobustnessState = robustness.State;
                }

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

    private void OnSessionChanged(
        StreamSessionSnapshot sessionSnapshot,
        IAudioInputSource inputSource,
        IAudioOutputSink outputSink)
    {
        LogSessionSnapshot(sessionSnapshot, inputSource, outputSink);
        PublishSnapshot(MapRuntimeSnapshot(
            sessionSnapshot,
            inputSource.GetStatisticsSnapshot(),
            outputSink.GetSnapshot(),
            snapshot.Fault));
    }

    private WindowsHostRuntimeSnapshot MapRuntimeSnapshot(
        StreamSessionSnapshot sessionSnapshot,
        StreamStatisticsSnapshot statisticsSnapshot,
        AudioOutputSnapshot outputSnapshot,
        HostFaultSnapshot? fault)
    {
        var readiness = MapReadiness(sessionSnapshot, fault);
        return BuildSnapshot(readiness, sessionSnapshot, statisticsSnapshot, outputSnapshot, fault);
    }

    private WindowsHostRuntimeSnapshot BuildSnapshot(
        HostReadinessSnapshot readiness,
        StreamSessionSnapshot sessionSnapshot,
        StreamStatisticsSnapshot statisticsSnapshot,
        AudioOutputSnapshot outputSnapshot,
        HostFaultSnapshot? fault)
    {
        return new WindowsHostRuntimeSnapshot(
            readiness,
            manualConnect,
            BuildOutputReadiness(readiness, outputSnapshot, fault),
            sessionSnapshot,
            statisticsSnapshot,
            outputSnapshot,
            fault,
            BuildDiagnostics(readiness, sessionSnapshot, statisticsSnapshot, outputSnapshot, fault));
    }

    private OutputReadinessSnapshot BuildOutputReadiness(
        HostReadinessSnapshot readiness,
        AudioOutputSnapshot outputSnapshot,
        HostFaultSnapshot? fault)
    {
        if (fault is not null)
        {
            return new OutputReadinessSnapshot(
                OutputReadinessState.Error,
                IsReady: false,
                Summary: $"Output is not ready in {config.Output.Mode} mode.",
                Detail: fault.Detail,
                Mode: config.Output.Mode,
                DeviceName: outputSnapshot.DeviceName,
                EndpointId: outputSnapshot.EndpointId,
                PairedCaptureEndpointName: outputSnapshot.PairedCaptureEndpointName);
        }

        if (readiness.State is HostReadinessState.Starting or HostReadinessState.Stopped)
        {
            return new OutputReadinessSnapshot(
                OutputReadinessState.Unknown,
                IsReady: false,
                Summary: $"Output mode is configured as {config.Output.Mode}.",
                Detail: "Start the host to validate the selected device and sink.",
                Mode: config.Output.Mode,
                DeviceName: outputSnapshot.DeviceName,
                EndpointId: outputSnapshot.EndpointId,
                PairedCaptureEndpointName: outputSnapshot.PairedCaptureEndpointName);
        }

        var summary = string.Equals(config.Output.Mode, OutputConfig.VbCableMode, StringComparison.OrdinalIgnoreCase)
            ? $"VB-CABLE render '{outputSnapshot.DeviceName ?? "Unknown"}' -> capture '{outputSnapshot.PairedCaptureEndpointName ?? "Unknown"}'."
            : string.Equals(config.Output.Mode, OutputConfig.DebugDrainMode, StringComparison.OrdinalIgnoreCase)
                ? "Debug drain output is active."
                : $"Playback device '{outputSnapshot.DeviceName ?? "System Default"}' is ready.";

        return new OutputReadinessSnapshot(
            OutputReadinessState.Ready,
            IsReady: true,
            Summary: summary,
            Detail: $"Sink={outputSnapshot.SinkKind}, Format={outputSnapshot.OutputFormat}, TargetLatencyMs={config.Output.TargetLatencyMs}",
            Mode: config.Output.Mode,
            DeviceName: outputSnapshot.DeviceName,
            EndpointId: outputSnapshot.EndpointId,
            PairedCaptureEndpointName: outputSnapshot.PairedCaptureEndpointName);
    }

    private IReadOnlyList<HostDiagnosticItem> BuildDiagnostics(
        HostReadinessSnapshot readiness,
        StreamSessionSnapshot sessionSnapshot,
        StreamStatisticsSnapshot statisticsSnapshot,
        AudioOutputSnapshot outputSnapshot,
        HostFaultSnapshot? fault)
    {
        var diagnostics = new List<HostDiagnosticItem>();

        if (fault is not null)
        {
            diagnostics.Add(new HostDiagnosticItem(HostDiagnosticSeverity.Error, "Startup / Runtime Fault", fault.Detail ?? fault.Summary));
        }

        if (manualConnect.RecommendedIpv4Address is null)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Warning,
                "Local Network",
                "No active LAN IPv4 address was detected. The iPhone client may not be able to reach this host until networking is available."));
        }
        else
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Information,
                "Manual Connect",
                $"Use {manualConnect.ConnectionHost}:{manualConnect.ControlPort} from the iPhone client for {manualConnect.TransportMode} mode."));
        }

        if (readiness.State is HostReadinessState.Ready or HostReadinessState.Streaming)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Information,
                "Output",
                BuildOutputReadiness(readiness, outputSnapshot, fault).Summary));
        }

        if (sessionSnapshot.State is StreamSessionState.Disconnected && sessionSnapshot.DisconnectCount > 0)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Warning,
                "Session",
                $"The client disconnected {sessionSnapshot.DisconnectCount} time(s). Waiting for a reconnect."));
        }
        else if (sessionSnapshot.State is StreamSessionState.Connected)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Information,
                "Session",
                "The client connected. Waiting for live audio frames."));
        }
        else if (sessionSnapshot.State is StreamSessionState.Streaming)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Information,
                "Session",
                $"Streaming with {statisticsSnapshot.BufferedFrameCount} frame(s) buffered in the host pipeline."));
        }

        if (statisticsSnapshot.DroppedFrames > 0 || statisticsSnapshot.RejectedFrames > 0)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Warning,
                "Input Buffer",
                $"Dropped={statisticsSnapshot.DroppedFrames}, Rejected={statisticsSnapshot.RejectedFrames}. The host input queue has seen overruns."));
        }

        if (outputSnapshot.UnderrunCount > 0)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Warning,
                "Playback Underruns",
                $"Underruns={outputSnapshot.UnderrunCount}, SilenceInserted={outputSnapshot.SilenceFramesInserted}, EstimatedLatencyMs={outputSnapshot.EstimatedLatencyMs:F1}."));
        }

        if (statisticsSnapshot.Robustness.State is StreamRobustnessState.Degraded)
        {
            diagnostics.Add(new HostDiagnosticItem(
                HostDiagnosticSeverity.Warning,
                "Stream Robustness",
                $"Missing={statisticsSnapshot.Robustness.MissingFramesDetected}, LateDropped={statisticsSnapshot.Robustness.LateFramesDropped}, LargestGap={statisticsSnapshot.Robustness.LargestObservedGap}."));
        }

        return diagnostics;
    }

    private HostReadinessSnapshot MapReadiness(StreamSessionSnapshot sessionSnapshot, HostFaultSnapshot? fault)
    {
        if (fault is not null)
        {
            return new HostReadinessSnapshot(
                HostReadinessState.Faulted,
                IsReady: false,
                IsStreaming: false,
                Summary: "Host faulted.",
                Detail: fault.Detail,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
        }

        return sessionSnapshot.State switch
        {
            StreamSessionState.Streaming => new HostReadinessSnapshot(
                HostReadinessState.Streaming,
                IsReady: true,
                IsStreaming: true,
                Summary: "Streaming audio from the iPhone client.",
                Detail: sessionSnapshot.RemoteEndpoint,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            StreamSessionState.Connected => new HostReadinessSnapshot(
                HostReadinessState.Ready,
                IsReady: true,
                IsStreaming: false,
                Summary: "Client connected. Waiting for audio frames.",
                Detail: sessionSnapshot.RemoteEndpoint,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            StreamSessionState.Disconnected => new HostReadinessSnapshot(
                HostReadinessState.Ready,
                IsReady: true,
                IsStreaming: false,
                Summary: "Client disconnected. Host is still ready.",
                Detail: sessionSnapshot.StatusDetail,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            StreamSessionState.Listening => new HostReadinessSnapshot(
                HostReadinessState.Ready,
                IsReady: true,
                IsStreaming: false,
                Summary: "Ready for the iPhone client.",
                Detail: sessionSnapshot.LocalEndpoint,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            StreamSessionState.Faulted => new HostReadinessSnapshot(
                HostReadinessState.Faulted,
                IsReady: false,
                IsStreaming: false,
                Summary: "Input session faulted.",
                Detail: sessionSnapshot.StatusDetail,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            _ => new HostReadinessSnapshot(
                HostReadinessState.Starting,
                IsReady: false,
                IsStreaming: false,
                Summary: "Starting host runtime.",
                Detail: sessionSnapshot.StatusDetail,
                UpdatedAtUtc: DateTimeOffset.UtcNow)
        };
    }

    private void LogHostReady(IAudioInputSource inputSource, AudioOutputSnapshot outputSnapshot, AudioStreamPipeline pipeline)
    {
        logger.Info("host_ready", "Windows host foundation is ready.", new Dictionary<string, object?>
        {
            ["sessionName"] = config.SessionName,
            ["bindAddress"] = config.Receiver.BindAddress,
            ["controlPort"] = config.Receiver.Port,
            ["audioPort"] = string.Equals(config.Receiver.TransportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase)
                ? config.Receiver.GetResolvedAudioPort()
                : null,
            ["transportMode"] = config.Receiver.TransportMode,
            ["payloadCodec"] = config.Receiver.PayloadCodec,
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
            ["startupPrebufferFrames"] = config.Robustness.StartupPrebufferFrames,
            ["targetBufferedFrames"] = config.Robustness.TargetBufferedFrames,
            ["maxLateFrameToleranceFrames"] = config.Robustness.MaxLateFrameToleranceFrames,
            ["missingFrameGraceMs"] = config.Robustness.MissingFrameGraceMs,
            ["concealMissingFramesWithSilence"] = config.Robustness.ConcealMissingFramesWithSilence,
            ["generatedSignalTestMode"] = config.TestMode.Enabled,
            ["recommendedIpv4Address"] = manualConnect.RecommendedIpv4Address,
            ["availableIpv4Addresses"] = manualConnect.AvailableIpv4Addresses
        });
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
            ["controlMessagesReceived"] = inputStats.Transport.ControlMessagesReceived,
            ["controlMessagesSent"] = inputStats.Transport.ControlMessagesSent,
            ["controlTimeoutCount"] = inputStats.Transport.ControlTimeoutCount,
            ["protocolErrorCount"] = inputStats.Transport.ProtocolErrorCount,
            ["audioPacketsRejected"] = inputStats.Transport.AudioPacketsRejected,
            ["duplicatePackets"] = inputStats.Transport.DuplicatePackets,
            ["outOfOrderPackets"] = inputStats.Transport.OutOfOrderPackets,
            ["decodeFailureCount"] = inputStats.Transport.DecodeFailureCount,
            ["bufferedFrames"] = inputStats.BufferedFrameCount,
            ["streamRobustnessState"] = inputStats.Robustness.State.ToString(),
            ["expectedNextSequence"] = inputStats.Robustness.ExpectedNextSequence,
            ["highestReceivedSequence"] = inputStats.Robustness.HighestReceivedSequence,
            ["sequenceGapsObserved"] = inputStats.Robustness.SequenceGapsObserved,
            ["lateFramesArrived"] = inputStats.Robustness.LateFramesArrived,
            ["lateFramesDropped"] = inputStats.Robustness.LateFramesDropped,
            ["missingFramesDetected"] = inputStats.Robustness.MissingFramesDetected,
            ["hostSilenceFramesInserted"] = inputStats.Robustness.SilenceFramesInserted,
            ["currentPrebufferDepth"] = inputStats.Robustness.CurrentPrebufferDepth,
            ["largestObservedGap"] = inputStats.Robustness.LargestObservedGap,
            ["startupPrebufferFrames"] = inputStats.Robustness.StartupPrebufferFrames,
            ["targetBufferedFrames"] = inputStats.Robustness.TargetBufferedFrames,
            ["maxLateFrameToleranceFrames"] = inputStats.Robustness.MaxLateFrameToleranceFrames,
            ["missingFrameGraceMs"] = inputStats.Robustness.MissingFrameGraceMs,
            ["hostEstimatedBufferLatencyMs"] = inputStats.Robustness.EstimatedBufferLatencyMs,
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

    private StreamSessionSnapshot CreateInitialSessionSnapshot() => new(
        StreamSessionState.Stopped,
        config.Receiver.TransportMode,
        null,
        null,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        null);

    private StreamStatisticsSnapshot CreateInitialStatisticsSnapshot() => new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        new StreamRobustnessSnapshot(
            StreamRobustnessState.Buffering,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            config.Robustness.StartupPrebufferFrames,
            config.Robustness.TargetBufferedFrames,
            config.Robustness.MaxLateFrameToleranceFrames,
            config.Robustness.MissingFrameGraceMs,
            0),
        TransportStatisticsSnapshot.Empty);

    private void PublishSnapshot(WindowsHostRuntimeSnapshot? nextSnapshot)
    {
        if (nextSnapshot is null)
        {
            return;
        }

        WindowsHostRuntimeSnapshot previousSnapshot;
        WindowsHostRuntimeSnapshot publishedSnapshot;
        EventHandler<WindowsHostRuntimeSnapshot>? handler;

        lock (syncRoot)
        {
            previousSnapshot = snapshot;
            snapshot = nextSnapshot;
            publishedSnapshot = snapshot;
            handler = SnapshotChanged;
        }

        LogSnapshotTransition(previousSnapshot, publishedSnapshot);
        handler?.Invoke(this, publishedSnapshot);
    }

    private void LogSnapshotTransition(
        WindowsHostRuntimeSnapshot previousSnapshot,
        WindowsHostRuntimeSnapshot nextSnapshot)
    {
        if (previousSnapshot.Readiness.State != nextSnapshot.Readiness.State ||
            !string.Equals(previousSnapshot.Readiness.Summary, nextSnapshot.Readiness.Summary, StringComparison.Ordinal))
        {
            logger.Info("host_readiness_changed", "Host readiness changed.", new Dictionary<string, object?>
            {
                ["previousState"] = previousSnapshot.Readiness.State.ToString(),
                ["state"] = nextSnapshot.Readiness.State.ToString(),
                ["summary"] = nextSnapshot.Readiness.Summary,
                ["detail"] = nextSnapshot.Readiness.Detail
            });
        }

        if (previousSnapshot.Output.State != nextSnapshot.Output.State ||
            !string.Equals(previousSnapshot.Output.DeviceName, nextSnapshot.Output.DeviceName, StringComparison.Ordinal) ||
            !string.Equals(previousSnapshot.Output.EndpointId, nextSnapshot.Output.EndpointId, StringComparison.Ordinal))
        {
            logger.Info("host_output_readiness_changed", "Output readiness changed.", new Dictionary<string, object?>
            {
                ["previousState"] = previousSnapshot.Output.State.ToString(),
                ["state"] = nextSnapshot.Output.State.ToString(),
                ["mode"] = nextSnapshot.Output.Mode,
                ["deviceName"] = nextSnapshot.Output.DeviceName,
                ["endpointId"] = nextSnapshot.Output.EndpointId,
                ["captureEndpointName"] = nextSnapshot.Output.PairedCaptureEndpointName,
                ["summary"] = nextSnapshot.Output.Summary
            });
        }

        if (previousSnapshot.Diagnostics.Count != nextSnapshot.Diagnostics.Count)
        {
            logger.Debug("host_diagnostics_changed", "Diagnostics count changed.", new Dictionary<string, object?>
            {
                ["previousCount"] = previousSnapshot.Diagnostics.Count,
                ["count"] = nextSnapshot.Diagnostics.Count,
                ["sessionState"] = nextSnapshot.Session.State.ToString(),
                ["readinessState"] = nextSnapshot.Readiness.State.ToString()
            });
        }

        if ((previousSnapshot.Fault is null) != (nextSnapshot.Fault is null) ||
            !string.Equals(previousSnapshot.Fault?.Detail, nextSnapshot.Fault?.Detail, StringComparison.Ordinal))
        {
            logger.Debug("host_fault_snapshot_changed", "Fault snapshot changed.", new Dictionary<string, object?>
            {
                ["hadFault"] = previousSnapshot.Fault is not null,
                ["hasFault"] = nextSnapshot.Fault is not null,
                ["summary"] = nextSnapshot.Fault?.Summary,
                ["detail"] = nextSnapshot.Fault?.Detail
            });
        }
    }
}
