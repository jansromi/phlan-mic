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

    private let microphonePermissionClient = MicrophonePermissionClient()
    private let logger = AppLogger()

    init() {
        log("App model initialized.")
        refreshConnectionReadiness()
        logStartupSnapshot()
    }

    func loadStartupState() async {
        let status = await microphonePermissionClient.currentStatus()
        microphonePermission = status
        log("Microphone permission state: \(status.label).")
    }

    func requestMicrophonePermission() async {
        let status = await microphonePermissionClient.requestPermission()
        microphonePermission = status
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

    func log(_ message: String) {
        logger.log(message)
        diagnostics.insert(DiagnosticEntry(timestamp: Date(), message: message), at: 0)
        diagnostics = Array(diagnostics.prefix(12))
    }

    private func logStartupSnapshot() {
        log("Running on \(startupSnapshot.platformDescription).")
        log("Preferred MVP audio format: \(MVPAudioFormat.defaultVoice.debugSummary).")
        log("Packet cadence: \(MVPAudioPacket.prototype.debugSummary).")
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
