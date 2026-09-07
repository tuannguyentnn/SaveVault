using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SaveGameBackup.Core.Models;
using SaveGameBackup.UI.ViewModels;
using SaveGameBackup.UI.ViewModels.SubViewModels;

namespace SaveGameBackup.UI.Views.Tabs
{
    public partial class SearchTabView : UserControl
    {
        public SearchTabView()
        {
            InitializeComponent();
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is MainViewModel vm && vm.SearchCommand.CanExecute(null))
                {
                    vm.SearchCommand.Execute(null);
                }
                else if (DataContext is SearchSubViewModel svm && svm.SearchCommand.CanExecute(null))
                {
                    svm.SearchCommand.Execute(null);
                }
            }
        }

        private void SuggestionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string gameName)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.SearchQuery = gameName;
                    if (vm.SearchCommand.CanExecute(null))
                    {
                        vm.SearchCommand.Execute(null);
                    }
                }
                else if (DataContext is SearchSubViewModel svm)
                {
                    svm.SearchQuery = gameName;
                    if (svm.SearchCommand.CanExecute(null))
                    {
                        svm.SearchCommand.Execute(null);
                    }
                }
            }
        }

        private void DetectedPathItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox || current is Button) return; // let CheckBox and Button handle their own clicks
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is FrameworkElement fe && fe.DataContext is DetectedPathItem item)
            {
                item.IsSelected = !item.IsSelected;
            }
        }
    }
}
