@preconcurrency import AVFoundation
import Foundation

struct CapturedAudioFrame: Equatable, Sendable {
    let sequenceNumber: UInt64
    let capturedAt: Duration
    let format: MVPAudioFormat
    let payload: Data

    var sampleCount: Int {
        payload.count / format.bytesPerFrame
    }

    var debugSummary: String {
        "seq \(sequenceNumber), \(sampleCount) frames, \(payload.count) bytes"
    }
}

struct AudioInputLevel: Equatable, Sendable {
    let averageLevel: Float
    let peakLevel: Float

    static let silence = AudioInputLevel(averageLevel: 0, peakLevel: 0)

    var averagePercentage: Int {
        Int((averageLevel * 100).rounded())
    }

    var peakPercentage: Int {
        Int((peakLevel * 100).rounded())
    }

    var averageDecibels: Int {
        Self.decibels(for: averageLevel)
    }

    var peakDecibels: Int {
        Self.decibels(for: peakLevel)
    }

    private static func decibels(for level: Float) -> Int {
        let decibels = 20 * log10(max(level, 0.000_1))
        return Int(max(-80, decibels).rounded())
    }
}

enum CaptureAudioSessionProfile: String, Sendable, Equatable {
    case recordMeasurement
    case playAndRecordMeasurement

    var debugLabel: String {
        switch self {
        case .recordMeasurement:
            "Record + Measurement"
        case .playAndRecordMeasurement:
            "PlayAndRecord + Measurement"
        }
    }

    var sessionCategory: AVAudioSession.Category {
        switch self {
        case .recordMeasurement:
            .record
        case .playAndRecordMeasurement:
            .playAndRecord
        }
    }

    var sessionMode: AVAudioSession.Mode {
        .measurement
    }

    var categoryOptions: AVAudioSession.CategoryOptions {
        []
    }
}

struct MicrophoneCaptureStartup: Equatable, Sendable {
    let requestedFormat: MVPAudioFormat
    let audioSessionProfile: CaptureAudioSessionProfile
    let inputFormatSummary: String
    let outputFormatSummary: String
    let actualSampleRate: Double
    let actualBufferDuration: TimeInterval
    let sessionCategory: String
    let sessionMode: String
    let routeSummary: String

    var debugSummary: String {
        let roundedSampleRate = Int(actualSampleRate.rounded())
        let roundedBufferMilliseconds = Int((actualBufferDuration * 1_000).rounded())
        return "Profile \(audioSessionProfile.debugLabel). Input \(inputFormatSummary). Output \(outputFormatSummary). Session \(roundedSampleRate) Hz / \(roundedBufferMilliseconds) ms buffer. Category \(sessionCategory), mode \(sessionMode), route \(routeSummary)."
    }
}

enum MicrophoneCaptureError: LocalizedError {
    case simulatorUnavailable
    case alreadyCapturing
    case unsupportedOutputFormat
    case unsupportedInputConversion
    case sessionConfigurationFailed(String)
    case conversionFailed(String)

    var errorDescription: String? {
        switch self {
        case .simulatorUnavailable:
            "Live microphone capture requires a physical iPhone."
        case .alreadyCapturing:
            "Microphone capture is already running."
        case .unsupportedOutputFormat:
            "The iOS client could not create the requested mono PCM output format."
        case .unsupportedInputConversion:
            "The current microphone input format could not be converted into the MVP PCM format."
        case .sessionConfigurationFailed(let detail):
            "AVAudioSession setup failed: \(detail)"
        case .conversionFailed(let detail):
            "Microphone audio conversion failed: \(detail)"
        }
    }
}

final class MicrophoneCaptureClient {
    private final class SessionNotificationRelay: @unchecked Sendable {
        weak var client: MicrophoneCaptureClient?

        init(client: MicrophoneCaptureClient) {
            self.client = client
        }

        func handleInterruption(_ notification: Notification) {
            client?.handleInterruptionNotification(notification)
        }

        func handleRouteChange(_ notification: Notification) {
            client?.handleRouteChangeNotification(notification)
        }

        func handleMediaServicesReset() {
            client?.emitSessionEventIfCapturing(.mediaServicesWereReset)
        }
    }

    private let session = AVAudioSession.sharedInstance()
    private let lock = NSLock()

    private var engine: AVAudioEngine?
    private var targetFormat: AVAudioFormat?
    private var converter: AVAudioConverter?
    private var accumulator: PCMFrameAccumulator?
    private var inputGain: Float = 1
    private var onInputLevel: (@Sendable (AudioInputLevel) -> Void)?
    private var onFrame: (@Sendable (CapturedAudioFrame) -> Void)?
    private var onFailure: (@Sendable (Error) -> Void)?
    private var onSessionEvent: (@Sendable (CaptureSessionEvent) -> Void)?
    private var notificationObservers: [NSObjectProtocol] = []
    private var sessionNotificationRelay: SessionNotificationRelay?

    func startCapture(
        format: MVPAudioFormat = .defaultVoice,
        profile: CaptureAudioSessionProfile = .recordMeasurement,
        onInputLevel: @escaping @Sendable (AudioInputLevel) -> Void,
        onFrame: @escaping @Sendable (CapturedAudioFrame) -> Void,
        onFailure: @escaping @Sendable (Error) -> Void,
        onSessionEvent: @escaping @Sendable (CaptureSessionEvent) -> Void
    ) throws -> MicrophoneCaptureStartup {
        #if targetEnvironment(simulator)
        throw MicrophoneCaptureError.simulatorUnavailable
        #else
        lock.lock()
        defer { lock.unlock() }

        guard engine == nil else {
            throw MicrophoneCaptureError.alreadyCapturing
        }

        do {
            try configureSession(for: format, profile: profile)
        } catch {
            throw MicrophoneCaptureError.sessionConfigurationFailed(error.localizedDescription)
        }

        let engine = AVAudioEngine()
        let inputNode = engine.inputNode
        let inputFormat = inputNode.inputFormat(forBus: 0)

        guard let targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatInt16,
            sampleRate: Double(format.sampleRate),
            channels: AVAudioChannelCount(format.channelCount),
            interleaved: true
        ) else {
            teardownCaptureState()
            throw MicrophoneCaptureError.unsupportedOutputFormat
        }

        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            teardownCaptureState()
            throw MicrophoneCaptureError.unsupportedInputConversion
        }

        self.engine = engine
        self.targetFormat = targetFormat
        self.converter = converter
        self.accumulator = PCMFrameAccumulator(format: format)
        self.onInputLevel = onInputLevel
        self.onFrame = onFrame
        self.onFailure = onFailure
        self.onSessionEvent = onSessionEvent
        registerSessionObservers()

        inputNode.installTap(
            onBus: 0,
            bufferSize: AVAudioFrameCount(format.framesPerPacket),
            format: inputFormat
        ) { [weak self] buffer, _ in
            self?.handleInputBuffer(buffer)
        }

        do {
            engine.prepare()
            try engine.start()
        } catch {
            teardownCaptureState()
            throw MicrophoneCaptureError.sessionConfigurationFailed(error.localizedDescription)
        }

        return MicrophoneCaptureStartup(
            requestedFormat: format,
            audioSessionProfile: profile,
            inputFormatSummary: inputFormat.debugSummary,
            outputFormatSummary: targetFormat.debugSummary,
            actualSampleRate: session.sampleRate,
            actualBufferDuration: session.ioBufferDuration,
            sessionCategory: session.category.rawValue,
            sessionMode: session.mode.rawValue,
            routeSummary: Self.routeSummary(for: session.currentRoute)
        )
        #endif
    }

    func stopCapture() {
        lock.lock()
        teardownCaptureState()
        lock.unlock()
    }

    func setInputGain(_ gain: Float) {
        lock.lock()
        inputGain = max(0, gain)
        lock.unlock()
    }

    private func configureSession(for format: MVPAudioFormat, profile: CaptureAudioSessionProfile) throws {
        let packetDurationSeconds = TimeInterval(format.packetDurationMilliseconds) / 1_000

        // We only uplink microphone audio. Voice chat processing can sound
        // aggressively gated or "choppy" even when transport is perfect.
        try session.setCategory(profile.sessionCategory, mode: profile.sessionMode, options: profile.categoryOptions)
        try? session.setPreferredInputNumberOfChannels(format.channelCount)
        try session.setPreferredSampleRate(Double(format.sampleRate))
        try session.setPreferredIOBufferDuration(packetDurationSeconds)
        try session.setActive(true)
    }

    private func teardownCaptureState() {
        removeSessionObservers()

        if let engine {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }

        self.engine = nil
        converter = nil
        accumulator = nil
        targetFormat = nil
        onInputLevel = nil
        onFrame = nil
        onFailure = nil
        onSessionEvent = nil
        try? session.setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func handleInputBuffer(_ buffer: AVAudioPCMBuffer) {
        lock.lock()

        guard
            let converter,
            let targetFormat,
            var accumulator
        else {
            lock.unlock()
            return
        }

        do {
            let convertedBuffer = try Self.convertBuffer(buffer, using: converter, to: targetFormat)
            Self.applyGain(inputGain, to: convertedBuffer)
            let inputLevel = Self.measureInputLevel(from: convertedBuffer)
            let payload = Self.extractPCMData(from: convertedBuffer)
            let frames = accumulator.append(payload)
            self.accumulator = accumulator

            let levelCallback = onInputLevel
            let frameCallback = onFrame
            lock.unlock()

            levelCallback?(inputLevel)
            for frame in frames {
                frameCallback?(frame)
            }
        } catch {
            let failureCallback = onFailure
            teardownCaptureState()
            lock.unlock()
            failureCallback?(error)
        }
    }

    private static func convertBuffer(
        _ inputBuffer: AVAudioPCMBuffer,
        using converter: AVAudioConverter,
        to outputFormat: AVAudioFormat
    ) throws -> AVAudioPCMBuffer {
        let sampleRateRatio = outputFormat.sampleRate / inputBuffer.format.sampleRate
        let outputFrameCapacity = AVAudioFrameCount((Double(inputBuffer.frameLength) * sampleRateRatio).rounded(.up)) + 32

        guard let convertedBuffer = AVAudioPCMBuffer(
            pcmFormat: outputFormat,
            frameCapacity: max(outputFrameCapacity, 32)
        ) else {
            throw MicrophoneCaptureError.unsupportedOutputFormat
        }

        let inputSource = ConversionInputSource(buffer: inputBuffer)
        var conversionError: NSError?

        let status = converter.convert(to: convertedBuffer, error: &conversionError) { _, outStatus in
            if let providedBuffer = inputSource.buffer {
                outStatus.pointee = .haveData
                inputSource.buffer = nil
                return providedBuffer
            }

            outStatus.pointee = .noDataNow
            return nil
        }

        if let conversionError {
            throw MicrophoneCaptureError.conversionFailed(conversionError.localizedDescription)
        }

        switch status {
        case .error:
            throw MicrophoneCaptureError.conversionFailed("The converter reported an unknown runtime error.")
        case .haveData, .inputRanDry, .endOfStream:
            return convertedBuffer
        @unknown default:
            return convertedBuffer
        }
    }

    private static func extractPCMData(from buffer: AVAudioPCMBuffer) -> Data {
        let audioBuffer = buffer.audioBufferList.pointee.mBuffers

        guard let rawData = audioBuffer.mData else {
            return Data()
        }

        return Data(bytes: rawData, count: Int(audioBuffer.mDataByteSize))
    }

    private static func applyGain(_ gain: Float, to buffer: AVAudioPCMBuffer) {
        guard gain != 1 else {
            return
        }

        let audioBuffer = buffer.audioBufferList.pointee.mBuffers

        guard let rawData = audioBuffer.mData else {
            return
        }

        let sampleCount = Int(audioBuffer.mDataByteSize) / MemoryLayout<Int16>.size
        guard sampleCount > 0 else {
            return
        }

        let samples = rawData.bindMemory(to: Int16.self, capacity: sampleCount)
        let lowerBound = Float(Int16.min)
        let upperBound = Float(Int16.max)

        for index in 0 ..< sampleCount {
            let scaledSample = (Float(samples[index]) * gain).rounded()
            let clampedSample = max(lowerBound, min(upperBound, scaledSample))
            samples[index] = Int16(clampedSample)
        }
    }

    private static func measureInputLevel(from buffer: AVAudioPCMBuffer) -> AudioInputLevel {
        let audioBuffer = buffer.audioBufferList.pointee.mBuffers

        guard let rawData = audioBuffer.mData else {
            return .silence
        }

        let sampleCount = Int(audioBuffer.mDataByteSize) / MemoryLayout<Int16>.size
        guard sampleCount > 0 else {
            return .silence
        }

        let samples = rawData.bindMemory(to: Int16.self, capacity: sampleCount)
        let maxSample = Float(Int16.max)

        var peakLevel: Float = 0
        var squaredSum: Float = 0

        for index in 0 ..< sampleCount {
            let normalizedSample = Float(samples[index]) / maxSample
            let magnitude = abs(normalizedSample)
            peakLevel = max(peakLevel, magnitude)
            squaredSum += normalizedSample * normalizedSample
        }

        let averageLevel = sqrt(squaredSum / Float(sampleCount))
        return AudioInputLevel(averageLevel: averageLevel, peakLevel: peakLevel)
    }

    private func registerSessionObservers() {
        removeSessionObservers()

        let center = NotificationCenter.default
        let relay = SessionNotificationRelay(client: self)
        sessionNotificationRelay = relay
        notificationObservers = [
            center.addObserver(
                forName: AVAudioSession.interruptionNotification,
                object: session,
                queue: nil
            ) { notification in
                relay.handleInterruption(notification)
            },
            center.addObserver(
                forName: AVAudioSession.routeChangeNotification,
                object: session,
                queue: nil
            ) { notification in
                relay.handleRouteChange(notification)
            },
            center.addObserver(
                forName: AVAudioSession.mediaServicesWereResetNotification,
                object: nil,
                queue: nil
            ) { _ in
                relay.handleMediaServicesReset()
            }
        ]
    }

    private func removeSessionObservers() {
        let center = NotificationCenter.default
        for observer in notificationObservers {
            center.removeObserver(observer)
        }
        notificationObservers.removeAll(keepingCapacity: false)
        sessionNotificationRelay = nil
    }

    private func handleInterruptionNotification(_ notification: Notification) {
        guard
            let rawValue = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
            let interruptionType = AVAudioSession.InterruptionType(rawValue: rawValue)
        else {
            return
        }

        switch interruptionType {
        case .began:
            emitSessionEventIfCapturing(.interruptionBegan)
        case .ended:
            let optionsRawValue = notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0
            let options = AVAudioSession.InterruptionOptions(rawValue: optionsRawValue)
            emitSessionEventIfCapturing(.interruptionEnded(shouldResume: options.contains(.shouldResume)))
        @unknown default:
            return
        }
    }

    private func handleRouteChangeNotification(_ notification: Notification) {
        let routeChangeReason: CaptureRouteChangeReason
        if
            let rawValue = notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
            let sessionReason = AVAudioSession.RouteChangeReason(rawValue: rawValue)
        {
            routeChangeReason = Self.routeChangeReason(from: sessionReason)
        } else {
            routeChangeReason = .unknown
        }

        emitSessionEventIfCapturing(
            .routeChanged(
                CaptureRouteChange(
                    reason: routeChangeReason,
                    inputAvailable: session.isInputAvailable,
                    routeSummary: Self.routeSummary(for: session.currentRoute)
                )
            )
        )
    }

    private func emitSessionEventIfCapturing(_ event: CaptureSessionEvent) {
        lock.lock()
        guard engine != nil else {
            lock.unlock()
            return
        }

        let callback = onSessionEvent
        lock.unlock()
        callback?(event)
    }

    private static func routeSummary(for route: AVAudioSessionRouteDescription) -> String {
        let inputs = route.inputs.map { "\($0.portType.rawValue)=\($0.portName)" }
        let outputs = route.outputs.map { "\($0.portType.rawValue)=\($0.portName)" }
        let inputSummary = inputs.isEmpty ? "none" : inputs.joined(separator: ", ")
        let outputSummary = outputs.isEmpty ? "none" : outputs.joined(separator: ", ")
        return "inputs[\(inputSummary)] outputs[\(outputSummary)]"
    }

    private static func routeChangeReason(from reason: AVAudioSession.RouteChangeReason) -> CaptureRouteChangeReason {
        switch reason {
        case .newDeviceAvailable:
            .newDeviceAvailable
        case .oldDeviceUnavailable:
            .oldDeviceUnavailable
        case .categoryChange:
            .categoryChange
        case .override:
            .override
        case .wakeFromSleep:
            .wakeFromSleep
        case .noSuitableRouteForCategory:
            .noSuitableRouteForCategory
        case .routeConfigurationChange:
            .routeConfigurationChange
        case .unknown:
            .unknown
        @unknown default:
            .unknown
        }
    }
}

private final class ConversionInputSource: @unchecked Sendable {
    var buffer: AVAudioPCMBuffer?

    init(buffer: AVAudioPCMBuffer?) {
        self.buffer = buffer
    }
}

private struct PCMFrameAccumulator {
    let format: MVPAudioFormat

    private var pendingPayload = Data()
    private var nextSequenceNumber: UInt64 = 0

    init(format: MVPAudioFormat) {
        self.format = format
    }

    mutating func append(_ payload: Data) -> [CapturedAudioFrame] {
        guard !payload.isEmpty else {
            return []
        }

        if pendingPayload.isEmpty, payload.count == format.bytesPerPacket {
            defer { nextSequenceNumber += 1 }
            return [
                CapturedAudioFrame(
                    sequenceNumber: nextSequenceNumber,
                    capturedAt: .milliseconds(Int64(nextSequenceNumber) * Int64(format.packetDurationMilliseconds)),
                    format: format,
                    payload: payload
                )
            ]
        }

        pendingPayload.append(payload)

        var frames: [CapturedAudioFrame] = []
        let packetByteCount = format.bytesPerPacket

        while pendingPayload.count >= packetByteCount {
            let packetPayload = Data(pendingPayload.prefix(packetByteCount))
            pendingPayload.removeFirst(packetByteCount)

            frames.append(
                CapturedAudioFrame(
                    sequenceNumber: nextSequenceNumber,
                    capturedAt: .milliseconds(Int64(nextSequenceNumber) * Int64(format.packetDurationMilliseconds)),
                    format: format,
                    payload: packetPayload
                )
            )

            nextSequenceNumber += 1
        }

        return frames
    }
}

private extension AVAudioFormat {
    var debugSummary: String {
        let roundedSampleRate = Int(sampleRate.rounded())
        let interleaving = isInterleaved ? "interleaved" : "non-interleaved"
        return "\(roundedSampleRate) Hz, \(channelCount) ch, \(commonFormat.debugSummary), \(interleaving)"
    }
}

private extension AVAudioCommonFormat {
    var debugSummary: String {
        switch self {
        case .pcmFormatFloat32:
            "Float32 PCM"
        case .pcmFormatFloat64:
            "Float64 PCM"
        case .pcmFormatInt16:
            "Int16 PCM"
        case .pcmFormatInt32:
            "Int32 PCM"
        case .otherFormat:
            "Other"
        @unknown default:
            "Unknown"
        }
    }
}
