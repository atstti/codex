import CoreMedia
import Dispatch
import Foundation
import ScreenCaptureKit

// This library is loaded by DAMeter's already-running application process.
// It must not create an NSApplication, change its activation policy, or run
// another application event loop. ScreenCaptureKit sees Bundle.main's identity.
public typealias DAMeterAudioPCMCallback = @convention(c) (
    UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32
) -> Void
public typealias DAMeterAudioStatusCallback = @convention(c) (
    UnsafeMutableRawPointer?, Int32, UnsafePointer<CChar>?
) -> Void

private enum BridgeStopReason {
    case requested
    case failed(Error)
}

/// A stop can arrive before the lifecycle task starts waiting for it.
private final class BridgeStopSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var result: BridgeStopReason?
    private var continuation: CheckedContinuation<BridgeStopReason, Never>?

    func finish(_ result: BridgeStopReason) {
        lock.lock()
        guard self.result == nil else { lock.unlock(); return }
        self.result = result
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume(returning: result)
    }

    func wait() async -> BridgeStopReason {
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

private enum BridgeError: LocalizedError {
    case noDisplay

    var errorDescription: String? {
        "音声キャプチャの基準となる画面が見つかりません。画面の接続後に再接続します。"
    }
}

@available(macOS 13.0, *)
private final class AudioBridgeSession: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let context: UnsafeMutableRawPointer?
    private let pcmCallback: DAMeterAudioPCMCallback
    private let statusCallback: DAMeterAudioStatusCallback
    private let callbackQueue = DispatchQueue(label: "org.atstti.DAMeter.audio.bridge", qos: .userInitiated)
    private let stopSignal = BridgeStopSignal()
    private let stateLock = NSLock()
    private var stopping = false
    private var failure: Error?
    private var callbacksClosed = false
    private var releaseAllowed = false
    @MainActor private var stream: SCStream?
    @MainActor private var outputAttached = false

    init(context: UnsafeMutableRawPointer?, pcm: @escaping DAMeterAudioPCMCallback,
         status: @escaping DAMeterAudioStatusCallback) {
        self.context = context
        pcmCallback = pcm
        statusCallback = status
    }

    func launch() {
        // This task owns the stream's complete lifetime, including an OS
        // authorization request that might not return after the user stops.
        Task { @MainActor [self] in
            await run()
        }
    }

    /// Safe from any thread and deliberately does not wait for an OS API.
    func requestStop() {
        stateLock.lock()
        stopping = true
        stateLock.unlock()
        stopSignal.finish(.requested)
    }

    private func requestFailure(_ error: Error) {
        stateLock.lock()
        guard !stopping, !callbacksClosed else { stateLock.unlock(); return }
        stopping = true
        failure = error
        stateLock.unlock()
        stopSignal.finish(.failed(error))
    }

    private var isStopping: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return stopping
    }

    private var acceptsPCM: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return !stopping && !callbacksClosed
    }

    private var acceptsStatus: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return !callbacksClosed
    }

    func canRelease() -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return releaseAllowed
    }

    private func closeCallbacks() -> Error? {
        stateLock.lock()
        defer { stateLock.unlock() }
        callbacksClosed = true
        return failure
    }

    private func allowRelease() {
        stateLock.lock()
        releaseAllowed = true
        stateLock.unlock()
    }

    private func status(_ code: Int32, _ message: String) {
        callbackQueue.async { [self] in
            guard acceptsStatus else { return }
            message.withCString { statusCallback(context, code, $0) }
        }
    }

    @MainActor
    private func run() async {
        do {
            try await startAndWaitForStop()
        } catch {
            requestFailure(error)
        }
        requestStop()
        await stopStream()
        await finishCallbacks()
    }

    @MainActor
    private func startAndWaitForStop() async throws {
        guard !isStopping else { return }
        // Ask the actual capture API for access in the main application's
        // process. A CoreGraphics permission preflight can disagree with it.
        status(1, "macOS の音声キャプチャへのアクセスを確認しています。")
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard !isStopping else { return }
        guard let display = content.displays.first else { throw BridgeError.noDisplay }
        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        // Only .audio is registered. No screen or microphone output is
        // registered, forwarded, stored in files, or sent over a network.
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.queueDepth = 3
        configuration.showsCursor = false
        guard !isStopping else { return }
        let session = SCStream(filter: filter, configuration: configuration, delegate: self)
        stream = session
        try session.addStreamOutput(self, type: .audio, sampleHandlerQueue: callbackQueue)
        outputAttached = true
        guard !isStopping else { return }
        status(1, "音声ストリームを開始しています。")
        try await session.startCapture()
        guard !isStopping else { return }
        status(2, "音声ストリームを開始しました。最初の音声データを待っています。")
        _ = await stopSignal.wait()
        guard !isStopping else { return }
    }

    @MainActor
    private func stopStream() async {
        guard let session = stream else { return }
        // Stopping remains set while an OS completion is pending. The managed
        // callback roots must remain alive until the final status is delivered.
        do { try await session.stopCapture() }
        catch { /* Shutdown errors do not replace the original stop reason. */ }
        // No stream can be started after this await, even when authorization
        // or startCapture completed after a concurrent stop request.
        if outputAttached {
            try? session.removeStreamOutput(self, type: .audio)
            outputAttached = false
        }
        stream = nil
    }

    @MainActor
    private func finishCallbacks() async {
        await withCheckedContinuation { (completion: CheckedContinuation<Void, Never>) in
            // This serial-queue marker drains audio/status callbacks already
            // running. Late SDK callbacks check the closed gate and do nothing.
            callbackQueue.async { [self] in
                let error = closeCallbacks()
                if let error = error {
                    let nativeError = error as NSError
                    let denied = nativeError.domain.contains("ScreenCaptureKit") && nativeError.code == -3801
                    let message = denied
                        ? "macOS が音声キャプチャへのアクセスを拒否しました。DAMeter の画面収録とシステムオーディオの許可を確認し、⌘Q で完全終了して再起動してください。 [domain=\(nativeError.domain), code=\(nativeError.code)]"
                        : "\(error.localizedDescription) [domain=\(nativeError.domain), code=\(nativeError.code)]"
                    message.withCString { statusCallback(context, denied ? 77 : 70, $0) }
                }
                // The caller may release the opaque handle inside status(0).
                // This queue closure retains self until that callback returns.
                allowRelease()
                "STOPPED".withCString { statusCallback(context, 0, $0) }
                completion.resume()
            }
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid, sampleBuffer.numSamples > 0, acceptsPCM else { return }
        do {
            let packet = try PCMTransport.packet(from: sampleBuffer)
            guard acceptsPCM else { return }
            // The shared converter's four-byte pipe header is omitted from
            // the C ABI. The raw stereo Float32LE pointer lives only in this call.
            packet.withUnsafeBytes { bytes in
                guard let base = bytes.baseAddress, bytes.count > 4 else { return }
                pcmCallback(context, base.advanced(by: 4).assumingMemoryBound(to: UInt8.self), Int32(bytes.count - 4))
            }
        } catch {
            requestFailure(error)
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        requestFailure(error)
    }
}

/// Returns one retained handle, or nil for an invalid callback/unsupported OS.
/// The caller must retain its callback context until status 0, even if an OS
/// authorization request does not return after dameter_audio_stop is called.
@_cdecl("dameter_audio_start")
public func dameterAudioStart(_ context: UnsafeMutableRawPointer?, _ pcm: DAMeterAudioPCMCallback?,
                             _ status: DAMeterAudioStatusCallback?) -> UnsafeMutableRawPointer? {
    guard #available(macOS 13.0, *), let pcm = pcm, let status = status else { return nil }
    let session = AudioBridgeSession(context: context, pcm: pcm, status: status)
    let handle = Unmanaged.passRetained(session).toOpaque()
    session.launch()
    return handle
}

/// Idempotent and nonblocking; completion is communicated only by status 0.
@_cdecl("dameter_audio_stop")
public func dameterAudioStop(_ handle: UnsafeMutableRawPointer?) {
    guard #available(macOS 13.0, *), let handle = handle else { return }
    Unmanaged<AudioBridgeSession>.fromOpaque(handle).takeUnretainedValue().requestStop()
}

/// Call exactly once after status 0. An early release is ignored so a pending
/// OS authorization request keeps its session alive. The caller owns context.
@_cdecl("dameter_audio_release")
public func dameterAudioRelease(_ handle: UnsafeMutableRawPointer?) {
    guard #available(macOS 13.0, *), let handle = handle else { return }
    let retained = Unmanaged<AudioBridgeSession>.fromOpaque(handle)
    guard retained.takeUnretainedValue().canRelease() else { return }
    retained.release()
}

/// Writes the main application's bundle identity, never the dylib's identity.
/// Return value is the required UTF-8 byte count INCLUDING its NUL terminator.
/// A short buffer receives a NUL-terminated prefix; nil/zero capacity queries size.
@_cdecl("dameter_audio_identity")
public func dameterAudioIdentity(_ buffer: UnsafeMutablePointer<UInt8>?, _ capacity: Int32) -> Int32 {
    let identity = Array((Bundle.main.bundleIdentifier ?? "").utf8)
    let required = identity.count + 1
    guard let buffer = buffer, capacity > 0 else { return Int32(required) }
    let count = min(identity.count, Int(capacity) - 1)
    for index in 0..<count { buffer[index] = identity[index] }
    buffer[count] = 0
    return Int32(required)
}

/// Nine deterministic converter cases; no permission request or capture.
@_cdecl("dameter_audio_self_test")
public func dameterAudioSelfTest() -> Int32 {
    do { try PCMTransport.selfTest(); return 0 }
    catch { return 70 }
}
