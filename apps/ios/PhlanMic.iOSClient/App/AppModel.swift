import Combine
import Foundation
import SwiftUI

enum AppSceneState: String, Sendable {
    case active
    case inactive
    case background

    var debugLabel: String {
        rawValue.capitalized
    }
}

enum CaptureInterruptionState: Sendable, Equatable {
    case notInterrupted
    case began
    case ended(shouldResume: Bool)

    var debugLabel: String {
        switch self {
        case .notInterrupted:
            "None"
        case .began:
            "Began"
        case .ended(let shouldResume):
            shouldResume ? "Ended (resume suggested)" : "Ended (manual restart)"
        }
    }
}

enum CaptureRouteChangeReason: String, Sendable, Equatable {
    case unknown
    case newDeviceAvailable
    case oldDeviceUnavailable
    case categoryChange
    case override
    case wakeFromSleep
    case noSuitableRouteForCategory
    case routeConfigurationChange

    var debugLabel: String {
        switch self {
        case .unknown:
            "Unknown"
        case .newDeviceAvailable:
            "New Device"
        case .oldDeviceUnavailable:
            "Old Device Unavailable"
        case .categoryChange:
            "Category Change"
        case .override:
            "Override"
        case .wakeFromSleep:
            "Wake From Sleep"
        case .noSuitableRouteForCategory:
            "No Suitable Route"
        case .routeConfigurationChange:
            "Route Reconfigured"
        }
    }

    var shouldStopRunningCaptureWhenInputRemainsAvailable: Bool {
        switch self {
        case .oldDeviceUnavailable, .noSuitableRouteForCategory:
            true
        case .unknown, .newDeviceAvailable, .categoryChange, .override, .wakeFromSleep, .routeConfigurationChange:
            false
        }
    }
}

struct CaptureRouteChange: Sendable, Equatable {
    let reason: CaptureRouteChangeReason
    let inputAvailable: Bool
    let routeSummary: String

    var debugLabel: String {
        "\(reason.debugLabel) | input \(inputAvailable ? "available" : "missing") | \(routeSummary)"
    }
}

enum CaptureSessionEvent: Sendable, Equatable {
    case interruptionBegan
    case interruptionEnded(shouldResume: Bool)
    case routeChanged(CaptureRouteChange)
    case mediaServicesWereReset
}

enum SessionStopReason: String, Sendable, Equatable {
    case userRequested
    case sceneBecameInactive
    case sceneEnteredBackground
    case audioInterrupted
    case routeInvalidated
    case captureFailed
    case transportFailed

    var debugLabel: String {
        switch self {
        case .userRequested:
            "User Requested"
        case .sceneBecameInactive:
            "Scene Inactive"
        case .sceneEnteredBackground:
            "Scene Backgrounded"
        case .audioInterrupted:
            "Audio Interrupted"
        case .routeInvalidated:
            "Route Invalidated"
        case .captureFailed:
            "Capture Failed"
        case .transportFailed:
            "Transport Failed"
        }
    }
}

enum BackgroundContinuationPolicy: String, Sendable, Equatable {
    case foregroundOnly
    case activeSessionOnly

    var debugLabel: String {
        switch self {
        case .foregroundOnly:
            "Foreground Only"
        case .activeSessionOnly:
            "Active Session Only"
        }
    }

    var defaultDetail: String {
        switch self {
        case .foregroundOnly:
            "Foreground start is required and live audio stops when the app is no longer visible."
        case .activeSessionOnly:
            "Foreground start is required. A live session may continue when the app is locked or backgrounded."
        }
    }
}

@MainActor
final class AppModel: ObservableObject {
    private enum TransportTerminalCause: Sendable, Equatable {
        case none
        case userStop
        case systemStop
        case timeout
        case failure

        var debugLabel: String {
            switch self {
            case .none:
                "None"
            case .userStop:
                "User Stop"
            case .systemStop:
                "System Stop"
            case .timeout:
                "Timeout"
            case .failure:
                "Failure"
            }
        }
    }

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
            CaptureAudioSessionProfile,
            @escaping @Sendable (AudioInputLevel) -> Void,
            @escaping @Sendable (CapturedAudioFrame) -> Void,
            @escaping @Sendable (Error) -> Void,
            @escaping @Sendable (CaptureSessionEvent) -> Void
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
                startCapture: { format, profile, onInputLevel, onFrame, onFailure, onSessionEvent in
                    try captureClient.startCapture(
                        format: format,
                        profile: profile,
                        onInputLevel: onInputLevel,
                        onFrame: onFrame,
                        onFailure: onFailure,
                        onSessionEvent: onSessionEvent
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
    @Published var lastSceneState = AppSceneState.active
    @Published var lastInterruptionState = CaptureInterruptionState.notInterrupted
    @Published var lastRouteChange: CaptureRouteChange?
    @Published var lastSystemStopReason: SessionStopReason?
    @Published var lastSystemStopDetail = "No lifecycle stop recorded."
    @Published var backgroundContinuationPolicy = BackgroundContinuationPolicy.activeSessionOnly {
        didSet {
            if !isStreamingInBackground, lastBackgroundTransitionTime == nil {
                backgroundStatusDetail = backgroundContinuationPolicy.defaultDetail
            }
        }
    }
    @Published var isStreamingInBackground = false
    @Published var backgroundStatusDetail = BackgroundContinuationPolicy.activeSessionOnly.defaultDetail

    private let dependencies: Dependencies
    private var currentTransportClient: AudioTransportClient?
    private var captureOwnedByTransport = false
    private var transportAttemptCount = 0
    private var currentSceneState = AppSceneState.active
    private var pendingSystemStop: PendingSystemStop?
    private var captureAudioSessionProfile = CaptureAudioSessionProfile.recordMeasurement
    private var lastBackgroundTransitionTime: Date?
    private var lastTransportTerminalCause = TransportTerminalCause.none

    private struct PendingSystemStop {
        let reason: SessionStopReason
        let detail: String
    }

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
            return "Stopped"
        }

        if shouldSurfaceLifecycleStop {
            return "Session Paused"
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
            if shouldSurfaceBackgroundStatus {
                return backgroundStatusDetail
            }

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
            return "Streaming is stopped. Debug capture is still running from the debug view."
        }

        if shouldSurfaceLifecycleStop {
            return lastSystemStopDetail
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
                return ""
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

        if shouldSurfaceLifecycleStop {
            return "Paused"
        }

        return setupStatus.rawValue
    }

    var connectionCardDetail: String {
        if transportStatus == .error {
            return lastTransportError
        }

        if transportStatus.isActive {
            if shouldSurfaceBackgroundStatus, transportStatus == .streaming {
                return backgroundStatusDetail
            }

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

        if shouldSurfaceLifecycleStop {
            return lastSystemStopDetail
        }

        return setupStatus == .ready
            ? "Tap to edit the host or port."
            : hostSetupHint
    }

    var connectionCardTintName: String {
        if transportStatus == .error {
            return transportStatus.tintName
        }

        if transportStatus.isActive {
            return transportStatus.tintName
        }

        if shouldSurfaceLifecycleStop {
            return "orange"
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

        if shouldSurfaceBackgroundStatus {
            return backgroundStatusDetail
        }

        if transportStatus.isActive && transportStatus != .streaming {
            return transportDetail
        }

        if shouldSurfaceLifecycleStop {
            return lastSystemStopDetail
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

    func handleScenePhaseChange(_ scenePhase: ScenePhase) {
        let nextSceneState: AppSceneState
        switch scenePhase {
        case .active:
            nextSceneState = .active
        case .inactive:
            nextSceneState = .inactive
        case .background:
            nextSceneState = .background
        @unknown default:
            nextSceneState = .active
        }

        guard nextSceneState != currentSceneState else {
            return
        }

        currentSceneState = nextSceneState
        lastSceneState = nextSceneState
        log("Scene state changed to \(nextSceneState.rawValue).")

        switch nextSceneState {
        case .active:
            handleSceneBecameActive()
            return
        case .inactive:
            handleSceneDeactivation(
                nextSceneState,
                stopReason: .sceneBecameInactive,
                stopDetail: "The session stopped because the app became inactive. Bring the app back to the foreground and start again.",
                stopLogMessage: "Scene became inactive while audio was active."
            )
        case .background:
            handleSceneDeactivation(
                nextSceneState,
                stopReason: .sceneEnteredBackground,
                stopDetail: "The session stopped because the app entered the background. Reopen the app and start again.",
                stopLogMessage: "Scene entered the background while audio was active."
            )
        }
    }

    func handleCaptureSessionEvent(_ event: CaptureSessionEvent) {
        switch event {
        case .interruptionBegan:
            lastInterruptionState = .began
            log("Audio session interruption began.")
            stopActiveSession(
                reason: .audioInterrupted,
                detail: "The session stopped because iOS interrupted microphone access. Start again when the interruption ends.",
                logMessage: "Audio interruption began while audio was active."
            )
        case .interruptionEnded(let shouldResume):
            lastInterruptionState = .ended(shouldResume: shouldResume)
            let detail = shouldResume
                ? "Audio interruption ended. iOS allows a resume, but this app requires a manual restart."
                : "Audio interruption ended. Restart the session when you are ready."
            log("Audio session interruption ended. shouldResume=\(shouldResume).")

            if lastSystemStopReason == .audioInterrupted, pendingSystemStop == nil, !transportStatus.isActive {
                lastSystemStopDetail = detail
                if captureStatus == .ready {
                    captureDetail = detail
                }
            }
        case .routeChanged(let routeChange):
            lastRouteChange = routeChange
            log("Audio route changed: \(routeChange.debugLabel).")

            guard hasActiveAudioSession else {
                return
            }

            guard
                !routeChange.inputAvailable ||
                routeChange.reason.shouldStopRunningCaptureWhenInputRemainsAvailable
            else {
                return
            }

            stopActiveSession(
                reason: .routeInvalidated,
                detail: routeInvalidationDetail(for: routeChange),
                logMessage: "Audio route change invalidated the running microphone path."
            )
        case .mediaServicesWereReset:
            log("Audio media services were reset.")
            stopActiveSession(
                reason: .captureFailed,
                detail: "The session stopped because iOS audio services were reset. Start again to restore capture.",
                logMessage: "Audio media services reset while audio was active."
            )
        }
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

        clearLifecycleStopPresentation()
        clearBackgroundContinuationStatus()
        lastTransportTerminalCause = .none
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
        clearLifecycleStopPresentation()
        clearBackgroundContinuationStatus()
        lastTransportTerminalCause = .none

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

        clearLifecycleStopPresentation()
        clearBackgroundContinuationStatus()
        lastTransportTerminalCause = .userStop
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

    var lastBackgroundTransitionSummary: String {
        guard let lastBackgroundTransitionTime else {
            return "None"
        }

        return lastBackgroundTransitionTime.formatted(date: .omitted, time: .standard)
    }

    var lastTransportTerminalCauseSummary: String {
        lastTransportTerminalCause.debugLabel
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
            if shouldSurfaceLifecycleStop {
                return "Paused"
            }

            return setupStatus == .ready ? "Ready" : "Setup"
        }
    }

    private var sessionHealthConnectionTintName: String {
        if transportStatus != .disconnected {
            return transportStatus.tintName
        }

        if shouldSurfaceLifecycleStop {
            return "orange"
        }

        return setupStatus.tintName
    }

    private var sessionHealthConnectionDetail: String {
        switch transportStatus {
        case .streaming:
            if shouldSurfaceBackgroundStatus {
                return backgroundStatusDetail
            }

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
            if shouldSurfaceLifecycleStop {
                return lastSystemStopDetail
            }

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
                captureAudioSessionProfile,
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
                },
                { event in
                    Task { @MainActor in
                        model.handleCaptureSessionEvent(event)
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
        guard captureStatus == .starting || captureStatus == .capturing || captureOwnedByTransport else {
            return
        }

        guard pendingSystemStop == nil else {
            return
        }

        latestInputLevel = .silence
        captureStatus = .error
        captureDetail = error.localizedDescription
        captureSessionSummary = captureOwnedByTransport ? "Stream capture failed." : "Capture failed."

        if captureOwnedByTransport {
            captureOwnedByTransport = false
            clearBackgroundContinuationStatus()
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
            if pendingSystemStop != nil {
                completePendingSystemStop()
                return
            }

            currentTransportClient = nil
            captureOwnedByTransport = false
            if transportStatus == .stopping {
                transportStatus = .disconnected
                transportDetail = detail
                clearBackgroundContinuationStatus()
                log(detail)
            } else if transportStatus != .error {
                transportStatus = .disconnected
                transportDetail = detail
                clearBackgroundContinuationStatus()
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
        if pendingSystemStop != nil {
            completePendingSystemStop()
            return
        }

        let resolvedDetail = resolvedTransportFailureDetail(from: detail)

        if captureStatus == .starting || captureStatus == .capturing {
            dependencies.stopCapture()
            resetCaptureAfterStop(reason: "Transport is idle. Ready to capture again.")
        }

        captureOwnedByTransport = false
        let transportClient = currentTransportClient
        currentTransportClient = nil
        lastTransportTerminalCause = isTransportTimeout(detail) ? .timeout : .failure
        transportStatus = .error
        transportDetail = resolvedDetail
        lastTransportError = resolvedDetail
        updateBackgroundContinuationStatusForTerminalFailure(detail: resolvedDetail)
        log(logMessage)
        transportClient?.disconnect()
    }

    private func stopCapture(reason: String, logMessage: String?) {
        guard captureStatus == .starting || captureStatus == .capturing else {
            return
        }

        dependencies.stopCapture()
        clearBackgroundContinuationStatus()
        resetCaptureAfterStop(reason: reason)

        if let logMessage {
            log(logMessage)
        }
    }

    private func stopActiveSession(reason: SessionStopReason, detail: String, logMessage: String) {
        guard hasActiveAudioSession else {
            return
        }

        guard pendingSystemStop == nil else {
            return
        }

        pendingSystemStop = PendingSystemStop(reason: reason, detail: detail)
        lastSystemStopReason = reason
        lastSystemStopDetail = detail
        lastTransportTerminalCause = .systemStop
        clearBackgroundContinuationStatus()
        log(logMessage)

        if captureStatus == .starting || captureStatus == .capturing {
            dependencies.stopCapture()
            resetCaptureAfterStop(reason: detail)
        }

        if transportStatus.isActive {
            transportStatus = .stopping
            transportDetail = detail
            currentTransportClient?.disconnect()
        } else {
            completePendingSystemStop()
        }
    }

    private func completePendingSystemStop() {
        guard let pendingSystemStop else {
            return
        }

        self.pendingSystemStop = nil
        currentTransportClient = nil
        captureOwnedByTransport = false
        transportStatus = .disconnected
        transportDetail = pendingSystemStop.detail
        lastTransportError = "No transport errors."
    }

    private func clearLifecycleStopPresentation() {
        pendingSystemStop = nil
        lastSystemStopReason = nil
        lastSystemStopDetail = "No lifecycle stop recorded."
    }

    private func clearBackgroundContinuationStatus() {
        isStreamingInBackground = false
        lastBackgroundTransitionTime = nil
        backgroundStatusDetail = backgroundContinuationPolicy.defaultDetail
    }

    private func handleSceneBecameActive() {
        guard backgroundContinuationPolicy == .activeSessionOnly else {
            return
        }

        guard lastBackgroundTransitionTime != nil else {
            return
        }

        let hadBackgroundStreaming = isStreamingInBackground
        isStreamingInBackground = false

        guard transportStatus.isActive || captureStatus == .capturing else {
            return
        }

        if transportStatus == .streaming || (captureStatus == .capturing && !captureOwnedByTransport) {
            backgroundStatusDetail = hadBackgroundStreaming
                ? "The active session continued while the app was in the background and is still live."
                : "The active session remained live through the last inactive transition."
            log("Returned to the foreground with the active session still running.")
        } else {
            backgroundStatusDetail = backgroundContinuationPolicy.defaultDetail
        }
    }

    private func handleSceneDeactivation(
        _ nextSceneState: AppSceneState,
        stopReason: SessionStopReason,
        stopDetail: String,
        stopLogMessage: String
    ) {
        if shouldContinueCurrentSessionInBackground {
            lastBackgroundTransitionTime = Date()
            isStreamingInBackground = transportStatus == .streaming
            backgroundStatusDetail = backgroundContinuationDetail(for: nextSceneState)
            log(backgroundContinuationLogMessage(for: nextSceneState))
            return
        }

        stopActiveSession(reason: stopReason, detail: stopDetail, logMessage: stopLogMessage)
    }

    private var shouldSurfaceLifecycleStop: Bool {
        lastSystemStopReason != nil && !transportStatus.isActive && transportStatus != .error
    }

    private var shouldContinueCurrentSessionInBackground: Bool {
        guard backgroundContinuationPolicy == .activeSessionOnly else {
            return false
        }

        if transportStatus == .streaming {
            return true
        }

        return !captureOwnedByTransport && captureStatus == .capturing
    }

    private var shouldSurfaceBackgroundStatus: Bool {
        !backgroundStatusDetail.isEmpty && (isStreamingInBackground || lastBackgroundTransitionTime != nil)
    }

    private var hasActiveAudioSession: Bool {
        transportStatus.isActive || captureStatus == .starting || captureStatus == .capturing
    }

    private func backgroundContinuationDetail(for sceneState: AppSceneState) -> String {
        switch sceneState {
        case .inactive:
            if transportStatus == .streaming {
                return "The live session is continuing while the app is inactive. If iOS suspends control traffic, the Windows host may time out."
            }

            return "The active capture session is continuing while the app is inactive."
        case .background:
            if transportStatus == .streaming {
                return "The live session is continuing in the background under the active-session-only policy."
            }

            return "The active capture session is continuing in the background."
        case .active:
            return backgroundContinuationPolicy.defaultDetail
        }
    }

    private func backgroundContinuationLogMessage(for sceneState: AppSceneState) -> String {
        switch sceneState {
        case .inactive:
            return transportStatus == .streaming
                ? "Allowed the live session to continue while the app became inactive."
                : "Allowed the active capture session to continue while the app became inactive."
        case .background:
            return transportStatus == .streaming
                ? "Allowed the live session to continue after the app entered the background."
                : "Allowed the active capture session to continue after the app entered the background."
        case .active:
            return "The app returned to the foreground."
        }
    }

    private func resolvedTransportFailureDetail(from detail: String) -> String {
        guard isTransportTimeout(detail) else {
            return detail
        }

        if currentSceneState == .background || currentSceneState == .inactive || lastBackgroundTransitionTime != nil {
            return "The Windows host timed out waiting for background control activity. Bring the app back to the foreground and reconnect."
        }

        return "The Windows host timed out waiting for control activity. Start the session again."
    }

    private func updateBackgroundContinuationStatusForTerminalFailure(detail: String) {
        if lastTransportTerminalCause == .timeout, currentSceneState != .active || lastBackgroundTransitionTime != nil {
            isStreamingInBackground = false
            backgroundStatusDetail = "Background continuation ended because the Windows host timed out waiting for control activity."
            return
        }

        clearBackgroundContinuationStatus()

        if lastTransportTerminalCause == .failure {
            backgroundStatusDetail = detail
        }
    }

    private func isTransportTimeout(_ detail: String) -> Bool {
        detail.localizedCaseInsensitiveContains("timed out")
    }

    private func routeInvalidationDetail(for routeChange: CaptureRouteChange) -> String {
        if !routeChange.inputAvailable {
            return "The session stopped because the microphone route was lost. Connect a valid input route and start again."
        }

        return "The session stopped because the audio route changed (\(routeChange.reason.debugLabel.lowercased())). Start again on the new route."
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

    private func resetCaptureAfterStop(reason: String) {
        latestInputLevel = .silence
        captureSessionSummary = "Capture stopped."
        captureOwnedByTransport = false
        captureStatus = microphonePermission == .granted ? .ready : .unavailable
        syncCaptureAvailability(reason: reason)
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
