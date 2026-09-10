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
        DataContext = vm;
    }

    private void OnLoggedOut()
    {
        var login = new LoginWindow();
        login.Show();
        Close();
    }
}
