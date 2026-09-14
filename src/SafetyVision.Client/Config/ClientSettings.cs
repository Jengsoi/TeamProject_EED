namespace SafetyVision.Client.Config;

// 설정 UI는 범위 밖이다. 서버 주소는 고정값을 사용하고 필요 시 이 파일을 수정한다.
public static class ClientSettings
{
    public const string ServerHost = "127.0.0.1";
    public const int ServerPort = 8910;

    public const int CameraIndex = 0;
    public const int CameraWidth = 1280;
    public const int CameraHeight = 720;

    // 서버가 프레임당 2개 모델(PPE 896 + Person 640)을 돌리면서 실측 처리 시간이 약 210ms/프레임
    // (~4.7fps)로 늘어났다. 6fps로 보내면 서버가 못 따라가 지연이 계속 누적되므로 실측치에 맞춰 낮춘다.
    // (서버도 밀린 프레임은 버리고 최신 프레임만 처리하도록 방어되어 있지만, 애초에 덜 버려지게 보낸다.)
    public const int MaxFrameSendFps = 4;
    public const int FrameJpegQuality = 80;
}
