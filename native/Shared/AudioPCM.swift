import AudioToolbox
import CoreMedia
import Foundation

/// Errors shared by the in-process bridge and the legacy pipe helper.
enum PCMConversionError: LocalizedError {
    case unsupportedFormat(String)
    case malformedBuffer
    case selfTestFailed(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedFormat(let details):
            return "未対応のシステム音声形式です: \(details)"
        case .malformedBuffer:
            return "システム音声バッファの形式またはサイズが不正です。"
        case .selfTestFailed(let details):
            return "PCM self-test failed: \(details)"
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
enum PCMTransport {
    static let maximumPayloadBytes = 4 * 1024 * 1024

    static func packet(from buffer: CMSampleBuffer) throws -> Data {
        guard buffer.isValid,
              let description = buffer.formatDescription?.audioStreamBasicDescription else {
            throw PCMConversionError.malformedBuffer
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
            throw PCMConversionError.unsupportedFormat("Linear PCM が必要です")
        }
        guard frames > 0, frames <= maximumPayloadBytes / 8,
              description.mChannelsPerFrame > 0,
              description.mChannelsPerFrame <= 64,
              description.mBytesPerFrame > 0 else {
            throw PCMConversionError.malformedBuffer
        }
        let isFloat = description.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let bits = Int(description.mBitsPerChannel)
        if (isFloat && bits != 32 && bits != 64) || (!isFloat && (bits < 1 || bits > 32)) {
            throw PCMConversionError.unsupportedFormat("\(bits) bit")
        }
        let bigEndian = description.mFormatFlags & kAudioFormatFlagIsBigEndian != 0
        let signed = description.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0
        let alignedHigh = description.mFormatFlags & kAudioFormatFlagIsAlignedHigh != 0
        var sources: [ChannelSource] = []
        for item in buffers {
            let channels = Int(item.mNumberChannels)
            guard channels > 0 else { continue }
            guard channels <= 64 else { throw PCMConversionError.malformedBuffer }
            let stride = Int(description.mBytesPerFrame)
            guard stride % channels == 0, let pointer = item.mData else {
                throw PCMConversionError.malformedBuffer
            }
            let sampleBytes = stride / channels
            guard sampleBytes >= (bits + 7) / 8,
                  sampleBytes <= 8,
                  (!isFloat || sampleBytes == bits / 8) else {
                throw PCMConversionError.malformedBuffer
            }
            for channel in 0..<channels where sources.count < 2 {
                let offset = channel * sampleBytes
                let requiredBytes = (frames - 1) * stride + offset + sampleBytes
                guard requiredBytes <= Int(item.mDataByteSize) else {
                    throw PCMConversionError.malformedBuffer
                }
                sources.append(ChannelSource(data: UnsafeRawPointer(pointer), stride: stride,
                                             offset: offset, bytesPerSample: sampleBytes))
            }
            if sources.count == 2 { break }
        }
        guard let left = sources.first else { throw PCMConversionError.malformedBuffer }
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
            throw PCMConversionError.selfTestFailed("短いバッファを拒否しませんでした")
        } catch PCMConversionError.malformedBuffer { }
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
        guard bytes.count == 4 + samples.count * 4 else { throw PCMConversionError.selfTestFailed("パケット長") }
        func word(at offset: Int) -> UInt32 {
            UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8 |
                UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
        }
        guard Int(word(at: 0)) == samples.count * 4 else { throw PCMConversionError.selfTestFailed("ヘッダー長") }
        for index in samples.indices {
            let actual = Float(bitPattern: word(at: 4 + index * 4))
            guard actual.isFinite, abs(actual - samples[index]) < 0.000001 else {
                throw PCMConversionError.selfTestFailed("PCM デコードまたはチャンネル配置")
            }
        }
    }
}
