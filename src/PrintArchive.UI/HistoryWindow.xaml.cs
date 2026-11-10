using System.Windows;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        // The production UI must request an authenticated archive session from the service.
        JobsGrid.ItemsSource = Array.Empty<PrintJobRecord>();
    }

    private void JobsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (JobsGrid.SelectedItem is not PrintJobRecord job) return;
        MessageBox.Show("Original-file extraction and temporary-file lifecycle are not enabled in this starter.",
            "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
