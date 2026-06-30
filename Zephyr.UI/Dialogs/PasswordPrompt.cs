using System.Windows;

namespace Zephyr.UI.Dialogs;

/// <summary>
/// Shared retry loop for password prompts: shows the title+prompt password dialog and keeps
/// re-asking (flagged as a retry) until <paramref name="validate"/> accepts the entry or the
/// user cancels. Returns the accepted password, or null on cancel.
/// </summary>
public static class PasswordPrompt
{
    public static string? Ask(Window? owner, string title, string prompt, Func<string, bool> validate)
    {
        bool retry = false;
        while (true)
        {
            var dlg = new PasswordDialog(title, prompt, retry) { Owner = owner };
            if (dlg.ShowDialog() != true) return null;
            if (validate(dlg.Password)) return dlg.Password;
            retry = true;
        }
    }
}
