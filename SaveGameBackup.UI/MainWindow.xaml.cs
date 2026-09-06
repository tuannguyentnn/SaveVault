using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SaveGameBackup.Core.Models;
using SaveGameBackup.UI.ViewModels;

namespace SaveGameBackup.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
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
            }
        }

        private void DetectedPathItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    if (current is CheckBox) return; // let CheckBox handle its own click
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is FrameworkElement fe && fe.DataContext is DetectedPathItem item)
            {
                item.IsSelected = !item.IsSelected;
            }
        }

        private void GameHistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SelectedGameSummary != null)
            {
                if (vm.OpenGameDetailsCommand.CanExecute(vm.SelectedGameSummary))
                {
                    vm.OpenGameDetailsCommand.Execute(vm.SelectedGameSummary);
                }
            }
        }

        private void RestoreItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                DependencyObject? current = dep;
                while (current != null && current != sender)
                {
                    // Don't toggle selection if clicking directly on CheckBox, TextBox, or Button
                    if (current is CheckBox || current is TextBox || current is Button) return;
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (sender is FrameworkElement fe && fe.DataContext is RestoreItemTarget item)
            {
                item.IsSelected = !item.IsSelected;
            }
        }
    }
}