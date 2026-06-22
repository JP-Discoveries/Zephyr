using System.Globalization;
using System.Windows.Data;

namespace Zephyr.UI.Converters;

[ValueConversion(typeof(Enum), typeof(bool))]
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.Equals(parameter) == true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
