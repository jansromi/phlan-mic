import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    enum ConnectionStatus: String {
        case setupRequired = "Setup Required"
        case ready = "Ready"
        case connecting = "Connecting"
        case connected = "Connected"
        case error = "Error"

        var tintName: String {
            switch self {
            case .setupRequired:
                "orange"
            case .ready:
                "blue"
            case .connecting:
                "yellow"
            case .connected:
                "green"
            case .error:
                "red"
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

    struct DiagnosticEntry: Identifiable, Equatable {
        let id = UUID()
        let timestamp: Date
        let message: String
    }

    @Published var hostConfiguration = HostConfiguration()
    @Published var microphonePermission = MicrophonePermissionState.unknown
    @Published var connectionStatus = ConnectionStatus.setupRequired
    @Published var connectionDetail = "Enter a host address and port to prepare the session."
    @Published var diagnostics: [DiagnosticEntry] = []
    @Published var startupSnapshot = StartupSnapshot.current
    @Published var captureStatus = CaptureStatus.unavailable
    @Published var captureDetail = "Request microphone permission before starting live capture."
    @Published var captureSessionSummary = "Not started."
    @Published var latestInputLevel = AudioInputLevel.silence
    @Published var capturedFrameCount = 0
    @Published var latestFrameSummary = "No audio frames captured yet."

    private let microphonePermissionClient = MicrophonePermissionClient()
    private let microphoneCaptureClient = MicrophoneCaptureClient()
    private let logger = AppLogger()

    init() {
        log("App model initialized.")
        refreshConnectionReadiness()
        syncCaptureAvailability()
        logStartupSnapshot()
    }

    func loadStartupState() async {
        let status = await microphonePermissionClient.currentStatus()
        microphonePermission = status
        syncCaptureAvailability()
        log("Microphone permission state: \(status.label).")
    }

    func requestMicrophonePermission() async {
        let status = await microphonePermissionClient.requestPermission()
        microphonePermission = status
        syncCaptureAvailability()
        log("Microphone permission request completed with state: \(status.label).")
    }

    func refreshConnectionReadiness() {
        guard let validatedPort = hostConfiguration.validatedPort else {
            connectionStatus = .setupRequired
            connectionDetail = "Enter a valid TCP port between 1 and 65535."
            return
        }

        guard !hostConfiguration.hostAddress.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            connectionStatus = .setupRequired
            connectionDetail = "Enter the Windows host IP address or hostname."
            return
        }

        connectionStatus = .ready
        connectionDetail = "Manual \(hostConfiguration.transportMode.label) bring-up is ready for \(hostConfiguration.hostAddress):\(validatedPort)."
    }

    func updateHostAddress(_ hostAddress: String) {
        hostConfiguration.hostAddress = hostAddress
        refreshConnectionReadiness()
    }

    func updatePortText(_ portText: String) {
        hostConfiguration.portText = portText
        refreshConnectionReadiness()
    }

    func updateTransportMode(_ transportMode: HostConfiguration.TransportMode) {
        hostConfiguration.transportMode = transportMode
        refreshConnectionReadiness()
    }

    func recordBringUpCheckpoint() {
        refreshConnectionReadiness()

        switch connectionStatus {
        case .ready:
            log("Bring-up checkpoint recorded for \(hostConfiguration.displayEndpoint) using \(hostConfiguration.transportMode.label).")
        default:
            log("Bring-up checkpoint attempted before configuration was ready.")
        }
    }

    func startCapture() async {
        guard captureStatus != .starting, captureStatus != .capturing else {
            return
        }

        if microphonePermission == .unknown {
            await requestMicrophonePermission()
        }

        guard microphonePermission == .granted else {
            syncCaptureAvailability()
            return
        }

        captureStatus = .starting
        captureDetail = "Configuring AVAudioSession and starting the microphone tap."
        captureSessionSummary = "Starting capture."
        latestInputLevel = .silence
        capturedFrameCount = 0
        latestFrameSummary = "Waiting for the first framed packet."

        do {
            let startup = try microphoneCaptureClient.startCapture(
                format: .defaultVoice,
                onInputLevel: { [weak self] inputLevel in
                    Task { @MainActor in
                        self?.latestInputLevel = inputLevel
                    }
                },
                onFrame: { [weak self] frame in
                    Task { @MainActor in
                        self?.handleCapturedFrame(frame)
                    }
                },
                onFailure: { [weak self] error in
                    Task { @MainActor in
                        self?.handleCaptureFailure(error)
                    }
                }
            )

            captureStatus = .capturing
            captureDetail = "Live microphone capture is running at \(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) ms packet cadence."
            captureSessionSummary = startup.debugSummary
            log("Microphone capture started. \(startup.debugSummary)")
        } catch {
            captureStatus = .error
            captureDetail = error.localizedDescription
            captureSessionSummary = "Capture failed to start."
            latestInputLevel = .silence
            log("Microphone capture failed to start: \(error.localizedDescription)")
        }
    }

    func stopCapture() {
        guard captureStatus == .starting || captureStatus == .capturing else {
            return
        }

        microphoneCaptureClient.stopCapture()
        latestInputLevel = .silence
        captureSessionSummary = "Capture stopped."
        syncCaptureAvailability(reason: "Capture stopped. Ready to start again.")
        log("Microphone capture stopped.")
    }

    func log(_ message: String) {
        logger.log(message)
        diagnostics.insert(DiagnosticEntry(timestamp: Date(), message: message), at: 0)
        diagnostics = Array(diagnostics.prefix(12))
    }

    var canRequestMicrophonePermission: Bool {
        microphonePermission != .simulatorUnavailable
    }

    var canStartCapture: Bool {
        microphonePermission == .granted && captureStatus != .starting && captureStatus != .capturing
    }

    var canStopCapture: Bool {
        captureStatus == .starting || captureStatus == .capturing
    }

    private func logStartupSnapshot() {
        log("Running on \(startupSnapshot.platformDescription).")
        log("Preferred MVP audio format: \(MVPAudioFormat.defaultVoice.debugSummary).")
        log("Packet cadence: \(MVPAudioPacket.prototype.debugSummary).")
    }

    private func handleCapturedFrame(_ frame: CapturedAudioFrame) {
        capturedFrameCount = Int(frame.sequenceNumber + 1)
        latestFrameSummary = frame.debugSummary

        if frame.sequenceNumber == 0 {
            log("First microphone frame captured: \(frame.debugSummary).")
        }
    }

    private func handleCaptureFailure(_ error: Error) {
        microphoneCaptureClient.stopCapture()
        latestInputLevel = .silence
        captureStatus = .error
        captureDetail = error.localizedDescription
        captureSessionSummary = "Capture failed."
        log("Microphone capture failed: \(error.localizedDescription)")
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
