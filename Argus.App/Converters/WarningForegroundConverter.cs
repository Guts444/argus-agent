using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Argus.App.Converters;

/// <summary>
/// Returns amber (#FFC857) when true (has warning), accent (#36F7FF) when false.
/// </summary>
public sealed class WarningForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC8, 0x57));
    private static readonly SolidColorBrush NormalBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x36, 0xF7, 0xFF));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? WarningBrush : NormalBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
