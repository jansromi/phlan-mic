import AVFoundation

enum MicrophonePermissionState: Equatable {
    case unknown
    case granted
    case denied
    case simulatorUnavailable

    var label: String {
        switch self {
        case .unknown:
            "Unknown"
        case .granted:
            "Granted"
        case .denied:
            "Denied"
        case .simulatorUnavailable:
            "Simulator Only"
        }
    }

    var detail: String {
        switch self {
        case .unknown:
            "The app has not requested microphone access yet."
        case .granted:
            "Microphone access is available for capture work on a real device."
        case .denied:
            "Microphone access is blocked. Enable it in Settings before capture work."
        case .simulatorUnavailable:
            "The simulator is suitable for UI bring-up, but live microphone capture still needs a physical iPhone."
        }
    }

    var tintName: String {
        switch self {
        case .unknown:
            "orange"
        case .granted:
            "green"
        case .denied:
            "red"
        case .simulatorUnavailable:
            "orange"
        }
    }
}

struct MicrophonePermissionClient {
    func currentStatus() async -> MicrophonePermissionState {
        #if targetEnvironment(simulator)
        .simulatorUnavailable
        #else
        switch AVAudioApplication.shared.recordPermission {
        case .undetermined:
            .unknown
        case .granted:
            .granted
        case .denied:
            .denied
        @unknown default:
            .unknown
        }
        #endif
    }

    func requestPermission() async -> MicrophonePermissionState {
        #if targetEnvironment(simulator)
        .simulatorUnavailable
        #else
        let granted = await withCheckedContinuation { continuation in
            AVAudioApplication.requestRecordPermission { isGranted in
                continuation.resume(returning: isGranted)
            }
        }

        return granted ? .granted : .denied
        #endif
    }
}
