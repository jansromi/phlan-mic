import XCTest
@testable import PhlanMic_iOSClient

@MainActor
final class AppModelTests: XCTestCase {
    func testPrimaryTapRequestsPermissionBeforeConnecting() async {
        let harness = Harness()
        harness.currentPermissionStatus = .unknown
        harness.requestPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "192.168.1.15")
        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.requestPermissionCallCount, 1)
        XCTAssertEqual(harness.connectCalls.count, 1)
        XCTAssertEqual(harness.connectCalls.first?.host, "192.168.1.15")
        XCTAssertEqual(harness.connectCalls.first?.port, HostConfiguration.defaultDebugTcpPort)
        XCTAssertEqual(model.transportStatus, .connecting)
    }

    func testPrimaryTapOpensHostSettingsWhenConfigurationIsInvalid() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness)
        await model.handlePrimaryMicTap()

        XCTAssertEqual(model.presentedSheet, .hostSettings)
        XCTAssertTrue(harness.connectCalls.isEmpty)
    }

    func testPrimaryTapStartsConnectAndStreamWhenConfigurationIsValid() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42", port: "43000")
        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.connectCalls.count, 1)
        XCTAssertEqual(harness.connectCalls.first?.host, "10.0.0.42")
        XCTAssertEqual(harness.connectCalls.first?.port, 43_000)
        XCTAssertNil(model.presentedSheet)
        XCTAssertEqual(model.transportStatus, .connecting)
    }

    func testPrimaryTapStopsAnActiveSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        model.transportStatus = .streaming

        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.disconnectCallCount, 1)
        XCTAssertEqual(model.transportStatus, .stopping)
    }

    func testErrorStateUpdatesPrimaryControlAndConnectionCard() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        model.transportStatus = .error
        model.transportDetail = "TCP connection failed: timed out"
        model.lastTransportError = "TCP connection failed: timed out"

        XCTAssertEqual(model.primaryMicVisualState, .error)
        XCTAssertEqual(model.connectionCardStatusLabel, "Error")
        XCTAssertEqual(model.connectionCardDetail, "TCP connection failed: timed out")
    }

    private func makeModel(
        harness: Harness,
        host: String = "",
        port: String = String(HostConfiguration.defaultDebugTcpPort)
    ) -> AppModel {
        let model = AppModel(dependencies: harness.dependencies)
        model.updateHostAddress(host)
        model.updatePortText(port)
        return model
    }
}

private final class Harness {
    var currentPermissionStatus: MicrophonePermissionState = .unknown
    var requestPermissionStatus: MicrophonePermissionState = .granted
    var requestPermissionCallCount = 0
    var connectCalls: [(host: String, port: UInt16)] = []
    var disconnectCallCount = 0
    var transportEventHandler: (@Sendable (DebugTcpPcmClientEvent) -> Void)?

    var dependencies: AppModel.Dependencies {
        AppModel.Dependencies(
            currentPermissionStatus: { [unowned self] in
                currentPermissionStatus
            },
            requestMicrophonePermission: { [unowned self] in
                requestPermissionCallCount += 1
                currentPermissionStatus = requestPermissionStatus
                return requestPermissionStatus
            },
            startCapture: { _, _, _, _ in
                MicrophoneCaptureStartup(
                    requestedFormat: .defaultVoice,
                    inputFormatSummary: "input",
                    outputFormatSummary: "output",
                    actualSampleRate: Double(MVPAudioFormat.defaultVoice.sampleRate),
                    actualBufferDuration: TimeInterval(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) / 1_000
                )
            },
            stopCapture: {},
            connectTransport: { [unowned self] host, port in
                connectCalls.append((host, port))
            },
            disconnectTransport: { [unowned self] in
                disconnectCallCount += 1
            },
            sendTransportPayload: { _, completion in
                completion(.success(0))
            },
            setTransportEventHandler: { [unowned self] handler in
                transportEventHandler = handler
            },
            log: { _ in }
        )
    }
}
