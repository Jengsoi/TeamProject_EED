using System.Windows.Controls;
using System.Windows.Input;
using SafetyVision.Client.ViewModels;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && HistoryGrid.SelectedItem is HistoryRowPayload row)
            vm.OpenDetailCommand.Execute(row.Id);
    }
}
