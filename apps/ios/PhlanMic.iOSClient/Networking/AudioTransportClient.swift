import Foundation

enum AudioTransportLifecycleState: Sendable {
    case connecting
    case controlConnected
    case handshakeAccepted
    case readyForAudio
}

enum AudioTransportEvent: Sendable {
    case stateChanged(AudioTransportLifecycleState, detail: String)
    case controlMessageSent(TransportControlMessageType)
    case controlMessageReceived(TransportControlMessageType)
    case keepAliveSent(Date)
    case failed(String)
    case stopped(String)
}

struct AudioTransportClient {
    let connect: @Sendable (_ host: String, _ port: UInt16, _ format: MVPAudioFormat) throws -> Void
    let disconnect: @Sendable () -> Void
    let sendFrame: @Sendable (_ frame: CapturedAudioFrame, _ completion: @escaping @Sendable (Result<Int, Error>) -> Void) -> Void
}
