namespace SafetyVision.Client.Config;

// 설정 UI는 범위 밖이다. 서버 주소는 고정값을 사용하고 필요 시 이 파일을 수정한다.
public static class ClientSettings
{
    public const string ServerHost = "127.0.0.1";
    public const int ServerPort = 8910;

    public const int CameraIndex = 0;
    public const int CameraWidth = 1280;
    public const int CameraHeight = 720;

    // 카메라를 여러 대로 늘릴 때, 클라이언트 인스턴스마다 이 값을 다르게 설정한다.
    public const string CameraName = "CAM 01";

    public const int MaxFrameSendFps = 6;
    public const int FrameJpegQuality = 80;
}
