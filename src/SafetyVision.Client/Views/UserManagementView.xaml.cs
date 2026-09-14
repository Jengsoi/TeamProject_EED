using System.Windows;
using System.Windows.Controls;
using SafetyVision.Client.ViewModels;

namespace SafetyVision.Client.Views;

public partial class UserManagementView : UserControl
{
    private UserManagementViewModel? _viewModel;

    public UserManagementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.ConfirmDeleteRequested -= ConfirmDelete;

        _viewModel = e.NewValue as UserManagementViewModel;
        if (_viewModel is not null)
            _viewModel.ConfirmDeleteRequested += ConfirmDelete;
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        string password = CreatePasswordBox.Password;
        try
        {
            if (_viewModel?.CreateCommand.CanExecute(password) == true)
                _viewModel.CreateCommand.Execute(password);
        }
        finally
        {
            // PasswordBox는 바인딩하지 않고, 명령을 시작한 즉시 입력값을 지운다.
            CreatePasswordBox.Clear();
        }
    }

    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        string password = ChangePasswordBox.Password;
        try
        {
            if (_viewModel?.ChangePasswordCommand.CanExecute(password) == true)
                _viewModel.ChangePasswordCommand.Execute(password);
        }
        finally
        {
            ChangePasswordBox.Clear();
        }
    }

    private bool ConfirmDelete()
    {
        var result = MessageBox.Show(
            "선택한 사용자를 삭제하시겠습니까? 이 작업은 되돌릴 수 없습니다.",
            "사용자 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }
}
