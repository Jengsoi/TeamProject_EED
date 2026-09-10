using CommunityToolkit.Mvvm.ComponentModel;

namespace SafetyVision.Client.ViewModels;

// 통계 분석·카메라 관리·장비 관리·사용자 관리·시스템 설정: 버튼만 구현, 서버 요청 없음.
public sealed partial class PlaceholderViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
    public string Message => "준비 중인 기능입니다.";
}
