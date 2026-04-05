import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    private final class InputLevelUpdateBuffer: @unchecked Sendable {
        private let lock = NSLock()
        private let updateEveryCallbacks: Int
        private var callbackCount = 0

        init(updateEveryCallbacks: Int) {
            self.updateEveryCallbacks = max(1, updateEveryCallbacks)
        }

        func record(_ inputLevel: AudioInputLevel) -> AudioInputLevel? {
            lock.lock()
            defer { lock.unlock() }

            callbackCount += 1
            if callbackCount == 1 || callbackCount.isMultiple(of: updateEveryCallbacks) {
                return inputLevel
            }

            return nil
        }
    }

    private final class CaptureFrameUpdateBuffer: @unchecked Sendable {
        struct Snapshot: Sendable {
            let capturedFrameCount: Int
            let latestFrameSummary: String
            let isFirstFrame: Bool
        }

        private let lock = NSLock()
        private let updateEveryFrames: Int

        init(updateEveryFrames: Int) {
            self.updateEveryFrames = max(1, updateEveryFrames)
        }

        func record(_ frame: CapturedAudioFrame) -> Snapshot? {
            lock.lock()
            defer { lock.unlock() }

            let capturedFrameCount = Int(frame.sequenceNumber + 1)
            let isFirstFrame = frame.sequenceNumber == 0
            if isFirstFrame || capturedFrameCount.isMultiple(of: updateEveryFrames) {
                return Snapshot(
                    capturedFrameCount: capturedFrameCount,
                    latestFrameSummary: frame.debugSummary,
                    isFirstFrame: isFirstFrame
                )
            }

            return nil
        }
    }

    private final class TransportSendProgressBuffer: @unchecked Sendable {
        struct Snapshot: Sendable {
            let totalFramesSent: Int
            let totalBytesSent: Int
            let isFirstSuccess: Bool
        }

        private let lock = NSLock()
        private let flushEveryFrames: Int
        private var totalFramesSent = 0
        private var totalBytesSent = 0

        init(flushEveryFrames: Int) {
            self.flushEveryFrames = max(1, flushEveryFrames)
        }

        func recordSuccess(bytesSent: Int) -> Snapshot? {
            lock.lock()
            defer { lock.unlock() }

            totalFramesSent += 1
            totalBytesSent += bytesSent

            let isFirstSuccess = totalFramesSent == 1
            if isFirstSuccess || totalFramesSent.isMultiple(of: flushEveryFrames) {
                return Snapshot(
                    totalFramesSent: totalFramesSent,
                    totalBytesSent: totalBytesSent,
                    isFirstSuccess: isFirstSuccess
                )
            }

            return nil
        }
    }

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
        case controlConnected = "Control Connected"
        case handshakeAccepted = "Handshake Accepted"
        case connected = "Ready For Audio"
        case streaming = "Streaming"
        case stopping = "Stopping"
        case error = "Error"

        var tintName: String {
            switch self {
            case .disconnected:
                "orange"
            case .connecting, .controlConnected, .handshakeAccepted, .stopping:
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
            case .connecting, .controlConnected, .handshakeAccepted, .connected, .streaming, .stopping:
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
        var makeTransportClient: (
            HostConfiguration.TransportMode,
            @escaping @Sendable (AudioTransportEvent) -> Void
        ) -> AudioTransportClient
        var log: (String) -> Void

        static func live() -> Dependencies {
            let permissionClient = MicrophonePermissionClient()
            let captureClient = MicrophoneCaptureClient()
            let logger = AppLogger()

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
                makeTransportClient: { transportMode, handler in
                    switch transportMode {
                    case .tcpDebug:
                        return DebugTcpPcmClient.makeTransportClient(eventHandler: handler)
                    case .udpRealtime:
                        let client = UdpRawPcmTransportClient(eventHandler: handler)
                        return AudioTransportClient(
                            connect: { host, port, format in
                                try client.connect(host: host, port: port, format: format)
                            },
                            disconnect: {
                                client.disconnect()
                            },
                            sendFrame: { frame, completion in
                                client.send(frame, completion: completion)
                            }
                        )
                    }
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
    @Published var transportControlMessagesSent = 0
    @Published var transportControlMessagesReceived = 0
    @Published var transportReconnectCount = 0
    @Published var lastSuccessfulSendTime: Date?
    @Published var lastKeepAliveTime: Date?
    @Published var lastTransportError = "No transport errors."
    @Published var presentedSheet: PresentedSheet?

    private let dependencies: Dependencies
    private var currentTransportClient: AudioTransportClient?
    private var captureOwnedByTransport = false
    private var transportAttemptCount = 0

    init(dependencies: Dependencies = .live()) {
        self.dependencies = dependencies

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

        if transportStatus.isActive && transportStatus != .streaming {
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
            return activeTransportStatusDetail(fallback: "Connecting to \(hostConfiguration.displayEndpoint).")
        case .controlConnected:
            return activeTransportStatusDetail(fallback: "Control channel connected. Beginning transport handshake.")
        case .handshakeAccepted:
            return activeTransportStatusDetail(fallback: "Handshake accepted. Waiting for realtime stream start.")
        case .connected:
            return activeTransportStatusDetail(fallback: "Transport ready. Starting microphone capture.")
        case .streaming:
            return "Streaming voice audio to \(hostConfiguration.displayEndpoint). Tap the mic again to stop."
        case .stopping:
            return activeTransportStatusDetail(fallback: "Stopping the current session.")
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
                return activeTransportStatusDetail(fallback: "Opening the transport connection to the Windows host.")
            case .controlConnected:
                return activeTransportStatusDetail(fallback: "The control channel is open and the handshake is in progress.")
            case .handshakeAccepted:
                return activeTransportStatusDetail(fallback: "The host accepted the handshake and is preparing the stream.")
            case .connected:
                return activeTransportStatusDetail(fallback: "The transport path is ready and the audio pipeline is starting.")
            case .streaming:
                return "Live session is active. Tap to edit host settings."
            case .stopping:
                return activeTransportStatusDetail(fallback: "Shutting down the live session.")
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

    var lastKeepAliveSummary: String {
        guard let lastKeepAliveTime else {
            return "No keepalive sent yet."
        }

        return lastKeepAliveTime.formatted(date: .omitted, time: .standard)
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

        if transportStatus.isActive && transportStatus != .streaming {
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
        guard let validatedPort = hostConfiguration.validatedPort else {
            setupStatus = .setupRequired
            setupDetail = hostConfiguration.transportMode == .udpRealtime
                ? "Enter a valid realtime control port between 1 and 65535."
                : "Enter a valid TCP port between 1 and 65535."
            return
        }

        guard !hostConfiguration.trimmedHostAddress.isEmpty else {
            setupStatus = .setupRequired
            setupDetail = "Enter the Windows host IP address or hostname."
            return
        }

        setupStatus = .ready
        setupDetail = "Ready to stream to \(hostConfiguration.trimmedHostAddress):\(validatedPort) over \(hostConfiguration.transportMode.label)."
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
            log("\(hostConfiguration.transportMode.label) connect attempted before setup was ready.")
            return
        }

        guard !transportStatus.isActive else {
            return
        }

        presentedSheet = nil

        if captureStatus == .starting || captureStatus == .capturing {
            stopCapture(
                reason: "Capture stopped so the transport session can restart cleanly.",
                logMessage: "Stopped the existing capture session before starting \(hostConfiguration.transportMode.label) streaming."
            )
        }

        transportFramesSent = 0
        transportBytesSent = 0
        transportControlMessagesSent = 0
        transportControlMessagesReceived = 0
        lastSuccessfulSendTime = nil
        lastKeepAliveTime = nil
        lastTransportError = "No transport errors."
        captureOwnedByTransport = false
        transportAttemptCount += 1
        transportReconnectCount = max(0, transportAttemptCount - 1)
        transportStatus = .connecting
        transportDetail = initialTransportConnectDetail

        log("Connecting to the Windows host at \(hostConfiguration.displayEndpoint) using \(hostConfiguration.transportMode.label).")

        let transportClient = dependencies.makeTransportClient(hostConfiguration.transportMode) { [weak self] event in
            Task { @MainActor in
                self?.handleTransportEvent(event)
            }
        }
        currentTransportClient = transportClient

        do {
            try transportClient.connect(
                hostConfiguration.trimmedHostAddress,
                hostConfiguration.validatedPort ?? HostConfiguration.defaultDebugTcpPort,
                .defaultVoice
            )
        } catch {
            handleTransportFailure(
                detail: error.localizedDescription,
                logMessage: "\(hostConfiguration.transportMode.label) connect failed: \(error.localizedDescription)"
            )
        }
    }

    func disconnectTransport() {
        guard transportStatus.isActive else {
            return
        }

        transportStatus = .stopping
        transportDetail = stoppingTransportDetail
        log("Stopping the \(hostConfiguration.transportMode.label) session.")

        if captureStatus == .starting || captureStatus == .capturing {
            stopCapture(
                reason: "Capture stopped. Ready to start again.",
                logMessage: nil
            )
        }

        captureOwnedByTransport = false
        currentTransportClient?.disconnect()
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
        case .controlConnected:
            return "Control"
        case .handshakeAccepted:
            return "Handshake"
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
        case .controlConnected:
            return activeTransportStatusDetail(fallback: "The TCP control channel is open and the host handshake is underway.")
        case .handshakeAccepted:
            return activeTransportStatusDetail(fallback: "The host accepted the handshake and the realtime stream is starting.")
        case .connected:
            return activeTransportStatusDetail(fallback: "The host transport is ready and the audio pipeline is starting.")
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
        case .connecting, .controlConnected, .handshakeAccepted, .connected:
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
        case .connecting, .controlConnected, .handshakeAccepted, .connected, .stopping:
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
            case .connecting, .controlConnected, .handshakeAccepted, .connected:
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

    private var initialTransportConnectDetail: String {
        switch hostConfiguration.transportMode {
        case .tcpDebug:
            return "Opening TCP debug connection to \(hostConfiguration.displayEndpoint)."
        case .udpRealtime:
            return "Opening TCP control channel to \(hostConfiguration.displayEndpoint)."
        }
    }

    private var stoppingTransportDetail: String {
        switch hostConfiguration.transportMode {
        case .tcpDebug:
            return "Stopping microphone capture and closing the TCP debug socket."
        case .udpRealtime:
            return "Stopping microphone capture and closing the realtime control session."
        }
    }

    private var streamingTransportDetail: String {
        switch hostConfiguration.transportMode {
        case .tcpDebug:
            return "Streaming raw PCM to \(hostConfiguration.displayEndpoint)."
        case .udpRealtime:
            return "Streaming realtime raw PCM to the Windows host."
        }
    }

    private func activeTransportStatusDetail(fallback: String) -> String {
        transportDetail.isEmpty ? fallback : transportDetail
    }

    private var hostSetupHint: String {
        switch setupStatus {
        case .ready:
            return "Ready to stream to \(hostConfiguration.displayEndpoint)."
        case .setupRequired:
            if hostConfiguration.trimmedHostAddress.isEmpty {
                return "Add your Windows host IP address to continue."
            }

            if hostConfiguration.validatedPort == nil {
                return hostConfiguration.transportMode == .udpRealtime
                    ? "Enter the TCP control port used by the Windows host."
                    : "Enter the TCP port used by the Windows host."
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
            ? "Configuring AVAudioSession for \(hostConfiguration.transportMode.label) streaming."
            : "Configuring AVAudioSession and starting the microphone tap."
        captureSessionSummary = streamToTransport ? "Starting stream capture." : "Starting capture."
        latestInputLevel = .silence
        capturedFrameCount = 0
        latestFrameSummary = "Waiting for the first framed packet."

        do {
            let model = self
            let transportClient = currentTransportClient
            let inputLevelUpdateBuffer = InputLevelUpdateBuffer(updateEveryCallbacks: streamToTransport ? 3 : 1)
            let captureFrameUpdateBuffer = CaptureFrameUpdateBuffer(updateEveryFrames: streamToTransport ? 25 : 1)
            let transportSendProgressBuffer = TransportSendProgressBuffer(flushEveryFrames: 25)
            let startup = try dependencies.startCapture(
                .defaultVoice,
                { inputLevel in
                    guard let levelUpdate = inputLevelUpdateBuffer.record(inputLevel) else {
                        return
                    }

                    Task { @MainActor in
                        model.latestInputLevel = levelUpdate
                    }
                },
                { frame in
                    if let captureSnapshot = captureFrameUpdateBuffer.record(frame) {
                        Task { @MainActor in
                            model.handleCapturedFrameUpdate(captureSnapshot, streamToTransport: streamToTransport)
                        }
                    }

                    if streamToTransport {
                        transportClient?.sendFrame(frame) { result in
                            switch result {
                            case .success(let bytesSent):
                                guard let progressSnapshot = transportSendProgressBuffer.recordSuccess(bytesSent: bytesSent) else {
                                    return
                                }

                                Task { @MainActor in
                                    model.handleSendProgress(progressSnapshot, latestFrame: frame)
                                }
                            case .failure:
                                Task { @MainActor in
                                    model.handleSendCompletion(result, for: frame)
                                }
                            }
                        }
                    }
                },
                { error in
                    Task { @MainActor in
                        model.handleCaptureFailure(error)
                    }
                }
            )

            captureStatus = .capturing
            captureDetail = streamToTransport
                ? "Live microphone capture is feeding the \(hostConfiguration.transportMode.label) stream at \(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) ms packet cadence."
                : "Live microphone capture is running at \(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) ms packet cadence."
            captureSessionSummary = startup.debugSummary
            if streamToTransport {
                log("Microphone capture started for \(hostConfiguration.transportMode.label) streaming. \(startup.debugSummary)")
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
                    detail: "\(hostConfiguration.transportMode.label) transport opened but microphone capture failed: \(error.localizedDescription)",
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

    private func handleCapturedFrameUpdate(
        _ snapshot: CaptureFrameUpdateBuffer.Snapshot,
        streamToTransport: Bool
    ) {
        capturedFrameCount = snapshot.capturedFrameCount
        latestFrameSummary = snapshot.latestFrameSummary

        if snapshot.isFirstFrame {
            if streamToTransport {
                log("First microphone frame captured for streaming: \(snapshot.latestFrameSummary).")
            } else {
                log("First microphone frame captured: \(snapshot.latestFrameSummary).")
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

    private func handleTransportEvent(_ event: AudioTransportEvent) {
        switch event {
        case .stateChanged(let lifecycleState, let detail):
            switch lifecycleState {
            case .connecting:
                guard transportStatus == .connecting else {
                    return
                }

                transportDetail = detail
            case .controlConnected:
                transportStatus = .controlConnected
                transportDetail = detail
                log(detail)
            case .handshakeAccepted:
                transportStatus = .handshakeAccepted
                transportDetail = detail
                log(detail)
            case .readyForAudio:
                guard transportStatus != .stopping else {
                    return
                }

                transportStatus = .connected
                transportDetail = detail
                captureOwnedByTransport = true
                log(detail)

                Task {
                    await startCapturePipeline(streamToTransport: true)
                }
            }
        case .controlMessageSent(let messageType):
            transportControlMessagesSent += 1
            if messageType != .keepAlive {
                log("Control message sent: \(messageType.rawValue).")
            }
        case .controlMessageReceived(let messageType):
            transportControlMessagesReceived += 1
            if messageType != .keepAlive {
                log("Control message received: \(messageType.rawValue).")
            }
        case .keepAliveSent(let date):
            lastKeepAliveTime = date
        case .failed(let detail):
            handleTransportFailure(detail: detail, logMessage: "\(hostConfiguration.transportMode.label) transport failed: \(detail)")
        case .stopped(let detail):
            currentTransportClient = nil
            captureOwnedByTransport = false
            if transportStatus == .stopping {
                transportStatus = .disconnected
                transportDetail = detail
                log(detail)
            } else if transportStatus != .error {
                transportStatus = .disconnected
                transportDetail = detail
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
                transportDetail = streamingTransportDetail
                log("Streaming started with microphone frame \(frame.sequenceNumber) over \(hostConfiguration.transportMode.label).")
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

    private func handleSendProgress(
        _ snapshot: TransportSendProgressBuffer.Snapshot,
        latestFrame: CapturedAudioFrame
    ) {
        guard captureOwnedByTransport || transportStatus == .connected || transportStatus == .streaming else {
            return
        }

        transportFramesSent = snapshot.totalFramesSent
        transportBytesSent = snapshot.totalBytesSent
        lastSuccessfulSendTime = Date()

        if transportStatus == .connected && snapshot.isFirstSuccess {
            transportStatus = .streaming
            transportDetail = streamingTransportDetail
            log("Streaming started with microphone frame \(latestFrame.sequenceNumber) over \(hostConfiguration.transportMode.label).")
        } else if transportFramesSent.isMultiple(of: 250) {
            log("Streaming health: \(transportFramesSent) frames / \(transportBytesSent) bytes sent to \(hostConfiguration.displayEndpoint).")
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
        let transportClient = currentTransportClient
        currentTransportClient = nil
        transportStatus = .error
        transportDetail = detail
        lastTransportError = detail
        log(logMessage)
        transportClient?.disconnect()
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
