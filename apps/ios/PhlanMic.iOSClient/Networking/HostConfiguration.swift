import Foundation

struct HostConfiguration: Equatable {
    static let defaultHostAddress = "192.168.50.18"
    static let defaultDebugTcpPort: UInt16 = 42_100

    enum TransportMode: String, CaseIterable, Identifiable {
        case tcpDebug
        case udpRealtime

        var id: String { rawValue }

        var label: String {
            switch self {
            case .tcpDebug:
                "TCP Debug"
            case .udpRealtime:
                "UDP Realtime"
            }
        }
    }

    var hostAddress = defaultHostAddress
    var portText = String(defaultDebugTcpPort)
    var transportMode: TransportMode = .udpRealtime

    var validatedPort: UInt16? {
        guard let port = UInt16(portText), port > 0 else {
            return nil
        }

        return port
    }

    var trimmedHostAddress: String {
        hostAddress.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var displayEndpoint: String {
        let endpointHost = trimmedHostAddress.isEmpty ? "<host>" : trimmedHostAddress
        let endpointPort = validatedPort.map(String.init) ?? "<port>"
        return "\(endpointHost):\(endpointPort)"
    }
}
