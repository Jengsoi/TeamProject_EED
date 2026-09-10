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

    public event Action? LoggedOut;

    public ShellViewModel(ServerConnection connection, string adminDisplayName)
    {
        Connection = connection;
        this.adminDisplayName = adminDisplayName;

        _dashboardVm = new DashboardViewModel(connection);
        _siteInspectionVm = new SiteInspectionViewModel(connection);
        _siteInspectionVm.ReturnToDashboardRequested += () => NavigateDashboardCommand.Execute(null);
        _historyVm = new HistoryViewModel(connection);

        CurrentViewModel = _dashboardVm;
        _ = _dashboardVm.LoadAsync();
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
