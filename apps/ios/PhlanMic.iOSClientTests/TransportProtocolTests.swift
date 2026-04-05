import XCTest
@testable import PhlanMic_iOSClient

final class TransportProtocolTests: XCTestCase {
    func testControlHelloRoundTrips() throws {
        let message = TransportControlMessage.hello(
            sessionName: "ios-test",
            supportedCodecs: [.rawPcm16],
            keepAliveIntervalMs: 1_000,
            sessionTimeoutMs: 5_000
        )

        let json = try TransportControlMessageProtocol.serialize(message)
        let decoded = try TransportControlMessageProtocol.deserialize(json)

        XCTAssertEqual(decoded.type, .hello)
        XCTAssertEqual(decoded.sessionName, "ios-test")
        XCTAssertEqual(decoded.supportedCodecs, ["RawPcm16"])
        XCTAssertEqual(decoded.keepAliveIntervalMs, 1_000)
        XCTAssertEqual(decoded.sessionTimeoutMs, 5_000)
    }

    func testControlDecodeRejectsUnsupportedProtocolVersion() {
        let json = """
        {"protocolVersion":999,"type":"hello","sessionName":"test","supportedCodecs":["RawPcm16"],"keepAliveIntervalMs":1000,"sessionTimeoutMs":5000}
        """

        XCTAssertThrowsError(try TransportControlMessageProtocol.deserialize(json)) { error in
            XCTAssertTrue(error.localizedDescription.localizedCaseInsensitiveContains("version"))
        }
    }

    func testAudioPacketRoundTripsWithDotNetGuidByteOrder() throws {
        let sessionID = UUID(uuidString: "00112233-4455-6677-8899-aabbccddeeff")!
        let packet = TransportAudioPacket(
            sessionID: sessionID,
            payloadCodec: .rawPcm16,
            sequenceNumber: 42,
            capturedAt: Date(timeIntervalSince1970: 1_700_000_000),
            payload: Data([0x01, 0x02, 0x03, 0x04])
        )

        let data = try TransportAudioPacketSerializer.serialize(packet)
        let decoded = try TransportAudioPacketSerializer.deserialize(data)

        XCTAssertEqual(decoded, packet)
        XCTAssertEqual(Array(data[8..<24]), [
            0x33, 0x22, 0x11, 0x00,
            0x55, 0x44,
            0x77, 0x66,
            0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
        ])
    }

    func testAudioPacketRejectsInvalidMagic() {
        let invalidPacket = Data(repeating: 0, count: TransportProtocolConstants.audioPacketHeaderSize)

        XCTAssertThrowsError(try TransportAudioPacketSerializer.deserialize(invalidPacket)) { error in
            XCTAssertTrue(error.localizedDescription.localizedCaseInsensitiveContains("magic"))
        }
    }
}
