@preconcurrency import ActivityKit
import Foundation

struct PhlanMicActivityAttributes: ActivityAttributes, Hashable, Sendable {
    struct ContentState: Codable, Hashable, Sendable {
        let transportStatusLabel: String
        let transportStatusTint: String
        let shortStatusLabel: String
        let audioLevelPercentage: Int
        let framesSent: Int
        let alertReason: String?
    }

    let hostEndpoint: String
    let transportMode: String
}
