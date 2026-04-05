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

struct MicrophoneCaptureStartup: Equatable, Sendable {
    let requestedFormat: MVPAudioFormat
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
        return "Input \(inputFormatSummary). Output \(outputFormatSummary). Session \(roundedSampleRate) Hz / \(roundedBufferMilliseconds) ms buffer. Category \(sessionCategory), mode \(sessionMode), route \(routeSummary)."
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
    private let session = AVAudioSession.sharedInstance()
    private let lock = NSLock()

    private var engine: AVAudioEngine?
    private var targetFormat: AVAudioFormat?
    private var converter: AVAudioConverter?
    private var accumulator: PCMFrameAccumulator?
    private var onInputLevel: (@Sendable (AudioInputLevel) -> Void)?
    private var onFrame: (@Sendable (CapturedAudioFrame) -> Void)?
    private var onFailure: (@Sendable (Error) -> Void)?

    func startCapture(
        format: MVPAudioFormat = .defaultVoice,
        onInputLevel: @escaping @Sendable (AudioInputLevel) -> Void,
        onFrame: @escaping @Sendable (CapturedAudioFrame) -> Void,
        onFailure: @escaping @Sendable (Error) -> Void
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
            try configureSession(for: format)
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

    private func configureSession(for format: MVPAudioFormat) throws {
        let packetDurationSeconds = TimeInterval(format.packetDurationMilliseconds) / 1_000

        // We only uplink microphone audio. Voice chat processing can sound
        // aggressively gated or "choppy" even when transport is perfect.
        try session.setCategory(.record, mode: .measurement, options: [])
        try? session.setPreferredInputNumberOfChannels(format.channelCount)
        try session.setPreferredSampleRate(Double(format.sampleRate))
        try session.setPreferredIOBufferDuration(packetDurationSeconds)
        try session.setActive(true)
    }

    private func teardownCaptureState() {
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

    private static func routeSummary(for route: AVAudioSessionRouteDescription) -> String {
        let inputs = route.inputs.map { "\($0.portType.rawValue)=\($0.portName)" }
        let outputs = route.outputs.map { "\($0.portType.rawValue)=\($0.portName)" }
        let inputSummary = inputs.isEmpty ? "none" : inputs.joined(separator: ", ")
        let outputSummary = outputs.isEmpty ? "none" : outputs.joined(separator: ", ")
        return "inputs[\(inputSummary)] outputs[\(outputSummary)]"
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
