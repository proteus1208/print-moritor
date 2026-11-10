using System.Windows;

namespace PrintArchive.UI;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        // Password verification and password-wrapped archive-key unlock must be wired
        // before production use. This starter intentionally does not accept a password
        // as proof of authentication.
        MessageBox.Show("Password verification is not yet configured. See README security notes.",
            "PrintArchive", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
