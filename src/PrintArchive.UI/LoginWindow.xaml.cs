using System.Windows;
using PrintArchive.Core;

namespace PrintArchive.UI;

public partial class LoginWindow : Window
{
    private readonly bool _enrollMode;

    public LoginWindow()
    {
        InitializeComponent();
        _enrollMode = !PasswordVault.IsEnrolled;
        if (_enrollMode)
        {
            Title = "PrintArchive — Create password";
            Subtitle.Text = "Create a password that protects the archive encryption key. You will need it to view and open archived documents.";
            PasswordLabel.Text = "New password (min 6 characters)";
            ConfirmPanel.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Create & unlock";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Password;
        try
        {
            byte[] key;
            if (_enrollMode)
            {
                if (password.Length < 6)
                {
                    MessageBox.Show("Password must be at least 6 characters.", "PrintArchive",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (password != ConfirmInput.Password)
                {
                    MessageBox.Show("Passwords do not match.", "PrintArchive",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                PasswordVault.Enroll(password);
                key = PasswordVault.Unlock(password);
            }
            else
            {
                key = PasswordVault.Unlock(password);
            }

            var history = new HistoryWindow(key);
            history.Show();
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Incorrect password.", "PrintArchive",
                MessageBoxButton.OK, MessageBoxImage.Error);
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not unlock archive:\n" + ex.Message, "PrintArchive",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // PasswordBox keeps its own copy; clear UI fields.
            PasswordInput.Clear();
            ConfirmInput.Clear();
        }
    }
}
