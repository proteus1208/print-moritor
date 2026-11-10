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
            Title = "PrintArchive - Create password";
            Subtitle.Text =
                "Create a password that protects the archive encryption key. You will need it to view and open archived documents.";
            PasswordLabel.Text = "New password (min 6 characters)";
            ConfirmPanel.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Create & unlock";
            Height = 520;
        }

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string password = PasswordInput.Password;
        try
        {
            byte[] key;
            if (_enrollMode)
            {
                if (password.Length < 6)
                {
                    ShowError("Password must be at least 6 characters.");
                    return;
                }
                if (password != ConfirmInput.Password)
                {
                    ShowError("Passwords do not match.");
                    return;
                }

                PasswordVault.Enroll(password);
                key = PasswordVault.Unlock(password);
            }
            else
            {
                key = PasswordVault.Unlock(password);
            }

            SessionCache.Save(key);
            App.OpenHistory(key);
            // Do not Close() before History is MainWindow — that used to shut the app down.
            Hide();
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("Incorrect password.");
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
        catch (Exception ex)
        {
            ShowError("Could not unlock archive: " + ex.Message);
        }
        finally
        {
            PasswordInput.Clear();
            ConfirmInput.Clear();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
