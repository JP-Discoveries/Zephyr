using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Zephyr.Core.Models;
using Zephyr.UI.Services;

namespace Zephyr.UI.Converters;

[ValueConversion(typeof(FileItem), typeof(ImageSource))]
public class FileItemToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileItem item || string.IsNullOrEmpty(item.FullPath)) return null;
        bool large = parameter is string s && s == "large";
        // Pass the item's extension explicitly — WPD paths end in an object ID with no
        // extension, so the icon service can't derive the file type from the path alone.
        return large
            ? ShellIconService.GetLargeIcon(item.FullPath, item.IsDirectory, item.Extension)
            : ShellIconService.GetSmallIcon(item.FullPath, item.IsDirectory, item.Extension);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
