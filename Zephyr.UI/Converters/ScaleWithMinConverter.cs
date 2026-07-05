using System.Globalization;
using System.Windows.Data;

namespace Zephyr.UI.Converters;

// Scales a dimension (e.g. a container's ActualWidth) by a fraction, kept at or
// above a floor but never exceeding the container itself (so it can't overflow a
// window smaller than the floor). Parameter format: "fraction;min", e.g. "0.75;600".
[ValueConversion(typeof(double), typeof(double))]
public class ScaleWithMinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double size || double.IsNaN(size) || size <= 0)
            return double.NaN;

        double fraction = 0.75, min = 0;
        var parts = parameter?.ToString()?.Split(';');
        if (parts is { Length: > 0 })
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
        if (parts is { Length: > 1 })
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out min);

        // Honour the floor when there's room, but never spill past the container.
        return Math.Min(size, Math.Max(size * fraction, min));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
