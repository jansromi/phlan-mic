import SwiftUI

private enum SessionLayout {
    static let panelWidth: CGFloat = 360
}

private enum AppPalette {
    static let appBackground = Color(uiColor: .systemGroupedBackground)
    static let panelBackground = Color(uiColor: .secondarySystemBackground)
    static let tileBackground = Color(uiColor: .tertiarySystemBackground)
    static let panelBorder = Color(uiColor: .separator).opacity(0.18)
    static let meterLow = Color(.sRGB, red: 0.16, green: 0.55, blue: 0.76)
    static let meterMid = Color(.sRGB, red: 0.29, green: 0.70, blue: 0.40)
    static let statusOrange = Color(.sRGB, red: 0.96, green: 0.52, blue: 0.12)
    static let statusBlue = Color(.sRGB, red: 0.11, green: 0.45, blue: 0.87)
    static let statusYellow = Color(.sRGB, red: 0.95, green: 0.76, blue: 0.06)
    static let statusGreen = Color(.sRGB, red: 0.18, green: 0.68, blue: 0.38)
    static let statusRed = Color(.sRGB, red: 0.86, green: 0.20, blue: 0.18)
    static let slate = Color(.sRGB, red: 0.25, green: 0.35, blue: 0.43)
}

private enum StatusLabelAnimation {
    static let maxDotCount = 3
}

struct ContentView: View {
    @ObservedObject var model: AppModel
    @State private var connectingTextStep = 0

    var body: some View {
        NavigationStack {
            ZStack {
                SessionBackground()

                VStack(spacing: 28) {
                    Spacer(minLength: 24)

                    VStack(spacing: 18) {
                        Button {
                            Task {
                                await model.handlePrimaryMicTap()
                            }
                        } label: {
                            PrimaryMicButton(state: model.primaryMicVisualState)
                        }
                        .buttonStyle(.plain)

                        VStack(spacing: 8) {
                            AnimatedConnectingLabel(
                                text: model.primaryStatusTitle,
                                isConnecting: shouldAnimateConnectingLabel(for: model.primaryStatusTitle),
                                step: connectingTextStep
                            )
                                .font(.system(size: 28, weight: .bold, design: .rounded))
                                .multilineTextAlignment(.center)
                        }
                    }

                    MeterPanel(inputLevel: model.latestInputLevel)
                    SessionHealthPanel(model: model)

                    Spacer()
                }
                .padding(.horizontal, 24)
                .padding(.top, 12)
            }
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Menu {
                        Button("Debug") {
                            model.presentDebug()
                        }
                    } label: {
                        Image(systemName: "line.3.horizontal")
                            .font(.title3.weight(.semibold))
                            .foregroundStyle(.primary)
                            .frame(width: 36, height: 36)
                    }
                }
            }
            .safeAreaInset(edge: .bottom) {
                ConnectionStatusCard(
                    model: model,
                    isConnecting: shouldAnimateConnectingLabel(for: model.connectionCardStatusLabel),
                    connectingStep: connectingTextStep
                )
                    .frame(maxWidth: SessionLayout.panelWidth)
                    .padding(.horizontal, 20)
                    .padding(.bottom, 12)
            }
        }
        .sheet(item: $model.presentedSheet) { sheet in
            NavigationStack {
                switch sheet {
                case .hostSettings:
                    HostSettingsView(model: model)
                case .debug:
                    DebugView(model: model)
                }
            }
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") {
                        model.dismissSheet()
                    }
                }
            }
        }
        .task {
            await model.loadStartupState()
        }
        .task(id: model.transportStatus) {
            if model.transportStatus != .connecting {
                connectingTextStep = 0
                return
            }

            connectingTextStep = 0

            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 450_000_000)
                guard !Task.isCancelled else {
                    return
                }

                connectingTextStep = (connectingTextStep + 1) % (StatusLabelAnimation.maxDotCount + 1)
            }
        }
    }

    private func shouldAnimateConnectingLabel(for text: String) -> Bool {
        model.transportStatus == .connecting && text == AppModel.TransportStatus.connecting.rawValue
    }
}

private struct AnimatedConnectingLabel: View {
    let text: String
    let isConnecting: Bool
    let step: Int

    var body: some View {
        if isConnecting {
            HStack(spacing: 0) {
                Text(AppModel.TransportStatus.connecting.rawValue)

                ZStack(alignment: .leading) {
                    Text(String(repeating: ".", count: StatusLabelAnimation.maxDotCount))
                        .hidden()

                    Text(String(repeating: ".", count: step))
                }
            }
        } else {
            Text(text)
        }
    }
}

private struct SessionBackground: View {
    var body: some View {
        AppPalette.appBackground
            .ignoresSafeArea()
    }
}

private struct PrimaryMicButton: View {
    let state: AppModel.PrimaryMicVisualState

    var body: some View {
        ZStack {
            Circle()
                .fill(baseColor.opacity(0.18))
                .frame(width: 220, height: 220)

            Circle()
                .fill(baseColor)
                .frame(width: 164, height: 164)

            Circle()
                .strokeBorder(Color.white.opacity(0.65), lineWidth: 3)
                .frame(width: 164, height: 164)

            Image(systemName: "mic.fill")
                .font(.system(size: 58, weight: .semibold))
                .foregroundStyle(.white)
        }
        .animation(.easeInOut(duration: 0.2), value: state.tintName)
        .accessibilityLabel("Primary microphone control")
    }

    private var baseColor: Color {
        StatusTint.color(named: state.tintName)
    }
}

private struct MeterPanel: View {
    let inputLevel: AudioInputLevel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Input Meter")
                .font(.headline)

            LevelMeterRow(
                label: "Average",
                value: inputLevel.averageLevel,
                percentage: inputLevel.averagePercentage,
                decibels: inputLevel.averageDecibels
            )

            LevelMeterRow(
                label: "Peak",
                value: inputLevel.peakLevel,
                percentage: inputLevel.peakPercentage,
                decibels: inputLevel.peakDecibels
            )
        }
        .padding(20)
        .frame(maxWidth: SessionLayout.panelWidth)
        .background(AppPalette.panelBackground, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 28, style: .continuous)
                .stroke(AppPalette.panelBorder, lineWidth: 1)
        )
    }
}

private struct LevelMeterRow: View {
    let label: String
    let value: Float
    let percentage: Int
    let decibels: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(label)
                    .font(.subheadline.weight(.semibold))
                Spacer()
                Text("\(percentage)%")
                    .font(.subheadline.monospacedDigit())
                Text("(\(decibels) dBFS)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(Color.primary.opacity(0.09))

                    Capsule()
                        .fill(meterColors.first ?? AppPalette.meterLow)
                        .frame(width: geometry.size.width * CGFloat(max(0, min(value, 1))))
                }
            }
            .frame(height: 12)
        }
    }

    private var meterColors: [Color] {
        if value > 0.8 {
            return [AppPalette.statusOrange, AppPalette.statusRed]
        }

        if value > 0.4 {
            return [AppPalette.statusGreen, AppPalette.statusOrange]
        }

        return [AppPalette.meterMid, AppPalette.meterLow]
    }
}

private struct ConnectionStatusCard: View {
    @ObservedObject var model: AppModel
    let isConnecting: Bool
    let connectingStep: Int

    var body: some View {
        Button {
            model.presentHostSettings()
        } label: {
            HStack(alignment: .top, spacing: 14) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .top, spacing: 8) {
                        HStack(spacing: 8) {
                            Circle()
                                .fill(StatusTint.color(named: model.connectionCardTintName))
                                .frame(width: 10, height: 10)

                            AnimatedConnectingLabel(
                                text: model.connectionCardStatusLabel,
                                isConnecting: isConnecting,
                                step: connectingStep
                            )
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(StatusTint.color(named: model.connectionCardTintName))
                        }

                        Spacer(minLength: 8)

                        Text(model.hostConfiguration.transportMode.label)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 5)
                            .background(AppPalette.tileBackground, in: Capsule())
                    }

                    Text(model.connectionCardTitle)
                        .font(.headline)
                        .foregroundStyle(.primary)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)

                    Text(model.connectionCardDetail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.leading)
                        .lineLimit(2)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Image(systemName: "slider.horizontal.3")
                    .font(.headline.weight(.bold))
                    .foregroundStyle(.secondary)
                    .padding(.top, 2)
            }
            .padding(18)
            .background(AppPalette.panelBackground, in: RoundedRectangle(cornerRadius: 26, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 26, style: .continuous)
                    .stroke(AppPalette.panelBorder, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
    }
}

private struct SessionHealthPanel: View {
    @ObservedObject var model: AppModel
    @State private var isExpanded = true
    @State private var selectedItemID: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Button {
                withAnimation(.easeInOut(duration: 0.2)) {
                    isExpanded.toggle()
                }
            } label: {
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Session Health")
                            .font(.headline)

                        Text(model.sessionHealthSummary)
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }

                    Spacer()

                    Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            if isExpanded {
                HStack(spacing: 12) {
                    ForEach(model.sessionHealthItems) { item in
                        Button {
                            selectedItemID = item.id
                        } label: {
                            SessionHealthTile(item: item)
                        }
                        .buttonStyle(.plain)
                    }
                }

                if let footnote = model.sessionHealthFootnote {
                    Text(footnote)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(20)
        .frame(maxWidth: SessionLayout.panelWidth)
        .background(AppPalette.panelBackground, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 28, style: .continuous)
                .stroke(AppPalette.panelBorder, lineWidth: 1)
        )
        .sheet(item: selectedHealthItemBinding) { item in
            SessionHealthDetailSheet(item: item)
                .presentationDetents([.height(220), .medium])
                .presentationDragIndicator(.visible)
        }
    }

    private var selectedHealthItemBinding: Binding<AppModel.SessionHealthItem?> {
        Binding(
            get: {
                guard let selectedItemID else {
                    return nil
                }

                return model.sessionHealthDetail(for: selectedItemID)
            },
            set: { newValue in
                selectedItemID = newValue?.id
            }
        )
    }
}

private struct SessionHealthTile: View {
    let item: AppModel.SessionHealthItem

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Circle()
                    .fill(StatusTint.color(named: item.tintName))
                    .frame(width: 8, height: 8)

                Text(item.title)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
            }

            Text(item.value)
                .font(.headline.monospacedDigit())
                .foregroundStyle(StatusTint.color(named: item.tintName))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(AppPalette.tileBackground, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }
}

private struct SessionHealthDetailSheet: View {
    let item: AppModel.SessionHealthItem

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 10) {
                Circle()
                    .fill(StatusTint.color(named: item.tintName))
                    .frame(width: 10, height: 10)

                Text(item.detailTitle)
                    .font(.headline)
            }

            Text(item.value)
                .font(.system(size: 28, weight: .bold, design: .rounded).monospacedDigit())
                .foregroundStyle(StatusTint.color(named: item.tintName))

            Text(item.detail)
                .font(.body)
                .foregroundStyle(.secondary)

            Spacer(minLength: 0)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Color(uiColor: .systemBackground))
    }
}

private struct HostSettingsView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Form {
            Section("Connection") {
                HostConfigurationFields(model: model, showsCheckpointButton: false)
            }

            Section("Status") {
                LabeledStatusRow(
                    title: "Setup",
                    value: model.setupStatus.rawValue,
                    tintName: model.setupStatus.tintName
                )

                Text(model.hostSettingsStatusText)
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                Text("Windows control/debug port default: \(HostConfiguration.defaultDebugTcpPort)")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
        .navigationTitle("Host Settings")
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct DebugView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Form {
            Section("Connection") {
                LabeledStatusRow(
                    title: "Setup",
                    value: model.setupStatus.rawValue,
                    tintName: model.setupStatus.tintName
                )

                HostConfigurationFields(model: model, showsCheckpointButton: true)

                LabeledStatusRow(
                    title: "Environment",
                    value: model.startupSnapshot.platformDescription,
                    tintName: model.startupSnapshot.isSimulator ? "orange" : "green"
                )
            }

            Section("Permission") {
                LabeledStatusRow(
                    title: "Microphone",
                    value: model.microphonePermission.label,
                    tintName: model.microphonePermission.tintName
                )

                if model.canRequestMicrophonePermission {
                    Button("Request Permission") {
                        Task {
                            await model.requestMicrophonePermission()
                        }
                    }
                }
            }

            Section("Capture") {
                LabeledStatusRow(
                    title: "Status",
                    value: model.captureStatus.rawValue,
                    tintName: model.captureStatus.tintName
                )

                if model.canStartCapture {
                    Button("Start Capture") {
                        Task {
                            await model.startCapture()
                        }
                    }
                }

                if model.canStopCapture {
                    Button("Stop Capture") {
                        model.stopCapture()
                    }
                }

                KeyValueRow(label: "Session", value: model.captureSessionSummary)

                if model.capturedFrameCount > 0 {
                    KeyValueRow(label: "Framed Packets", value: "\(model.capturedFrameCount)")
                    KeyValueRow(label: "Latest Frame", value: model.latestFrameSummary)
                }
            }

            Section("Transport") {
                LabeledStatusRow(
                    title: "Status",
                    value: model.transportStatus.rawValue,
                    tintName: model.transportStatus.tintName
                )

                if model.canConnectAndStream {
                    Button("Connect and Stream") {
                        Task {
                            await model.connectAndStream()
                        }
                    }
                }

                if model.canDisconnectTransport {
                    Button("Disconnect") {
                        model.disconnectTransport()
                    }
                }

                KeyValueRow(label: "Endpoint", value: model.hostConfiguration.displayEndpoint)

                if model.transportFramesSent > 0 || model.transportBytesSent > 0 {
                    KeyValueRow(label: "Frames Sent", value: "\(model.transportFramesSent)")
                    KeyValueRow(label: "Bytes Sent", value: "\(model.transportBytesSent)")
                    KeyValueRow(label: "Last Send", value: model.lastSuccessfulSendSummary)
                }

                if model.transportControlMessagesSent > 0 || model.transportControlMessagesReceived > 0 {
                    KeyValueRow(label: "Control Sent", value: "\(model.transportControlMessagesSent)")
                    KeyValueRow(label: "Control Received", value: "\(model.transportControlMessagesReceived)")
                }

                if model.transportReconnectCount > 0 {
                    KeyValueRow(label: "Reconnects", value: "\(model.transportReconnectCount)")
                }

                if model.lastKeepAliveTime != nil {
                    KeyValueRow(label: "Last Keepalive", value: model.lastKeepAliveSummary)
                }

                if model.lastTransportError != "No transport errors." {
                    KeyValueRow(label: "Last Error", value: model.lastTransportError)
                }
            }

            Section("Lifecycle") {
                KeyValueRow(label: "Scene", value: model.lastSceneState.debugLabel)
                KeyValueRow(label: "Background Policy", value: model.backgroundContinuationPolicy.debugLabel)
                KeyValueRow(label: "Background Active", value: model.isStreamingInBackground ? "Yes" : "No")
                KeyValueRow(label: "Last Background", value: model.lastBackgroundTransitionSummary)
                KeyValueRow(label: "Interruption", value: model.lastInterruptionState.debugLabel)
                KeyValueRow(label: "Route Change", value: model.lastRouteChange?.debugLabel ?? "None")
                KeyValueRow(label: "System Stop", value: model.lastSystemStopReason?.debugLabel ?? "None")
                KeyValueRow(label: "Transport End", value: model.lastTransportTerminalCauseSummary)

                Text(model.backgroundStatusDetail)
                    .font(.footnote)
                    .foregroundStyle(.secondary)

                if model.lastSystemStopReason != nil {
                    Text(model.lastSystemStopDetail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }

            Section("Audio") {
                MeterRow(
                    label: "Average",
                    value: model.latestInputLevel.averageLevel,
                    detail: "\(model.latestInputLevel.averagePercentage)% (\(model.latestInputLevel.averageDecibels) dBFS)"
                )
                MeterRow(
                    label: "Peak",
                    value: model.latestInputLevel.peakLevel,
                    detail: "\(model.latestInputLevel.peakPercentage)% (\(model.latestInputLevel.peakDecibels) dBFS)"
                )
            }

            Section("Diagnostics") {
                if model.diagnostics.isEmpty {
                    Text("No diagnostics.")
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(model.diagnostics) { entry in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(entry.timestamp.formatted(date: .omitted, time: .standard))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Text(entry.message)
                                .font(.footnote)
                        }
                        .padding(.vertical, 2)
                    }
                }
            }
        }
        .navigationTitle("Debug")
        .navigationBarTitleDisplayMode(.inline)
    }
}

private struct HostConfigurationFields: View {
    @ObservedObject var model: AppModel
    let showsCheckpointButton: Bool

    var body: some View {
        TextField("192.168.1.10", text: hostAddressBinding)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()

        TextField("42100", text: portTextBinding)
            .keyboardType(.numberPad)

        Picker("Transport", selection: transportModeBinding) {
            ForEach(HostConfiguration.TransportMode.allCases) { mode in
                Text(mode.label).tag(mode)
            }
        }

        if showsCheckpointButton {
            Button("Record Bring-Up Checkpoint") {
                model.recordBringUpCheckpoint()
            }
        }
    }

    private var hostAddressBinding: Binding<String> {
        Binding(
            get: { model.hostConfiguration.hostAddress },
            set: { model.updateHostAddress($0) }
        )
    }

    private var portTextBinding: Binding<String> {
        Binding(
            get: { model.hostConfiguration.portText },
            set: { model.updatePortText($0) }
        )
    }

    private var transportModeBinding: Binding<HostConfiguration.TransportMode> {
        Binding(
            get: { model.hostConfiguration.transportMode },
            set: { model.updateTransportMode($0) }
        )
    }
}

private struct LabeledStatusRow: View {
    let title: String
    let value: String
    let tintName: String

    var body: some View {
        HStack {
            Text(title)
            Spacer()
            Text(value)
                .fontWeight(.semibold)
                .foregroundStyle(StatusTint.color(named: tintName))
        }
    }
}

private struct KeyValueRow: View {
    let label: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.subheadline.weight(.semibold))
            Text(value)
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}

private struct MeterRow: View {
    let label: String
    let value: Float
    let detail: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(label)
                    .font(.subheadline.weight(.semibold))
                Spacer()
                Text(detail)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }

            ProgressView(value: Double(max(0, min(value, 1))), total: 1)
                .tint(value > 0.8 ? AppPalette.statusRed : (value > 0.4 ? AppPalette.statusOrange : AppPalette.statusGreen))
        }
        .padding(.vertical, 2)
    }
}

private enum StatusTint {
    static func color(named tintName: String) -> Color {
        switch tintName {
        case "orange":
            AppPalette.statusOrange
        case "blue":
            AppPalette.statusBlue
        case "yellow":
            AppPalette.statusYellow
        case "green":
            AppPalette.statusGreen
        case "red":
            AppPalette.statusRed
        case "slate":
            AppPalette.slate
        default:
            .primary
        }
    }
}
