import SwiftUI

private enum SessionLayout {
    static let panelWidth: CGFloat = 360
    static let panelCornerRadius: CGFloat = 22
    static let tileCornerRadius: CGFloat = 16
    static let panelPadding: CGFloat = 18
}

private enum AppTypography {
    static let primaryStatus = Font.system(size: 30, weight: .bold, design: .rounded)
    static let cardTitle = Font.system(size: 17, weight: .semibold, design: .rounded)
    static let sectionLabel = Font.system(size: 12, weight: .semibold, design: .rounded)
    static let metricLabel = Font.system(size: 13, weight: .semibold, design: .rounded)
    static let metricValue = Font.system(size: 15, weight: .semibold, design: .rounded)
    static let detail = Font.system(size: 13, weight: .medium, design: .rounded)
    static let badge = Font.system(size: 12, weight: .semibold, design: .rounded)
    static let largeValue = Font.system(size: 28, weight: .bold, design: .rounded)
}

private enum AppPalette {
    static let backgroundTop = Color(.sRGB, red: 0.94, green: 0.96, blue: 0.99)
    static let backgroundBottom = Color(.sRGB, red: 0.84, green: 0.88, blue: 0.94)
    static let backgroundGlow = Color.white.opacity(0.42)
    static let panelBackgroundTop = Color(.sRGB, red: 0.985, green: 0.99, blue: 0.995)
    static let panelBackgroundBottom = Color(.sRGB, red: 0.90, green: 0.93, blue: 0.97)
    static let tileBackgroundTop = Color(.sRGB, red: 0.955, green: 0.968, blue: 0.985)
    static let tileBackgroundBottom = Color(.sRGB, red: 0.88, green: 0.91, blue: 0.95)
    static let panelBorder = slate.opacity(0.14)
    static let tileBorder = slate.opacity(0.10)
    static let panelShadow = Color.black.opacity(0.10)
    static let meterTrack = Color(.sRGB, red: 0.12, green: 0.18, blue: 0.26).opacity(0.10)
    static let textPrimary = Color(.sRGB, red: 0.12, green: 0.17, blue: 0.24)
    static let textSecondary = Color(.sRGB, red: 0.29, green: 0.36, blue: 0.45)
    static let textMuted = Color(.sRGB, red: 0.42, green: 0.48, blue: 0.57)
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

                VStack(spacing: 22) {
                    Spacer(minLength: 20)

                    VStack(spacing: 16) {
                        Button {
                            Task {
                                await model.handlePrimaryMicTap()
                            }
                        } label: {
                            PrimaryMicButton(state: model.primaryMicVisualState)
                        }
                        .buttonStyle(.plain)
                        .sensoryFeedback(.impact(weight: .medium, intensity: 0.7), trigger: model.primaryMicVisualState)

                        VStack(spacing: 8) {
                            AnimatedConnectingLabel(
                                text: model.primaryStatusTitle,
                                isConnecting: shouldAnimateConnectingLabel(for: model.primaryStatusTitle),
                                step: connectingTextStep
                            )
                                .font(AppTypography.primaryStatus)
                                .foregroundStyle(AppPalette.textPrimary)
                                .multilineTextAlignment(.center)
                        }
                    }

                    InputMeterPanel(model: model)
                    SessionHealthPanel(model: model)

                    Spacer()
                }
                .padding(.horizontal, 24)
                .padding(.top, 16)
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
                            .foregroundStyle(AppPalette.textPrimary)
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
        .sensoryFeedback(.success, trigger: model.transportStatus) { oldValue, newValue in
            newValue == .streaming
        }
        .sensoryFeedback(.error, trigger: model.transportStatus) { oldValue, newValue in
            newValue == .error
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
                .contentTransition(.numericText())
                .animation(.easeInOut(duration: 0.25), value: text)
        }
    }
}

private struct SessionBackground: View {
    var body: some View {
        ZStack {
            LinearGradient(
                colors: [AppPalette.backgroundTop, AppPalette.backgroundBottom],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )

            Circle()
                .fill(AppPalette.backgroundGlow)
                .frame(width: 280, height: 280)
                .blur(radius: 70)
                .offset(x: -110, y: -280)

            Circle()
                .fill(AppPalette.statusBlue.opacity(0.12))
                .frame(width: 240, height: 240)
                .blur(radius: 84)
                .offset(x: 150, y: 260)
        }
        .ignoresSafeArea()
    }
}


private struct PrimaryMicButton: View {
    let state: AppModel.PrimaryMicVisualState
    @State private var isPulsing = false

    var body: some View {
        ZStack {
            Circle()
                .fill(baseColor.opacity(0.18))
                .frame(width: 220, height: 220)

            if state == .live {
                Circle()
                    .stroke(baseColor.opacity(0.35), lineWidth: 2)
                    .frame(width: 180, height: 180)
                    .scaleEffect(isPulsing ? 1.3 : 1.0)
                    .opacity(isPulsing ? 0 : 0.6)
            }

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
        .onChange(of: state) { _, newState in
            if newState == .live {
                isPulsing = false
                withAnimation(.easeOut(duration: 1.5).repeatForever(autoreverses: false)) {
                    isPulsing = true
                }
            } else {
                isPulsing = false
            }
        }
    }

    private var baseColor: Color {
        StatusTint.color(named: state.tintName)
    }
}

private struct InputMeterPanel: View {
    @ObservedObject var model: AppModel
    @State private var isExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if isExpanded {
                Button {
                    toggleExpanded()
                } label: {
                    headerContent
                }
                .buttonStyle(.plain)
            } else {
                headerContent
            }

            LevelMeterRow(
                label: "Average",
                value: model.latestInputLevel.averageLevel,
                percentage: model.latestInputLevel.averagePercentage,
                decibels: model.latestInputLevel.averageDecibels
            )

            if isExpanded {
                LevelMeterRow(
                    label: "Peak",
                    value: model.latestInputLevel.peakLevel,
                    percentage: model.latestInputLevel.peakPercentage,
                    decibels: model.latestInputLevel.peakDecibels
                )

                VStack(alignment: .leading, spacing: 10) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("Mic Gain")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(AppPalette.textPrimary)

                        Spacer()

                        Text(model.micGainLabel)
                            .font(.subheadline.monospacedDigit())
                            .foregroundStyle(AppPalette.textPrimary)

                        Text("(\(model.micGainDecibelsLabel))")
                            .font(.caption)
                            .foregroundStyle(AppPalette.textSecondary)
                    }

                    Slider(
                        value: micGainBinding,
                        in: Double(AppModel.minimumMicGain) ... Double(AppModel.maximumMicGain),
                        step: 0.1
                    )
                    .accessibilityLabel("Microphone gain")
                }
                .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .padding(SessionLayout.panelPadding)
        .frame(maxWidth: SessionLayout.panelWidth)
        .sessionCard()
        .contentShape(RoundedRectangle(cornerRadius: SessionLayout.panelCornerRadius, style: .continuous))
        .onTapGesture {
            guard !isExpanded else {
                return
            }

            toggleExpanded()
        }
        .sensoryFeedback(.selection, trigger: isExpanded)
    }

    private var micGainBinding: Binding<Double> {
        Binding(
            get: { Double(model.micGain) },
            set: { model.updateMicGain(Float($0)) }
        )
    }

    private var headerContent: some View {
        HStack(spacing: 12) {
            Text("Input Meter")
                .font(AppTypography.cardTitle)
                .foregroundStyle(AppPalette.textPrimary)

            Spacer()

            SummaryBadge(
                text: "\(model.latestInputLevel.averagePercentage)% · \(model.latestInputLevel.averageDecibels)dB",
                tint: meterSummaryTint
            )

            Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                .font(AppTypography.sectionLabel)
                .foregroundStyle(AppPalette.textSecondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
    }

    private var meterSummaryTint: Color {
        StatusTint.color(named: meterSummaryTintName)
    }

    private var meterSummaryTintName: String {
        let value = model.latestInputLevel.averageLevel
        if value > 0.8 {
            return "red"
        }

        if value > 0.4 {
            return "orange"
        }

        return "green"
    }

    private func toggleExpanded() {
        withAnimation(.easeInOut(duration: 0.2)) {
            isExpanded.toggle()
        }
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
                Text(label.uppercased())
                    .font(AppTypography.sectionLabel)
                    .foregroundStyle(AppPalette.textSecondary)
                Spacer()
                Text("\(percentage)%")
                    .foregroundStyle(AppPalette.textPrimary)
                    .font(AppTypography.metricValue.monospacedDigit())
                Text("\(decibels)dB")
                    .font(AppTypography.detail.monospacedDigit())
                    .foregroundStyle(AppPalette.textSecondary)
            }

            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(AppPalette.meterTrack)

                    Capsule()
                        .fill(meterColors.first ?? AppPalette.meterLow)
                        .frame(width: geometry.size.width * CGFloat(max(0, min(value, 1))))
                        .animation(.linear(duration: 0.08), value: value)
                }
            }
            .frame(height: 10)
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
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .top, spacing: 8) {
                        HStack(spacing: 8) {
                            Circle()
                                .fill(StatusTint.color(named: model.connectionCardTintName))
                                .frame(width: 8, height: 8)

                            AnimatedConnectingLabel(
                                text: model.connectionCardStatusLabel,
                                isConnecting: isConnecting,
                                step: connectingStep
                            )
                                .font(AppTypography.sectionLabel)
                                .foregroundStyle(StatusTint.color(named: model.connectionCardTintName))
                        }

                        Spacer(minLength: 8)

                        SummaryBadge(
                            text: model.hostConfiguration.transportMode.label,
                            tint: AppPalette.textSecondary
                        )
                    }

                    Text(model.connectionCardTitle)
                        .font(AppTypography.cardTitle)
                        .foregroundStyle(AppPalette.textPrimary)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Image(systemName: "slider.horizontal.3")
                    .font(AppTypography.metricLabel)
                    .foregroundStyle(AppPalette.textSecondary)
                    .padding(.top, 2)
            }
            .padding(SessionLayout.panelPadding)
            .sessionCard(cornerRadius: 20)
        }
        .buttonStyle(.plain)
    }
}

private struct SessionHealthPanel: View {
    @ObservedObject var model: AppModel
    @State private var isExpanded = true
    @State private var selectedItemID: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Button {
                withAnimation(.easeInOut(duration: 0.2)) {
                    isExpanded.toggle()
                }
            } label: {
                HStack(spacing: 12) {
                    Text("Session Health")
                        .font(AppTypography.cardTitle)
                        .foregroundStyle(AppPalette.textPrimary)

                    Spacer()

                    Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                        .font(AppTypography.metricLabel)
                        .foregroundStyle(AppPalette.textSecondary)
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
            }
        }
        .padding(SessionLayout.panelPadding)
        .frame(maxWidth: SessionLayout.panelWidth)
        .sessionCard()
        .sensoryFeedback(.selection, trigger: isExpanded)
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

                Text(item.title.uppercased())
                    .font(AppTypography.sectionLabel)
                    .foregroundStyle(AppPalette.textSecondary)
            }

            Text(item.value)
                .font(AppTypography.metricValue.monospacedDigit())
                .foregroundStyle(StatusTint.color(named: item.tintName))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .tileCard()
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
                    .font(AppTypography.cardTitle)
            }

            Text(item.value)
                .font(AppTypography.largeValue.monospacedDigit())
                .foregroundStyle(StatusTint.color(named: item.tintName))

            Text(item.detail)
                .font(.body)
                .foregroundStyle(AppPalette.textSecondary)

            Spacer(minLength: 0)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Color(uiColor: .systemBackground))
    }
}

private struct SummaryBadge: View {
    let text: String
    let tint: Color

    var body: some View {
        Text(text)
            .font(AppTypography.badge.monospacedDigit())
            .foregroundStyle(tint)
            .lineLimit(1)
            .minimumScaleFactor(0.85)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(
                Capsule(style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: [AppPalette.tileBackgroundTop, AppPalette.tileBackgroundBottom],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
            )
            .overlay(
                Capsule(style: .continuous)
                    .stroke(AppPalette.tileBorder, lineWidth: 1)
            )
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

                if !model.hostSettingsStatusText.isEmpty {
                    Text(model.hostSettingsStatusText)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }

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

        Toggle("Tone Generator (440 Hz)", isOn: $model.useToneGenerator)

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

private struct SessionCardModifier: ViewModifier {
    let cornerRadius: CGFloat

    func body(content: Content) -> some View {
        content
            .background(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: [AppPalette.panelBackgroundTop, AppPalette.panelBackgroundBottom],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
            )
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .stroke(AppPalette.panelBorder, lineWidth: 1)
            )
            .shadow(color: AppPalette.panelShadow, radius: 18, x: 0, y: 10)
    }
}

private struct TileCardModifier: ViewModifier {
    func body(content: Content) -> some View {
        content
            .background(
                RoundedRectangle(cornerRadius: SessionLayout.tileCornerRadius, style: .continuous)
                    .fill(
                        LinearGradient(
                            colors: [AppPalette.tileBackgroundTop, AppPalette.tileBackgroundBottom],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
            )
            .overlay(
                RoundedRectangle(cornerRadius: SessionLayout.tileCornerRadius, style: .continuous)
                    .stroke(AppPalette.tileBorder, lineWidth: 1)
            )
    }
}

private extension View {
    func sessionCard(cornerRadius: CGFloat = SessionLayout.panelCornerRadius) -> some View {
        modifier(SessionCardModifier(cornerRadius: cornerRadius))
    }

    func tileCard() -> some View {
        modifier(TileCardModifier())
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
