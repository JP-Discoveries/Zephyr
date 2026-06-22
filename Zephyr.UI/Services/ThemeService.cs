using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Zephyr.UI.Services;

public class ThemeService
{
    private readonly UISettings _uiSettings = new();

    public bool IsDarkMode(string themeOverride = "Auto")
    {
        if (themeOverride == "Dark")  return true;
        if (themeOverride == "Light") return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int i ? i == 0 : true;
    }

    public Color GetAccentColor()
    {
        var c = _uiSettings.GetColorValue(UIColorType.Accent);
        return Color.FromRgb(c.R, c.G, c.B);
    }

    public void Apply(Application app, string themeOverride = "Auto")
    {
        var themeUri = IsDarkMode(themeOverride)
            ? new Uri("pack://application:,,,/Themes/Dark.xaml")
            : new Uri("pack://application:,,,/Themes/Light.xaml");

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Dark.xaml") == true ||
            d.Source?.OriginalString.Contains("Light.xaml") == true);
        if (existing != null) dicts.Remove(existing);
        dicts.Insert(0, new ResourceDictionary { Source = themeUri });

        var accent = GetAccentColor();
        app.Resources["ZephyrAccent"] = new SolidColorBrush(accent);
        app.Resources["ZephyrAccentColor"] = accent;
        app.Resources["ZephyrAccentHover"] = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Min(255, accent.R + 20),
            (byte)Math.Min(255, accent.G + 20),
            (byte)Math.Min(255, accent.B + 20)));
    }
}
