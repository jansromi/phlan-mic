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
        XCTAssertEqual(harness.transport.connectCalls.count, 1)
        XCTAssertEqual(harness.transport.connectCalls.first?.host, "192.168.1.15")
        XCTAssertEqual(harness.transport.connectCalls.first?.port, HostConfiguration.defaultDebugTcpPort)
        XCTAssertEqual(model.transportStatus, .connecting)
    }

    func testPrimaryTapOpensHostSettingsWhenConfigurationIsInvalid() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness)
        await model.handlePrimaryMicTap()

        XCTAssertEqual(model.presentedSheet, .hostSettings)
        XCTAssertTrue(harness.transport.connectCalls.isEmpty)
    }

    func testPrimaryTapStartsConnectAndStreamWhenTcpConfigurationIsValid() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42", port: "43000")
        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.createdTransportModes, [.tcpDebug])
        XCTAssertEqual(harness.transport.connectCalls.count, 1)
        XCTAssertEqual(harness.transport.connectCalls.first?.host, "10.0.0.42")
        XCTAssertEqual(harness.transport.connectCalls.first?.port, 43_000)
        XCTAssertNil(model.presentedSheet)
        XCTAssertEqual(model.transportStatus, .connecting)
    }

    func testUdpRealtimeConfigurationIsReadyAndConnects() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )

        XCTAssertEqual(model.setupStatus, .ready)

        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.createdTransportModes, [.udpRealtime])
        XCTAssertEqual(harness.transport.connectCalls.count, 1)
        XCTAssertEqual(model.transportStatus, .connecting)
    }

    func testPrimaryTapStopsAnActiveSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        model.transportStatus = .streaming

        await model.handlePrimaryMicTap()

        XCTAssertEqual(harness.transport.disconnectCallCount, 1)
        XCTAssertEqual(model.transportStatus, .stopping)
    }

    func testRealtimeTransportEventsAdvanceStateAndCounters() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )

        await model.connectAndStream()
        harness.transport.emit(.stateChanged(.controlConnected, detail: "TCP control channel connected."))
        XCTAssertEqual(model.transportStatus, .controlConnected)

        harness.transport.emit(.controlMessageSent(.hello))
        harness.transport.emit(.controlMessageReceived(.helloAccepted))
        harness.transport.emit(.stateChanged(.handshakeAccepted, detail: "Handshake accepted."))
        XCTAssertEqual(model.transportStatus, .handshakeAccepted)
        XCTAssertEqual(model.transportControlMessagesSent, 1)
        XCTAssertEqual(model.transportControlMessagesReceived, 1)

        harness.transport.emit(.stateChanged(.readyForAudio, detail: "UDP audio stream accepted."))
        XCTAssertEqual(model.transportStatus, .connected)

        let keepAliveTime = Date(timeIntervalSince1970: 1_700_000_000)
        harness.transport.emit(.keepAliveSent(keepAliveTime))
        XCTAssertEqual(model.lastKeepAliveTime, keepAliveTime)
    }

    func testTransportFailureUpdatesPrimaryControlAndConnectionCard() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        model.transportStatus = .error
        model.transportDetail = "Control channel failed: timed out"
        model.lastTransportError = "Control channel failed: timed out"

        XCTAssertEqual(model.primaryMicVisualState, .error)
        XCTAssertEqual(model.connectionCardStatusLabel, "Error")
        XCTAssertEqual(model.connectionCardDetail, "Control channel failed: timed out")
    }

    func testSessionHealthShowsReadyStateBeforeStreaming() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")

        XCTAssertEqual(model.sessionHealthItems.map(\.title), ["Status", "Sent", "Last"])
        XCTAssertEqual(model.sessionHealthItems.map(\.value), ["Ready", "Idle", "None"])
        XCTAssertEqual(model.sessionHealthSummary, "Ready • Idle • None")
        XCTAssertEqual(model.sessionHealthFootnote, "Ready to stream when you tap the microphone.")
    }

    func testSessionHealthShowsStreamingStateAfterSuccessfulSend() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        let lastSendTime = Date(timeIntervalSince1970: 1_700_000_000)
        model.transportStatus = .streaming
        model.transportFramesSent = 128
        model.lastSuccessfulSendTime = lastSendTime

        XCTAssertEqual(model.sessionHealthItems[0].value, "Live")
        XCTAssertEqual(model.sessionHealthItems[1].value, "128")
        XCTAssertEqual(
            model.sessionHealthItems[2].value,
            lastSendTime.formatted(date: .omitted, time: .shortened)
        )
        XCTAssertEqual(
            model.sessionHealthItems[0].detail,
            "The session is live and microphone audio is reaching 10.0.0.42:42100."
        )
        XCTAssertEqual(model.sessionHealthFootnote, "Streaming to 10.0.0.42:42100.")
    }

    private func makeModel(
        harness: Harness,
        host: String = "",
        port: String = String(HostConfiguration.defaultDebugTcpPort),
        transportMode: HostConfiguration.TransportMode = .tcpDebug
    ) -> AppModel {
        let model = AppModel(dependencies: harness.dependencies)
        model.updateHostAddress(host)
        model.updatePortText(port)
        model.updateTransportMode(transportMode)
        return model
    }
}

private final class Harness {
    var currentPermissionStatus: MicrophonePermissionState = .unknown
    var requestPermissionStatus: MicrophonePermissionState = .granted
    var requestPermissionCallCount = 0
    var createdTransportModes: [HostConfiguration.TransportMode] = []
    let transport = MockTransportClient()

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
            makeTransportClient: { [unowned self] transportMode, handler in
                createdTransportModes.append(transportMode)
                transport.eventHandler = handler
                return transport.client
            },
            log: { _ in }
        )
    }
}

private final class MockTransportClient: @unchecked Sendable {
    var connectCalls: [(host: String, port: UInt16, format: MVPAudioFormat)] = []
    var disconnectCallCount = 0
    var sentFrames: [CapturedAudioFrame] = []
    var eventHandler: (@Sendable (AudioTransportEvent) -> Void)?

    var client: AudioTransportClient {
        AudioTransportClient(
            connect: { [unowned self] host, port, format in
                connectCalls.append((host, port, format))
            },
            disconnect: { [unowned self] in
                disconnectCallCount += 1
            },
            sendFrame: { [unowned self] frame, completion in
                sentFrames.append(frame)
                completion(.success(frame.payload.count))
            }
        )
    }

    func emit(_ event: AudioTransportEvent) {
        eventHandler?(event)
    }
}
