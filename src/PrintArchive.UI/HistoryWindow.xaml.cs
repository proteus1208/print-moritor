using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class HistoryWindow : Window
{
    private readonly CryptoService _crypto;
    private readonly ArchiveStore _store;
    private readonly StorageManager _storage;
    private readonly List<string> _openedTempFiles = [];
    private bool _suppressFilter = true;
    private bool _locking;

    public HistoryWindow(byte[] masterKey)
    {
        InitializeComponent();
        _crypto = new CryptoService(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);
        _store = new ArchiveStore(_crypto);
        _storage = new StorageManager(_store);

        PrinterFilter.Items.Clear();
        PrinterFilter.Items.Add("(All printers)");
        foreach (string printer in _store.Database.GetPrinters())
            PrinterFilter.Items.Add(printer);
        PrinterFilter.SelectedIndex = 0;
        _suppressFilter = false;

        Reload();
    }

    private void Reload()
    {
        string? search = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
        string? printer = PrinterFilter.SelectedItem as string;
        DateTimeOffset? from = FromDate.SelectedDate is DateTime fd
            ? new DateTimeOffset(fd.Date) : null;
        DateTimeOffset? to = ToDate.SelectedDate is DateTime td
            ? new DateTimeOffset(td.Date.AddDays(1).AddTicks(-1)) : null;

        var jobs = _store.Query(search, printer, from, to);
        JobsGrid.ItemsSource = jobs;
        UsageText.Text = "Storage " + _storage.FormatUsage();
        UpdateDetails(JobsGrid.SelectedItem as PrintJobRecord);
    }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilter) return;
        Reload();
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        Reload();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        SearchBox.Text = "";
        PrinterFilter.SelectedIndex = 0;
        FromDate.SelectedDate = null;
        ToDate.SelectedDate = null;
        _suppressFilter = false;
        Reload();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        string? selected = PrinterFilter.SelectedItem as string;
        PrinterFilter.Items.Clear();
        PrinterFilter.Items.Add("(All printers)");
        foreach (string printer in _store.Database.GetPrinters())
            PrinterFilter.Items.Add(printer);
        PrinterFilter.SelectedItem = selected is not null && PrinterFilter.Items.Contains(selected)
            ? selected : "(All printers)";
        _suppressFilter = false;
        Reload();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_storage);
        settings.Owner = this;
        if (settings.ShowDialog() == true)
            Reload();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _locking = true;
        SessionCache.Clear();
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDetails(JobsGrid.SelectedItem as PrintJobRecord);

    private void UpdateDetails(PrintJobRecord? job)
    {
        if (job is null)
        {
            DetailTitle.Text = "Select a print job";
            DetailBody.Text = "Double-click a row to decrypt and open the archived document when available.";
            return;
        }

        DetailTitle.Text = job.DocumentName;
        DetailBody.Text =
            $"Printed {job.Timestamp:yyyy-MM-dd HH:mm:ss} by {job.Username} on {job.PrinterName}\n" +
            $"Pages: {job.PageCount?.ToString() ?? "-"}    Size: {(job.DataSize is long b ? StorageManager.FormatBytes(b) : "-")}\n" +
            $"Capture: {job.CaptureStatus}\n" +
            $"Source: {job.OriginalFilePath ?? "-"}\n" +
            $"Archive: {job.ArchiveFilePath ?? "-"}";
    }

    private void JobsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenSelected();

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (JobsGrid.SelectedItem is not PrintJobRecord job) return;
        try
        {
            if (string.IsNullOrWhiteSpace(job.ArchiveFilePath) || !File.Exists(job.ArchiveFilePath))
            {
                MessageBox.Show(
                    "No encrypted document is archived for this job.\n\n" +
                    "PrintArchive stores metadata for every completed print. Original files are archived only when the source path can be resolved.",
                    "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string temp = _store.DecryptToTemp(job);
            TempFileManager.Track(temp);
            _openedTempFiles.Add(temp);
            Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open archived document:\n" + ex.Message,
                "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not PrintJobRecord job) return;
        if (string.IsNullOrWhiteSpace(job.ArchiveFilePath) || !File.Exists(job.ArchiveFilePath))
        {
            MessageBox.Show("No archived document to export for this job.",
                "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string suggested = Path.GetFileName(job.OriginalFilePath ?? job.DocumentName);
        foreach (char c in Path.GetInvalidFileNameChars())
            suggested = suggested.Replace(c, '_');

        var dialog = new SaveFileDialog
        {
            FileName = suggested,
            Title = "Export archived document"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _store.Export(job, dialog.FileName);
            MessageBox.Show("Exported successfully.", "PrintArchive",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed:\n" + ex.Message,
                "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not PrintJobRecord job) return;
        var result = MessageBox.Show(
            $"Delete archived job \"{job.DocumentName}\"?\nThis removes metadata and any encrypted document.",
            "PrintArchive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _store.DeleteRecord(job.Id);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Delete failed:\n" + ex.Message,
                "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (string path in _openedTempFiles.ToArray())
        {
            TempFileManager.Release(path);
            _openedTempFiles.Remove(path);
        }
        TempFileManager.CleanupAllUnlocked();
        _store.Dispose();
        _crypto.Dispose();

        if (!_locking)
            Application.Current.Shutdown();
    }
}
