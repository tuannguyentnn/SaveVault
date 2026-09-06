using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    }
}