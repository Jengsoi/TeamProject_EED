using System.Windows;
using System.Windows.Input;
using SafetyVision.Client.Networking;
using SafetyVision.Client.ViewModels;

namespace SafetyVision.Client.Views;

public partial class LoginWindow : Window
{
    private LoginViewModel ViewModel => (LoginViewModel)DataContext;

    public LoginWindow()
    {
        InitializeComponent();
        ViewModel.LoginSucceeded += OnLoginSucceeded;
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e) => ViewModel.LoginCommand.Execute(PasswordBox.Password);

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ViewModel.LoginCommand.Execute(PasswordBox.Password);
    }

    private void OnLoginSucceeded(ServerConnection connection, string displayName)
    {
        var shell = new ShellWindow(connection, displayName);
        shell.Show();
        Close();
    }
}
