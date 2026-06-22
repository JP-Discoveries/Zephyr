using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Zephyr.UI.Dialogs;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public InputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        InputBox.Text   = initial;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }

    private void OK_Click(object sender, RoutedEventArgs e)     => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { Commit();             e.Handled = true; }
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void Commit()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        Result       = InputBox.Text.Trim();
        DialogResult = true;
    }
}
