using System.Windows.Input;
using Zephyr.Core.Settings;
using Zephyr.UI.ViewModels;

namespace Zephyr.UI.Services;

/// <summary>
/// Parses, formats and resolves keyboard gestures for the rebindable-hotkey system.
/// Gestures are stored canonically as "Mod+Mod+Key" using <see cref="Key"/> enum names
/// (e.g. "Ctrl+Shift+N", "F2", "Shift+Delete"), independent of WPF's display quirks.
/// </summary>
public static class HotkeyService
{
    /// <summary>The gesture currently in effect for a command (user override or its default).</summary>
    public static string EffectiveGesture(AppCommand cmd) =>
        SettingsService.Current.Hotkeys.TryGetValue(cmd.Id, out var g) ? g : cmd.DefaultGesture;

    public static bool TryParse(string? gesture, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt":                  mods |= ModifierKeys.Alt;     break;
                case "shift":                mods |= ModifierKeys.Shift;   break;
                case "win": case "windows":  mods |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out Key k)) return false;
                    key = k;
                    break;
            }
        }
        return key != Key.None;
    }

    /// <summary>Builds the canonical gesture string from a captured key + modifiers.</summary>
    public static string ToCanonical(Key key, ModifierKeys mods)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>A friendly version of a canonical gesture for display (e.g. "Ctrl+,").</summary>
    public static string ToDisplay(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "—";
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = PrettyToken(parts[i]);
        return string.Join("+", parts);
    }

    private static string PrettyToken(string token) => token switch
    {
        "Control" => "Ctrl",
        "Windows" => "Win",
        "Delete"  => "Del",
        "Return"  => "Enter",
        "Escape"  => "Esc",
        "Next"    => "PageDown",
        "Prior"   => "PageUp",
        "Oemtilde" or "OemTilde"   => "`",
        "OemComma"                 => ",",
        "OemPeriod"                => ".",
        "OemPlus"                  => "=",
        "OemMinus"                 => "-",
        "OemQuestion"              => "/",
        "OemBackslash" or "Oem5"   => "\\",
        _ when token.Length == 2 && token[0] == 'D' && char.IsDigit(token[1]) => token[1].ToString(),
        _ => token,
    };

    /// <summary>True when a modifier key (or only modifiers) was pressed — not a complete chord yet.</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or
        Key.System or Key.None or Key.DeadCharProcessed or Key.ImeProcessed;
}
