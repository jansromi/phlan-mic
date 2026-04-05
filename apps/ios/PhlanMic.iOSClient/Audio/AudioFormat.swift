import Foundation

struct MVPAudioFormat: Equatable {
    let sampleRate: Int
    let channelCount: Int
    let bitsPerSample: Int
    let packetDurationMilliseconds: Int

    static let defaultVoice = MVPAudioFormat(
        sampleRate: 48_000,
        channelCount: 1,
        bitsPerSample: 16,
        packetDurationMilliseconds: 20
    )

    var framesPerPacket: Int {
        sampleRate * packetDurationMilliseconds / 1_000
    }

    var bytesPerFrame: Int {
        channelCount * bitsPerSample / 8
    }

    var bytesPerPacket: Int {
        framesPerPacket * bytesPerFrame
    }

    var debugSummary: String {
        "\(sampleRate) Hz, \(channelCount) ch, \(bitsPerSample)-bit PCM, \(packetDurationMilliseconds) ms (\(framesPerPacket) frames / \(bytesPerPacket) bytes)"
    }
}
