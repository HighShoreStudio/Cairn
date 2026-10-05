using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HighshoreCairn.Converters;

/// <summary>"#RRGGBB" (or "#AARRGGBB") string -> SolidColorBrush. Invalid values give a neutral gray.</summary>
public class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush Fallback = Freeze(new SolidColorBrush(Color.FromRgb(0x8D, 0x8D, 0x8D)));

    public static SolidColorBrush Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Fallback;
        if (Cache.TryGetValue(hex, out var cached)) return cached;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color color)
            {
                var brush = Freeze(new SolidColorBrush(color));
                Cache[hex] = brush;
                return brush;
            }
        }
        catch (FormatException)
        {
            // fall through
        }
        return Fallback;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Parse(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>true -> Collapsed, false -> Visible.</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Path data string ("M0,0 L10,10 ...") -> Geometry, for icons stored as text in a ViewModel.</summary>
public class StringToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string data && data.Length > 0 ? Geometry.Parse(data) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
