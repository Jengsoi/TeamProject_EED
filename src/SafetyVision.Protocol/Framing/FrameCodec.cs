using System.Buffers.Binary;

namespace SafetyVision.Protocol.Framing;

public enum ProtocolMessageType : byte
{
    Json = 0x01,
    Image = 0x02
}

// 07_통신프로토콜.md 2절: [4바이트 길이(BE)][1바이트 타입][N바이트 페이로드].
public static class FrameCodec
{
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    private const int HeaderLength = 5;

    public static async Task WriteAsync(Stream stream, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), payload.Length);
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(ProtocolMessageType Type, byte[] Payload)> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderLength];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidDataException($"잘못된 페이로드 길이: {length}");

        var type = (ProtocolMessageType)header[4];
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("연결이 예기치 않게 종료되었습니다.");
            offset += read;
        }
    }
}
