using System.Text.Json;
using System.Text.Json.Serialization;
using SafetyVision.Protocol.Framing;

namespace SafetyVision.Protocol;

// 07_통신프로토콜.md 3절 공통 필드: type / correlationId / payload.
public sealed record Envelope(string Type, Guid CorrelationId, JsonElement Payload);

public static class ProtocolMessage
{
    public static Task SendAsync<T>(Stream stream, string type, Guid correlationId, T payload, CancellationToken ct = default)
    {
        var wrapper = new EnvelopeDto<T>(type, correlationId, payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wrapper, ProtocolJson.Options);
        return FrameCodec.WriteAsync(stream, ProtocolMessageType.Json, bytes, ct);
    }

    public static Task SendImageAsync(Stream stream, ReadOnlyMemory<byte> jpegBytes, CancellationToken ct = default)
        => FrameCodec.WriteAsync(stream, ProtocolMessageType.Image, jpegBytes, ct);

    public static async Task<Envelope> ReceiveEnvelopeAsync(Stream stream, CancellationToken ct = default)
    {
        var (type, payload) = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);
        if (type != ProtocolMessageType.Json)
            throw new InvalidDataException("예상한 JSON 메시지가 아니라 이미지 프레임이 도착했습니다.");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        string msgType = root.GetProperty("type").GetString()
            ?? throw new InvalidDataException("메시지에 type 필드가 없습니다.");
        Guid correlationId = root.GetProperty("correlationId").GetGuid();
        JsonElement payloadElement = root.GetProperty("payload").Clone();
        return new Envelope(msgType, correlationId, payloadElement);
    }

    public static async Task<byte[]> ReceiveImageAsync(Stream stream, CancellationToken ct = default)
    {
        var (type, payload) = await FrameCodec.ReadAsync(stream, ct).ConfigureAwait(false);
        if (type != ProtocolMessageType.Image)
            throw new InvalidDataException("예상한 이미지 프레임이 아니라 JSON 메시지가 도착했습니다.");
        return payload;
    }

    public static T DeserializePayload<T>(this Envelope envelope) =>
        envelope.Payload.Deserialize<T>(ProtocolJson.Options)!;

    private sealed record EnvelopeDto<T>(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("correlationId")] Guid CorrelationId,
        [property: JsonPropertyName("payload")] T Payload);
}
