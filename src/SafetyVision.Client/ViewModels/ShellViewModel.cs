using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

// SCR-02 상단 셸: 좌측 메뉴 탐색 + 현재 화면 전환.
public sealed partial class ShellViewModel : ObservableObject
{
    public ServerConnection Connection { get; }

    [ObservableProperty] private string adminDisplayName;
    [ObservableProperty] private object? currentViewModel;
    [ObservableProperty] private string activeMenu = "dashboard";

    private readonly DashboardViewModel _dashboardVm;
    private readonly SiteInspectionViewModel _siteInspectionVm;
    private readonly HistoryViewModel _historyVm;
    private readonly StatisticsViewModel _statisticsVm;

    public event Action? LoggedOut;
    public event Action? ConnectionLost;

    private bool _intentionalDisconnect;

    public ShellViewModel(ServerConnection connection, string adminDisplayName)
    {
        Connection = connection;
        this.adminDisplayName = adminDisplayName;

        _dashboardVm = new DashboardViewModel(connection);
        _siteInspectionVm = new SiteInspectionViewModel(connection);
        _siteInspectionVm.ReturnToDashboardRequested += () => NavigateDashboardCommand.Execute(null);
        _siteInspectionVm.ReauthenticationRequired += OnReauthenticationRequired;
        _historyVm = new HistoryViewModel(connection);
        _statisticsVm = new StatisticsViewModel(connection);

        Connection.Disconnected += OnConnectionDisconnected;

        CurrentViewModel = _dashboardVm;
        _ = _dashboardVm.LoadAsync();
    }

    // 07_통신프로토콜.md §6: 연결 실패 시 안내 후 재연결(여기서는 재로그인)을 유도한다.
    // 로그아웃으로 인한 의도적 연결 종료와, 현장 검사 화면 자체의 재연결 흐름(SCR-04 안내문구·자체 재시도)은 제외한다.
    private void OnConnectionDisconnected()
    {
        if (_intentionalDisconnect) return;
        if (ReferenceEquals(CurrentViewModel, _siteInspectionVm)) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ConnectionLost?.Invoke());
    }

    private void OnReauthenticationRequired()
    {
        Connection.Dispose();
        ConnectionLost?.Invoke();
    }

    [RelayCommand]
    private async Task NavigateDashboardAsync()
    {
        await LeaveSiteInspectionIfNeededAsync();
        ActiveMenu = "dashboard";
        CurrentViewModel = _dashboardVm;
        await _dashboardVm.LoadAsync();
    }

    [RelayCommand]
    private async Task NavigateSiteInspectionAsync()
    {
        ActiveMenu = "site";
        CurrentViewModel = _siteInspectionVm;
        await _siteInspectionVm.EnterAsync();
    }

    [RelayCommand]
    private async Task NavigateHistoryAsync()
    {
        await LeaveSiteInspectionIfNeededAsync();
        ActiveMenu = "history";
        CurrentViewModel = _historyVm;
        await _historyVm.LoadAsync(1);
    }

    [RelayCommand]
    private async Task NavigateStatisticsAsync()
    {
        await LeaveSiteInspectionIfNeededAsync();
        ActiveMenu = "statistics";
        CurrentViewModel = _statisticsVm;
        await _statisticsVm.LoadAsync();
    }

    [RelayCommand]
    private async Task NavigatePlaceholderAsync(string title)
    {
        await LeaveSiteInspectionIfNeededAsync();
        ActiveMenu = title;
        CurrentViewModel = new PlaceholderViewModel(title);
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await LeaveSiteInspectionIfNeededAsync();
        _intentionalDisconnect = true;
        try { await Connection.RequestAsync(MessageTypes.LogoutRequest, new LogoutRequestPayload(), TimeSpan.FromSeconds(3)); }
        catch (Exception) { /* 연결이 이미 끊겼으면 그냥 로그아웃 처리한다 */ }
        Connection.Dispose();
        LoggedOut?.Invoke();
    }

    private async Task LeaveSiteInspectionIfNeededAsync()
    {
        if (ReferenceEquals(CurrentViewModel, _siteInspectionVm))
            await _siteInspectionVm.LeaveAsync();
    }
}
