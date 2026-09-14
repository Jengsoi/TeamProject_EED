using System.IO;
using System.Text.Json;

namespace SafetyVision.Client.Config;

/// <summary>
/// 사용자가 선택한 카메라와 해상도를 보관하는 런타임 설정입니다.
/// </summary>
public sealed class CameraOptions
{
    public int CameraIndex { get; set; }
    public int CameraWidth { get; set; }
    public int CameraHeight { get; set; }
}

/// <summary>
/// 카메라 설정을 로컬 앱 데이터 폴더에 안전하게 저장하고 불러옵니다.
/// </summary>
public static class CameraOptionsStore
{
    private const int DefaultCameraIndex = 0;
    private const int DefaultCameraWidth = 1280;
    private const int DefaultCameraHeight = 720;

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CameraOptions Current { get; } = CreateDefaults();

    /// <summary>
    /// 저장된 설정을 읽습니다. 어떤 파일 시스템 오류도 기본값으로 처리합니다.
    /// </summary>
    public static void Load()
    {
        var loadedOptions = CreateDefaults();

        try
        {
            var filePath = GetSettingsFilePath();
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var deserialized = JsonSerializer.Deserialize<CameraOptions>(json, JsonOptions);

                if (IsValid(deserialized))
                    loadedOptions = deserialized!;
            }
        }
        catch (Exception)
        {
            // 파일이 없거나, 읽을 수 없거나, JSON이 손상된 경우 기본값을 사용한다.
        }

        lock (SyncRoot)
        {
            Current.CameraIndex = loadedOptions.CameraIndex;
            Current.CameraWidth = loadedOptions.CameraWidth;
            Current.CameraHeight = loadedOptions.CameraHeight;
        }
    }

    /// <summary>
    /// 현재 설정을 저장합니다. 저장 실패는 앱 동작을 방해하지 않습니다.
    /// </summary>
    public static void Save()
    {
        try
        {
            CameraOptions snapshot;
            lock (SyncRoot)
            {
                snapshot = new CameraOptions
                {
                    CameraIndex = Current.CameraIndex,
                    CameraWidth = Current.CameraWidth,
                    CameraHeight = Current.CameraHeight
                };
            }

            var filePath = GetSettingsFilePath();
            var directoryPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            Directory.CreateDirectory(directoryPath);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception)
        {
            // 권한, 디스크, 경로 오류가 있어도 카메라 화면은 계속 사용할 수 있어야 한다.
        }
    }

    private static CameraOptions CreateDefaults() => new()
    {
        CameraIndex = DefaultCameraIndex,
        CameraWidth = DefaultCameraWidth,
        CameraHeight = DefaultCameraHeight
    };

    private static string GetSettingsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafetyVision",
        "client-settings.json");

    private static bool IsValid(CameraOptions? options) => options is not null
        && options.CameraIndex >= 0
        && options.CameraWidth > 0
        && options.CameraHeight > 0;
}
