namespace SafetyVision.Core.StateMachine;

// 03_화면설계.md SCR-04 안내 문구. 서버가 상태 전이 시점에 결정해 클라이언트로 보낸다.
public static class InspectionMessages
{
    public const string Waiting = "검사 영역 안에 서 주세요.";
    public const string TooSmall = "카메라에 조금 더 가까이 서 주세요.";
    public const string TooClose = "상체가 화면 안에 들어오도록 카메라에서 조금 뒤로 서 주세요.";
    public const string StableDetected = "작업자를 감지했습니다. 잠시 서 있어 주세요.";
    public const string Inspecting = "AI 분석 중... 잠시 서 있어 주세요.";
    public const string MultiplePersons = "한 명씩 검사해 주세요.";
}
