import Foundation

final class ToneGeneratorCaptureClient {
    private var timer: DispatchSourceTimer?
    private var sequenceNumber: UInt64 = 0
    private var startTime: ContinuousClock.Instant?
    private var phase: Double = 0

    private var onInputLevel: (@Sendable (AudioInputLevel) -> Void)?
    private var onCaptureDiagnostic: (@Sendable (CaptureBufferDiagnostic) -> Void)?
    private var onFrame: (@Sendable (CapturedAudioFrame) -> Void)?

    private let frequencyHz: Double = 440
    private let amplitude: Double = 0.8

    func start(
        format: MVPAudioFormat,
        onInputLevel: @escaping @Sendable (AudioInputLevel) -> Void,
        onCaptureDiagnostic: @escaping @Sendable (CaptureBufferDiagnostic) -> Void,
        onFrame: @escaping @Sendable (CapturedAudioFrame) -> Void
    ) {
        stop()

        self.onInputLevel = onInputLevel
        self.onCaptureDiagnostic = onCaptureDiagnostic
        self.onFrame = onFrame
        self.sequenceNumber = 0
        self.phase = 0
        self.startTime = ContinuousClock.now

        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .userInteractive))
        let intervalNs = UInt64(format.packetDurationMilliseconds) * 1_000_000
        timer.schedule(deadline: .now(), repeating: .nanoseconds(Int(intervalNs)))

        timer.setEventHandler { [weak self] in
            self?.generateFrame(format: format)
        }

        self.timer = timer
        timer.resume()
    }

    func stop() {
        timer?.cancel()
        timer = nil
        onInputLevel = nil
        onCaptureDiagnostic = nil
        onFrame = nil
    }

    private func generateFrame(format: MVPAudioFormat) {
        let sampleCount = format.framesPerPacket
        let phaseIncrement = 2.0 * .pi * frequencyHz / Double(format.sampleRate)
        let maxAmplitude = Double(Int16.max) * amplitude

        var payload = Data(count: format.bytesPerPacket)
        payload.withUnsafeMutableBytes { rawBuffer in
            let samples = rawBuffer.bindMemory(to: Int16.self)
            for i in 0 ..< sampleCount {
                let value = sin(phase + phaseIncrement * Double(i)) * maxAmplitude
                samples[i] = Int16(clamping: Int(value.rounded()))
            }
        }

        phase += phaseIncrement * Double(sampleCount)
        phase = phase.truncatingRemainder(dividingBy: 2.0 * .pi)

        let elapsed = startTime.map { ContinuousClock.now - $0 } ?? .zero

        let frame = CapturedAudioFrame(
            sequenceNumber: sequenceNumber,
            capturedAt: elapsed,
            format: format,
            payload: payload
        )
        sequenceNumber += 1

        let peakLevel = Float(amplitude)
        let rmsLevel = peakLevel / sqrt(2)
        let level = AudioInputLevel(averageLevel: rmsLevel, peakLevel: peakLevel)
        onInputLevel?(level)
        onCaptureDiagnostic?(
            CaptureBufferDiagnostic(
                convertedFrameLength: sampleCount,
                converterBufferByteCount: format.bytesPerPacket,
                extractedPayloadByteCount: format.bytesPerPacket,
                extractedPayloadLevel: level,
                framedPacketCount: 1,
                pendingPacketBytes: 0
            )
        )
        onFrame?(frame)
    }
}
