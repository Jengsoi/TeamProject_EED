using System.Text.Json;

namespace SafetyVision.Protocol;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
