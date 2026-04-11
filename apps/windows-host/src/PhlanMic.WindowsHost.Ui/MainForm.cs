using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost.Ui;

internal sealed class MainForm : Form
{
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromMilliseconds(250);
    private readonly object snapshotGate = new();
    private readonly HostConfigStore configStore;
    private readonly HostOutputDeviceCatalog outputCatalog;
    private readonly StructuredConsoleLogger logger;
    private readonly string configPath;
    private readonly Label stateValueLabel;
    private readonly Label summaryValueLabel;
    private readonly Label configPathValueLabel;
    private readonly Button startButton;
    private readonly Button stopButton;
    private readonly Button settingsButton;
    private readonly Button copyButton;
    private readonly TextBox manualConnectTextBox;
    private readonly TextBox outputTextBox;
    private readonly TextBox sessionTextBox;
    private readonly TextBox diagnosticsTextBox;
    private readonly AudioLevelMeterControl signalMeterControl;
    private readonly System.Windows.Forms.Timer snapshotRefreshTimer;
    private WindowsHostRuntime runtime;
    private WindowsHostRuntimeSnapshot? lastAppliedSnapshot;
    private WindowsHostRuntimeSnapshot? pendingSnapshot;
    private HostRuntimeConfig? pendingEffectiveConfig;
    private bool changingRuntimeState;

    public MainForm(
        HostConfigStore configStore,
        HostOutputDeviceCatalog outputCatalog,
        HostRuntimeConfig initialEffectiveConfig,
        StructuredConsoleLogger logger,
        string configPath)
    {
        this.configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        this.outputCatalog = outputCatalog ?? throw new ArgumentNullException(nameof(outputCatalog));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        runtime = CreateRuntime(initialEffectiveConfig ?? throw new ArgumentNullException(nameof(initialEffectiveConfig)));

        Text = "PhlanMic Windows Host";
        var applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (applicationIcon is not null)
        {
            Icon = applicationIcon;
        }
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 760);
        Size = new Size(1040, 820);

        stateValueLabel = CreateValueLabel(new Font(Font, FontStyle.Bold));
        summaryValueLabel = CreateValueLabel();
        configPathValueLabel = CreateValueLabel();
        startButton = new Button { AutoSize = true, Text = "Start Host" };
        stopButton = new Button { AutoSize = true, Text = "Stop Host" };
        settingsButton = new Button { AutoSize = true, Text = "Settings..." };
        copyButton = new Button { AutoSize = true, Text = "Copy Manual Connect" };
        manualConnectTextBox = CreateMultilineTextBox();
        outputTextBox = CreateMultilineTextBox();
        sessionTextBox = CreateMultilineTextBox();
        diagnosticsTextBox = CreateMultilineTextBox();
        signalMeterControl = new AudioLevelMeterControl
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8)
        };
        snapshotRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)SnapshotRefreshInterval.TotalMilliseconds,
            Enabled = true
        };

        Controls.Add(BuildLayout());

        startButton.Click += async (_, _) => await StartRuntimeAsync();
        stopButton.Click += async (_, _) => await StopRuntimeAsync();
        settingsButton.Click += async (_, _) => await OpenSettingsAsync();
        copyButton.Click += (_, _) => CopyManualConnect();
        Shown += (_, _) =>
        {
            this.logger.Info("ui_window_shown", "The Windows host UI window is visible.", new Dictionary<string, object?>
            {
                ["configPath"] = this.configPath
            });
        };
        snapshotRefreshTimer.Tick += (_, _) => SafeFlushPendingSnapshot();

        runtime.SnapshotChanged += OnSnapshotChanged;
        QueueSnapshot(runtime.Snapshot);
        FlushPendingSnapshot();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        snapshotRefreshTimer.Stop();
        snapshotRefreshTimer.Dispose();
        logger.Info("ui_window_closing", "The Windows host UI window is closing.", new Dictionary<string, object?>
        {
            ["readinessState"] = runtime.Snapshot.Readiness.State.ToString(),
            ["sessionState"] = runtime.Snapshot.Session.State.ToString()
        });
        runtime.SnapshotChanged -= OnSnapshotChanged;
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnFormClosed(e);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        root.Controls.Add(BuildHeaderPanel(), 0, 0);
        root.Controls.Add(BuildTopSections(), 0, 1);
        root.Controls.Add(BuildAudioActivitySection(), 0, 2);
        root.Controls.Add(BuildBottomSections(), 0, 3);

        return root;
    }

    private Control BuildTopSections()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        layout.Controls.Add(BuildSection("Manual Connect", manualConnectTextBox), 0, 0);
        layout.Controls.Add(BuildSection("Output Readiness", outputTextBox), 1, 0);
        return layout;
    }

    private Control BuildHeaderPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 0, 0, 12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var detailsPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill
        };
        detailsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        detailsPanel.Controls.Add(CreateCaptionLabel("State"), 0, 0);
        detailsPanel.Controls.Add(stateValueLabel, 1, 0);
        detailsPanel.Controls.Add(CreateCaptionLabel("Summary"), 0, 1);
        detailsPanel.Controls.Add(summaryValueLabel, 1, 1);
        detailsPanel.Controls.Add(CreateCaptionLabel("Config"), 0, 2);
        detailsPanel.Controls.Add(configPathValueLabel, 1, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight
        };
        buttonPanel.WrapContents = true;
        buttonPanel.Margin = new Padding(0, 8, 0, 0);
        buttonPanel.Controls.Add(startButton);
        buttonPanel.Controls.Add(stopButton);
        buttonPanel.Controls.Add(settingsButton);
        buttonPanel.Controls.Add(copyButton);

        layout.Controls.Add(detailsPanel, 0, 0);
        layout.Controls.Add(buttonPanel, 0, 1);
        return layout;
    }

    private Control BuildBottomSections()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        layout.Controls.Add(BuildSection("Session and Stream", sessionTextBox), 0, 0);
        layout.Controls.Add(BuildSection("Diagnostics", diagnosticsTextBox), 1, 0);
        return layout;
    }

    private Control BuildAudioActivitySection()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(signalMeterControl, 0, 0);
        var section = BuildSection("Audio Activity", layout);
        section.AutoSize = true;
        section.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        return section;
    }

    private static GroupBox BuildSection(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        content.Dock = DockStyle.Fill;
        group.Controls.Add(content);
        return group;
    }

    private static Label CreateCaptionLabel(string text) =>
        new()
        {
            AutoSize = true,
            Text = text + ":",
            ForeColor = Color.DimGray,
            Padding = new Padding(0, 2, 12, 2)
        };

    private static Label CreateValueLabel(Font? font = null) =>
        new()
        {
            AutoSize = true,
            Font = font ?? SystemFonts.MessageBoxFont,
            MaximumSize = new Size(720, 0)
        };

    private static TextBox CreateMultilineTextBox() =>
        new()
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            Font = new Font("Cascadia Mono", 9f, FontStyle.Regular),
            WordWrap = false
        };

    private async Task StartRuntimeAsync()
    {
        if (changingRuntimeState)
        {
            return;
        }

        try
        {
            changingRuntimeState = true;
            UpdateButtons(runtime.Snapshot);
            await ApplyPendingEffectiveConfigIfPossibleAsync();
            logger.Info("ui_start_requested", "Start host was requested from the desktop UI.", new Dictionary<string, object?>
            {
                ["configPath"] = configPath,
                ["currentState"] = runtime.Snapshot.Readiness.State.ToString()
            });
            await runtime.StartAsync();
        }
        catch (Exception exception)
        {
            logger.Error("ui_start_failed", "The desktop UI failed to start the host runtime.", exception, new Dictionary<string, object?>
            {
                ["configPath"] = configPath
            });
            MessageBox.Show(
                $"Failed to start the host.\r\n\r\n{exception.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            changingRuntimeState = false;
            UpdateButtons(runtime.Snapshot);
        }
    }

    private async Task StopRuntimeAsync()
    {
        if (changingRuntimeState)
        {
            return;
        }

        try
        {
            changingRuntimeState = true;
            UpdateButtons(runtime.Snapshot);
            logger.Info("ui_stop_requested", "Stop host was requested from the desktop UI.", new Dictionary<string, object?>
            {
                ["currentState"] = runtime.Snapshot.Readiness.State.ToString(),
                ["connectionCount"] = runtime.Snapshot.Session.ConnectionCount
            });
            await runtime.StopAsync();
            await ApplyPendingEffectiveConfigIfPossibleAsync();
        }
        catch (Exception exception)
        {
            logger.Error("ui_stop_failed", "The desktop UI failed to stop the host runtime cleanly.", exception, new Dictionary<string, object?>
            {
                ["configPath"] = configPath
            });
            MessageBox.Show(
                $"Failed to stop the host cleanly.\r\n\r\n{exception.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            changingRuntimeState = false;
            UpdateButtons(runtime.Snapshot);
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (changingRuntimeState)
        {
            return;
        }

        try
        {
            var rawConfig = configStore.LoadRaw(configPath);
            var environmentOverrides = configStore.GetEnvironmentOverrides();

            using var dialog = new SettingsForm(configStore, outputCatalog, configPath, rawConfig, environmentOverrides);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var effectiveConfig = configStore.LoadEffective(configPath);
            var runtimeIsActive = runtime.Snapshot.Readiness.State is HostReadinessState.Starting or HostReadinessState.Ready or HostReadinessState.Streaming;

            if (runtimeIsActive)
            {
                var response = MessageBox.Show(
                    this,
                    "Settings were saved. Restart the host now to apply them?",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (response == DialogResult.Yes)
                {
                    await RestartRuntimeAsync(effectiveConfig);
                }
                else
                {
                    pendingEffectiveConfig = effectiveConfig;
                }

                return;
            }

            await ReplaceRuntimeAsync(effectiveConfig, startAfterReplace: false);
        }
        catch (Exception exception)
        {
            logger.Error("ui_open_settings_failed", "Opening or applying host settings from the desktop UI failed.", exception, new Dictionary<string, object?>
            {
                ["configPath"] = configPath
            });
            MessageBox.Show(
                $"Failed to apply host settings.\r\n\r\n{exception.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void CopyManualConnect()
    {
        try
        {
            logger.Info("ui_copy_manual_connect", "Copy manual connect details was requested from the desktop UI.", new Dictionary<string, object?>
            {
                ["connectionHost"] = runtime.Snapshot.ManualConnect.ConnectionHost,
                ["controlPort"] = runtime.Snapshot.ManualConnect.ControlPort,
                ["transportMode"] = runtime.Snapshot.ManualConnect.TransportMode
            });
            Clipboard.SetText(runtime.Snapshot.ManualConnect.GetCopyText());
        }
        catch (Exception exception)
        {
            logger.Error("ui_copy_manual_connect_failed", "Failed to copy manual connect details from the desktop UI.", exception);
            MessageBox.Show(
                $"Failed to copy manual connect details.\r\n\r\n{exception.Message}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnSnapshotChanged(object? sender, WindowsHostRuntimeSnapshot snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        QueueSnapshot(snapshot);
    }

    private void QueueSnapshot(WindowsHostRuntimeSnapshot snapshot)
    {
        lock (snapshotGate)
        {
            pendingSnapshot = snapshot;
        }
    }

    private void FlushPendingSnapshot()
    {
        WindowsHostRuntimeSnapshot? snapshot;
        lock (snapshotGate)
        {
            snapshot = pendingSnapshot;
            pendingSnapshot = null;
        }

        if (snapshot is null)
        {
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void SafeFlushPendingSnapshot()
    {
        try
        {
            FlushPendingSnapshot();
        }
        catch (Exception exception)
        {
            logger.Error("ui_snapshot_apply_failed", "Applying a runtime snapshot to the desktop UI failed.", exception, new Dictionary<string, object?>
            {
                ["hasPendingSnapshot"] = pendingSnapshot is not null,
                ["lastAppliedReadinessState"] = lastAppliedSnapshot?.Readiness.State.ToString(),
                ["lastAppliedSessionState"] = lastAppliedSnapshot?.Session.State.ToString()
            });
        }
    }

    private void ApplySnapshot(WindowsHostRuntimeSnapshot snapshot)
    {
        var previousSnapshot = lastAppliedSnapshot;
        stateValueLabel.Text = snapshot.Readiness.State.ToString();
        stateValueLabel.ForeColor = GetStateColor(snapshot.Readiness.State);
        summaryValueLabel.Text = snapshot.Readiness.Detail is null
            ? snapshot.Readiness.Summary
            : $"{snapshot.Readiness.Summary} {snapshot.Readiness.Detail}";
        configPathValueLabel.Text = configPath;

        manualConnectTextBox.Text = snapshot.ManualConnect.GetSummaryText();
        outputTextBox.Text = BuildOutputText(snapshot);
        sessionTextBox.Text = BuildSessionText(snapshot);
        diagnosticsTextBox.Text = BuildDiagnosticsText(snapshot);
        ApplySignalMeter(snapshot);

        if (previousSnapshot is not null &&
            (previousSnapshot.Readiness.State != snapshot.Readiness.State ||
            previousSnapshot.Session.State != snapshot.Session.State))
        {
            logger.Debug("ui_snapshot_applied", "Applied a new runtime snapshot to the desktop UI.", new Dictionary<string, object?>
            {
                ["readinessState"] = snapshot.Readiness.State.ToString(),
                ["sessionState"] = snapshot.Session.State.ToString(),
                ["outputState"] = snapshot.Output.State.ToString(),
                ["diagnosticCount"] = snapshot.Diagnostics.Count
            });
        }

        lastAppliedSnapshot = snapshot;
        UpdateButtons(snapshot);
    }

    private void UpdateButtons(WindowsHostRuntimeSnapshot snapshot)
    {
        startButton.Enabled = !changingRuntimeState && snapshot.Readiness.State is HostReadinessState.Stopped or HostReadinessState.Faulted;
        stopButton.Enabled = !changingRuntimeState && snapshot.Readiness.State is HostReadinessState.Starting or HostReadinessState.Ready or HostReadinessState.Streaming;
        settingsButton.Enabled = !changingRuntimeState;
        copyButton.Enabled = !changingRuntimeState;
    }

    private static string BuildOutputText(WindowsHostRuntimeSnapshot snapshot)
    {
        var signalMeter = snapshot.AudioOutput.SignalMeter;
        var builder = new StringBuilder()
            .AppendLine($"Mode: {snapshot.Output.Mode}")
            .AppendLine($"State: {snapshot.Output.State}")
            .AppendLine($"Ready: {snapshot.Output.IsReady}")
            .AppendLine($"Summary: {snapshot.Output.Summary}");

        if (!string.IsNullOrWhiteSpace(snapshot.Output.Detail))
        {
            builder.AppendLine($"Detail: {snapshot.Output.Detail}");
        }

        builder.AppendLine($"Device: {snapshot.AudioOutput.DeviceName ?? "n/a"}")
            .AppendLine($"Sink: {snapshot.AudioOutput.SinkKind}")
            .AppendLine($"Endpoint Id: {snapshot.AudioOutput.EndpointId ?? "n/a"}")
            .AppendLine($"Capture Pair: {snapshot.AudioOutput.PairedCaptureEndpointName ?? "n/a"}")
            .AppendLine($"Output Format: {snapshot.AudioOutput.OutputFormat}")
            .AppendLine($"Estimated Latency Ms: {snapshot.AudioOutput.EstimatedLatencyMs:F1}")
            .AppendLine($"Underruns: {snapshot.AudioOutput.UnderrunCount}")
            .AppendLine($"Glitch Rate / Min: {snapshot.AudioOutput.GlitchRatePerMinute:F2}")
            .AppendLine($"Signal Peak / RMS: {FormatPercent(signalMeter.DisplayPeakNormalized)} / {FormatPercent(signalMeter.RmsNormalized)}");

        return builder.ToString().TrimEnd();
    }

    private static string BuildSessionText(WindowsHostRuntimeSnapshot snapshot)
    {
        var stats = snapshot.Statistics;
        var robustness = stats.Robustness;
        var builder = new StringBuilder()
            .AppendLine($"Session State: {snapshot.Session.State}")
            .AppendLine($"Transport: {snapshot.Session.TransportMode}")
            .AppendLine($"Local Endpoint: {snapshot.Session.LocalEndpoint ?? "n/a"}")
            .AppendLine($"Remote Endpoint: {snapshot.Session.RemoteEndpoint ?? "n/a"}")
            .AppendLine($"Connections: {snapshot.Session.ConnectionCount}")
            .AppendLine($"Disconnects: {snapshot.Session.DisconnectCount}")
            .AppendLine($"Last Activity: {FormatTimestamp(stats.LastActivityUtc)}")
            .AppendLine($"Bytes Received: {stats.BytesReceived}")
            .AppendLine($"Packets Received: {stats.PacketsReceived}")
            .AppendLine($"Frames Received: {stats.FramesReceived}")
            .AppendLine($"Accepted / Rejected / Dropped: {stats.AcceptedFrames} / {stats.RejectedFrames} / {stats.DroppedFrames}")
            .AppendLine($"Buffered Frames: {stats.BufferedFrameCount}")
            .AppendLine($"Robustness State: {robustness.State}")
            .AppendLine($"Missing / Late Dropped / Silence: {robustness.MissingFramesDetected} / {robustness.LateFramesDropped} / {robustness.SilenceFramesInserted}")
            .AppendLine($"Estimated Buffer Latency Ms: {robustness.EstimatedBufferLatencyMs}")
            .AppendLine($"Output Submitted / Completed: {snapshot.AudioOutput.SubmittedFrames} / {snapshot.AudioOutput.CompletedFrames}")
            .AppendLine($"Last Output Frame: {FormatTimestamp(snapshot.AudioOutput.LastCompletedAtUtc)}");

        if (!string.IsNullOrWhiteSpace(snapshot.Session.StatusDetail))
        {
            builder.AppendLine($"Detail: {snapshot.Session.StatusDetail}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildDiagnosticsText(WindowsHostRuntimeSnapshot snapshot)
    {
        if (snapshot.Diagnostics.Count == 0)
        {
            return "No diagnostics yet.";
        }

        var builder = new StringBuilder();
        foreach (var item in snapshot.Diagnostics)
        {
            builder.Append('[')
                .Append(item.Severity)
                .Append("] ")
                .Append(item.Title)
                .Append(": ")
                .AppendLine(item.Message);
        }

        if (snapshot.Fault is not null)
        {
            builder.AppendLine()
                .AppendLine("Last Fault")
                .AppendLine($"Summary: {snapshot.Fault.Summary}")
                .AppendLine($"Type: {snapshot.Fault.ExceptionType}")
                .AppendLine($"At: {FormatTimestamp(snapshot.Fault.OccurredAtUtc)}");

            if (!string.IsNullOrWhiteSpace(snapshot.Fault.Detail))
            {
                builder.AppendLine($"Detail: {snapshot.Fault.Detail}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private void ApplySignalMeter(WindowsHostRuntimeSnapshot snapshot)
    {
        var signalMeter = snapshot.AudioOutput.SignalMeter;
        signalMeterControl.ApplyLevels(
            signalMeter.RmsNormalized,
            signalMeter.DisplayPeakNormalized,
            signalMeter.SignalDetected,
            signalMeter.ClippedSampleCount > 0);
    }

    private static Color GetStateColor(HostReadinessState state) =>
        state switch
        {
            HostReadinessState.Ready => Color.SeaGreen,
            HostReadinessState.Streaming => Color.DarkGreen,
            HostReadinessState.Starting or HostReadinessState.Stopping => Color.DarkGoldenrod,
            HostReadinessState.Faulted => Color.Firebrick,
            _ => Color.DimGray
        };

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a";

    private static string FormatPercent(double value) => $"{Math.Round(Math.Max(0, Math.Min(1, value)) * 100):0}%";

    private WindowsHostRuntime CreateRuntime(HostRuntimeConfig effectiveConfig)
    {
        ConfigureLogger(effectiveConfig);
        return new WindowsHostRuntime(logger, effectiveConfig);
    }

    private void ConfigureLogger(HostRuntimeConfig effectiveConfig)
    {
        logger.MinimumLevel = StructuredLogLevelParser.Parse(effectiveConfig.LogLevel);
        logger.OutputFormat = StructuredConsoleLogFormatParser.Parse(effectiveConfig.LogFormat);
    }

    private async Task ApplyPendingEffectiveConfigIfPossibleAsync()
    {
        if (pendingEffectiveConfig is null ||
            runtime.Snapshot.Readiness.State is not (HostReadinessState.Stopped or HostReadinessState.Faulted))
        {
            return;
        }

        var effectiveConfig = pendingEffectiveConfig;
        pendingEffectiveConfig = null;
        await ReplaceRuntimeAsync(effectiveConfig, startAfterReplace: false);
    }

    private async Task RestartRuntimeAsync(HostRuntimeConfig effectiveConfig)
    {
        changingRuntimeState = true;
        UpdateButtons(runtime.Snapshot);

        try
        {
            await ReplaceRuntimeAsync(effectiveConfig, startAfterReplace: true);
        }
        finally
        {
            changingRuntimeState = false;
            UpdateButtons(runtime.Snapshot);
        }
    }

    private async Task ReplaceRuntimeAsync(HostRuntimeConfig effectiveConfig, bool startAfterReplace)
    {
        ArgumentNullException.ThrowIfNull(effectiveConfig);

        var previousRuntime = runtime;
        previousRuntime.SnapshotChanged -= OnSnapshotChanged;

        try
        {
            if (previousRuntime.Snapshot.Readiness.State is HostReadinessState.Starting or HostReadinessState.Ready or HostReadinessState.Streaming)
            {
                await previousRuntime.StopAsync();
            }
        }
        finally
        {
            await previousRuntime.DisposeAsync();
        }

        runtime = CreateRuntime(effectiveConfig);
        pendingEffectiveConfig = null;
        runtime.SnapshotChanged += OnSnapshotChanged;
        QueueSnapshot(runtime.Snapshot);
        SafeFlushPendingSnapshot();

        if (startAfterReplace)
        {
            await runtime.StartAsync();
        }
    }
}
