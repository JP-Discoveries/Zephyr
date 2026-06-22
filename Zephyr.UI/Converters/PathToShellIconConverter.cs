using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Zephyr.UI.Services;

namespace Zephyr.UI.Converters;

// Converts a file/folder path string to a shell icon.
// Pass ConverterParameter="folder" to treat the path as a directory.
[ValueConversion(typeof(string), typeof(ImageSource))]
public class PathToShellIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        bool isDir = parameter is string s && s == "folder";
        return ShellIconService.GetSmallIcon(path, isDir);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
