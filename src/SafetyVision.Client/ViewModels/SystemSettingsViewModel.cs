using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Config;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

public sealed record SystemSettingsItemViewModel(string Key, string Value, string Description);

public sealed class SystemSettingsGroupViewModel
{
    public SystemSettingsGroupViewModel(string groupName, IEnumerable<SystemSettingsItemViewModel> items)
    {
        GroupName = groupName;
        Items = new ObservableCollection<SystemSettingsItemViewModel>(items);
    }

    public string GroupName { get; }
    public ObservableCollection<SystemSettingsItemViewModel> Items { get; }
}

// SCR-09 시스템 설정: 서버에서 읽어 온 상태와 설정값을 조회 전용으로 표시한다.
public sealed partial class SystemSettingsViewModel(ServerConnection connection) : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private bool isServerHealthy;
    [ObservableProperty] private string serverStatusText = "오류";
    [ObservableProperty] private string serverVersion = "-";
    [ObservableProperty] private string serverStartedAtText = "-";
    [ObservableProperty] private string serverUptimeText = "-";
    [ObservableProperty] private string listenPortText = "-";

    [ObservableProperty] private bool isDatabaseReady;
    [ObservableProperty] private string databaseStatusText = "오류";
    [ObservableProperty] private string databaseHost = "-";
    [ObservableProperty] private string databaseName = "-";

    [ObservableProperty] private bool isDetectorAvailable;
    [ObservableProperty] private bool isDetectorUnavailable;
    [ObservableProperty] private string aiModelStatusText = "오류";
    [ObservableProperty] private string detectorStatusText = "사용 불가";
    [ObservableProperty] private string modelName = "-";
    [ObservableProperty] private string modelVersion = "-";
    [ObservableProperty] private string modelPath = "-";
    [ObservableProperty] private string? detectorUnavailableReason;
    [ObservableProperty] private bool useFakeDetection;
    [ObservableProperty] private string usePpeAsPersonProxyText = "사용 안 함";

    [ObservableProperty] private string clientVersion = GetClientVersion();
    [ObservableProperty] private string serverAddress = $"{ClientSettings.ServerHost}:{ClientSettings.ServerPort}";
    [ObservableProperty] private bool isClientConnected;
    [ObservableProperty] private string clientConnectionStatusText = "확인 대기";

    public ObservableCollection<SystemSettingsGroupViewModel> Groups { get; } = [];

    public async Task LoadAsync()
    {
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
                UpdateClientConnectionStatus();
            });

            var envelope = await connection.RequestAsync(
                MessageTypes.SystemSettingsRequest,
                new SystemSettingsRequestPayload(),
                TimeSpan.FromSeconds(10));
            var response = envelope.DeserializePayload<SystemSettingsResponsePayload>();

            await RunOnUiThreadAsync(() => Apply(response));
        }
        catch (Exception)
        {
            try
            {
                await RunOnUiThreadAsync(() =>
                {
                    IsServerHealthy = false;
                    ServerStatusText = "오류";
                    UpdateClientConnectionStatus();
                    ErrorMessage = "시스템 설정 정보를 불러오지 못했습니다. 잠시 후 다시 시도해 주세요.";
                });
            }
            catch (Exception)
            {
                // 애플리케이션 종료 중에는 UI 오류 상태를 갱신하지 못할 수 있다.
            }
        }
        finally
        {
            try
            {
                await RunOnUiThreadAsync(() => IsBusy = false);
            }
            catch (Exception)
            {
                // 애플리케이션 종료 중 Dispatcher 예외가 호출자에게 전파되지 않도록 한다.
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private void Apply(SystemSettingsResponsePayload response)
    {
        if (response is null)
        {
            IsServerHealthy = false;
            ServerStatusText = "오류";
            ErrorMessage = "시스템 설정 응답이 비어 있습니다.";
            UpdateClientConnectionStatus();
            return;
        }

        IsServerHealthy = true;
        ServerStatusText = "정상";
        ServerVersion = response.ServerVersion ?? "-";
        ServerStartedAtText = response.ServerStartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        ServerUptimeText = ToUptimeText(response.ServerStartedAtUtc);
        ListenPortText = response.ListenPort > 0 ? response.ListenPort.ToString() : "-";

        IsDatabaseReady = response.DatabaseReady;
        DatabaseStatusText = response.DatabaseReady ? "정상" : "오류";
        DatabaseHost = string.IsNullOrWhiteSpace(response.DatabaseHost) ? "-" : response.DatabaseHost;
        DatabaseName = string.IsNullOrWhiteSpace(response.DatabaseName) ? "-" : response.DatabaseName;

        IsDetectorAvailable = response.DetectorAvailable;
        IsDetectorUnavailable = !response.DetectorAvailable;
        DetectorStatusText = response.DetectorAvailable ? "사용 가능" : "사용 불가";
        UseFakeDetection = response.UseFakeDetection;
        AiModelStatusText = !response.DetectorAvailable
            ? "오류"
            : response.UseFakeDetection ? "개발 Fake 모드" : "정상";
        ModelName = response.ModelName ?? "-";
        ModelVersion = response.ModelVersion ?? "-";
        ModelPath = response.ModelPath ?? "-";
        DetectorUnavailableReason = response.DetectorAvailable
            ? null
            : string.IsNullOrWhiteSpace(response.DetectorUnavailableReason)
                ? "검출기를 사용할 수 없습니다."
                : response.DetectorUnavailableReason;
        UsePpeAsPersonProxyText = response.UsePpeAsPersonProxy ? "사용" : "사용 안 함";

        Groups.Clear();
        foreach (var group in response.Groups ?? [])
        {
            if (group is null) continue;

            var items = new List<SystemSettingsItemViewModel>();
            foreach (var item in group.Items ?? [])
            {
                if (item is null) continue;
                items.Add(new SystemSettingsItemViewModel(
                    item.Key ?? "-",
                    item.Value ?? "-",
                    item.Description ?? string.Empty));
            }

            Groups.Add(new SystemSettingsGroupViewModel(group.GroupName ?? "설정", items));
        }

        UpdateClientConnectionStatus();
    }

    private void UpdateClientConnectionStatus()
    {
        IsClientConnected = connection.IsConnected;
        ClientConnectionStatusText = IsClientConnected ? "연결됨" : "연결 끊김";
    }

    private static string GetClientVersion()
    {
        try
        {
            return typeof(SystemSettingsViewModel).Assembly.GetName().Version?.ToString() ?? "알 수 없음";
        }
        catch (Exception)
        {
            return "알 수 없음";
        }
    }

    private static string ToUptimeText(DateTimeOffset startedAtUtc)
    {
        try
        {
            var uptime = DateTimeOffset.UtcNow - startedAtUtc;
            if (uptime < TimeSpan.Zero) return "계산 불가";

            return uptime.Days > 0
                ? $"{uptime.Days}일 {uptime.Hours}시간 {uptime.Minutes}분"
                : uptime.Hours > 0
                    ? $"{uptime.Hours}시간 {uptime.Minutes}분"
                    : $"{uptime.Minutes}분";
        }
        catch (Exception)
        {
            return "계산 불가";
        }
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }
}
