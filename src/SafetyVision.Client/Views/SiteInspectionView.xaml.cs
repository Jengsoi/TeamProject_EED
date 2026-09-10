using System.Windows;
using System.Windows.Controls;
using SafetyVision.Client.ViewModels;

namespace SafetyVision.Client.Views;

public partial class SiteInspectionView : UserControl
{
    public SiteInspectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SiteInspectionViewModel vm)
                vm.ConfirmDiscardUnsavedRequested += OnConfirmDiscardUnsaved;
        };
    }

    private bool OnConfirmDiscardUnsaved()
    {
        var result = MessageBox.Show(
            "저장되지 않은 결과는 이력에 남지 않습니다. 대시보드로 돌아가시겠습니까?",
            "저장 실패",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }
}
