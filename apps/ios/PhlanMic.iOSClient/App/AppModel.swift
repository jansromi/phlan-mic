import Combine
import Foundation

private final class TransportEventRelay: @unchecked Sendable {
    var handler: (@Sendable (DebugTcpPcmClientEvent) -> Void)?
}

@MainActor
final class AppModel: ObservableObject {
    enum SetupStatus: String {
        case setupRequired = "Setup Required"
        case ready = "Ready"

        var tintName: String {
            switch self {
            case .setupRequired:
                "orange"
            case .ready:
                "blue"
            }
        }
    }

    enum CaptureStatus: String {
        case unavailable = "Unavailable"
        case ready = "Ready"
        case starting = "Starting"
        case capturing = "Capturing"
        case error = "Error"

        var tintName: String {
            switch self {
            case .unavailable:
                "orange"
            case .ready:
                "blue"
            case .starting:
                "yellow"
            case .capturing:
                "green"
            case .error:
                "red"
            }
        }
    }

    enum TransportStatus: String {
        case disconnected = "Disconnected"
        case connecting = "Connecting"
        case connected = "Connected"
        case streaming = "Streaming"
        case stopping = "Stopping"
        case error = "Error"

        var tintName: String {
            switch self {
            case .disconnected:
                "orange"
            case .connecting, .stopping:
                "yellow"
            case .connected:
                "blue"
            case .streaming:
                "green"
            case .error:
                "red"
            }
        }

        var isActive: Bool {
            switch self {
            case .connecting, .connected, .streaming, .stopping:
                true
            case .disconnected, .error:
                false
            }
        }
    }

    enum PresentedSheet: String, Identifiable {
        case hostSettings
        case debug

        var id: String { rawValue }
    }

    enum PrimaryMicVisualState: Equatable {
        case idle
        case pending
        case live
        case blocked
        case error

        var tintName: String {
            switch self {
            case .idle:
                "slate"
            case .pending:
                "yellow"
            case .live:
                "green"
            case .blocked:
                "orange"
            case .error:
                "red"
            }
        }
    }

    struct Dependencies {
        var currentPermissionStatus: () async -> MicrophonePermissionState
        var requestMicrophonePermission: () async -> MicrophonePermissionState
        var startCapture: (
            MVPAudioFormat,
            @escaping @Sendable (AudioInputLevel) -> Void,
            @escaping @Sendable (CapturedAudioFrame) -> Void,
            @escaping @Sendable (Error) -> Void
        ) throws -> MicrophoneCaptureStartup
        var stopCapture: () -> Void
        var connectTransport: (String, UInt16) throws -> Void
        var disconnectTransport: () -> Void
        var sendTransportPayload: (Data, @escaping @Sendable (Result<Int, Error>) -> Void) -> Void
        var setTransportEventHandler: (@escaping @Sendable (DebugTcpPcmClientEvent) -> Void) -> Void
        var log: (String) -> Void

        static func live() -> Dependencies {
            let permissionClient = MicrophonePermissionClient()
            let captureClient = MicrophoneCaptureClient()
            let logger = AppLogger()
            let relay = TransportEventRelay()
            let transportClient = DebugTcpPcmClient { event in
                relay.handler?(event)
            }

            return Dependencies(
                currentPermissionStatus: {
                    await permissionClient.currentStatus()
                },
                requestMicrophonePermission: {
                    await permissionClient.requestPermission()
                },
                startCapture: { format, onInputLevel, onFrame, onFailure in
                    try captureClient.startCapture(
                        format: format,
                        onInputLevel: onInputLevel,
                        onFrame: onFrame,
                        onFailure: onFailure
                    )
                },
                stopCapture: {
                    captureClient.stopCapture()
                },
                connectTransport: { host, port in
                    try transportClient.connect(host: host, port: port)
                },
                disconnectTransport: {
                    transportClient.disconnect()
                },
                sendTransportPayload: { payload, completion in
                    transportClient.send(payload, completion: completion)
                },
                setTransportEventHandler: { handler in
                    relay.handler = handler
                },
                log: { message in
                    logger.log(message)
                }
            )
        }
    }

    struct DiagnosticEntry: Identifiable, Equatable {
        let id = UUID()
        let timestamp: Date
        let message: String
    }

    struct SessionHealthItem: Equatable, Identifiable {
        let id: String
        let title: String
        let detailTitle: String
        let value: String
        let detail: String
        let tintName: String
    }

    @Published var hostConfiguration = HostConfiguration()
    @Published var microphonePermission = MicrophonePermissionState.unknown
    @Published var setupStatus = SetupStatus.setupRequired
    @Published var setupDetail = "Enter the Windows host IP address and TCP port to prepare the session."
    @Published var diagnostics: [DiagnosticEntry] = []
    @Published var startupSnapshot = StartupSnapshot.current
    @Published var captureStatus = CaptureStatus.unavailable
    @Published var captureDetail = "Request microphone permission before starting live capture."
    @Published var captureSessionSummary = "Not started."
    @Published var latestInputLevel = AudioInputLevel.silence
    @Published var capturedFrameCount = 0
    @Published var latestFrameSummary = "No audio frames captured yet."
    @Published var transportStatus = TransportStatus.disconnected
    @Published var transportDetail = "Connect to the Windows host to start streaming."
    @Published var transportFramesSent = 0
    @Published var transportBytesSent = 0
    @Published var lastSuccessfulSendTime: Date?
    @Published var lastTransportError = "No transport errors."
    @Published var presentedSheet: PresentedSheet?

    private let dependencies: Dependencies
    private var captureOwnedByTransport = false

    init(dependencies: Dependencies = .live()) {
        self.dependencies = dependencies
        dependencies.setTransportEventHandler { [weak self] event in
            Task { @MainActor in
                self?.handleTransportEvent(event)
            }
        }

        log("App model initialized.")
        refreshSetupReadiness()
        syncCaptureAvailability()
        logStartupSnapshot()
    }

    var primaryMicVisualState: PrimaryMicVisualState {
        if transportStatus == .error || (captureOwnedByTransport && captureStatus == .error) {
            return .error
        }

        if transportStatus == .streaming {
            return .live
        }

        if transportStatus.isActive || captureStatus == .starting {
            return .pending
        }

        switch microphonePermission {
        case .denied, .simulatorUnavailable:
            return .blocked
        case .unknown, .granted:
            return .idle
        }
    }

    var primaryStatusTitle: String {
        if transportStatus == .streaming {
            return "Streaming Live"
        }

        if transportStatus == .connecting || transportStatus == .connected || transportStatus == .stopping {
            return transportStatus.rawValue
        }

        if captureStatus == .capturing && !captureOwnedByTransport {
            return "Debug Capture Active"
        }

        switch microphonePermission {
        case .unknown:
            return "Tap To Enable Mic"
        case .denied:
            return "Microphone Blocked"
        case .simulatorUnavailable:
            return "Device Required"
        case .granted:
            return setupStatus == .ready ? "Tap To Start" : "Host Setup Needed"
        }
    }

    var primaryStatusDetail: String {
        if transportStatus == .streaming {
            return "Streaming voice audio to \(hostConfiguration.displayEndpoint). Tap the mic again to stop."
        }

        switch transportStatus {
        case .connecting:
            return "Connecting to \(hostConfiguration.displayEndpoint)."
        case .connected:
            return "Connection established. Waiting for audio frames."
        case .streaming:
            return "Streaming voice audio to \(hostConfiguration.displayEndpoint). Tap the mic again to stop."
        case .stopping:
            return "Stopping the current session."
        case .error:
            return transportDetail
        case .disconnected:
            break
        }

        if captureStatus == .capturing && !captureOwnedByTransport {
            return "Standalone capture is active from the debug view."
        }

        switch microphonePermission {
        case .unknown:
            return "The first tap will ask for microphone access."
        case .denied:
            return "Allow microphone access in iOS Settings before streaming."
        case .simulatorUnavailable:
            return "Live microphone capture still requires a physical iPhone."
        case .granted:
            if setupStatus == .ready {
                return "Ready to stream to \(hostConfiguration.displayEndpoint)."
            }

            return hostSetupHint
        }
    }

    var connectionCardTitle: String {
        if hostConfiguration.trimmedHostAddress.isEmpty {
            return "Windows Host"
        }

        return hostConfiguration.displayEndpoint
    }

    var connectionCardStatusLabel: String {
        if transportStatus == .error {
            return transportStatus.rawValue
        }

        if transportStatus.isActive {
            return transportStatus.rawValue
        }

        return setupStatus.rawValue
    }

    var connectionCardDetail: String {
        if transportStatus == .error {
            return lastTransportError
        }

        if transportStatus.isActive {
            switch transportStatus {
            case .connecting:
                return "Opening the socket to the Windows host."
            case .connected:
                return "Connected to the host and preparing audio."
            case .streaming:
                return "Live session is active. Tap to edit host settings."
            case .stopping:
                return "Shutting down the live session."
            case .disconnected, .error:
                break
            }
        }

        return setupStatus == .ready
            ? "Ready to stream. Tap to edit the host or port."
            : hostSetupHint
    }

    var connectionCardTintName: String {
        if transportStatus == .error {
            return transportStatus.tintName
        }

        if transportStatus.isActive {
            return transportStatus.tintName
        }

        return setupStatus.tintName
    }

    var hostSettingsStatusText: String {
        if setupStatus == .ready {
            return "Ready to stream to \(hostConfiguration.displayEndpoint) over \(hostConfiguration.transportMode.label)."
        }

        return setupDetail
    }

    var lastSuccessfulSendSummary: String {
        guard let lastSuccessfulSendTime else {
            return "No successful sends yet."
        }

        return lastSuccessfulSendTime.formatted(date: .omitted, time: .standard)
    }

    var sessionHealthItems: [SessionHealthItem] {
        [
            SessionHealthItem(
                id: "connection",
                title: "Status",
                detailTitle: "Connection Status",
                value: sessionHealthConnectionValue,
                detail: sessionHealthConnectionDetail,
                tintName: sessionHealthConnectionTintName
            ),
            SessionHealthItem(
                id: "sent",
                title: "Sent",
                detailTitle: "Outgoing Frames",
                value: sessionHealthSentValue,
                detail: sessionHealthSentDetail,
                tintName: sessionHealthSentTintName
            ),
            SessionHealthItem(
                id: "last-send",
                title: "Last",
                detailTitle: "Last Successful Send",
                value: sessionHealthLastSendValue,
                detail: sessionHealthLastSendDetail,
                tintName: sessionHealthLastSendTintName
            )
        ]
    }

    var sessionHealthSummary: String {
        sessionHealthItems.map(\.value).joined(separator: " • ")
    }

    var sessionHealthFootnote: String? {
        if transportStatus == .error {
            return lastTransportError
        }

        if transportStatus == .streaming {
            return "Streaming to \(hostConfiguration.displayEndpoint)."
        }

        if transportStatus == .connecting || transportStatus == .connected || transportStatus == .stopping {
            return transportDetail
        }

        if setupStatus == .ready {
            return "Ready to stream when you tap the microphone."
        }

        return nil
    }

    func sessionHealthDetail(for itemID: String) -> SessionHealthItem? {
        sessionHealthItems.first { $0.id == itemID }
    }

    func loadStartupState() async {
        let status = await dependencies.currentPermissionStatus()
        microphonePermission = status
        syncCaptureAvailability()
        log("Microphone permission state: \(status.label).")
    }

    func requestMicrophonePermission() async {
        let status = await dependencies.requestMicrophonePermission()
        microphonePermission = status
        syncCaptureAvailability()
        log("Microphone permission request completed with state: \(status.label).")
    }

    func handlePrimaryMicTap() async {
        if transportStatus.isActive {
            disconnectTransport()
            return
        }

        if microphonePermission == .unknown {
            await requestMicrophonePermission()
        }

        guard microphonePermission == .granted else {
            log("Primary microphone action blocked because microphone access was unavailable.")
            return
        }

        refreshSetupReadiness()

        guard setupStatus == .ready else {
            presentedSheet = .hostSettings
            log("Primary microphone action opened host settings because setup was incomplete.")
            return
        }

        await connectAndStream()
    }

    func presentHostSettings() {
        presentedSheet = .hostSettings
    }

    func presentDebug() {
        presentedSheet = .debug
    }

    func dismissSheet() {
        presentedSheet = nil
    }

    func refreshSetupReadiness() {
        guard hostConfiguration.transportMode == .tcpDebug else {
            setupStatus = .setupRequired
            setupDetail = "UDP Realtime is not implemented yet. Switch back to TCP Debug for the current Windows host."
            return
        }

        guard let validatedPort = hostConfiguration.validatedPort else {
            setupStatus = .setupRequired
            setupDetail = "Enter a valid TCP port between 1 and 65535."
            return
        }

        guard !hostConfiguration.trimmedHostAddress.isEmpty else {
            setupStatus = .setupRequired
            setupDetail = "Enter the Windows host IP address or hostname."
            return
        }

        setupStatus = .ready
        setupDetail = "Ready to stream to \(hostConfiguration.trimmedHostAddress):\(validatedPort)."
    }

    func updateHostAddress(_ hostAddress: String) {
        hostConfiguration.hostAddress = hostAddress
        refreshSetupReadiness()
    }

    func updatePortText(_ portText: String) {
        hostConfiguration.portText = portText
        refreshSetupReadiness()
    }

    func updateTransportMode(_ transportMode: HostConfiguration.TransportMode) {
        hostConfiguration.transportMode = transportMode
        refreshSetupReadiness()
    }

    func recordBringUpCheckpoint() {
        refreshSetupReadiness()

        switch setupStatus {
        case .ready:
            log("Bring-up checkpoint recorded for \(hostConfiguration.displayEndpoint) using \(hostConfiguration.transportMode.label).")
        case .setupRequired:
            log("Bring-up checkpoint attempted before configuration was ready.")
        }
    }

    func startCapture() async {
        guard !transportStatus.isActive else {
            return
        }

        guard captureStatus != .starting, captureStatus != .capturing else {
            return
        }

        captureOwnedByTransport = false
        await startCapturePipeline(streamToTransport: false)
    }

    func connectAndStream() async {
        refreshSetupReadiness()

        guard setupStatus == .ready else {
            presentedSheet = .hostSettings
            log("TCP debug connect attempted before setup was ready.")
            return
        }

        guard !transportStatus.isActive else {
            return
        }

        presentedSheet = nil

        if captureStatus == .starting || captureStatus == .capturing {
            stopCapture(
                reason: "Capture stopped so the TCP debug stream can restart cleanly.",
                logMessage: "Stopped the existing capture session before starting TCP debug streaming."
            )
        }

        transportFramesSent = 0
        transportBytesSent = 0
        lastSuccessfulSendTime = nil
        lastTransportError = "No transport errors."
        captureOwnedByTransport = false
        transportStatus = .connecting
        transportDetail = "Opening TCP debug connection to \(hostConfiguration.displayEndpoint)."

        log("Connecting to the Windows debug TCP receiver at \(hostConfiguration.displayEndpoint).")

        do {
            try dependencies.connectTransport(
                hostConfiguration.trimmedHostAddress,
                hostConfiguration.validatedPort ?? HostConfiguration.defaultDebugTcpPort
            )
        } catch {
            handleTransportFailure(
                detail: error.localizedDescription,
                logMessage: "TCP debug connect failed: \(error.localizedDescription)"
            )
        }
    }

    func disconnectTransport() {
        guard transportStatus.isActive else {
            return
        }

        transportStatus = .stopping
        transportDetail = "Stopping microphone capture and closing the TCP debug socket."
        log("Stopping the TCP debug stream.")

        if captureStatus == .starting || captureStatus == .capturing {
            stopCapture(
                reason: "Capture stopped. Ready to start again.",
                logMessage: nil
            )
        }

        captureOwnedByTransport = false
        dependencies.disconnectTransport()
    }

    func log(_ message: String) {
        dependencies.log(message)
        diagnostics.insert(DiagnosticEntry(timestamp: Date(), message: message), at: 0)
        diagnostics = Array(diagnostics.prefix(12))
    }

    var canRequestMicrophonePermission: Bool {
        microphonePermission != .simulatorUnavailable
    }

    var canStartCapture: Bool {
        microphonePermission == .granted &&
            captureStatus != .starting &&
            captureStatus != .capturing &&
            !transportStatus.isActive
    }

    var canStopCapture: Bool {
        !captureOwnedByTransport && (captureStatus == .starting || captureStatus == .capturing)
    }

    var canConnectAndStream: Bool {
        setupStatus == .ready && !transportStatus.isActive
    }

    var canDisconnectTransport: Bool {
        transportStatus.isActive
    }

    private var sessionHealthConnectionValue: String {
        switch transportStatus {
        case .streaming:
            return "Live"
        case .connecting:
            return "Joining"
        case .connected:
            return "Linked"
        case .stopping:
            return "Stopping"
        case .error:
            return "Error"
        case .disconnected:
            return setupStatus == .ready ? "Ready" : "Setup"
        }
    }

    private var sessionHealthConnectionTintName: String {
        if transportStatus != .disconnected {
            return transportStatus.tintName
        }

        return setupStatus.tintName
    }

    private var sessionHealthConnectionDetail: String {
        switch transportStatus {
        case .streaming:
            return "The session is live and microphone audio is reaching \(hostConfiguration.displayEndpoint)."
        case .connecting:
            return "The app is opening a connection to \(hostConfiguration.displayEndpoint)."
        case .connected:
            return "The host connection is open and the audio pipeline is starting."
        case .stopping:
            return "The current session is shutting down cleanly."
        case .error:
            return lastTransportError
        case .disconnected:
            return setupStatus == .ready
                ? "The host configuration is ready and the session is idle."
                : hostSetupHint
        }
    }

    private var sessionHealthSentValue: String {
        switch transportStatus {
        case .connecting, .connected:
            return "Waiting"
        case .streaming:
            return transportFramesSent == 0 ? "Starting" : "\(transportFramesSent)"
        case .stopping:
            return "\(transportFramesSent)"
        case .error:
            return transportFramesSent == 0 ? "Failed" : "\(transportFramesSent)"
        case .disconnected:
            return transportFramesSent == 0 ? "Idle" : "\(transportFramesSent)"
        }
    }

    private var sessionHealthSentTintName: String {
        switch transportStatus {
        case .error:
            return "red"
        case .streaming:
            return "green"
        case .connecting, .connected, .stopping:
            return "yellow"
        case .disconnected:
            return transportFramesSent > 0 ? "blue" : "slate"
        }
    }

    private var sessionHealthSentDetail: String {
        if transportFramesSent == 0 && transportBytesSent == 0 {
            return transportStatus.isActive
                ? "The connection is active, but no audio frames have been confirmed yet."
                : "No audio frames have been sent in this session."
        }

        return "\(transportFramesSent) frames and \(transportBytesSent) bytes have been sent in this session."
    }

    private var sessionHealthLastSendValue: String {
        guard let lastSuccessfulSendTime else {
            switch transportStatus {
            case .error:
                return "Failed"
            case .connecting, .connected:
                return "Pending"
            case .streaming:
                return "Pending"
            case .stopping, .disconnected:
                return "None"
            }
        }

        return lastSuccessfulSendTime.formatted(date: .omitted, time: .shortened)
    }

    private var sessionHealthLastSendTintName: String {
        if transportStatus == .error {
            return "red"
        }

        if lastSuccessfulSendTime != nil {
            return transportStatus == .streaming ? "green" : "blue"
        }

        return transportStatus.isActive ? "yellow" : "slate"
    }

    private var sessionHealthLastSendDetail: String {
        guard let lastSuccessfulSendTime else {
            return transportStatus == .error
                ? "No successful send was recorded before the last transport failure."
                : "No successful send has been recorded yet."
        }

        return "The latest confirmed send completed at \(lastSuccessfulSendTime.formatted(date: .omitted, time: .standard))."
    }

    private var hostSetupHint: String {
        switch setupStatus {
        case .ready:
            return "Ready to stream to \(hostConfiguration.displayEndpoint)."
        case .setupRequired:
            if hostConfiguration.transportMode != .tcpDebug {
                return "UDP Realtime is not available yet. Use TCP Debug for now."
            }

            if hostConfiguration.trimmedHostAddress.isEmpty {
                return "Add your Windows host IP address to continue."
            }

            if hostConfiguration.validatedPort == nil {
                return "Enter the TCP port used by the Windows host."
            }

            return "Finish the Windows host details to start streaming."
        }
    }

    private func logStartupSnapshot() {
        log("Running on \(startupSnapshot.platformDescription).")
        log("Preferred MVP audio format: \(MVPAudioFormat.defaultVoice.debugSummary).")
        log("Packet cadence: \(MVPAudioPacket.prototype.debugSummary).")
    }

    private func startCapturePipeline(streamToTransport: Bool) async {
        if microphonePermission == .unknown {
            await requestMicrophonePermission()
        }

        guard microphonePermission == .granted else {
            if streamToTransport {
                handleTransportFailure(
                    detail: "Microphone access is required before streaming to the Windows host.",
                    logMessage: "Streaming could not start because microphone access was unavailable."
                )
            }
            syncCaptureAvailability()
            return
        }

        captureStatus = .starting
        captureDetail = streamToTransport
            ? "Configuring AVAudioSession for TCP debug streaming."
            : "Configuring AVAudioSession and starting the microphone tap."
        captureSessionSummary = streamToTransport ? "Starting stream capture." : "Starting capture."
        latestInputLevel = .silence
        capturedFrameCount = 0
        latestFrameSummary = "Waiting for the first framed packet."

        do {
            let startup = try dependencies.startCapture(
                .defaultVoice,
                { [weak self] inputLevel in
                    Task { @MainActor in
                        self?.latestInputLevel = inputLevel
                    }
                },
                { [weak self] frame in
                    Task { @MainActor in
                        self?.handleCapturedFrame(frame, streamToTransport: streamToTransport)
                    }
                },
                { [weak self] error in
                    Task { @MainActor in
                        self?.handleCaptureFailure(error)
                    }
                }
            )

            captureStatus = .capturing
            captureDetail = streamToTransport
                ? "Live microphone capture is feeding the TCP debug stream at \(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) ms packet cadence."
                : "Live microphone capture is running at \(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) ms packet cadence."
            captureSessionSummary = startup.debugSummary
            if streamToTransport {
                log("Microphone capture started for TCP debug streaming. \(startup.debugSummary)")
            } else {
                log("Microphone capture started. \(startup.debugSummary)")
            }
        } catch {
            captureStatus = .error
            captureDetail = error.localizedDescription
            captureSessionSummary = streamToTransport ? "Stream capture failed to start." : "Capture failed to start."
            latestInputLevel = .silence

            if streamToTransport {
                captureOwnedByTransport = false
                handleTransportFailure(
                    detail: "TCP debug connection opened but microphone capture failed: \(error.localizedDescription)",
                    logMessage: "Microphone capture failed to start for streaming: \(error.localizedDescription)"
                )
            } else {
                log("Microphone capture failed to start: \(error.localizedDescription)")
            }
        }
    }

    func stopCapture() {
        stopCapture(reason: "Capture stopped. Ready to start again.", logMessage: "Microphone capture stopped.")
    }

    private func handleCapturedFrame(_ frame: CapturedAudioFrame, streamToTransport: Bool) {
        capturedFrameCount = Int(frame.sequenceNumber + 1)
        latestFrameSummary = frame.debugSummary

        if frame.sequenceNumber == 0 {
            if streamToTransport {
                log("First microphone frame captured for streaming: \(frame.debugSummary).")
            } else {
                log("First microphone frame captured: \(frame.debugSummary).")
            }
        }

        guard streamToTransport else {
            return
        }

        dependencies.sendTransportPayload(frame.payload) { [weak self] result in
            Task { @MainActor in
                self?.handleSendCompletion(result, for: frame)
            }
        }
    }

    private func handleCaptureFailure(_ error: Error) {
        latestInputLevel = .silence
        captureStatus = .error
        captureDetail = error.localizedDescription
        captureSessionSummary = captureOwnedByTransport ? "Stream capture failed." : "Capture failed."

        if captureOwnedByTransport {
            captureOwnedByTransport = false
            handleTransportFailure(
                detail: "Microphone capture failed while streaming: \(error.localizedDescription)",
                logMessage: "Streaming stopped because microphone capture failed: \(error.localizedDescription)"
            )
        } else {
            log("Microphone capture failed: \(error.localizedDescription)")
        }
    }

    private func handleTransportEvent(_ event: DebugTcpPcmClientEvent) {
        switch event {
        case .connecting:
            guard transportStatus == .connecting else {
                return
            }

            transportDetail = "Opening TCP debug connection to \(hostConfiguration.displayEndpoint)."
        case .ready:
            guard transportStatus == .connecting else {
                return
            }

            transportStatus = .connected
            transportDetail = "TCP debug connection established. Starting microphone capture."
            captureOwnedByTransport = true
            log("TCP debug connection established to \(hostConfiguration.displayEndpoint).")

            Task {
                await startCapturePipeline(streamToTransport: true)
            }
        case .failed(let detail):
            handleTransportFailure(detail: detail, logMessage: "TCP debug transport failed: \(detail)")
        case .peerClosed:
            handleTransportFailure(
                detail: DebugTcpPcmClientError.disconnectedByPeer.localizedDescription,
                logMessage: "The Windows debug receiver closed the TCP connection."
            )
        case .cancelled:
            if transportStatus == .stopping {
                transportStatus = .disconnected
                transportDetail = "TCP debug connection closed. Ready to reconnect."
                log("TCP debug connection closed.")
            }
        }
    }

    private func handleSendCompletion(_ result: Result<Int, Error>, for frame: CapturedAudioFrame) {
        guard captureOwnedByTransport || transportStatus == .connected || transportStatus == .streaming else {
            return
        }

        switch result {
        case .success(let bytesSent):
            transportFramesSent += 1
            transportBytesSent += bytesSent
            lastSuccessfulSendTime = Date()

            if transportStatus == .connected {
                transportStatus = .streaming
                transportDetail = "Streaming raw PCM to \(hostConfiguration.displayEndpoint)."
                log("Raw PCM streaming started with microphone frame \(frame.sequenceNumber).")
            } else if transportFramesSent.isMultiple(of: 250) {
                log("Streaming health: \(transportFramesSent) frames / \(transportBytesSent) bytes sent to \(hostConfiguration.displayEndpoint).")
            }
        case .failure(let error):
            handleTransportFailure(
                detail: error.localizedDescription,
                logMessage: "PCM send failed after frame \(frame.sequenceNumber): \(error.localizedDescription)"
            )
        }
    }

    private func handleTransportFailure(detail: String, logMessage: String) {
        if captureStatus == .starting || captureStatus == .capturing {
            dependencies.stopCapture()
            latestInputLevel = .silence
            captureSessionSummary = "Capture stopped."
            syncCaptureAvailability(reason: "Transport is idle. Ready to capture again.")
        }

        captureOwnedByTransport = false
        transportStatus = .error
        transportDetail = detail
        lastTransportError = detail
        log(logMessage)
        dependencies.disconnectTransport()
    }

    private func stopCapture(reason: String, logMessage: String?) {
        guard captureStatus == .starting || captureStatus == .capturing else {
            return
        }

        dependencies.stopCapture()
        latestInputLevel = .silence
        captureSessionSummary = "Capture stopped."
        captureOwnedByTransport = false
        syncCaptureAvailability(reason: reason)

        if let logMessage {
            log(logMessage)
        }
    }

    private func syncCaptureAvailability(reason: String? = nil) {
        guard captureStatus != .starting, captureStatus != .capturing else {
            return
        }

        switch microphonePermission {
        case .unknown:
            captureStatus = .unavailable
            captureDetail = "Request microphone access before starting live capture."
        case .granted:
            captureStatus = .ready
            captureDetail = reason ?? "Ready to capture mono \(MVPAudioFormat.defaultVoice.debugSummary)."
        case .denied:
            captureStatus = .unavailable
            captureDetail = "Microphone access is blocked. Enable it in Settings before starting capture."
        case .simulatorUnavailable:
            captureStatus = .unavailable
            captureDetail = "Live microphone capture requires a physical iPhone."
        }
    }
}

struct StartupSnapshot {
    let operatingSystemVersion: String
    let isSimulator: Bool

    static var current: StartupSnapshot {
        let version = ProcessInfo.processInfo.operatingSystemVersion
        let operatingSystemVersion = "\(version.majorVersion).\(version.minorVersion).\(version.patchVersion)"

        #if targetEnvironment(simulator)
        let isSimulator = true
        #else
        let isSimulator = false
        #endif

        return StartupSnapshot(operatingSystemVersion: operatingSystemVersion, isSimulator: isSimulator)
    }

    var platformDescription: String {
        if isSimulator {
            "iOS Simulator \(operatingSystemVersion)"
        } else {
            "iPhone/iPadOS \(operatingSystemVersion)"
        }
    }
}
