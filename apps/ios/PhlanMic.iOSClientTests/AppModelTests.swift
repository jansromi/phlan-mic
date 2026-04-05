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

    func testStandaloneDebugCaptureShowsStoppedPrimaryStatus() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")
        model.captureStatus = .capturing

        XCTAssertEqual(model.primaryStatusTitle, "Stopped")
        XCTAssertEqual(
            model.primaryStatusDetail,
            "Streaming is stopped. Debug capture is still running from the debug view."
        )
    }

    func testSessionHealthShowsReadyStateBeforeStreaming() {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(harness: harness, host: "10.0.0.42")

        XCTAssertEqual(model.sessionHealthItems.map(\.title), ["Status", "Sent", "Last"])
        XCTAssertEqual(model.sessionHealthItems.map(\.value), ["Ready", "Idle", "None"])
        XCTAssertEqual(model.sessionHealthSummary, "Ready • Idle • None")
        XCTAssertNil(model.sessionHealthFootnote)
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
        XCTAssertNil(model.sessionHealthFootnote)
    }

    func testSceneBecomingInactiveStopsStreamingSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        model.handleScenePhaseChange(.inactive)

        XCTAssertEqual(model.lastSystemStopReason, .sceneBecameInactive)
        XCTAssertEqual(model.transportStatus, .stopping)
        XCTAssertEqual(harness.stopCaptureCallCount, 1)
        XCTAssertEqual(harness.transport.disconnectCallCount, 1)

        harness.transport.emit(.stopped("Realtime transport stopped."))

        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(model.connectionCardStatusLabel, "Paused")
        XCTAssertEqual(model.lastTransportError, "No transport errors.")
        XCTAssertEqual(
            model.transportDetail,
            "The session stopped because the app became inactive. Bring the app back to the foreground and start again."
        )
    }

    func testSceneEnteringBackgroundStopsStreamingSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        model.handleScenePhaseChange(.background)
        harness.transport.emit(.stopped("Realtime transport stopped."))

        XCTAssertEqual(model.lastSystemStopReason, .sceneEnteredBackground)
        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(
            model.transportDetail,
            "The session stopped because the app entered the background. Reopen the app and start again."
        )
    }

    func testInterruptionBeginStopsStreamingSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        harness.emitCaptureSessionEvent(.interruptionBegan)
        harness.transport.emit(.stopped("Realtime transport stopped."))

        XCTAssertEqual(model.lastSystemStopReason, .audioInterrupted)
        XCTAssertEqual(model.lastInterruptionState, .began)
        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(
            model.transportDetail,
            "The session stopped because iOS interrupted microphone access. Start again when the interruption ends."
        )
    }

    func testInterruptionEndLeavesAppReadyButDoesNotAutoResume() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        harness.emitCaptureSessionEvent(.interruptionBegan)
        harness.transport.emit(.stopped("Realtime transport stopped."))
        harness.emitCaptureSessionEvent(.interruptionEnded(shouldResume: true))

        XCTAssertEqual(model.lastInterruptionState, .ended(shouldResume: true))
        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(model.captureStatus, .ready)
        XCTAssertEqual(harness.transport.connectCalls.count, 1)
        XCTAssertEqual(
            model.lastSystemStopDetail,
            "Audio interruption ended. iOS allows a resume, but this app requires a manual restart."
        )
    }

    func testRouteInvalidationStopsStreamingSession() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await model.connectAndStream()
        model.microphonePermission = .granted
        model.captureStatus = .capturing
        model.transportStatus = .streaming

        harness.emitCaptureSessionEvent(
            .routeChanged(
                CaptureRouteChange(
                    reason: .oldDeviceUnavailable,
                    inputAvailable: false,
                    routeSummary: "inputs[none] outputs[builtInSpeaker=Speaker]"
                )
            )
        )
        harness.transport.emit(.stopped("Realtime transport stopped."))

        XCTAssertEqual(model.lastSystemStopReason, .routeInvalidated)
        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(
            model.transportDetail,
            "The session stopped because the microphone route was lost. Connect a valid input route and start again."
        )
    }

    func testCategoryChangeDoesNotStopStreamingSessionWhenInputRemainsAvailable() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await model.connectAndStream()
        model.microphonePermission = .granted
        model.captureStatus = .capturing
        model.transportStatus = .streaming

        harness.emitCaptureSessionEvent(
            .routeChanged(
                CaptureRouteChange(
                    reason: .categoryChange,
                    inputAvailable: true,
                    routeSummary: "inputs[MicrophoneBuiltIn=iPhone Microphone] outputs[none]"
                )
            )
        )

        XCTAssertEqual(model.lastRouteChange?.reason, .categoryChange)
        XCTAssertEqual(model.lastSystemStopReason, nil)
        XCTAssertEqual(model.transportStatus, .streaming)
        XCTAssertEqual(model.captureStatus, .capturing)
        XCTAssertEqual(harness.stopCaptureCallCount, 0)
        XCTAssertEqual(harness.transport.disconnectCallCount, 0)
    }

    func testSystemStopDoesNotLookLikeTransportFailure() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        model.handleScenePhaseChange(.inactive)
        harness.transport.emit(.stopped("Realtime transport stopped."))

        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertNotEqual(model.transportStatus, .error)
        XCTAssertEqual(model.lastTransportError, "No transport errors.")
        XCTAssertEqual(model.connectionCardStatusLabel, "Paused")
    }

    func testLifecycleStopWhileAlreadyStoppingIsIdempotent() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        model.handleScenePhaseChange(.inactive)
        model.handleScenePhaseChange(.background)
        harness.emitCaptureSessionEvent(.interruptionBegan)

        XCTAssertEqual(model.lastSystemStopReason, .sceneBecameInactive)
        XCTAssertEqual(harness.stopCaptureCallCount, 1)
        XCTAssertEqual(harness.transport.disconnectCallCount, 1)
    }

    func testTransportFailureAfterLifecycleStopDoesNotCorruptState() async {
        let harness = Harness()
        harness.currentPermissionStatus = .granted

        let model = makeModel(
            harness: harness,
            host: "10.0.0.42",
            port: "42100",
            transportMode: .udpRealtime
        )
        await activateStreamingSession(model: model, harness: harness)

        model.handleScenePhaseChange(.inactive)
        harness.transport.emit(.failed("Control channel failed: timed out"))

        XCTAssertEqual(model.lastSystemStopReason, .sceneBecameInactive)
        XCTAssertEqual(model.transportStatus, .disconnected)
        XCTAssertEqual(model.lastTransportError, "No transport errors.")
        XCTAssertEqual(model.connectionCardStatusLabel, "Paused")
        XCTAssertEqual(
            model.transportDetail,
            "The session stopped because the app became inactive. Bring the app back to the foreground and start again."
        )
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

    private func activateStreamingSession(model: AppModel, harness: Harness) async {
        await model.connectAndStream()
        harness.transport.emit(.stateChanged(.readyForAudio, detail: "UDP audio stream accepted. Starting microphone capture."))
        model.transportStatus = .streaming
        model.transportDetail = "Streaming realtime raw PCM to the Windows host."
        XCTAssertEqual(model.captureStatus, .capturing)
    }
}

private final class Harness {
    var currentPermissionStatus: MicrophonePermissionState = .unknown
    var requestPermissionStatus: MicrophonePermissionState = .granted
    var requestPermissionCallCount = 0
    var stopCaptureCallCount = 0
    var createdTransportModes: [HostConfiguration.TransportMode] = []
    var captureSessionEventHandler: (@Sendable (CaptureSessionEvent) -> Void)?
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
            startCapture: { [unowned self] _, _, _, _, onSessionEvent in
                captureSessionEventHandler = onSessionEvent
                return MicrophoneCaptureStartup(
                    requestedFormat: .defaultVoice,
                    inputFormatSummary: "input",
                    outputFormatSummary: "output",
                    actualSampleRate: Double(MVPAudioFormat.defaultVoice.sampleRate),
                    actualBufferDuration: TimeInterval(MVPAudioFormat.defaultVoice.packetDurationMilliseconds) / 1_000,
                    sessionCategory: "record",
                    sessionMode: "measurement",
                    routeSummary: "inputs[builtInMic=Built-In Microphone] outputs[none]"
                )
            },
            stopCapture: { [unowned self] in
                stopCaptureCallCount += 1
            },
            makeTransportClient: { [unowned self] transportMode, handler in
                createdTransportModes.append(transportMode)
                transport.eventHandler = handler
                return transport.client
            },
            log: { _ in }
        )
    }

    func emitCaptureSessionEvent(_ event: CaptureSessionEvent) {
        captureSessionEventHandler?(event)
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
