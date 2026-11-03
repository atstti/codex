using System.Buffers.Binary;
using DAMeter.Core;
using Xunit;

namespace DAMeter.Core.Tests;

public sealed class PcmFrameProtocolTests
{
    [Fact]
    public async Task CleanEndOfStreamProducesNoFrame()
    {
        using var stream = new MemoryStream();

        Assert.Null(await PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task FragmentedReadsPreservePacketBoundariesAndExactPayloadBytes()
    {
        byte[] first = [0, 1, 2, 3, 4, 5, 6, 7];
        byte[] second = [255, 254, 253, 252, 251, 250, 249, 248, 80, 81, 82, 83, 84, 85, 86, 87];
        using var stream = new FragmentedStream([.. Packet(first), .. Packet(second)]);

        Assert.Equal(first, await PcmFrameProtocol.ReadFrameAsync(stream));
        Assert.Equal(second, await PcmFrameProtocol.ReadFrameAsync(stream));
        Assert.Null(await PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(9u)]
    [InlineData(4u * 1024 * 1024 + 8)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidLengthsAreRejectedWithoutWaitingForPayload(uint length)
    {
        using var stream = new MemoryStream(Header(length));

        await Assert.ThrowsAsync<InvalidDataException>(() => PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task LengthIsLittleEndianInsteadOfNativeOrNetworkByteOrder()
    {
        // Big-endian eight would be read as 134217728 by the documented protocol.
        using var stream = new MemoryStream([0, 0, 0, 8]);

        await Assert.ThrowsAsync<InvalidDataException>(() => PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PartialHeaderIsAnErrorRatherThanCleanEndOfStream(int bytes)
    {
        using var stream = new FragmentedStream(Header(8)[..bytes]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task TruncatedPayloadIsRejectedRatherThanAnalyzedAsCompleteAudio(int payloadBytes)
    {
        using var stream = new FragmentedStream([.. Header(8), .. new byte[payloadBytes]]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task MaximumSupportedPayloadIsAcceptedWithoutLosingLastStereoFrame()
    {
        byte[] payload = new byte[PcmFrameProtocol.MaxPayloadBytes];
        payload[0] = 21;
        payload[^1] = 42;
        using var stream = new MemoryStream(Packet(payload));

        byte[]? actual = await PcmFrameProtocol.ReadFrameAsync(stream);

        Assert.Equal(payload, actual);
        Assert.Null(await PcmFrameProtocol.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task CancellationInterruptsAnIncompletePendingFrame()
    {
        using var stream = new PendingStream();
        using var cancellation = new CancellationTokenSource();
        Task<byte[]?> reading = PcmFrameProtocol.ReadFrameAsync(stream, cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public async Task ReceivedFloat32StereoFramesReachSharedAnalyzerWithoutChannelOrPrecisionChanges()
    {
        float[] values = [0.125f, -0.25f, -0.5f, 0.75f];
        byte[] payload = new byte[values.Length * sizeof(float)];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(index * sizeof(float)), values[index]);
        }

        using var stream = new FragmentedStream(Packet(payload));
        byte[]? frame = await PcmFrameProtocol.ReadFrameAsync(stream);
        Assert.NotNull(frame);
        AudioLevels levels = AudioSampleAnalyzer.Analyze(frame, 32, 2, SampleEncoding.IeeeFloat);

        Assert.Equal(0.5, levels.LeftPeak, 10);
        Assert.Equal(0.75, levels.RightPeak, 10);
        Assert.Equal(Math.Sqrt((0.125 * 0.125 + 0.5 * 0.5) / 2), levels.LeftRms, 10);
        Assert.Equal(Math.Sqrt((0.25 * 0.25 + 0.75 * 0.75) / 2), levels.RightRms, 10);
    }

    private static byte[] Header(uint length)
    {
        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    private static byte[] Packet(byte[] payload) => [.. Header((uint)payload.Length), .. payload];

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
    }

    private sealed class PendingStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
