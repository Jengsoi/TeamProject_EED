namespace SafetyVision.Protocol.Dto;

public sealed record SystemSettingsRequestPayload;

public sealed record SettingItemPayload(string Key, string Value, string Description);

public sealed record SettingGroupPayload(string GroupName, IReadOnlyList<SettingItemPayload> Items);

public sealed record SystemSettingsResponsePayload(
    string ServerVersion,
    DateTimeOffset ServerStartedAtUtc,
    int ListenPort,
    bool DatabaseReady,
    string DatabaseHost,
    string DatabaseName,
    string ModelName,
    string ModelVersion,
    string ModelPath,
    bool DetectorAvailable,
    string? DetectorUnavailableReason,
    bool UseFakeDetection,
    bool UsePpeAsPersonProxy,
    IReadOnlyList<SettingGroupPayload> Groups);
