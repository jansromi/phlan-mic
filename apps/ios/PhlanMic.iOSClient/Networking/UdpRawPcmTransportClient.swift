import Foundation
import Network

enum UdpRawPcmTransportClientError: LocalizedError {
    case invalidHost
    case invalidPort
    case alreadyConnected
    case notReady
    case controlConnectionFailed(String)
    case controlSendFailed(String)
    case invalidHostResponse(String)
    case hostRejected(String)
    case udpSendFailed(String)
    case disconnectedByPeer

    var errorDescription: String? {
        switch self {
        case .invalidHost:
            "Enter a valid Windows host address before connecting."
        case .invalidPort:
            "Enter a valid realtime control port before connecting."
        case .alreadyConnected:
            "The UDP realtime client is already connected."
        case .notReady:
            "The UDP realtime session is not ready for audio yet."
        case .controlConnectionFailed(let detail):
            "Control channel failed: \(detail)"
        case .controlSendFailed(let detail):
            "Control channel send failed: \(detail)"
        case .invalidHostResponse(let detail):
            "Host protocol mismatch: \(detail)"
        case .hostRejected(let detail):
            "Host rejected realtime session: \(detail)"
        case .udpSendFailed(let detail):
            "UDP send failed: \(detail)"
        case .disconnectedByPeer:
            "The Windows host closed the realtime control channel."
        }
    }
}

final class UdpRawPcmTransportClient: @unchecked Sendable {
    private static let defaultKeepAliveIntervalMs = 1_000
    private static let defaultSessionTimeoutMs = 5_000

    private let queue = DispatchQueue(label: "com.roba.phlanmic.ios-client.udp-raw-pcm")
    private let eventHandler: @Sendable (AudioTransportEvent) -> Void

    private var controlConnection: NWConnection?
    private var udpConnection: NWConnection?
    private var receiveBuffer = Data()
    private var negotiatedSessionID: UUID?
    private var negotiatedAudioPort: UInt16?
    private var currentFormat = MVPAudioFormat.defaultVoice
    private var keepAliveIntervalMs = 1_000
    private var sessionTimeoutMs = 5_000
    private var keepAliveTimer: DispatchSourceTimer?
    private var disconnectRequested = false
    private var terminalEventEmitted = false
    private var captureEpoch: Date?

    init(eventHandler: @escaping @Sendable (AudioTransportEvent) -> Void) {
        self.eventHandler = eventHandler
    }

    func connect(host: String, port: UInt16, format: MVPAudioFormat) throws {
        let trimmedHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedHost.isEmpty else {
            throw UdpRawPcmTransportClientError.invalidHost
        }

        guard let endpointPort = NWEndpoint.Port(rawValue: port) else {
            throw UdpRawPcmTransportClientError.invalidPort
        }

        let alreadyConnected = queue.sync { controlConnection != nil }
        guard !alreadyConnected else {
            throw UdpRawPcmTransportClientError.alreadyConnected
        }

        queue.async { [weak self] in
            guard let self else {
                return
            }

            self.resetStateForConnect(format: format)

            let controlConnection = NWConnection(
                host: NWEndpoint.Host(trimmedHost),
                port: endpointPort,
                using: .tcp
            )

            self.controlConnection = controlConnection
            controlConnection.stateUpdateHandler = { [weak self, weak controlConnection] state in
                guard let self, let controlConnection else {
                    return
                }

                self.handleControlStateUpdate(state, for: controlConnection, host: trimmedHost, port: port)
            }

            self.emit(.stateChanged(.connecting, detail: "Opening TCP control channel to \(trimmedHost):\(port)."))
            controlConnection.start(queue: self.queue)
        }
    }

    func disconnect() {
        queue.async { [weak self] in
            guard let self else {
                return
            }

            self.disconnectRequested = true
            self.stopKeepAliveTimer()

            if let sessionID = self.negotiatedSessionID, let controlConnection = self.controlConnection {
                self.sendControlMessage(
                    .stopStream(sessionID: sessionID, detail: "iOS client requested stream stop."),
                    over: controlConnection
                ) { [weak self] _ in
                    self?.closeConnections()
                    self?.emitTerminalIfNeeded(.stopped("Realtime transport stopped."))
                }
            } else {
                self.closeConnections()
                self.emitTerminalIfNeeded(.stopped("Realtime transport stopped."))
            }
        }
    }

    func send(_ frame: CapturedAudioFrame, completion: @escaping @Sendable (Result<Int, Error>) -> Void) {
        queue.async { [weak self] in
            guard let self else {
                completion(.failure(UdpRawPcmTransportClientError.notReady))
                return
            }

            guard
                let sessionID = self.negotiatedSessionID,
                let udpConnection = self.udpConnection
            else {
                completion(.failure(UdpRawPcmTransportClientError.notReady))
                return
            }

            if self.captureEpoch == nil {
                self.captureEpoch = Date().addingTimeInterval(-frame.capturedAt.timeInterval)
            }

            let capturedAt = self.captureEpoch?.addingTimeInterval(frame.capturedAt.timeInterval) ?? Date()

            let packet = TransportAudioPacket(
                sessionID: sessionID,
                payloadCodec: .rawPcm16,
                sequenceNumber: Int64(frame.sequenceNumber + 1),
                capturedAt: capturedAt,
                payload: frame.payload
            )

            do {
                let packetBytes = try TransportAudioPacketSerializer.serialize(packet)
                udpConnection.send(content: packetBytes, completion: .contentProcessed { [weak self] error in
                    guard let self else {
                        completion(.failure(UdpRawPcmTransportClientError.notReady))
                        return
                    }

                    if let error {
                        let failure = UdpRawPcmTransportClientError.udpSendFailed(Self.describe(error))
                        self.fail(with: failure.localizedDescription)
                        completion(.failure(failure))
                    } else {
                        completion(.success(packetBytes.count))
                    }
                })
            } catch {
                let failure = UdpRawPcmTransportClientError.invalidHostResponse(error.localizedDescription)
                self.fail(with: failure.localizedDescription)
                completion(.failure(failure))
            }
        }
    }

    private func resetStateForConnect(format: MVPAudioFormat) {
        disconnectRequested = false
        terminalEventEmitted = false
        receiveBuffer.removeAll(keepingCapacity: true)
        negotiatedSessionID = nil
        negotiatedAudioPort = nil
        currentFormat = format
        keepAliveIntervalMs = Self.defaultKeepAliveIntervalMs
        sessionTimeoutMs = Self.defaultSessionTimeoutMs
        captureEpoch = nil
    }

    private func handleControlStateUpdate(
        _ state: NWConnection.State,
        for connection: NWConnection,
        host: String,
        port: UInt16
    ) {
        guard isCurrentControlConnection(connection) else {
            return
        }

        switch state {
        case .setup, .preparing:
            emit(.stateChanged(.connecting, detail: "Opening TCP control channel to \(host):\(port)."))
        case .ready:
            emit(.stateChanged(.controlConnected, detail: "TCP control channel connected. Sending hello."))
            receiveControlMessages(on: connection)
            sendHello(over: connection)
        case .waiting(let error):
            fail(with: UdpRawPcmTransportClientError.controlConnectionFailed(Self.describe(error)).localizedDescription)
        case .failed(let error):
            fail(with: UdpRawPcmTransportClientError.controlConnectionFailed(Self.describe(error)).localizedDescription)
        case .cancelled:
            if disconnectRequested {
                emitTerminalIfNeeded(.stopped("Realtime transport stopped."))
            } else {
                fail(with: UdpRawPcmTransportClientError.disconnectedByPeer.localizedDescription)
            }
        @unknown default:
            break
        }
    }

    private func sendHello(over connection: NWConnection) {
        let message = TransportControlMessage.hello(
            sessionName: "phlan-mic-ios",
            supportedCodecs: [.rawPcm16],
            keepAliveIntervalMs: keepAliveIntervalMs,
            sessionTimeoutMs: sessionTimeoutMs
        )

        sendControlMessage(message, over: connection) { _ in }
    }

    private func sendStartStream(over connection: NWConnection, sessionID: UUID) {
        let message = TransportControlMessage.startStream(
            sessionID: sessionID,
            payloadCodec: .rawPcm16,
            format: currentFormat
        )

        sendControlMessage(message, over: connection) { [weak self] success in
            guard success else {
                return
            }

            self?.emit(.stateChanged(.handshakeAccepted, detail: "Handshake accepted. Requesting UDP stream start on port \(self?.negotiatedAudioPort.map(String.init) ?? "<audio>")."))
        }
    }

    private func sendKeepAlive(over connection: NWConnection, sessionID: UUID) {
        sendControlMessage(.keepAlive(sessionID: sessionID), over: connection) { [weak self] success in
            guard let self, success else {
                return
            }

            self.emit(.keepAliveSent(Date()))
        }
    }

    private func sendControlMessage(
        _ message: TransportControlMessage,
        over connection: NWConnection,
        completion: @escaping @Sendable (Bool) -> Void
    ) {
        do {
            let line = try TransportControlMessageProtocol.serializeLine(message)
            connection.send(content: line, completion: .contentProcessed { [weak self] error in
                guard let self else {
                    completion(false)
                    return
                }

                if let error {
                    self.fail(with: UdpRawPcmTransportClientError.controlSendFailed(Self.describe(error)).localizedDescription)
                    completion(false)
                    return
                }

                self.emit(.controlMessageSent(message.type))
                completion(true)
            })
        } catch {
            fail(with: UdpRawPcmTransportClientError.invalidHostResponse(error.localizedDescription).localizedDescription)
            completion(false)
        }
    }

    private func receiveControlMessages(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8 * 1_024) { [weak self, weak connection] data, _, isComplete, error in
            guard let self, let connection else {
                return
            }

            guard self.isCurrentControlConnection(connection) else {
                return
            }

            if let error {
                self.fail(with: UdpRawPcmTransportClientError.controlConnectionFailed(Self.describe(error)).localizedDescription)
                return
            }

            if let data, !data.isEmpty {
                self.receiveBuffer.append(data)
                self.processBufferedControlLines(on: connection)
            }

            if isComplete {
                if self.disconnectRequested {
                    self.emitTerminalIfNeeded(.stopped("Realtime transport stopped."))
                } else {
                    self.fail(with: UdpRawPcmTransportClientError.disconnectedByPeer.localizedDescription)
                }
                return
            }

            self.receiveControlMessages(on: connection)
        }
    }

    private func processBufferedControlLines(on connection: NWConnection) {
        while let newlineIndex = receiveBuffer.firstIndex(of: 0x0A) {
            let lineData = receiveBuffer.prefix(upTo: newlineIndex)
            receiveBuffer.removeSubrange(...newlineIndex)

            guard !lineData.isEmpty else {
                continue
            }

            do {
                let line = String(decoding: lineData, as: UTF8.self)
                let message = try TransportControlMessageProtocol.deserialize(line)
                emit(.controlMessageReceived(message.type))
                try handleControlMessage(message, on: connection)
            } catch {
                fail(with: UdpRawPcmTransportClientError.invalidHostResponse(error.localizedDescription).localizedDescription)
                return
            }
        }
    }

    private func handleControlMessage(_ message: TransportControlMessage, on connection: NWConnection) throws {
        switch message.type {
        case .helloAccepted:
            let sessionID = try message.requiredSessionID()
            let payloadCodec = try message.requiredPayloadCodec()
            guard payloadCodec == .rawPcm16 else {
                throw UdpRawPcmTransportClientError.invalidHostResponse(
                    "Expected RawPcm16 but host negotiated \(payloadCodec.protocolValue)."
                )
            }

            guard
                message.sampleRate == currentFormat.sampleRate,
                message.channels == currentFormat.channelCount,
                message.bitsPerSample == currentFormat.bitsPerSample,
                message.frameDurationMs == currentFormat.packetDurationMilliseconds
            else {
                throw UdpRawPcmTransportClientError.invalidHostResponse(
                    "Host format does not match \(currentFormat.debugSummary)."
                )
            }

            guard let audioPortValue = message.audioPort, let audioPort = UInt16(exactly: audioPortValue) else {
                throw UdpRawPcmTransportClientError.invalidHostResponse("helloAccepted did not include a valid audioPort.")
            }

            negotiatedSessionID = sessionID
            negotiatedAudioPort = audioPort
            keepAliveIntervalMs = message.keepAliveIntervalMs ?? Self.defaultKeepAliveIntervalMs
            sessionTimeoutMs = message.sessionTimeoutMs ?? Self.defaultSessionTimeoutMs
            createUDPConnection(for: audioPort)
            sendStartStream(over: connection, sessionID: sessionID)
        case .startAccepted:
            let sessionID = try message.requiredSessionID()
            guard sessionID == negotiatedSessionID else {
                throw UdpRawPcmTransportClientError.invalidHostResponse("startAccepted session id did not match the negotiated session.")
            }

            startKeepAliveTimer()
            emit(.stateChanged(.readyForAudio, detail: message.detail ?? "UDP audio stream accepted. Starting microphone capture."))
        case .error:
            let detail = message.detail ?? "The Windows host rejected the realtime session."
            if detail.caseInsensitiveCompare("Session timed out.") == .orderedSame {
                fail(with: "Keepalive timeout: Session timed out.")
            } else {
                fail(with: UdpRawPcmTransportClientError.hostRejected(detail).localizedDescription)
            }
        default:
            throw UdpRawPcmTransportClientError.invalidHostResponse(
                "Unexpected control message type '\(message.type.rawValue)'."
            )
        }
    }

    private func createUDPConnection(for audioPort: UInt16) {
        guard let controlConnection else {
            return
        }

        udpConnection?.cancel()
        let endpointHost: NWEndpoint.Host
        switch controlConnection.endpoint {
        case .hostPort(let host, _):
            endpointHost = host
        default:
            return
        }

        guard let port = NWEndpoint.Port(rawValue: audioPort) else {
            return
        }

        let udpConnection = NWConnection(host: endpointHost, port: port, using: .udp)
        self.udpConnection = udpConnection
        udpConnection.stateUpdateHandler = { [weak self, weak udpConnection] state in
            guard let self, let udpConnection else {
                return
            }

            guard self.isCurrentUDPConnection(udpConnection) else {
                return
            }

            if case .failed(let error) = state {
                self.fail(with: UdpRawPcmTransportClientError.udpSendFailed(Self.describe(error)).localizedDescription)
            }
        }
        udpConnection.start(queue: queue)
    }

    private func startKeepAliveTimer() {
        stopKeepAliveTimer()

        guard let controlConnection, let sessionID = negotiatedSessionID else {
            return
        }

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + .milliseconds(keepAliveIntervalMs), repeating: .milliseconds(keepAliveIntervalMs))
        timer.setEventHandler { [weak self, weak controlConnection] in
            guard let self, let controlConnection else {
                return
            }

            guard self.isCurrentControlConnection(controlConnection), !self.disconnectRequested else {
                return
            }

            self.sendKeepAlive(over: controlConnection, sessionID: sessionID)
        }

        keepAliveTimer = timer
        timer.resume()
    }

    private func stopKeepAliveTimer() {
        keepAliveTimer?.cancel()
        keepAliveTimer = nil
    }

    private func closeConnections() {
        udpConnection?.cancel()
        controlConnection?.cancel()
        udpConnection = nil
        controlConnection = nil
        stopKeepAliveTimer()
    }

    private func fail(with detail: String) {
        stopKeepAliveTimer()
        closeConnections()
        emitTerminalIfNeeded(.failed(detail))
    }

    private func emit(_ event: AudioTransportEvent) {
        eventHandler(event)
    }

    private func emitTerminalIfNeeded(_ event: AudioTransportEvent) {
        guard !terminalEventEmitted else {
            return
        }

        terminalEventEmitted = true
        emit(event)
    }

    private func isCurrentControlConnection(_ connection: NWConnection) -> Bool {
        guard let controlConnection else {
            return false
        }

        return ObjectIdentifier(controlConnection) == ObjectIdentifier(connection)
    }

    private func isCurrentUDPConnection(_ connection: NWConnection) -> Bool {
        guard let udpConnection else {
            return false
        }

        return ObjectIdentifier(udpConnection) == ObjectIdentifier(connection)
    }

    private static func describe(_ error: NWError) -> String {
        switch error {
        case .dns(let serviceError):
            "DNS error: \(serviceError)"
        case .posix(let code):
            POSIXError(code).localizedDescription
        case .tls(let code):
            "TLS error: \(code)"
        case .wifiAware(let code):
            "Wi-Fi Aware error: \(code)"
        @unknown default:
            error.localizedDescription
        }
    }
}

private extension Duration {
    var timeInterval: TimeInterval {
        let components = components
        return TimeInterval(components.seconds) + (TimeInterval(components.attoseconds) / 1_000_000_000_000_000_000)
    }
}
