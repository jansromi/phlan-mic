import Foundation

enum TransportProtocolError: LocalizedError, Equatable, Sendable {
    case emptyControlPayload
    case invalidControlPayload
    case unsupportedProtocolVersion(Int)
    case unsupportedMessageType(String)
    case invalidSessionIdentifier
    case unsupportedPayloadCodec(String)
    case invalidField(String)
    case packetTooSmall
    case invalidPacketMagic
    case unsupportedPacketProtocolVersion(UInt8)
    case unsupportedPacketCodec(UInt8)
    case invalidPayloadLength(Int)
    case payloadTooLarge(Int)
    case packetLengthMismatch

    var errorDescription: String? {
        switch self {
        case .emptyControlPayload:
            "Control message payload cannot be empty."
        case .invalidControlPayload:
            "Control message payload was not valid JSON."
        case .unsupportedProtocolVersion(let version):
            "Protocol version \(version) is not supported."
        case .unsupportedMessageType(let type):
            "Unsupported control message type '\(type)'."
        case .invalidSessionIdentifier:
            "Control message session id is missing or invalid."
        case .unsupportedPayloadCodec(let codec):
            "Payload codec '\(codec)' is missing or unsupported."
        case .invalidField(let detail):
            detail
        case .packetTooSmall:
            "Audio packet is smaller than the fixed protocol header."
        case .invalidPacketMagic:
            "Audio packet magic header is invalid."
        case .unsupportedPacketProtocolVersion(let version):
            "Audio packet protocol version \(version) is not supported."
        case .unsupportedPacketCodec(let codec):
            "Audio packet codec value \(codec) is not supported."
        case .invalidPayloadLength(let length):
            "Audio packet payload length \(length) is invalid."
        case .payloadTooLarge(let length):
            "Audio packet payload length \(length) exceeds the supported maximum."
        case .packetLengthMismatch:
            "Audio packet payload length does not match the datagram size."
        }
    }
}

enum TransportPayloadCodec: UInt8, Codable, Equatable, Sendable {
    case rawPcm16 = 1
    case opus = 2

    var protocolValue: String {
        switch self {
        case .rawPcm16:
            "RawPcm16"
        case .opus:
            "Opus"
        }
    }

    init(protocolValue: String) throws {
        switch protocolValue.lowercased() {
        case "rawpcm16":
            self = .rawPcm16
        case "opus":
            self = .opus
        default:
            throw TransportProtocolError.unsupportedPayloadCodec(protocolValue)
        }
    }
}

enum TransportControlMessageType: String, Codable, Equatable, Sendable {
    case hello
    case helloAccepted
    case startStream
    case startAccepted
    case stopStream
    case keepAlive
    case error
}

enum TransportProtocolConstants {
    static let protocolVersion = 1
    static let audioPacketMagic = "PHLM"
    static let audioPacketHeaderSize = 44
    static let maxPayloadLengthBytes = 8 * 1_024
}

struct TransportControlMessage: Codable, Equatable, Sendable {
    var protocolVersion = TransportProtocolConstants.protocolVersion
    var type: TransportControlMessageType
    var sessionId: String?
    var sessionName: String?
    var audioPort: Int?
    var supportedCodecs: [String]?
    var payloadCodec: String?
    var sampleRate: Int?
    var channels: Int?
    var bitsPerSample: Int?
    var frameDurationMs: Int?
    var keepAliveIntervalMs: Int?
    var sessionTimeoutMs: Int?
    var detail: String?

    func validated() throws -> Self {
        if protocolVersion != TransportProtocolConstants.protocolVersion {
            throw TransportProtocolError.unsupportedProtocolVersion(protocolVersion)
        }

        if let keepAliveIntervalMs, keepAliveIntervalMs <= 0 {
            throw TransportProtocolError.invalidField("Keep-alive interval must be greater than zero when provided.")
        }

        if let sessionTimeoutMs, sessionTimeoutMs <= 0 {
            throw TransportProtocolError.invalidField("Session timeout must be greater than zero when provided.")
        }

        if let audioPort, !(1...65_535).contains(audioPort) {
            throw TransportProtocolError.invalidField("Audio port must be between 1 and 65535 when provided.")
        }

        if let sampleRate, sampleRate <= 0 {
            throw TransportProtocolError.invalidField("Sample rate must be greater than zero when provided.")
        }

        if let channels, channels <= 0 {
            throw TransportProtocolError.invalidField("Channel count must be greater than zero when provided.")
        }

        if let bitsPerSample, bitsPerSample <= 0 {
            throw TransportProtocolError.invalidField("Bits-per-sample must be greater than zero when provided.")
        }

        if let frameDurationMs, frameDurationMs <= 0 {
            throw TransportProtocolError.invalidField("Frame duration must be greater than zero when provided.")
        }

        return self
    }

    func requiredSessionID() throws -> UUID {
        guard let sessionId, let uuid = UUID(uuidString: sessionId) else {
            throw TransportProtocolError.invalidSessionIdentifier
        }

        return uuid
    }

    func requiredPayloadCodec() throws -> TransportPayloadCodec {
        guard let payloadCodec else {
            throw TransportProtocolError.unsupportedPayloadCodec("<missing>")
        }

        return try TransportPayloadCodec(protocolValue: payloadCodec)
    }

    static func hello(
        sessionName: String,
        supportedCodecs: [TransportPayloadCodec],
        keepAliveIntervalMs: Int,
        sessionTimeoutMs: Int
    ) -> TransportControlMessage {
        TransportControlMessage(
            type: .hello,
            sessionName: sessionName,
            supportedCodecs: supportedCodecs.map(\.protocolValue),
            keepAliveIntervalMs: keepAliveIntervalMs,
            sessionTimeoutMs: sessionTimeoutMs
        )
    }

    static func startStream(sessionID: UUID, payloadCodec: TransportPayloadCodec, format: MVPAudioFormat) -> TransportControlMessage {
        TransportControlMessage(
            type: .startStream,
            sessionId: sessionID.uuidString.lowercased(),
            payloadCodec: payloadCodec.protocolValue,
            sampleRate: format.sampleRate,
            channels: format.channelCount,
            bitsPerSample: format.bitsPerSample,
            frameDurationMs: format.packetDurationMilliseconds
        )
    }

    static func stopStream(sessionID: UUID, detail: String? = nil) -> TransportControlMessage {
        TransportControlMessage(
            type: .stopStream,
            sessionId: sessionID.uuidString.lowercased(),
            detail: detail
        )
    }

    static func keepAlive(sessionID: UUID) -> TransportControlMessage {
        TransportControlMessage(
            type: .keepAlive,
            sessionId: sessionID.uuidString.lowercased()
        )
    }
}

enum TransportControlMessageProtocol {
    private static let encoder = JSONEncoder()
    private static let decoder = JSONDecoder()

    static func serialize(_ message: TransportControlMessage) throws -> String {
        let data = try encoder.encode(message.validated())
        guard let string = String(data: data, encoding: .utf8) else {
            throw TransportProtocolError.invalidControlPayload
        }

        return string
    }

    static func deserialize(_ json: String) throws -> TransportControlMessage {
        guard !json.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw TransportProtocolError.emptyControlPayload
        }

        let data = Data(json.utf8)
        let message: TransportControlMessage
        do {
            message = try decoder.decode(TransportControlMessage.self, from: data)
        } catch {
            throw TransportProtocolError.invalidControlPayload
        }

        return try message.validated()
    }

    static func serializeLine(_ message: TransportControlMessage) throws -> Data {
        let json = try serialize(message)
        return Data((json + "\n").utf8)
    }
}

struct TransportAudioPacket: Equatable, Sendable {
    let sessionID: UUID
    let payloadCodec: TransportPayloadCodec
    let sequenceNumber: Int64
    let capturedAt: Date
    let payload: Data

    func validated() throws -> Self {
        guard sessionID != UUID.zero else {
            throw TransportProtocolError.invalidField("Audio packet session id must be provided.")
        }

        if sequenceNumber <= 0 {
            throw TransportProtocolError.invalidField("Audio packet sequence number must be greater than zero.")
        }

        if payload.isEmpty {
            throw TransportProtocolError.invalidField("Audio packet payload cannot be empty.")
        }

        if payload.count > TransportProtocolConstants.maxPayloadLengthBytes {
            throw TransportProtocolError.payloadTooLarge(payload.count)
        }

        return self
    }
}

enum TransportAudioPacketSerializer {
    static func serialize(_ packet: TransportAudioPacket) throws -> Data {
        let packet = try packet.validated()
        var data = Data(count: TransportProtocolConstants.audioPacketHeaderSize + packet.payload.count)

        data.replaceSubrange(0..<4, with: Data(TransportProtocolConstants.audioPacketMagic.utf8))
        data[4] = UInt8(TransportProtocolConstants.protocolVersion)
        data[5] = packet.payloadCodec.rawValue
        data[6] = 0
        data[7] = 0
        data.replaceSubrange(8..<24, with: packet.sessionID.dotNetGuidBytes)
        data.replaceSubrange(24..<32, with: packet.sequenceNumber.bigEndianData)
        data.replaceSubrange(32..<40, with: Int64(packet.capturedAt.timeIntervalSince1970 * 1_000).bigEndianData)
        data.replaceSubrange(40..<44, with: Int32(packet.payload.count).bigEndianData)
        data.replaceSubrange(TransportProtocolConstants.audioPacketHeaderSize..<data.count, with: packet.payload)
        return data
    }

    static func deserialize(_ data: Data) throws -> TransportAudioPacket {
        if data.count < TransportProtocolConstants.audioPacketHeaderSize {
            throw TransportProtocolError.packetTooSmall
        }

        if String(decoding: data.prefix(4), as: UTF8.self) != TransportProtocolConstants.audioPacketMagic {
            throw TransportProtocolError.invalidPacketMagic
        }

        let version = data[4]
        if version != UInt8(TransportProtocolConstants.protocolVersion) {
            throw TransportProtocolError.unsupportedPacketProtocolVersion(version)
        }

        guard let payloadCodec = TransportPayloadCodec(rawValue: data[5]) else {
            throw TransportProtocolError.unsupportedPacketCodec(data[5])
        }

        let sessionID = try UUID(dotNetBytes: data.subdata(in: 8..<24))
        let sequenceNumber = try Int64(bigEndianData: data.subdata(in: 24..<32))
        let capturedAtMs = try Int64(bigEndianData: data.subdata(in: 32..<40))
        let payloadLength = try Int32(bigEndianData: data.subdata(in: 40..<44))

        if payloadLength <= 0 {
            throw TransportProtocolError.invalidPayloadLength(Int(payloadLength))
        }

        if payloadLength > TransportProtocolConstants.maxPayloadLengthBytes {
            throw TransportProtocolError.payloadTooLarge(Int(payloadLength))
        }

        let expectedLength = TransportProtocolConstants.audioPacketHeaderSize + Int(payloadLength)
        if data.count != expectedLength {
            throw TransportProtocolError.packetLengthMismatch
        }

        return try TransportAudioPacket(
            sessionID: sessionID,
            payloadCodec: payloadCodec,
            sequenceNumber: sequenceNumber,
            capturedAt: Date(timeIntervalSince1970: TimeInterval(capturedAtMs) / 1_000),
            payload: data.subdata(in: TransportProtocolConstants.audioPacketHeaderSize..<expectedLength)
        ).validated()
    }
}

private extension FixedWidthInteger {
    var bigEndianData: Data {
        withUnsafeBytes(of: bigEndian) { Data($0) }
    }

    init(bigEndianData data: Data) throws {
        guard data.count == MemoryLayout<Self>.size else {
            throw TransportProtocolError.invalidField("Integer field length was invalid.")
        }

        self = data.withUnsafeBytes { rawBuffer in
            rawBuffer.loadUnaligned(as: Self.self).bigEndian
        }
    }
}

private extension UUID {
    static let zero = UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))

    init(dotNetBytes data: Data) throws {
        guard data.count == 16 else {
            throw TransportProtocolError.invalidField("GUID byte length was invalid.")
        }

        let reordered = [
            data[3], data[2], data[1], data[0],
            data[5], data[4],
            data[7], data[6],
            data[8], data[9], data[10], data[11], data[12], data[13], data[14], data[15]
        ]

        self = UUID(uuid: (
            reordered[0], reordered[1], reordered[2], reordered[3],
            reordered[4], reordered[5], reordered[6], reordered[7],
            reordered[8], reordered[9], reordered[10], reordered[11],
            reordered[12], reordered[13], reordered[14], reordered[15]
        ))
    }

    var dotNetGuidBytes: Data {
        let bytes = withUnsafeBytes(of: uuid) { Array($0) }
        return Data([
            bytes[3], bytes[2], bytes[1], bytes[0],
            bytes[5], bytes[4],
            bytes[7], bytes[6],
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]
        ])
    }
}
