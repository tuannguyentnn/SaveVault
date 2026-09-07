using System.Windows.Controls;
using System.Windows.Input;
using SaveGameBackup.UI.ViewModels;
using SaveGameBackup.UI.ViewModels.SubViewModels;

namespace SaveGameBackup.UI.Views.Tabs
{
    public partial class HistoryTabView : UserControl
    {
        public HistoryTabView()
        {
            InitializeComponent();
        }

        private void GameHistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.SelectedGameHistory != null && vm.OpenGameDetailsCommand.CanExecute(vm.SelectedGameHistory))
                {
                    vm.OpenGameDetailsCommand.Execute(vm.SelectedGameHistory);
                }
                else if (vm.SelectedGameSummary != null && vm.OpenGameDetailsCommand.CanExecute(vm.SelectedGameSummary))
                {
                    vm.OpenGameDetailsCommand.Execute(vm.SelectedGameSummary);
                }
            }
            else if (DataContext is HistorySubViewModel hvm)
            {
                if (hvm.SelectedGameHistory != null && hvm.OpenGameDetailsCommand.CanExecute(hvm.SelectedGameHistory))
                {
                    hvm.OpenGameDetailsCommand.Execute(hvm.SelectedGameHistory);
                }
                else if (hvm.SelectedGameSummary != null && hvm.OpenGameDetailsCommand.CanExecute(hvm.SelectedGameSummary))
                {
                    hvm.OpenGameDetailsCommand.Execute(hvm.SelectedGameSummary);
                }
            }
        }
    }
}
