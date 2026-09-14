using System.Windows;
using SafetyVision.Client.Config;

namespace SafetyVision.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CameraOptionsStore.Load();
        var login = new Views.LoginWindow();
        login.Show();
    }
}
