using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

public partial class ZephyrMessageBox : Window
{
    public ZephyrMessageBox(string message, string title, string okLabel = "OK", bool showCancel = false)
    {
        InitializeComponent();
        Title            = title;
        MessageText.Text = message;
        OkBtn.Content    = okLabel;
        if (showCancel) CancelBtn.Visibility = Visibility.Visible;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val  = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void OK_Click(object sender, RoutedEventArgs e)     => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public static void Show(string message, string title = "Zephyr", Window? owner = null)
    {
        var dlg = new ZephyrMessageBox(message, title)
            { Owner = owner ?? Application.Current?.MainWindow };
        dlg.ShowDialog();
    }

    public static bool Confirm(string message, string title, string okLabel = "OK", Window? owner = null)
    {
        var dlg = new ZephyrMessageBox(message, title, okLabel, showCancel: true)
            { Owner = owner ?? Application.Current?.MainWindow };
        return dlg.ShowDialog() == true;
    }
}
