using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SaveGameBackup.Core.Models;

namespace SaveGameBackup.UI.Views.Modals
{
    public partial class RestoreModal : UserControl
    {
        public RestoreModal()
        {
            InitializeComponent();
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
