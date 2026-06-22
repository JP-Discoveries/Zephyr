using System.Windows;
using System.Windows.Controls;

namespace Zephyr.UI.Controls;

/// <summary>
/// A password entry box with an eye toggle that reveals the typed text. WPF's
/// <see cref="PasswordBox"/> can't show its content, so a masked PasswordBox and a
/// plain TextBox are kept in sync and swapped when the eye is toggled.
/// </summary>
public partial class RevealablePasswordBox : UserControl
{
    private const string EyeGlyphShow = "";   // RedEye - click to reveal
    private const string EyeGlyphHide = "";   // Hide   - click to mask

    private bool _syncing;

    public RevealablePasswordBox() => InitializeComponent();

    /// <summary>The current entered text, read from whichever editor is active.</summary>
    public string Password => Plain.Visibility == Visibility.Visible ? Plain.Text : Pwd.Password;

    public void Clear()
    {
        _syncing = true;
        Pwd.Clear();
        Plain.Clear();
        _syncing = false;
    }

    public new void Focus()
    {
        if (Plain.Visibility == Visibility.Visible) Plain.Focus();
        else Pwd.Focus();
    }

    private void Pwd_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Plain.Text = Pwd.Password;
        _syncing = false;
    }

    private void Plain_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Pwd.Password = Plain.Text;
        _syncing = false;
    }

    private void Eye_Checked(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        Plain.Text = Pwd.Password;
        _syncing = false;
        Pwd.Visibility   = Visibility.Collapsed;
        Plain.Visibility = Visibility.Visible;
        EyeGlyph.Text    = EyeGlyphHide;
        Eye.ToolTip      = "Hide password";
        Plain.Focus();
        Plain.CaretIndex = Plain.Text.Length;
    }

    private void Eye_Unchecked(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        Pwd.Password = Plain.Text;
        _syncing = false;
        Plain.Visibility = Visibility.Collapsed;
        Pwd.Visibility   = Visibility.Visible;
        EyeGlyph.Text    = EyeGlyphShow;
        Eye.ToolTip      = "Show password";
        Pwd.Focus();
    }
}
