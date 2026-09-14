using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Config;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

// SCR-01 로그인.
public sealed partial class LoginViewModel : ObservableObject
{
    [ObservableProperty]
    private string loginId = "";

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool isBusy;

    public event Action<ServerConnection, string>? LoginSucceeded;

    [RelayCommand]
    private async Task LoginAsync(string? password)
    {
        if (IsBusy) return;
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(LoginId) || string.IsNullOrEmpty(password))
        {
            ErrorMessage = "아이디와 비밀번호를 입력해 주세요.";
            return;
        }

        IsBusy = true;
        var connection = new ServerConnection(ClientSettings.ServerHost, ClientSettings.ServerPort);
        try
        {
            bool connected = await connection.ConnectWithRetryAsync(10, TimeSpan.FromSeconds(1.5));
            if (!connected)
            {
                ErrorMessage = "서버에 연결할 수 없습니다. 연결을 확인해 주세요.";
                connection.Dispose();
                return;
            }

            var envelope = await connection.RequestAsync(MessageTypes.LoginRequest, new LoginRequestPayload(LoginId, password));
            var response = envelope.DeserializePayload<LoginResponsePayload>();

            if (response.Success)
            {
                LoginSucceeded?.Invoke(connection, response.DisplayName ?? LoginId);
            }
            else
            {
                ErrorMessage = response.FailureMessage ?? "아이디 또는 비밀번호를 확인해 주세요.";
                connection.Dispose();
            }
        }
        catch (Exception)
        {
            ErrorMessage = "서버에 연결할 수 없습니다. 연결을 확인해 주세요.";
            connection.Dispose();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
