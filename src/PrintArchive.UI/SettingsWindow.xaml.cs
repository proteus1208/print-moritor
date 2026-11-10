using System.Globalization;
using System.Windows;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class SettingsWindow : Window
{
    private readonly StorageManager _storage;

    public SettingsWindow(StorageManager storage)
    {
        InitializeComponent();
        _storage = storage;
        var settings = SettingsStore.Load();
        QuotaBox.Text = (settings.MaxBytes / (1024d * 1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture);
        UsageLabel.Text = "Used " + _storage.FormatUsage();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(QuotaBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double gb) || gb < 0.1)
        {
            MessageBox.Show("Enter a storage limit of at least 0.1 GB.", "PrintArchive",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool changingPassword = !string.IsNullOrEmpty(CurrentPassword.Password)
            || !string.IsNullOrEmpty(NewPassword.Password)
            || !string.IsNullOrEmpty(ConfirmPassword.Password);

        if (changingPassword)
        {
            if (NewPassword.Password.Length < 6)
            {
                MessageBox.Show("New password must be at least 6 characters.", "PrintArchive",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (NewPassword.Password != ConfirmPassword.Password)
            {
                MessageBox.Show("New passwords do not match.", "PrintArchive",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                PasswordVault.ChangePassword(CurrentPassword.Password, NewPassword.Password);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Current password is incorrect.", "PrintArchive",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change password:\n" + ex.Message, "PrintArchive",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        long maxBytes = (long)(gb * 1024 * 1024 * 1024);
        var settings = SettingsStore.Load();
        settings.MaxBytes = maxBytes;
        SettingsStore.Save(settings);
        _storage.QuotaBytes = maxBytes;
        _storage.EnforceQuota();

        DialogResult = true;
        Close();
    }
}
