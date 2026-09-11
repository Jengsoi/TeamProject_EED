namespace SafetyVision.Protocol;

// 07_통신프로토콜.md 5절 메시지 카탈로그.
public static class MessageTypes
{
    public const string LoginRequest = nameof(LoginRequest);
    public const string LoginResponse = nameof(LoginResponse);
    public const string LogoutRequest = nameof(LogoutRequest);
    public const string LogoutResponse = nameof(LogoutResponse);

    public const string DashboardStatsRequest = nameof(DashboardStatsRequest);
    public const string DashboardStatsResponse = nameof(DashboardStatsResponse);

    // 임시 조치(팀 결정): 원래 문서 스펙은 통계 분석을 "버튼만 구현"으로 두었으나, 실제 화면을 만들기로 함.
    public const string StatisticsRequest = nameof(StatisticsRequest);
    public const string StatisticsResponse = nameof(StatisticsResponse);

    public const string InspectionSessionStart = nameof(InspectionSessionStart);
    public const string InspectionSessionStarted = nameof(InspectionSessionStarted);
    public const string FrameMeta = nameof(FrameMeta);
    public const string InspectionStateChanged = nameof(InspectionStateChanged);
    public const string InspectionResult = nameof(InspectionResult);
    public const string RetryInspectionRequest = nameof(RetryInspectionRequest);
    public const string SaveResultAck = nameof(SaveResultAck);
    public const string RetrySaveRequest = nameof(RetrySaveRequest);
    public const string InspectionSessionEnd = nameof(InspectionSessionEnd);

    public const string HistoryPageRequest = nameof(HistoryPageRequest);
    public const string HistoryPageResponse = nameof(HistoryPageResponse);
    public const string InspectionDetailRequest = nameof(InspectionDetailRequest);
    public const string InspectionDetailResponse = nameof(InspectionDetailResponse);

    public const string ErrorNotification = nameof(ErrorNotification);
}
