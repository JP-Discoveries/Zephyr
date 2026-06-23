using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Zephyr.Core.Models;

namespace Zephyr.UI.Converters;

/// <summary>
/// Maps a <see cref="CompareStatus"/> to its accent colour for the dual-pane compare view.
/// Default: a solid dot colour (Identical = green so matches are visible). Pass
/// ConverterParameter="tint" for a low-alpha row wash that only highlights differences
/// (Identical/None stay Transparent so unchanged rows aren't drowned in colour).
/// </summary>
[ValueConversion(typeof(CompareStatus), typeof(Brush))]
public class CompareStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Identical = Freeze(0xFF, 0x16, 0xC6, 0x0C); // green  – same
    private static readonly Brush Unique    = Freeze(0xFF, 0x00, 0x78, 0xD7); // blue   – only here
    private static readonly Brush Newer     = Freeze(0xFF, 0x00, 0xB7, 0xC3); // teal   – newer here
    private static readonly Brush Older      = Freeze(0xFF, 0xF7, 0x63, 0x0C); // amber  – older here
    private static readonly Brush Different  = Freeze(0xFF, 0x87, 0x64, 0xB8); // purple – differs

    private static readonly Brush UniqueTint    = Freeze(0x33, 0x00, 0x78, 0xD7);
    private static readonly Brush NewerTint     = Freeze(0x33, 0x00, 0xB7, 0xC3);
    private static readonly Brush OlderTint      = Freeze(0x33, 0xF7, 0x63, 0x0C);
    private static readonly Brush DifferentTint = Freeze(0x33, 0x87, 0x64, 0xB8);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool tint = parameter as string == "tint";
        return value switch
        {
            CompareStatus.Identical => tint ? Brushes.Transparent : Identical,
            CompareStatus.Unique    => tint ? UniqueTint          : Unique,
            CompareStatus.Newer     => tint ? NewerTint           : Newer,
            CompareStatus.Older     => tint ? OlderTint            : Older,
            CompareStatus.Different => tint ? DifferentTint       : Different,
            _ => Brushes.Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
