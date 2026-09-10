using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;
using SafetyVision.Protocol.Framing;
using Xunit;

namespace SafetyVision.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsJsonPayload()
    {
        using var stream = new MemoryStream();
        var payload = "{\"hello\":\"world\"}"u8.ToArray();

        await FrameCodec.WriteAsync(stream, ProtocolMessageType.Json, payload);
        stream.Position = 0;
        var (type, read) = await FrameCodec.ReadAsync(stream);

        Assert.Equal(ProtocolMessageType.Json, type);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsImagePayload_AcrossSlowStream()
    {
        using var stream = new MemoryStream();
        var payload = new byte[10_000];
        new Random(42).NextBytes(payload);

        await FrameCodec.WriteAsync(stream, ProtocolMessageType.Image, payload);
        stream.Position = 0;

        var (type, read) = await FrameCodec.ReadAsync(new SlowStream(stream, chunkSize: 7));

        Assert.Equal(ProtocolMessageType.Image, type);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadAsync_OversizedPayloadLength_ThrowsInvalidData()
    {
        using var stream = new MemoryStream();
        var header = new byte[5];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), FrameCodec.MaxPayloadBytes + 1);
        header[4] = (byte)ProtocolMessageType.Json;
        await stream.WriteAsync(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_NegativePayloadLength_ThrowsInvalidData()
    {
        using var stream = new MemoryStream();
        var header = new byte[5];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), -1);
        header[4] = (byte)ProtocolMessageType.Json;
        await stream.WriteAsync(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task WriteThenRead_ZeroLengthPayload_RoundTrips()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, ProtocolMessageType.Json, Array.Empty<byte>());
        stream.Position = 0;

        var (type, payload) = await FrameCodec.ReadAsync(stream);

        Assert.Equal(ProtocolMessageType.Json, type);
        Assert.Empty(payload);
    }

    [Fact]
    public async Task ReadAsync_TruncatedStream_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteAsync(stream, ProtocolMessageType.Json, new byte[100]);
        stream.SetLength(stream.Length - 50); // 페이로드 중간에서 잘림
        stream.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task ProtocolMessage_SendAndReceiveEnvelope_RoundTripsPayload()
    {
        using var stream = new MemoryStream();
        var correlationId = Guid.NewGuid();
        var request = new LoginRequestPayload("admin", "SafetyVision!2026");

        await ProtocolMessage.SendAsync(stream, MessageTypes.LoginRequest, correlationId, request);
        stream.Position = 0;

        var envelope = await ProtocolMessage.ReceiveEnvelopeAsync(stream);
        var decoded = envelope.DeserializePayload<LoginRequestPayload>();

        Assert.Equal(MessageTypes.LoginRequest, envelope.Type);
        Assert.Equal(correlationId, envelope.CorrelationId);
        Assert.Equal(request, decoded);
    }

    // 페이로드를 여러 조각으로 나눠 반환해 ReadAsync가 실제 소켓처럼 짧은 읽기를 재조립하는지 검증한다.
    private sealed class SlowStream(Stream inner, int chunkSize) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, Math.Min(count, chunkSize));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, Math.Min(count, chunkSize), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
