using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Zephyr.UI.Converters;

[ValueConversion(typeof(bool), typeof(GridLength))]
public class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool show = value is true;
        if (!show) return new GridLength(0);
        var p = parameter?.ToString() ?? "200";
        if (p == "*") return new GridLength(1, GridUnitType.Star);
        return int.TryParse(p, out var px) ? new GridLength(px) : new GridLength(200);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
