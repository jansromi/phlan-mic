using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PhlanMic.WindowsHost.Ui;

internal sealed class MainForm : Form
{
    private readonly WindowsHostRuntime runtime;
    private readonly string configPath;
    private readonly Label stateValueLabel;
    private readonly Label summaryValueLabel;
    private readonly Label configPathValueLabel;
    private readonly Button startButton;
    private readonly Button stopButton;
    private readonly Button copyButton;
    private readonly TextBox manualConnectTextBox;
    private readonly TextBox outputTextBox;
    private readonly TextBox sessionTextBox;
    private readonly TextBox diagnosticsTextBox;
    private bool changingRuntimeState;

    public MainForm(WindowsHostRuntime runtime, string configPath)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));

        Text = "PhlanMic Windows Host";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 760);
        Size = new Size(1040, 820);

        stateValueLabel = CreateValueLabel(new Font(Font, FontStyle.Bold));
        summaryValueLabel = CreateValueLabel();
        configPathValueLabel = CreateValueLabel();
        startButton = new Button { AutoSize = true, Text = "Start Host" };
        stopButton = new Button { AutoSize = true, Text = "Stop Host" };
        copyButton = new Button { AutoSize = true, Text = "Copy Manual Connect" };
        manualConnectTextBox = CreateMultilineTextBox();
        outputTextBox = CreateMultilineTextBox();
        sessionTextBox = CreateMultilineTextBox();
        diagnosticsTextBox = CreateMultilineTextBox();

        Controls.Add(BuildLayout());

        startButton.Click += async (_, _) => await StartRuntimeAsync();
        stopButton.Click += async (_, _) => await StopRuntimeAsync();
        copyButton.Click += (_, _) => CopyManualConnect();
        Shown += async (_, _) => await StartRuntimeAsync();

        runtime.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot(runtime.Snapshot);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
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
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        root.Controls.Add(BuildHeaderPanel(), 0, 0);
        root.Controls.Add(BuildSection("Manual Connect", manualConnectTextBox), 0, 1);
        root.Controls.Add(BuildSection("Output Readiness", outputTextBox), 0, 2);
        root.Controls.Add(BuildBottomSections(), 0, 3);

        return root;
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
            await runtime.StartAsync();
        }
        catch (Exception exception)
        {
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
            await runtime.StopAsync();
        }
        catch (Exception exception)
        {
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

    private void CopyManualConnect()
    {
        try
        {
            Clipboard.SetText(runtime.Snapshot.ManualConnect.GetCopyText());
        }
        catch (Exception exception)
        {
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

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplySnapshot(snapshot)));
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(WindowsHostRuntimeSnapshot snapshot)
    {
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

        UpdateButtons(snapshot);
    }

    private void UpdateButtons(WindowsHostRuntimeSnapshot snapshot)
    {
        startButton.Enabled = !changingRuntimeState && snapshot.Readiness.State is HostReadinessState.Stopped or HostReadinessState.Faulted;
        stopButton.Enabled = !changingRuntimeState && snapshot.Readiness.State is HostReadinessState.Starting or HostReadinessState.Ready or HostReadinessState.Streaming;
        copyButton.Enabled = !changingRuntimeState;
    }

    private static string BuildOutputText(WindowsHostRuntimeSnapshot snapshot)
    {
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
            .AppendLine($"Glitch Rate / Min: {snapshot.AudioOutput.GlitchRatePerMinute:F2}");

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
}
