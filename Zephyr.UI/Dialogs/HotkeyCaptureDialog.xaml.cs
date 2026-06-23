using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Zephyr.UI.Services;

namespace Zephyr.UI.Dialogs;

public partial class HotkeyCaptureDialog : Window
{
    /// <summary>Captured gesture in canonical form (empty = the user chose to clear it).</summary>
    public string Gesture { get; private set; } = "";

    public HotkeyCaptureDialog(string commandName, string currentDisplay)
    {
        InitializeComponent();
        PromptText.Text = $"Set a shortcut for “{commandName}”.";
        if (currentDisplay is { Length: > 0 } and not "—")
            CaptureText.Text = currentDisplay;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { DialogResult = false; return; }
        if (key == Key.Back)   { Gesture = ""; CaptureText.Text = "Press keys…"; OkButton.IsEnabled = true; return; }
        if (HotkeyService.IsModifierKey(key)) return;  // wait for the non-modifier key

        Gesture = HotkeyService.ToCanonical(key, Keyboard.Modifiers);
        CaptureText.Text = HotkeyService.ToDisplay(Gesture);
        OkButton.IsEnabled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Gesture = "";
        CaptureText.Text = "Press keys…";
        OkButton.IsEnabled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)     => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int val  = 1;
        DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int));
    }
}
