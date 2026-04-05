import Foundation
import Network

enum DebugTcpPcmClientError: LocalizedError {
    case invalidHost
    case invalidPort
    case alreadyConnected
    case notConnected
    case connectionFailed(String)
    case sendFailed(String)
    case disconnectedByPeer

    var errorDescription: String? {
        switch self {
        case .invalidHost:
            "Enter a valid Windows host address before connecting."
        case .invalidPort:
            "Enter a valid debug TCP port before connecting."
        case .alreadyConnected:
            "The debug TCP client is already connected."
        case .notConnected:
            "The debug TCP client is not connected."
        case .connectionFailed(let detail):
            "TCP connection failed: \(detail)"
        case .sendFailed(let detail):
            "PCM send failed: \(detail)"
        case .disconnectedByPeer:
            "The Windows host closed the TCP connection."
        }
    }
}

enum DebugTcpPcmClientEvent: Sendable {
    case connecting
    case ready
    case failed(String)
    case cancelled
    case peerClosed
}

final class DebugTcpPcmClient: @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.roba.phlanmic.ios-client.debug-tcp")
    private let eventHandler: @Sendable (DebugTcpPcmClientEvent) -> Void

    private var connection: NWConnection?
    private var isReady = false

    init(eventHandler: @escaping @Sendable (DebugTcpPcmClientEvent) -> Void) {
        self.eventHandler = eventHandler
    }

    func connect(host: String, port: UInt16) throws {
        let trimmedHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedHost.isEmpty else {
            throw DebugTcpPcmClientError.invalidHost
        }

        guard let endpointPort = NWEndpoint.Port(rawValue: port) else {
            throw DebugTcpPcmClientError.invalidPort
        }

        let alreadyConnected = queue.sync { connection != nil }
        guard !alreadyConnected else {
            throw DebugTcpPcmClientError.alreadyConnected
        }

        queue.async { [weak self] in
            guard let self else {
                return
            }

            let connection = NWConnection(host: NWEndpoint.Host(trimmedHost), port: endpointPort, using: .tcp)
            self.connection = connection
            self.isReady = false

            connection.stateUpdateHandler = { [weak self, weak connection] state in
                guard let self, let connection else {
                    return
                }

                self.handleStateUpdate(state, for: connection)
            }

            self.eventHandler(.connecting)
            connection.start(queue: self.queue)
        }
    }

    func disconnect() {
        queue.async { [weak self] in
            guard let self else {
                return
            }

            guard let connection = self.connection else {
                return
            }

            self.isReady = false
            connection.cancel()
        }
    }

    func send(_ payload: Data, completion: @escaping @Sendable (Result<Int, Error>) -> Void) {
        queue.async { [weak self] in
            guard let self else {
                completion(.failure(DebugTcpPcmClientError.notConnected))
                return
            }

            guard let connection = self.connection, self.isReady else {
                completion(.failure(DebugTcpPcmClientError.notConnected))
                return
            }

            connection.send(content: payload, completion: .contentProcessed { error in
                if let error {
                    completion(.failure(DebugTcpPcmClientError.sendFailed(Self.describe(error))))
                } else {
                    completion(.success(payload.count))
                }
            })
        }
    }

    private func handleStateUpdate(_ state: NWConnection.State, for connection: NWConnection) {
        guard isCurrentConnection(connection) else {
            return
        }

        switch state {
        case .setup, .preparing:
            eventHandler(.connecting)
        case .waiting(let error):
            finishConnection(connection)
            eventHandler(.failed(DebugTcpPcmClientError.connectionFailed(Self.describe(error)).localizedDescription))
        case .ready:
            isReady = true
            eventHandler(.ready)
            receiveDisconnectSignal(on: connection)
        case .failed(let error):
            finishConnection(connection)
            eventHandler(.failed(DebugTcpPcmClientError.connectionFailed(Self.describe(error)).localizedDescription))
        case .cancelled:
            finishConnection(connection)
            eventHandler(.cancelled)
        @unknown default:
            break
        }
    }

    private func receiveDisconnectSignal(on connection: NWConnection) {
        guard isCurrentConnection(connection) else {
            return
        }

        connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [weak self, weak connection] _, _, isComplete, error in
            guard let self, let connection else {
                return
            }

            guard self.isCurrentConnection(connection) else {
                return
            }

            if let error {
                self.finishConnection(connection)
                self.eventHandler(.failed(DebugTcpPcmClientError.connectionFailed(Self.describe(error)).localizedDescription))
                return
            }

            if isComplete {
                self.finishConnection(connection)
                self.eventHandler(.peerClosed)
                return
            }

            self.receiveDisconnectSignal(on: connection)
        }
    }

    private func finishConnection(_ connection: NWConnection) {
        guard isCurrentConnection(connection) else {
            return
        }

        isReady = false
        self.connection = nil
    }

    private func isCurrentConnection(_ connection: NWConnection) -> Bool {
        guard let activeConnection = self.connection else {
            return false
        }

        return ObjectIdentifier(activeConnection) == ObjectIdentifier(connection)
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
