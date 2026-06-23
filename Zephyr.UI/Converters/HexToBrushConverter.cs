using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Zephyr.UI.Converters;

/// <summary>Converts a "#RRGGBB" hex string into a frozen SolidColorBrush. An empty or
/// invalid string yields Transparent.</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> _cache = [];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex)) return Brushes.Transparent;
        if (_cache.TryGetValue(hex, out var cached)) return cached;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            _cache[hex] = brush;
            return brush;
        }
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
