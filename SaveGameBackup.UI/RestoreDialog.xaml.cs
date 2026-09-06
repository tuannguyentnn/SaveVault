using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SaveGameBackup.Core.Models;
using SaveGameBackup.Core.Services;

namespace SaveGameBackup.UI;

public partial class RestoreDialog : Window
{
    private readonly BackupRecord _record;
    private readonly BackupService _backupService;
    private readonly ObservableCollection<RestoreItemTarget> _items;
    private readonly List<RestoreItemRowControl> _rows = new();

    public bool RestoreConfirmed { get; private set; }

    public RestoreDialog(BackupRecord record, BackupService backupService, List<RestoreItemTarget> items)
    {
        InitializeComponent();
        _record = record;
        _backupService = backupService;
        _items = new ObservableCollection<RestoreItemTarget>(items);

        LoadInfo();
        BuildRows();
        UpdateSelectedCount();
    }

    private void LoadInfo()
    {
        Title = $"Khôi phục - {_record.GameName}";
        SubTitleText.Text = $"Game: {_record.GameName}";
        BackupPathText.Text = _record.BackupPath;
        BackupTypeText.Text = _record.IsCompressed ? "📦 ZIP" : "📁 Thư mục";
        BackupTypeBadge.Background = new SolidColorBrush(_record.IsCompressed
            ? Color.FromRgb(0x7C, 0x3A, 0xED)
            : Color.FromRgb(0x05, 0x96, 0x69));
        DateText.Text = _record.BackupDate.ToString("dd/MM/yyyy HH:mm");
    }

    private void BuildRows()
    {
        RestoreItemsPanel.Children.Clear();
        _rows.Clear();

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var row = new RestoreItemRowControl(item, i + 1);
            row.SelectionChanged += (_, _) => UpdateSelectedCount();
            _rows.Add(row);
            RestoreItemsPanel.Children.Add(row);
        }
    }

    private void UpdateSelectedCount()
    {
        var selected = _items.Count(i => i.IsSelected);
        SelectedCountText.Text = $"Đã chọn {selected} / {_items.Count} vị trí lưu";
        RestoreBtn.IsEnabled = selected > 0;
    }

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = true;
        UpdateSelectedCount();
    }

    private void DeselectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.IsSelected = false;
        UpdateSelectedCount();
    }

    private void ResetPathsBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.RestoreDestinationPath = item.OriginalSourcePath;
        }
        foreach (var row in _rows)
        {
            row.RefreshDestPath();
        }
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        var activeItems = _items.Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.RestoreDestinationPath)).ToList();
        if (activeItems.Count == 0)
        {
            MessageBox.Show("Vui lòng chọn ít nhất 1 vị trí lưu để khôi phục!", "Chưa chọn", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RestoreBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    RestoreProgressBar.Value = p.Percent;
                    ProgressPercentText.Text = $"{p.Percent}%";
                    ProgressMessageText.Text = string.IsNullOrEmpty(p.Message) ? "Đang khôi phục..." : p.Message;
                });
            });

            await _backupService.RestoreAsync(_record, activeItems, progress);

            RestoreConfirmed = true;
            MessageBox.Show(
                $"Khôi phục thành công!\n\nĐã khôi phục {activeItems.Count} vị trí lưu cho game '{_record.GameName}'.",
                "Khôi phục thành công", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khôi phục:\n{ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            RestoreBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            ProgressArea.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>
/// Dynamic row UI control for each restore item target
/// </summary>
public class RestoreItemRowControl : Border
{
    private readonly RestoreItemTarget _item;
    private TextBox _destPathBox = null!;
    private CheckBox _selectChk = null!;

    public event EventHandler? SelectionChanged;

    public RestoreItemRowControl(RestoreItemTarget item, int index)
    {
        _item = item;

        Background = new SolidColorBrush(index % 2 == 0
            ? Color.FromRgb(0x0F, 0x17, 0x2A)
            : Color.FromRgb(0x13, 0x1C, 0x2E));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(20, 12, 20, 12);
        CornerRadius = new CornerRadius(0);

        BuildContent();
    }

    private void BuildContent()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // checkbox
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // paths
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // stats

        // Checkbox
        _selectChk = new CheckBox
        {
            IsChecked = _item.IsSelected,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _selectChk.Checked += (_, _) => { _item.IsSelected = true; SelectionChanged?.Invoke(this, EventArgs.Empty); };
        _selectChk.Unchecked += (_, _) => { _item.IsSelected = false; SelectionChanged?.Invoke(this, EventArgs.Empty); };
        Grid.SetColumn(_selectChk, 0);
        grid.Children.Add(_selectChk);

        // Paths column
        var pathsPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // Original source path
        var srcLabel = new TextBlock
        {
            Text = "Vị trí gốc:",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var srcPath = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_item.OriginalSourcePath) ? "(Không xác định)" : _item.OriginalSourcePath,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // Destination path
        var destLabel = new TextBlock
        {
            Text = "Vị trí khôi phục đích:",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            Margin = new Thickness(0, 0, 0, 2)
        };

        var destRow = new Grid();
        destRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _destPathBox = new TextBox
        {
            Text = _item.RestoreDestinationPath,
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x19)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            Height = 30,
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
        };
        _destPathBox.TextChanged += (_, _) => { _item.RestoreDestinationPath = _destPathBox.Text; };
        Grid.SetColumn(_destPathBox, 0);
        destRow.Children.Add(_destPathBox);

        var browseBtn = new Button
        {
            Content = "📂 Duyệt...",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            Template = CreateSimpleButtonTemplate()
        };
        browseBtn.Click += BrowseBtn_Click;
        Grid.SetColumn(browseBtn, 1);
        destRow.Children.Add(browseBtn);

        pathsPanel.Children.Add(srcLabel);
        pathsPanel.Children.Add(srcPath);
        pathsPanel.Children.Add(destLabel);
        pathsPanel.Children.Add(destRow);

        Grid.SetColumn(pathsPanel, 1);
        grid.Children.Add(pathsPanel);

        // Stats column
        var statsPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
            MinWidth = 80
        };
        var fileCountBlock = new TextBlock
        {
            Text = $"{_item.FileCount} files",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var sizeBlock = new TextBlock
        {
            Text = _item.FormattedSize,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };
        statsPanel.Children.Add(fileCountBlock);
        statsPanel.Children.Add(sizeBlock);
        Grid.SetColumn(statsPanel, 2);
        grid.Children.Add(statsPanel);

        Child = grid;
    }

    private static ControlTemplate CreateSimpleButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(cp);
        template.VisualTree = factory;
        return template;
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục đích để khôi phục save game vào",
            InitialDirectory = Directory.Exists(_item.RestoreDestinationPath) ? _item.RestoreDestinationPath : null
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            _destPathBox.Text = dialog.FolderName;
            _item.RestoreDestinationPath = dialog.FolderName;
        }
    }

    public void RefreshDestPath()
    {
        _destPathBox.Text = _item.RestoreDestinationPath;
    }
}
