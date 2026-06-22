using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

public partial class SetPasswordDialog : Window
{
    /// <summary>The chosen password. Valid only when DialogResult is true.</summary>
    public string Password { get; private set; } = "";

    public SetPasswordDialog(string folderName)
    {
        InitializeComponent();
        PromptText.Text = $"Set a password to lock \"{folderName}\". You'll be asked for it before this folder's contents are shown in Zephyr.";
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
            Fail("Please enter a password.");
            return;
        }
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            Fail("Passwords don't match.");
            ConfirmBox.Clear();
            ConfirmBox.Focus();
            return;
        }
        Password     = PasswordBox.Password;
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text       = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
