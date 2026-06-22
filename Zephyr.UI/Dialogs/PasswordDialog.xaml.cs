using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

public partial class PasswordDialog : Window
{
    /// <summary>The entered password. Valid only when DialogResult is true.</summary>
    public string Password { get; private set; } = "";

    public PasswordDialog(string archiveName, bool retry = false)
        : this("Password Required",
               $"\"{archiveName}\" is password-protected. Enter its password to continue.",
               retry) { }

    public PasswordDialog(string title, string prompt, bool retry = false)
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        if (retry)
        {
            ErrorText.Text       = "Incorrect password — try again.";
            ErrorText.Visibility = Visibility.Visible;
        }
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ErrorText.Text       = "Please enter a password.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Password     = PasswordBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
