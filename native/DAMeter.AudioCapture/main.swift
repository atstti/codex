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
    case outputClosed(Int32)
    case invalidAgentBundle
    case agentActivationFailed

    var errorDescription: String? {
        switch self {
        case .permissionRequired:
            return "macOS が音声キャプチャへのアクセスを拒否しました。許可が ON の場合も DAMeter を ⌘Q で完全終了して再起動してください。設定に「DAMeter 音声取得」が表示される場合は、その項目も確認してください。"
        case .noDisplay:
            return "音声キャプチャの基準となる画面が見つかりません。画面の接続後に再接続します。"
        case .outputClosed(let code):
            return "音声解析プロセスへの接続が終了しました (errno \(code))。"
        case .invalidAgentBundle:
            return "音声ヘルパーの配置情報が不正です。DAMeter の修正版を再インストールしてください。"
        case .agentActivationFailed:
            return "音声ヘルパーをバックグラウンドモードで起動できませんでした。"
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

/// Keep the API error identity so an existing permission grant can be
/// distinguished from a capture configuration or audio-buffer failure.
private func reportCaptureFailure(_ error: Error) -> Int32 {
    let nativeError = error as NSError
    let identity = "[domain=\(nativeError.domain), code=\(nativeError.code)]"
    if nativeError.domain.contains("ScreenCaptureKit") && nativeError.code == -3801 {
        diagnostic("ERROR: \(CaptureError.permissionRequired.localizedDescription) \(identity)")
        return 77
    }
    diagnostic("ERROR: \(error.localizedDescription) \(identity)")
    return 70
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

// The converter and its nine synthetic self-tests are shared with the dylib.
extension PCMTransport {
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
}

@available(macOS 13.0, *)
private final class AudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private let stopSignal: StopSignal
    private let queue = DispatchQueue(label: "org.atstti.DAMeter.audio", qos: .userInitiated)
    private var stream: SCStream?
    private let stateLock = NSLock()
    private var stopping = false

    init(stopSignal: StopSignal) { self.stopSignal = stopSignal }

    @MainActor
    func start() async throws {
        // Let ScreenCaptureKit perform its own authorization. A separate
        // CoreGraphics screen preflight can disagree with the permission used
        // by the actual capture API, especially after a helper bundle update.
        diagnostic("STATUS: macOS の音声キャプチャへのアクセスを確認しています。")
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { throw CaptureError.noDisplay }
        // Match Apple's display/audio sample: include all applications rather
        // than constructing a window-exclusion filter with an empty list.
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
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
        diagnostic("STATUS: 音声ストリームを開始しています。")
        try await session.startCapture()
        diagnostic("STATUS: 音声ストリームを開始しました。最初の音声データを待っています。")
    }

    private func markStopping() {
        stateLock.lock()
        stopping = true
        stateLock.unlock()
    }

    @MainActor
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
        // This helper uses Swift's async main loop instead of NSApplication.run.
        // Complete AppKit/LaunchServices startup before requesting capture access.
        // Accessory policy remains in force, so no regular Dock app is created.
        application.finishLaunching()
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
            do {
                try PCMTransport.selfTest()
                diagnostic("STATUS: PCM self-test passed (9 cases; no permission or capture).")
                Darwin.exit(0)
            }
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
            case .failed(let error): exitCode = reportCaptureFailure(error)
            }
        } catch {
            exitCode = reportCaptureFailure(error)
        }
        await capture.stop()
        sources.forEach { $0.cancel() }
        watchdog.cancel()
        Darwin.exit(exitCode)
    }
}
