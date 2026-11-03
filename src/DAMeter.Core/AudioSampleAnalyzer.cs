using System.Buffers.Binary;

namespace DAMeter.Core;

/// <summary>
/// Decodes little-endian, interleaved WASAPI samples without retaining audio data.
/// Mono is duplicated to both meters. For multichannel audio, only channels 1 and 2
/// are metered; other channels are deliberately not mixed into the stereo meters.
/// </summary>
public static class AudioSampleAnalyzer
{
    public static AudioLevels Analyze(
        ReadOnlySpan<byte> data,
        int bitsPerSample,
        int channels,
        SampleEncoding encoding)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        var supported = encoding switch
        {
            SampleEncoding.Pcm => bitsPerSample is 8 or 16 or 24 or 32,
            SampleEncoding.IeeeFloat => bitsPerSample is 32 or 64,
            _ => false
        };
        if (!supported)
            throw new NotSupportedException($"Unsupported audio format: {encoding}, {bitsPerSample} bits.");

        var sampleBytes = bitsPerSample / 8;
        if (channels > int.MaxValue / sampleBytes)
            throw new ArgumentOutOfRangeException(nameof(channels));
        var frameBytes = channels * sampleBytes;
        var frames = data.Length / frameBytes;
        if (frames == 0)
            return AudioLevels.Silence;

        var left = new SampleStatistics();
        var right = new SampleStatistics();
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameBytes;
            var l = ReadSample(data.Slice(offset, sampleBytes), bitsPerSample, encoding);
            var r = channels == 1
                ? l
                : ReadSample(data.Slice(offset + sampleBytes, sampleBytes), bitsPerSample, encoding);
            left.Add(l);
            right.Add(r);
        }

        return new AudioLevels(left.Peak, right.Peak, left.Rms(frames), right.Rms(frames));
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, int bits, SampleEncoding encoding)
    {
        double sample;
        if (encoding == SampleEncoding.IeeeFloat)
        {
            sample = bits == 32
                ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                : BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        }
        else
        {
            sample = bits switch
            {
                8 => (bytes[0] - 128) / 128.0,
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
                24 => ReadInt24(bytes) / 8388608.0,
                32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
                _ => 0 // The public entry point rejects unsupported widths.
            };
        }

        return double.IsFinite(sample) ? sample : 0;
    }

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value << 8) >> 8;
    }

    private struct SampleStatistics
    {
        public double Peak;
        private double _scale;
        private double _scaledSquares;

        public void Add(double sample)
        {
            var absolute = Math.Abs(sample);
            Peak = Math.Max(Peak, absolute);
            if (absolute == 0)
                return;

            // Scaled sum of squares also handles malformed but finite Float64
            // over-range samples without overflowing and producing NaN RMS.
            if (absolute > _scale)
            {
                var ratio = _scale / absolute;
                _scaledSquares = 1 + _scaledSquares * ratio * ratio;
                _scale = absolute;
            }
            else
            {
                var ratio = absolute / _scale;
                _scaledSquares += ratio * ratio;
            }
        }

        public readonly double Rms(int count) => _scale == 0
            ? 0
            : _scale * Math.Sqrt(_scaledSquares / count);
    }
}
