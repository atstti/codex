import AppKit
import AudioToolbox
import CoreGraphics
import CoreMedia
import Darwin
import Dispatch
import Foundation
import ScreenCaptureKit

// A local, binary pipe transport. No audio is written to disk or sent over a network.
// Packet: UInt32 little-endian byte count, then interleaved Float32 little-endian L/R.
private enum CaptureError: LocalizedError {
    case permissionRequired
    case noDisplay
    case unsupportedFormat(String)
    case malformedBuffer
    case outputClosed(Int32)
    case invalidAgentBundle
    case agentActivationFailed
    case selfTestFailed(String)

    var errorDescription: String? {
        switch self {
        case .permissionRequired:
            return "システム設定の「プライバシーとセキュリティ」→「画面収録」（または「画面とシステムオーディオの収録」）で DAMeter の権限を許可してください。必要なら DAMeter を終了・再起動し、再接続を押してください。"
        case .noDisplay:
            return "音声キャプチャの基準となる画面が見つかりません。画面の接続後に再接続します。"
        case .unsupportedFormat(let details):
            return "未対応のシステム音声形式です: \(details)"
        case .malformedBuffer:
            return "システム音声バッファの形式またはサイズが不正です。"
        case .outputClosed(let code):
            return "音声解析プロセスへの接続が終了しました (errno \(code))。"
        case .invalidAgentBundle:
            return "音声ヘルパーの配置情報が不正です。DAMeter の修正版を再インストールしてください。"
        case .agentActivationFailed:
            return "音声ヘルパーをバックグラウンドモードで起動できませんでした。"
        case .selfTestFailed(let details):
            return "PCM self-test failed: \(details)"
        }
    }
}

private func diagnostic(_ message: String) {
    // Diagnostic messages contain only state/errors, never audio samples.
    let line = message.replacingOccurrences(of: "\n", with: " ") + "\n"
    if let data = line.data(using: .utf8) {
        try? FileHandle.standardError.write(contentsOf: data)
    }
}

private enum StopReason {
    case requested
    case failed(Error)
}

/// Completion may arrive before or after the async waiter is installed.
private final class StopSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var result: StopReason?
    private var continuation: CheckedContinuation<StopReason, Never>?

    func finish(_ result: StopReason) {
        lock.lock()
        guard self.result == nil else { lock.unlock(); return }
        self.result = result
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume(returning: result)
    }

    func wait() async -> StopReason {
        await withCheckedContinuation { waiter in
            lock.lock()
            if let result = result {
                lock.unlock()
                waiter.resume(returning: result)
            } else {
                continuation = waiter
                lock.unlock()
            }
        }
    }
}

private struct ChannelSource {
    let data: UnsafeRawPointer
    let stride: Int
    let offset: Int
    let bytesPerSample: Int
}

/// Keeps all unsafe AudioBufferList pointers within withAudioBufferList's lifetime.
private enum PCMTransport {
    static let maximumPayloadBytes = 4 * 1024 * 1024

    static func packet(from buffer: CMSampleBuffer) throws -> Data {
        guard buffer.isValid,
              let description = buffer.formatDescription?.audioStreamBasicDescription else {
            throw CaptureError.malformedBuffer
        }
        // The CoreMedia overlay retains the block for this closure. No pointer
        // escapes; a Result keeps the body nonthrowing for all SDK overlays.
        let result: Result<Data, Error> = try buffer.withAudioBufferList { buffers, retainedBlock in
            _ = retainedBlock
            return Result { try packet(description: description, frames: buffer.numSamples, buffers: buffers) }
        }
        return try result.get()
    }

    private static func packet(description: AudioStreamBasicDescription, frames: Int,
                               buffers: UnsafeMutableAudioBufferListPointer) throws -> Data {
        guard description.mFormatID == kAudioFormatLinearPCM else {
            throw CaptureError.unsupportedFormat("Linear PCM が必要です")
        }
        guard frames > 0, frames <= maximumPayloadBytes / 8,
              description.mChannelsPerFrame > 0,
              description.mChannelsPerFrame <= 64,
              description.mBytesPerFrame > 0 else {
            throw CaptureError.malformedBuffer
        }
        let isFloat = description.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let bits = Int(description.mBitsPerChannel)
        if (isFloat && bits != 32 && bits != 64) || (!isFloat && (bits < 1 || bits > 32)) {
            throw CaptureError.unsupportedFormat("\(bits) bit")
        }
        let bigEndian = description.mFormatFlags & kAudioFormatFlagIsBigEndian != 0
        let signed = description.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0
        let alignedHigh = description.mFormatFlags & kAudioFormatFlagIsAlignedHigh != 0
        var sources: [ChannelSource] = []
        for item in buffers {
            let channels = Int(item.mNumberChannels)
            guard channels > 0 else { continue }
            guard channels <= 64 else { throw CaptureError.malformedBuffer }
            let stride = Int(description.mBytesPerFrame)
            guard stride % channels == 0, let pointer = item.mData else {
                throw CaptureError.malformedBuffer
            }
            let sampleBytes = stride / channels
            guard sampleBytes >= (bits + 7) / 8,
                  sampleBytes <= 8,
                  (!isFloat || sampleBytes == bits / 8) else {
                throw CaptureError.malformedBuffer
            }
            for channel in 0..<channels where sources.count < 2 {
                let offset = channel * sampleBytes
                let requiredBytes = (frames - 1) * stride + offset + sampleBytes
                guard requiredBytes <= Int(item.mDataByteSize) else {
                    throw CaptureError.malformedBuffer
                }
                sources.append(ChannelSource(data: UnsafeRawPointer(pointer), stride: stride,
                                             offset: offset, bytesPerSample: sampleBytes))
            }
            if sources.count == 2 { break }
        }
        guard let left = sources.first else { throw CaptureError.malformedBuffer }
        let right = sources.count > 1 ? sources[1] : left
        let payloadBytes = frames * 8
        var result = Data(count: 4 + payloadBytes)
        result.withUnsafeMutableBytes { storage in
            let bytes = storage.bindMemory(to: UInt8.self)
            put(UInt32(payloadBytes), into: bytes, at: 0)
            for frame in 0..<frames {
                let l = sample(left, frame: frame, bits: bits, isFloat: isFloat,
                               bigEndian: bigEndian, signed: signed, alignedHigh: alignedHigh)
                let r = sample(right, frame: frame, bits: bits, isFloat: isFloat,
                               bigEndian: bigEndian, signed: signed, alignedHigh: alignedHigh)
                put(l.bitPattern, into: bytes, at: 4 + frame * 8)
                put(r.bitPattern, into: bytes, at: 8 + frame * 8)
            }
        }
        return result
    }

    private static func sample(_ source: ChannelSource, frame: Int, bits: Int,
                               isFloat: Bool, bigEndian: Bool, signed: Bool, alignedHigh: Bool) -> Float {
        let pointer = source.data.advanced(by: frame * source.stride + source.offset)
            .assumingMemoryBound(to: UInt8.self)
        var raw: UInt64 = 0
        for byte in 0..<source.bytesPerSample {
            let shift = bigEndian ? (source.bytesPerSample - 1 - byte) * 8 : byte * 8
            raw |= UInt64(pointer[byte]) << shift
        }
        let value: Double
        if isFloat {
            value = bits == 32 ? Double(Float(bitPattern: UInt32(truncatingIfNeeded: raw))) : Double(bitPattern: raw)
        } else {
            if alignedHigh { raw >>= source.bytesPerSample * 8 - bits }
            let mask = (UInt64(1) << bits) - 1
            raw &= mask
            let midpoint = Int64(1) << (bits - 1)
            if signed {
                let decoded = raw & UInt64(midpoint) != 0 ? Int64(raw) - (Int64(1) << bits) : Int64(raw)
                value = Double(decoded) / Double(midpoint)
            } else {
                value = Double(Int64(raw) - midpoint) / Double(midpoint)
            }
        }
        let result = Float(value)
        return result.isFinite ? result : 0
    }

    private static func put(_ value: UInt32, into bytes: UnsafeMutableBufferPointer<UInt8>, at offset: Int) {
        bytes[offset] = UInt8(truncatingIfNeeded: value)
        bytes[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
        bytes[offset + 2] = UInt8(truncatingIfNeeded: value >> 16)
        bytes[offset + 3] = UInt8(truncatingIfNeeded: value >> 24)
    }

    static func write(_ packet: Data) throws {
        try packet.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            var written = 0
            while written < bytes.count {
                let count = Darwin.write(STDOUT_FILENO, base.advanced(by: written), bytes.count - written)
                if count > 0 { written += count }
                else if count < 0 && errno == EINTR { continue }
                else { throw CaptureError.outputClosed(errno) }
            }
        }
    }

    /// Exercises the real converter with synthetic buffer layouts, never capture
    /// permissions, devices, screen content, or microphone input.
    static func selfTest() throws {
        let f32 = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked
        let signed = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked
        let expected: [Float] = [0.25, -0.5, 1, -1]
        try expect(testPacket(bits: 32, flags: f32, channels: 2, bytesPerFrame: 8, frames: 2,
                              planes: [floatBytes(expected)], planeChannels: [2]), samples: expected)
        try expect(testPacket(bits: 32, flags: f32 | kAudioFormatFlagIsNonInterleaved, channels: 2,
                              bytesPerFrame: 4, frames: 2,
                              planes: [floatBytes([0.25, 1]), floatBytes([-0.5, -1])], planeChannels: [1, 1]), samples: expected)
        try expect(testPacket(bits: 24, flags: signed, channels: 1, bytesPerFrame: 3, frames: 2,
                              planes: [[0xff, 0xff, 0x7f, 0, 0, 0x80]], planeChannels: [1]),
                   samples: [Float(8_388_607.0 / 8_388_608.0), Float(8_388_607.0 / 8_388_608.0), -1, -1])
        let doubles: [Double] = [.nan, .infinity, 0.5]
        let doubleBytes = doubles.flatMap { value in (0..<8).map { UInt8(truncatingIfNeeded: value.bitPattern >> ($0 * 8)) } }
        try expect(testPacket(bits: 64, flags: f32, channels: 1, bytesPerFrame: 8, frames: 3,
                              planes: [doubleBytes], planeChannels: [1]), samples: [0, 0, 0, 0, 0.5, 0.5])
        try expect(testPacket(bits: 8, flags: kAudioFormatFlagIsPacked, channels: 1, bytesPerFrame: 1, frames: 3,
                              planes: [[0, 128, 255]], planeChannels: [1]), samples: [-1, -1, 0, 0, Float(127.0 / 128), Float(127.0 / 128)])
        try expect(testPacket(bits: 16, flags: signed, channels: 2, bytesPerFrame: 4, frames: 2,
                              planes: [[0, 0x40, 0, 0x80, 0, 0, 0xff, 0x7f]], planeChannels: [2]),
                   samples: [0.5, -1, 0, Float(32_767.0 / 32_768)])
        try expect(testPacket(bits: 24, flags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsAlignedHigh,
                              channels: 2, bytesPerFrame: 8, frames: 1,
                              planes: [[0, 0, 0, 0x40, 0, 0, 0, 0xc0]], planeChannels: [2]), samples: [0.5, -0.5])
        let bigEndian = expected.flatMap { value in (0..<4).map { UInt8(truncatingIfNeeded: value.bitPattern >> ((3 - $0) * 8)) } }
        try expect(testPacket(bits: 32, flags: f32 | kAudioFormatFlagIsBigEndian, channels: 2,
                              bytesPerFrame: 8, frames: 2, planes: [bigEndian], planeChannels: [2]), samples: expected)
        do {
            _ = try testPacket(bits: 32, flags: f32, channels: 1, bytesPerFrame: 4, frames: 2,
                               planes: [[0, 0, 0]], planeChannels: [1])
            throw CaptureError.selfTestFailed("短いバッファを拒否しませんでした")
        } catch CaptureError.malformedBuffer { }
        diagnostic("STATUS: PCM self-test passed (9 cases; no permission or capture).")
    }

    private static func floatBytes(_ values: [Float]) -> [UInt8] {
        values.flatMap { value in (0..<4).map { UInt8(truncatingIfNeeded: value.bitPattern >> ($0 * 8)) } }
    }

    private static func testPacket(bits: UInt32, flags: AudioFormatFlags, channels: UInt32,
                                   bytesPerFrame: UInt32, frames: Int,
                                   planes: [[UInt8]], planeChannels: [UInt32]) throws -> Data {
        let description = AudioStreamBasicDescription(mSampleRate: 48_000,
            mFormatID: kAudioFormatLinearPCM, mFormatFlags: flags,
            mBytesPerPacket: bytesPerFrame, mFramesPerPacket: 1,
            mBytesPerFrame: bytesPerFrame, mChannelsPerFrame: channels,
            mBitsPerChannel: bits, mReserved: 0)
        let raw = UnsafeMutableRawPointer.allocate(
            byteCount: MemoryLayout<AudioBufferList>.size + (planes.count - 1) * MemoryLayout<AudioBuffer>.stride,
            alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        let list = raw.bindMemory(to: AudioBufferList.self, capacity: 1)
        list.initialize(to: AudioBufferList())
        defer { list.deinitialize(count: 1) }
        list.pointee.mNumberBuffers = UInt32(planes.count)
        let buffers = UnsafeMutableAudioBufferListPointer(list)
        var allocations: [UnsafeMutablePointer<UInt8>] = []
        defer { allocations.forEach { $0.deallocate() } }
        for index in planes.indices {
            let pointer = UnsafeMutablePointer<UInt8>.allocate(capacity: max(1, planes[index].count))
            allocations.append(pointer)
            planes[index].withUnsafeBufferPointer { source in
                if let base = source.baseAddress { pointer.initialize(from: base, count: source.count) }
            }
            buffers[index] = AudioBuffer(mNumberChannels: planeChannels[index],
                mDataByteSize: UInt32(planes[index].count), mData: UnsafeMutableRawPointer(pointer))
        }
        return try packet(description: description, frames: frames, buffers: buffers)
    }

    private static func expect(_ packet: Data, samples: [Float]) throws {
        let bytes = [UInt8](packet)
        guard bytes.count == 4 + samples.count * 4 else { throw CaptureError.selfTestFailed("パケット長") }
        func word(at offset: Int) -> UInt32 {
            UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8 |
                UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
        }
        guard Int(word(at: 0)) == samples.count * 4 else { throw CaptureError.selfTestFailed("ヘッダー長") }
        for index in samples.indices {
            let actual = Float(bitPattern: word(at: 4 + index * 4))
            guard actual.isFinite, abs(actual - samples[index]) < 0.000001 else {
                throw CaptureError.selfTestFailed("PCM デコードまたはチャンネル配置")
            }
        }
    }
}

@available(macOS 13.0, *)
private final class AudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let stopSignal: StopSignal
    private let queue = DispatchQueue(label: "org.atstti.DAMeter.audio", qos: .userInitiated)
    private var stream: SCStream?
    private let stateLock = NSLock()
    private var stopping = false

    init(stopSignal: StopSignal) { self.stopSignal = stopSignal }

    func start() async throws {
        if !CGPreflightScreenCaptureAccess() {
            diagnostic("STATUS: システム音声の取得には macOS の画面収録権限が必要です。許可ダイアログを確認してください。")
            guard CGRequestScreenCaptureAccess() else { throw CaptureError.permissionRequired }
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw CaptureError.noDisplay }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        // ScreenCaptureKit needs a display filter on macOS 13. No screen output
        // handler is registered, and screen frames are never exposed or saved.
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.queueDepth = 3
        configuration.showsCursor = false
        let session = SCStream(filter: filter, configuration: configuration, delegate: self)
        stream = session
        try session.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
        try await session.startCapture()
        diagnostic("STATUS: システム音声を取得中です（48 kHz・ステレオ）。マイク入力は使用しません。")
    }

    private func markStopping() {
        stateLock.lock()
        stopping = true
        stateLock.unlock()
    }

    func stop() async {
        markStopping()
        guard let session = stream else { return }
        do { try await session.stopCapture() }
        catch { diagnostic("STATUS: 音声キャプチャの終了: \(error.localizedDescription)") }
        try? session.removeStreamOutput(self, type: .audio)
        stream = nil
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid, sampleBuffer.numSamples > 0 else { return }
        stateLock.lock()
        let active = !stopping
        stateLock.unlock()
        guard active else { return }
        do { try PCMTransport.write(PCMTransport.packet(from: sampleBuffer)) }
        catch { stopSignal.finish(.failed(error)) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        stateLock.lock()
        let unexpected = !stopping
        stateLock.unlock()
        if unexpected { stopSignal.finish(.failed(error)) }
    }
}

@main
private struct DAMeterAudioCapture {
    /// A helper executable inside the main application's MacOS directory can be
    /// attributed to the main app by LaunchServices. Its own UI-element bundle
    /// gives it a distinct identity before AppKit or the permission API starts.
    @MainActor
    private static func configureAgent() throws {
        let bundle = Bundle.main
        guard bundle.bundleIdentifier == "org.atstti.DAMeter.AudioCapture",
              bundle.object(forInfoDictionaryKey: "CFBundleExecutable") as? String == "DAMeter.AudioCapture",
              bundle.object(forInfoDictionaryKey: "LSUIElement") as? Bool == true else {
            throw CaptureError.invalidAgentBundle
        }
        let application = NSApplication.shared
        if application.activationPolicy() != .accessory && !application.setActivationPolicy(.accessory) {
            throw CaptureError.agentActivationFailed
        }
        guard application.activationPolicy() == .accessory else {
            throw CaptureError.agentActivationFailed
        }
    }

    @MainActor
    static func main() async {
        guard #available(macOS 13.0, *) else {
            diagnostic("ERROR: システム音声の取得には macOS 13 Ventura 以降が必要です。")
            Darwin.exit(69)
        }
        if CommandLine.arguments == [CommandLine.arguments[0], "--self-test"] {
            do { try PCMTransport.selfTest(); Darwin.exit(0) }
            catch { diagnostic("ERROR: \(error.localizedDescription)"); Darwin.exit(70) }
        }
        if CommandLine.arguments == [CommandLine.arguments[0], "--check"] {
            diagnostic("STATUS: DAMeter.AudioCapture protocol=1 macOS13+ ScreenCaptureKit stereo-float32-le")
            Darwin.exit(0)
        }
        if CommandLine.arguments == [CommandLine.arguments[0], "--agent-check"] {
            do {
                try configureAgent()
                diagnostic("STATUS: bundle=org.atstti.DAMeter.AudioCapture executable=DAMeter.AudioCapture LSUIElement=true activationPolicy=accessory")
                Darwin.exit(0)
            } catch {
                diagnostic("ERROR: \(error.localizedDescription)")
                Darwin.exit(78)
            }
        }
        if CommandLine.arguments.count > 1 {
            diagnostic("ERROR: Usage: DAMeter.AudioCapture [--check|--agent-check|--self-test]")
            Darwin.exit(64)
        }
        do { try configureAgent() }
        catch {
            diagnostic("ERROR: \(error.localizedDescription)")
            Darwin.exit(78)
        }
        signal(SIGPIPE, SIG_IGN)
        let stopSignal = StopSignal()
        let sources = [SIGINT, SIGTERM].map { value -> DispatchSourceSignal in
            signal(value, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: value, queue: .global())
            source.setEventHandler { stopSignal.finish(.requested) }
            source.resume()
            return source
        }
        let parent = getppid()
        let watchdog = DispatchSource.makeTimerSource(queue: .global())
        watchdog.schedule(deadline: .now() + 2, repeating: 2)
        watchdog.setEventHandler { if getppid() != parent { stopSignal.finish(.requested) } }
        watchdog.resume()
        Task.detached {
            while let line = readLine() {
                if line.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "stop" { break }
            }
            stopSignal.finish(.requested)
        }
        let capture = AudioCapture(stopSignal: stopSignal)
        let exitCode: Int32
        do {
            try await capture.start()
            switch await stopSignal.wait() {
            case .requested: exitCode = 0
            case .failed(let error):
                let nativeError = error as NSError
                if nativeError.domain.contains("ScreenCaptureKit") && nativeError.code == -3801 {
                    diagnostic("ERROR: \(CaptureError.permissionRequired.localizedDescription)")
                    exitCode = 77
                } else {
                    diagnostic("ERROR: \(error.localizedDescription)")
                    exitCode = 70
                }
            }
        } catch CaptureError.permissionRequired {
            diagnostic("ERROR: \(CaptureError.permissionRequired.localizedDescription)")
            exitCode = 77
        } catch {
            let nativeError = error as NSError
            // ScreenCaptureKit's documented user-declined error can still arrive
            // after a permission request (for example before the app restarts).
            if nativeError.domain.contains("ScreenCaptureKit") && nativeError.code == -3801 {
                diagnostic("ERROR: \(CaptureError.permissionRequired.localizedDescription)")
                exitCode = 77
            } else {
                diagnostic("ERROR: \(error.localizedDescription)")
                exitCode = 70
            }
        }
        await capture.stop()
        sources.forEach { $0.cancel() }
        watchdog.cancel()
        Darwin.exit(exitCode)
    }
}
