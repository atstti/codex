using System.Buffers.Binary;

namespace DAMeter.Core;

/// <summary>
/// Reads bounded, length-prefixed stereo Float32 PCM packets from the macOS
/// capture helper. Audio stays in memory and is not written to a file.
/// </summary>
public static class PcmFrameProtocol
{
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Returns null only for clean EOF between packets. A partial header or body
    /// throws EndOfStreamException; invalid lengths are rejected before allocation.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(uint)];
        var firstRead = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (firstRead == 0) return null;

        await stream.ReadExactlyAsync(header.AsMemory(firstRead), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxPayloadBytes || length % (2 * sizeof(float)) != 0)
            throw new InvalidDataException("Invalid stereo Float32 audio packet length.");

        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
