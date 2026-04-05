import Foundation

struct MVPAudioPacket: Equatable {
    let sequenceNumber: UInt64
    let capturedAt: Duration
    let format: MVPAudioFormat
    let payloadByteCount: Int

    static let prototype = MVPAudioPacket(
        sequenceNumber: 0,
        capturedAt: .milliseconds(0),
        format: .defaultVoice,
        payloadByteCount: MVPAudioFormat.defaultVoice.bytesPerPacket
    )

    var debugSummary: String {
        "seq \(sequenceNumber), \(format.framesPerPacket) frames, payload \(payloadByteCount) bytes"
    }
}
