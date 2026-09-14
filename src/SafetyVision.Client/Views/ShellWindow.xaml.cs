using System.Windows;
using SafetyVision.Client.Networking;
using SafetyVision.Client.ViewModels;

namespace SafetyVision.Client.Views;

public partial class ShellWindow : Window
{
    public ShellWindow(ServerConnection connection, string displayName)
    {
        InitializeComponent();
        var vm = new ShellViewModel(connection, displayName);
        vm.LoggedOut += OnLoggedOut;
        vm.ConnectionLost += OnConnectionLost;
        DataContext = vm;
    }

    private void OnLoggedOut()
    {
        var login = new LoginWindow();
        login.Show();
        Close();
    }

    private void OnConnectionLost()
    {
        ((ShellViewModel)DataContext).Connection.Dispose();
        MessageBox.Show(this, "서버 연결이 끊겼습니다. 연결을 확인한 뒤 다시 로그인해 주세요.",
            "연결 끊김", MessageBoxButton.OK, MessageBoxImage.Warning);
        var login = new LoginWindow();
        login.Show();
        Close();
    }
}
