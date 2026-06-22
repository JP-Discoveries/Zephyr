using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Zephyr.UI.Services;

namespace Zephyr.UI.Converters;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (!PreviewService.IsImage(Path.GetExtension(path))) return null;

        // Parse decode width — ConverterParameter arrives as string from XAML
        int decodeWidth = 256;
        if (parameter is string s && int.TryParse(s, out int sw)) decodeWidth = sw;
        else if (parameter is int iw) decodeWidth = iw;

        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource        = new Uri(path);
            img.CacheOption      = BitmapCacheOption.OnLoad;      // release file handle after load
            img.CreateOptions    = BitmapCreateOptions.None;       // use WPF bitmap cache
            img.DecodePixelWidth = decodeWidth;                    // limit memory per image
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
