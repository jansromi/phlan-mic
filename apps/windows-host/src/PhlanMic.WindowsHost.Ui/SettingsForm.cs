using System.Drawing;
using System.Text;
using System.Windows.Forms;
using PhlanMic.Host.Core;

namespace PhlanMic.WindowsHost.Ui;

internal sealed class SettingsForm : Form
{
    private readonly HostConfigStore configStore;
    private readonly HostOutputDeviceCatalog outputCatalog;
    private readonly string configPath;
    private readonly Label warningLabel;
    private readonly Label validationLabel;
    private readonly ComboBox logLevelComboBox;
    private readonly ComboBox logFormatComboBox;
    private readonly TextBox sessionNameTextBox;
    private readonly TextBox bindAddressTextBox;
    private readonly ComboBox transportModeComboBox;
    private readonly NumericUpDown portNumeric;
    private readonly CheckBox autoAudioPortCheckBox;
    private readonly NumericUpDown audioPortNumeric;
    private readonly ComboBox payloadCodecComboBox;
    private readonly NumericUpDown keepAliveNumeric;
    private readonly NumericUpDown sessionTimeoutNumeric;
    private readonly ComboBox outputModeComboBox;
    private readonly ComboBox waveOutDeviceComboBox;
    private readonly ComboBox vbCableEndpointComboBox;
    private readonly NumericUpDown outputLatencyNumeric;
    private readonly CheckBox logAvailableDevicesCheckBox;
    private readonly CheckBox logEndpointInventoryCheckBox;
    private readonly NumericUpDown maxBufferedFramesNumeric;
    private readonly CheckBox dropOldestWhenFullCheckBox;
    private readonly NumericUpDown startupPrebufferNumeric;
    private readonly NumericUpDown targetBufferedNumeric;
    private readonly NumericUpDown maxLateToleranceNumeric;
    private readonly NumericUpDown missingFrameGraceNumeric;
    private readonly CheckBox concealMissingFramesCheckBox;
    private readonly NumericUpDown sampleRateNumeric;
    private readonly NumericUpDown channelsNumeric;
    private readonly ComboBox bitsPerSampleComboBox;
    private readonly NumericUpDown frameDurationNumeric;
    private readonly CheckBox testModeEnabledCheckBox;
    private readonly NumericUpDown signalFrequencyNumeric;
    private readonly Button saveButton;

    public SettingsForm(
        HostConfigStore configStore,
        HostOutputDeviceCatalog outputCatalog,
        string configPath,
        HostRuntimeConfig rawConfig,
        IReadOnlyList<HostConfigEnvironmentOverride> environmentOverrides)
    {
        this.configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        this.outputCatalog = outputCatalog ?? throw new ArgumentNullException(nameof(outputCatalog));
        this.configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));

        Text = "Host Settings";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(880, 760);
        Size = new Size(980, 840);

        warningLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkGoldenrod,
            MaximumSize = new Size(860, 0),
            Visible = environmentOverrides.Count > 0,
            Text = BuildOverrideWarning(environmentOverrides)
        };

        validationLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Firebrick,
            MaximumSize = new Size(860, 0)
        };

        sessionNameTextBox = CreateTextBox();
        logLevelComboBox = CreateComboBox(nameof(StructuredLogLevel.Information), nameof(StructuredLogLevel.Warning), nameof(StructuredLogLevel.Error), nameof(StructuredLogLevel.Debug), nameof(StructuredLogLevel.Trace));
        logFormatComboBox = CreateComboBox(nameof(StructuredConsoleLogFormat.Text), nameof(StructuredConsoleLogFormat.Json));
        bindAddressTextBox = CreateTextBox();
        transportModeComboBox = CreateComboBox(ReceiverConfig.DebugTcpRawPcmTransportMode, ReceiverConfig.UdpRawPcmTransportMode);
        portNumeric = CreateNumericUpDown(1, 65_535);
        autoAudioPortCheckBox = new CheckBox { AutoSize = true, Text = "Auto (port + 1)" };
        audioPortNumeric = CreateNumericUpDown(1, 65_535);
        payloadCodecComboBox = CreateComboBox("RawPcm16");
        keepAliveNumeric = CreateNumericUpDown(1, 60_000);
        sessionTimeoutNumeric = CreateNumericUpDown(1, 120_000);
        outputModeComboBox = CreateComboBox(OutputConfig.VbCableMode, OutputConfig.VbCableToneProbeMode, OutputConfig.DebugDrainMode, OutputConfig.WaveOutMode);
        waveOutDeviceComboBox = CreateComboBox();
        vbCableEndpointComboBox = CreateComboBox();
        outputLatencyNumeric = CreateNumericUpDown(1, 10_000);
        logAvailableDevicesCheckBox = new CheckBox { AutoSize = true, Text = "Log available WaveOut devices" };
        logEndpointInventoryCheckBox = new CheckBox { AutoSize = true, Text = "Log Core Audio endpoint inventory" };
        maxBufferedFramesNumeric = CreateNumericUpDown(1, 512);
        dropOldestWhenFullCheckBox = new CheckBox { AutoSize = true, Text = "Drop oldest frame when buffer is full" };
        startupPrebufferNumeric = CreateNumericUpDown(1, 512);
        targetBufferedNumeric = CreateNumericUpDown(1, 512);
        maxLateToleranceNumeric = CreateNumericUpDown(0, 512);
        missingFrameGraceNumeric = CreateNumericUpDown(0, 60_000);
        concealMissingFramesCheckBox = new CheckBox { AutoSize = true, Text = "Insert silence for missing frames" };
        sampleRateNumeric = CreateNumericUpDown(1, 384_000);
        channelsNumeric = CreateNumericUpDown(1, 8);
        bitsPerSampleComboBox = CreateComboBox("16");
        frameDurationNumeric = CreateNumericUpDown(1, 1_000);
        testModeEnabledCheckBox = new CheckBox { AutoSize = true, Text = "Enable local tone input (bypasses receiver)" };
        signalFrequencyNumeric = CreateNumericUpDown(1, 50_000);
        saveButton = new Button { AutoSize = true, Text = "Save" };
        var cancelButton = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };

        PopulateWaveOutDevices();
        PopulateVbCableEndpoints();
        ApplyDraft(HostSettingsDraft.FromConfig(rawConfig));

        autoAudioPortCheckBox.CheckedChanged += (_, _) => RefreshControlState();
        transportModeComboBox.SelectedIndexChanged += (_, _) => RefreshControlState();
        outputModeComboBox.SelectedIndexChanged += (_, _) => RefreshControlState();
        testModeEnabledCheckBox.CheckedChanged += (_, _) => RefreshControlState();
        saveButton.Click += (_, _) => SaveAndClose();

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(BuildLayout(cancelButton));
        RefreshControlState();
    }

    private Control BuildLayout(Button cancelButton)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var headerPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        headerPanel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(860, 0),
            Text = $"Editing: {configPath}"
        });
        if (warningLabel.Visible)
        {
            headerPanel.Controls.Add(warningLabel);
        }

        root.Controls.Add(headerPanel, 0, 0);
        root.Controls.Add(BuildTabs(), 0, 1);
        root.Controls.Add(validationLabel, 0, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(saveButton);
        root.Controls.Add(buttonPanel, 0, 3);

        return root;
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };
        tabs.TabPages.Add(CreateTabPage("General", BuildGeneralPage()));
        tabs.TabPages.Add(CreateTabPage("Receiver", BuildReceiverPage()));
        tabs.TabPages.Add(CreateTabPage("Output", BuildOutputPage()));
        tabs.TabPages.Add(CreateTabPage("Buffer && Robustness", BuildBufferRobustnessPage()));
        tabs.TabPages.Add(CreateTabPage("Audio && Test", BuildAudioTestPage()));
        return tabs;
    }

    private Control BuildGeneralPage()
    {
        var layout = CreatePageTable();
        var row = 0;
        AddRow(layout, ref row, "Session Name", sessionNameTextBox);
        AddRow(layout, ref row, "Log Level", logLevelComboBox);
        AddRow(layout, ref row, "Log Format", logFormatComboBox);
        return WrapScrollable(layout);
    }

    private Control BuildReceiverPage()
    {
        var layout = CreatePageTable();
        var row = 0;
        AddRow(layout, ref row, "Bind Address", bindAddressTextBox);
        AddRow(layout, ref row, "Transport Mode", transportModeComboBox);
        AddRow(layout, ref row, "Control Port", portNumeric);
        AddRow(layout, ref row, "Audio Port", CreateInlineRow(autoAudioPortCheckBox, audioPortNumeric));
        AddRow(layout, ref row, "Payload Codec", payloadCodecComboBox);
        AddRow(layout, ref row, "Keep Alive (ms)", keepAliveNumeric);
        AddRow(layout, ref row, "Session Timeout (ms)", sessionTimeoutNumeric);
        return WrapScrollable(layout);
    }

    private Control BuildOutputPage()
    {
        var layout = CreatePageTable();
        var row = 0;
        AddRow(layout, ref row, "Output Mode", outputModeComboBox);
        AddRow(layout, ref row, "WaveOut Device", waveOutDeviceComboBox);
        AddRow(layout, ref row, "VB-CABLE Render Endpoint", vbCableEndpointComboBox);
        AddRow(layout, ref row, "Target Latency (ms)", outputLatencyNumeric);
        AddRow(layout, ref row, "Device Logging", CreateInlineRow(logAvailableDevicesCheckBox, logEndpointInventoryCheckBox));
        return WrapScrollable(layout);
    }

    private Control BuildBufferRobustnessPage()
    {
        var layout = CreatePageTable();
        var row = 0;
        AddRow(layout, ref row, "Max Buffered Frames", maxBufferedFramesNumeric);
        AddRow(layout, ref row, "Buffer Policy", dropOldestWhenFullCheckBox);
        AddRow(layout, ref row, "Startup Prebuffer Frames", startupPrebufferNumeric);
        AddRow(layout, ref row, "Target Buffered Frames", targetBufferedNumeric);
        AddRow(layout, ref row, "Max Late Frame Tolerance", maxLateToleranceNumeric);
        AddRow(layout, ref row, "Missing Frame Grace (ms)", missingFrameGraceNumeric);
        AddRow(layout, ref row, "Missing Frame Handling", concealMissingFramesCheckBox);
        return WrapScrollable(layout);
    }

    private Control BuildAudioTestPage()
    {
        var layout = CreatePageTable();
        var row = 0;
        AddRow(layout, ref row, "Sample Rate", sampleRateNumeric);
        AddRow(layout, ref row, "Channels", channelsNumeric);
        AddRow(layout, ref row, "Bits Per Sample", bitsPerSampleComboBox);
        AddRow(layout, ref row, "Frame Duration (ms)", frameDurationNumeric);
        AddRow(layout, ref row, "Local Tone Input", testModeEnabledCheckBox);
        AddRow(layout, ref row, "Tone Frequency (Hz)", signalFrequencyNumeric);
        return WrapScrollable(layout);
    }

    private void PopulateWaveOutDevices()
    {
        waveOutDeviceComboBox.Items.Clear();
        waveOutDeviceComboBox.Items.Add(new ComboBoxOption<int>(-1, "System Default (WAVE_MAPPER)"));

        foreach (var device in outputCatalog.GetWaveOutDevices())
        {
            var display = $"{device.DeviceId}: {device.Name} ({device.Channels} ch, driver {device.DriverVersion})";
            waveOutDeviceComboBox.Items.Add(new ComboBoxOption<int>(device.DeviceId, display));
        }
    }

    private void PopulateVbCableEndpoints()
    {
        vbCableEndpointComboBox.Items.Clear();
        vbCableEndpointComboBox.Items.Add(new ComboBoxOption<string?>(null, "Auto-detect"));

        foreach (var endpoint in outputCatalog.GetVbCableRenderEndpoints())
        {
            var details = new List<string>();
            if (endpoint.IsActive)
            {
                details.Add("active");
            }
            else
            {
                details.Add("inactive");
            }

            if (endpoint.IsDefaultConsole)
            {
                details.Add("default console");
            }

            if (endpoint.IsDefaultMultimedia)
            {
                details.Add("default multimedia");
            }

            if (endpoint.IsDefaultCommunications)
            {
                details.Add("default comms");
            }

            var suffix = details.Count == 0 ? string.Empty : $" [{string.Join(", ", details)}]";
            vbCableEndpointComboBox.Items.Add(new ComboBoxOption<string?>(endpoint.EndpointId, endpoint.FriendlyName + suffix));
        }
    }

    private void ApplyDraft(HostSettingsDraft draft)
    {
        sessionNameTextBox.Text = draft.SessionName;
        logLevelComboBox.SelectedItem = draft.LogLevel;
        logFormatComboBox.SelectedItem = draft.LogFormat;
        bindAddressTextBox.Text = draft.ReceiverBindAddress;
        transportModeComboBox.SelectedItem = draft.ReceiverTransportMode;
        portNumeric.Value = draft.ReceiverPort;
        autoAudioPortCheckBox.Checked = draft.UseAutomaticAudioPort;
        audioPortNumeric.Value = draft.ReceiverAudioPort;
        payloadCodecComboBox.SelectedItem = draft.ReceiverPayloadCodec;
        keepAliveNumeric.Value = draft.ReceiverKeepAliveIntervalMs;
        sessionTimeoutNumeric.Value = draft.ReceiverSessionTimeoutMs;
        maxBufferedFramesNumeric.Value = draft.BufferMaxBufferedFrames;
        dropOldestWhenFullCheckBox.Checked = draft.BufferDropOldestWhenFull;
        startupPrebufferNumeric.Value = draft.RobustnessStartupPrebufferFrames;
        targetBufferedNumeric.Value = draft.RobustnessTargetBufferedFrames;
        maxLateToleranceNumeric.Value = draft.RobustnessMaxLateFrameToleranceFrames;
        missingFrameGraceNumeric.Value = draft.RobustnessMissingFrameGraceMs;
        concealMissingFramesCheckBox.Checked = draft.RobustnessConcealMissingFramesWithSilence;
        sampleRateNumeric.Value = draft.AudioSampleRate;
        channelsNumeric.Value = draft.AudioChannels;
        bitsPerSampleComboBox.SelectedItem = draft.AudioBitsPerSample.ToString();
        frameDurationNumeric.Value = draft.AudioFrameDurationMs;
        testModeEnabledCheckBox.Checked = draft.TestModeEnabled;
        signalFrequencyNumeric.Value = draft.TestModeSignalFrequencyHz;
        outputModeComboBox.SelectedItem = draft.OutputMode;
        SelectComboBoxValue(waveOutDeviceComboBox, draft.OutputDeviceId);
        SelectComboBoxValue(vbCableEndpointComboBox, draft.OutputEndpointId);
        outputLatencyNumeric.Value = draft.OutputTargetLatencyMs;
        logAvailableDevicesCheckBox.Checked = draft.OutputLogAvailableDevices;
        logEndpointInventoryCheckBox.Checked = draft.OutputLogEndpointInventory;
    }

    private HostSettingsDraft CaptureDraft()
    {
        var selectedWaveOut = waveOutDeviceComboBox.SelectedItem as ComboBoxOption<int>;
        var selectedEndpoint = vbCableEndpointComboBox.SelectedItem as ComboBoxOption<string?>;

        return new HostSettingsDraft
        {
            SessionName = sessionNameTextBox.Text,
            LogLevel = (string)(logLevelComboBox.SelectedItem ?? nameof(StructuredLogLevel.Information)),
            LogFormat = (string)(logFormatComboBox.SelectedItem ?? nameof(StructuredConsoleLogFormat.Text)),
            ReceiverBindAddress = bindAddressTextBox.Text,
            ReceiverTransportMode = (string)(transportModeComboBox.SelectedItem ?? ReceiverConfig.DebugTcpRawPcmTransportMode),
            ReceiverPort = DecimalToInt(portNumeric.Value),
            UseAutomaticAudioPort = autoAudioPortCheckBox.Checked,
            ReceiverAudioPort = DecimalToInt(audioPortNumeric.Value),
            ReceiverPayloadCodec = (string)(payloadCodecComboBox.SelectedItem ?? "RawPcm16"),
            ReceiverKeepAliveIntervalMs = DecimalToInt(keepAliveNumeric.Value),
            ReceiverSessionTimeoutMs = DecimalToInt(sessionTimeoutNumeric.Value),
            BufferMaxBufferedFrames = DecimalToInt(maxBufferedFramesNumeric.Value),
            BufferDropOldestWhenFull = dropOldestWhenFullCheckBox.Checked,
            RobustnessStartupPrebufferFrames = DecimalToInt(startupPrebufferNumeric.Value),
            RobustnessTargetBufferedFrames = DecimalToInt(targetBufferedNumeric.Value),
            RobustnessMaxLateFrameToleranceFrames = DecimalToInt(maxLateToleranceNumeric.Value),
            RobustnessMissingFrameGraceMs = DecimalToInt(missingFrameGraceNumeric.Value),
            RobustnessConcealMissingFramesWithSilence = concealMissingFramesCheckBox.Checked,
            AudioSampleRate = DecimalToInt(sampleRateNumeric.Value),
            AudioChannels = DecimalToInt(channelsNumeric.Value),
            AudioBitsPerSample = int.Parse((string)(bitsPerSampleComboBox.SelectedItem ?? "16"), System.Globalization.CultureInfo.InvariantCulture),
            AudioFrameDurationMs = DecimalToInt(frameDurationNumeric.Value),
            TestModeEnabled = testModeEnabledCheckBox.Checked,
            TestModeSignalFrequencyHz = DecimalToInt(signalFrequencyNumeric.Value),
            OutputMode = (string)(outputModeComboBox.SelectedItem ?? OutputConfig.VbCableMode),
            OutputDeviceId = selectedWaveOut?.Value ?? -1,
            OutputEndpointId = selectedEndpoint?.Value,
            OutputTargetLatencyMs = DecimalToInt(outputLatencyNumeric.Value),
            OutputLogAvailableDevices = logAvailableDevicesCheckBox.Checked,
            OutputLogEndpointInventory = logEndpointInventoryCheckBox.Checked
        };
    }

    private void RefreshControlState()
    {
        var transportMode = (string?)transportModeComboBox.SelectedItem ?? ReceiverConfig.DebugTcpRawPcmTransportMode;
        var outputMode = (string?)outputModeComboBox.SelectedItem ?? OutputConfig.VbCableMode;
        var localToneProbeMode = OutputConfig.UsesLocalToneProbe(outputMode);

        audioPortNumeric.Enabled = string.Equals(transportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase) &&
            !autoAudioPortCheckBox.Checked;
        autoAudioPortCheckBox.Enabled = string.Equals(transportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase);

        waveOutDeviceComboBox.Enabled = OutputConfig.UsesWaveOutDevice(outputMode);
        vbCableEndpointComboBox.Enabled = OutputConfig.UsesVbCableEndpoint(outputMode);
        if (localToneProbeMode)
        {
            testModeEnabledCheckBox.Checked = true;
        }

        testModeEnabledCheckBox.Enabled = !localToneProbeMode;
        signalFrequencyNumeric.Enabled = localToneProbeMode || testModeEnabledCheckBox.Checked;
    }

    private void SaveAndClose()
    {
        validationLabel.Text = string.Empty;
        saveButton.Enabled = false;

        try
        {
            var config = CaptureDraft().ToConfig();
            configStore.SaveRaw(configPath, config);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            validationLabel.Text = exception.Message;
        }
        finally
        {
            saveButton.Enabled = true;
        }
    }

    private static TabPage CreateTabPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private static TableLayoutPanel CreatePageTable()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return layout;
    }

    private static Panel WrapScrollable(Control content)
    {
        var panel = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill
        };
        panel.Controls.Add(content);
        return panel;
    }

    private static Control CreateInlineRow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        foreach (var control in controls)
        {
            panel.Controls.Add(control);
        }

        return panel;
    }

    private static void AddRow(TableLayoutPanel layout, ref int row, string labelText, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 12, 6),
            Text = labelText + ":"
        }, 0, row);
        control.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(control, 1, row);
        row++;
    }

    private static TextBox CreateTextBox() =>
        new()
        {
            Width = 280
        };

    private static ComboBox CreateComboBox(params string[] items)
    {
        var comboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320
        };
        comboBox.Items.AddRange(items);
        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }

        return comboBox;
    }

    private static NumericUpDown CreateNumericUpDown(decimal minimum, decimal maximum) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Width = 140,
            ThousandsSeparator = true
        };

    private static int DecimalToInt(decimal value) => decimal.ToInt32(value);

    private static void SelectComboBoxValue<TValue>(ComboBox comboBox, TValue value)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxOption<TValue> option &&
                EqualityComparer<TValue>.Default.Equals(option.Value, value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static string BuildOverrideWarning(IReadOnlyList<HostConfigEnvironmentOverride> overrides)
    {
        var builder = new StringBuilder();
        builder.Append("Active PHLANMIC environment overrides will still win at runtime. ");
        builder.Append("Saved file values may not match the live host until those overrides are cleared.");

        if (overrides.Count > 0)
        {
            builder.Append(" Active overrides: ");
            builder.Append(string.Join(", ", overrides.Select(overrideValue => overrideValue.Suffix)));
        }

        return builder.ToString();
    }

    private sealed record ComboBoxOption<TValue>(TValue Value, string Display)
    {
        public override string ToString() => Display;
    }
}
