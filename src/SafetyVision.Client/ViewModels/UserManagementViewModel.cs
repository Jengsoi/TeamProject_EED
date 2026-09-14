using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

// SCR-08 사용자 관리. 비밀번호는 PasswordBox에서 명령 매개 변수로만 받고 바인딩하지 않는다.
public sealed partial class UserManagementViewModel(ServerConnection connection) : ObservableObject
{
    private const string TimeoutOrDisconnectedMessage = "서버 응답 시간이 초과되었거나 연결이 끊어졌습니다.";

    [ObservableProperty] private UserSummaryPayload? selectedUser;
    [ObservableProperty] private string newLoginId = string.Empty;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string? errorMessage;

    public ObservableCollection<UserSummaryPayload> Users { get; } = [];

    public event Func<bool>? ConfirmDeleteRequested;

    public async Task LoadAsync()
    {
        await RunBusyAsync(
            () => LoadUsersCoreAsync(showStatus: true),
            "사용자 목록을 불러오지 못했습니다. 서버 연결을 확인해 주세요.");
    }

    [RelayCommand]
    private Task RefreshAsync() => RunSafeCommandAsync(
        LoadAsync,
        "사용자 목록을 불러오지 못했습니다. 서버 연결을 확인해 주세요.");

    [RelayCommand]
    private Task CreateAsync(string? password) => RunSafeCommandAsync(
        () => CreateCoreAsync(password),
        "사용자를 추가하지 못했습니다. 서버 연결을 확인해 주세요.");

    [RelayCommand]
    private Task ChangePasswordAsync(string? password) => RunSafeCommandAsync(
        () => ChangePasswordCoreAsync(password),
        "비밀번호를 변경하지 못했습니다. 서버 연결을 확인해 주세요.");

    [RelayCommand]
    private Task DeleteAsync() => RunSafeCommandAsync(
        DeleteCoreAsync,
        "사용자를 삭제하지 못했습니다. 서버 연결을 확인해 주세요.");

    private async Task CreateCoreAsync(string? password)
    {
        try
        {
            string loginId = NewLoginId?.Trim() ?? string.Empty;
            string name = NewName?.Trim() ?? string.Empty;
            string inputPassword = password ?? string.Empty;

            var validationFailure = ValidateNewUser(loginId, name, inputPassword);
            if (validationFailure is not null)
            {
                await SetErrorAsync(validationFailure);
                return;
            }

            await RunBusyAsync(async () =>
            {
                var envelope = await connection.RequestAsync(
                    MessageTypes.UserCreateRequest,
                    new UserCreateRequestPayload(loginId, name, inputPassword),
                    TimeSpan.FromSeconds(10));
                var response = envelope.DeserializePayload<UserMutationResponsePayload>();

                if (!response.Success)
                {
                    await SetErrorAsync(response.Message ?? "사용자를 추가하지 못했습니다.");
                    return;
                }

                await LoadUsersCoreAsync(showStatus: false);
                await RunOnUiThreadAsync(() =>
                {
                    NewLoginId = string.Empty;
                    NewName = string.Empty;
                    StatusMessage = response.Message ?? "사용자를 추가했습니다.";
                    ErrorMessage = null;
                });
            }, "사용자를 추가하지 못했습니다. 서버 연결을 확인해 주세요.");
        }
        finally
        {
            // 문자열은 변경할 수 없지만 ViewModel의 속성에는 보관하지 않으며, 참조도 즉시 놓는다.
            password = null;
        }
    }

    private async Task ChangePasswordCoreAsync(string? password)
    {
        try
        {
            var user = SelectedUser;
            if (user is null)
            {
                await SetErrorAsync("비밀번호를 변경할 사용자를 선택해 주세요.");
                return;
            }

            string inputPassword = password ?? string.Empty;
            if (inputPassword.Length < 8)
            {
                await SetErrorAsync("비밀번호는 8자 이상이어야 합니다.");
                return;
            }

            await RunBusyAsync(async () =>
            {
                var envelope = await connection.RequestAsync(
                    MessageTypes.UserPasswordChangeRequest,
                    new UserPasswordChangeRequestPayload(user.Id, inputPassword),
                    TimeSpan.FromSeconds(10));
                var response = envelope.DeserializePayload<UserMutationResponsePayload>();

                if (!response.Success)
                {
                    await SetErrorAsync(response.Message ?? "비밀번호를 변경하지 못했습니다.");
                    return;
                }

                await RunOnUiThreadAsync(() =>
                {
                    StatusMessage = response.Message ?? "비밀번호를 변경했습니다.";
                    ErrorMessage = null;
                });
            }, "비밀번호를 변경하지 못했습니다. 서버 연결을 확인해 주세요.");
        }
        finally
        {
            password = null;
        }
    }

    private async Task DeleteCoreAsync()
    {
        var user = SelectedUser;
        if (user is null)
        {
            await SetErrorAsync("삭제할 사용자를 선택해 주세요.");
            return;
        }

        // View가 구독하는 확인 콜백이 없으면 삭제하지 않는다.
        if (ConfirmDeleteRequested?.Invoke() != true) return;

        await RunBusyAsync(async () =>
        {
            var envelope = await connection.RequestAsync(
                MessageTypes.UserDeleteRequest,
                new UserDeleteRequestPayload(user.Id),
                TimeSpan.FromSeconds(10));
            var response = envelope.DeserializePayload<UserMutationResponsePayload>();

            if (!response.Success)
            {
                await SetErrorAsync(response.Message ?? "사용자를 삭제하지 못했습니다.");
                return;
            }

            await LoadUsersCoreAsync(showStatus: false);
            await RunOnUiThreadAsync(() =>
            {
                StatusMessage = response.Message ?? "사용자를 삭제했습니다.";
                ErrorMessage = null;
            });
        }, "사용자를 삭제하지 못했습니다. 서버 연결을 확인해 주세요.");
    }

    private async Task LoadUsersCoreAsync(bool showStatus)
    {
        var envelope = await connection.RequestAsync(
            MessageTypes.UserListRequest,
            new UserListRequestPayload(),
            TimeSpan.FromSeconds(10));
        var response = envelope.DeserializePayload<UserListResponsePayload>();

        await RunOnUiThreadAsync(() =>
        {
            long? selectedId = SelectedUser?.Id;
            Users.Clear();
            foreach (var user in response.Users) Users.Add(user);
            SelectedUser = selectedId is { } id ? Users.FirstOrDefault(user => user.Id == id) : null;
            ErrorMessage = null;
            if (showStatus) StatusMessage = $"사용자 {response.Users.Count}명을 불러왔습니다.";
        });
    }

    private async Task RunBusyAsync(Func<Task> operation, string unexpectedFailureMessage)
    {
        if (!await TryBeginBusyAsync()) return;

        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            await SetErrorAsync(TimeoutOrDisconnectedMessage);
        }
        catch (Exception)
        {
            await SetErrorAsync(unexpectedFailureMessage);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false);
        }
    }

    private async Task RunSafeCommandAsync(Func<Task> operation, string fallbackMessage)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            await SetErrorAsync(TimeoutOrDisconnectedMessage);
        }
        catch (Exception)
        {
            await SetErrorAsync(fallbackMessage);
        }
    }

    private async Task<bool> TryBeginBusyAsync()
    {
        bool started = false;
        await RunOnUiThreadAsync(() =>
        {
            if (IsBusy) return;
            IsBusy = true;
            ErrorMessage = null;
            started = true;
        });
        return started;
    }

    private Task SetErrorAsync(string message) => RunOnUiThreadAsync(() =>
    {
        ErrorMessage = message;
        StatusMessage = null;
    });

    private string? ValidateNewUser(string loginId, string name, string password)
    {
        if (loginId.Length == 0) return "아이디를 입력해 주세요.";
        if (loginId.Length < 3 || loginId.Length > 64) return "아이디는 3자 이상 64자 이하여야 합니다.";
        if (name.Length == 0) return "이름을 입력해 주세요.";
        if (name.Length > 100) return "이름은 100자 이하여야 합니다.";
        if (password.Length < 8) return "비밀번호는 8자 이상이어야 합니다.";
        if (Users.Any(user => string.Equals(user.LoginId, loginId, StringComparison.OrdinalIgnoreCase)))
            return "이미 사용 중인 아이디입니다.";
        return null;
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }
        catch (TaskCanceledException)
        {
            // 애플리케이션 종료 중 취소된 UI 갱신은 명령 실패로 전파하지 않는다.
        }
        catch (InvalidOperationException)
        {
            // Dispatcher가 종료 중이면 더 이상 바인딩된 값을 변경할 수 없다.
        }
    }
}
