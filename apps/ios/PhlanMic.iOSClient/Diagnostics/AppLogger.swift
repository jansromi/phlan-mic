import Foundation
import OSLog

struct AppLogger {
    private let logger = Logger(subsystem: "com.roba.phlanmic.ios-client", category: "app")

    func log(_ message: String) {
        logger.log("\(message, privacy: .public)")
    }
}
