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
    private List<PrintJobRecord> _dayJobs = [];

    public HistoryWindow(byte[] masterKey)
    {
        InitializeComponent();
        _crypto = new CryptoService(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);
        _store = new ArchiveStore(_crypto);
        _storage = new StorageManager(_store);

        Timeline.JobClicked += job =>
        {
            JobsGrid.SelectedItem = _dayJobs.FirstOrDefault(j => j.Id == job.Id) ?? job;
            JobsGrid.ScrollIntoView(JobsGrid.SelectedItem);
        };

        PrinterFilter.Items.Clear();
        PrinterFilter.Items.Add("(All printers)");
        foreach (string printer in _store.Database.GetPrinters())
            PrinterFilter.Items.Add(printer);
        PrinterFilter.SelectedIndex = 0;

        DayPicker.SelectedDate = DateTime.Today;
        _suppressFilter = false;
        Reload();
    }

    private DateTime SelectedDay =>
        DayPicker.SelectedDate?.Date ?? DateTime.Today;

    private void Reload()
    {
        string? search = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text;
        string? printer = PrinterFilter.SelectedItem as string;
        var day = SelectedDay;
        var from = new DateTimeOffset(day);
        var to = new DateTimeOffset(day.AddDays(1).AddTicks(-1));

        var jobs = _store.Query(search, printer, from, to)
            .OrderByDescending(j => j.Timestamp)
            .ToList();

        ApplyRepeatCounts(jobs);
        _dayJobs = jobs;

        JobsGrid.ItemsSource = jobs;
        Timeline.Jobs = jobs;
        TimelineHeader.Text = $"Timeline for {day:yyyy-MM-dd}  (0:00 – 24:00)";
        UsageText.Text = $"{jobs.Count} job(s) · Storage {_storage.FormatUsage()}";

        if (JobsGrid.SelectedItem is PrintJobRecord selected)
            Timeline.SelectedJobId = selected.Id;
        UpdateDetails(JobsGrid.SelectedItem as PrintJobRecord);
    }

    private static void ApplyRepeatCounts(List<PrintJobRecord> jobs)
    {
        // Repeats = how many times the same document was printed this day (case-insensitive).
        var groups = jobs
            .GroupBy(j => NormalizeDoc(j.DocumentName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Timestamp).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            string key = NormalizeDoc(job.DocumentName);
            var list = groups[key];
            job.RepeatCount = list.Count;
            job.RepeatIndex = list.FindIndex(x => x.Id == job.Id) + 1;
        }
    }

    private static string NormalizeDoc(string name)
    {
        string cleaned = DocumentCapture.CleanDocumentTitle(name);
        return Path.GetFileName(cleaned).Trim().ToLowerInvariant();
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

    private void DayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        Reload();
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        DayPicker.SelectedDate = SelectedDay.AddDays(-1);
        _suppressFilter = false;
        Reload();
    }

    private void NextDay_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        DayPicker.SelectedDate = SelectedDay.AddDays(1);
        _suppressFilter = false;
        Reload();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        DayPicker.SelectedDate = DateTime.Today;
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

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var job = JobsGrid.SelectedItem as PrintJobRecord;
        Timeline.SelectedJobId = job?.Id;
        UpdateDetails(job);
    }

    private void UpdateDetails(PrintJobRecord? job)
    {
        if (job is null)
        {
            DetailTitle.Text = "Select a print job";
            DetailBody.Text = "Use the day timeline or table. Green = success, amber = canceled, red = failed.";
            return;
        }

        DetailTitle.Text = job.DocumentName;
        DetailBody.Text =
            $"{job.PrintOutcome} at {job.Timestamp:yyyy-MM-dd HH:mm:ss} by {job.Username} on {job.PrinterName}\n" +
            $"Duration: {job.DurationLabel}    Pages: {job.PageCount?.ToString() ?? "-"}    " +
            $"Size: {(job.DataSize is long b ? StorageManager.FormatBytes(b) : "-")}\n" +
            $"Repeats today: {job.RepeatLabel} (same document printed {job.RepeatCount} time(s))\n" +
            $"Capture: {job.CaptureStatus}\n" +
            $"Source: {job.OriginalFilePath ?? "-"}\n" +
            $"Screenshot: {job.ScreenshotPath ?? "-"}";
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
                    "Originals are archived for successful prints when the source path can be resolved.",
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
