import Foundation

struct HostConfiguration: Equatable {
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

    var hostAddress = ""
    var portText = "9000"
    var transportMode: TransportMode = .tcpDebug

    var validatedPort: UInt16? {
        guard let port = UInt16(portText), port > 0 else {
            return nil
        }

        return port
    }

    var displayEndpoint: String {
        let host = hostAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        let endpointHost = host.isEmpty ? "<host>" : host
        let endpointPort = validatedPort.map(String.init) ?? "<port>"
        return "\(endpointHost):\(endpointPort)"
    }
}
