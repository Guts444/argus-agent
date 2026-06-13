using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Argus.App.Converters;

/// <summary>
/// Returns amber (#22FFAA00) when true (has warning), accent (#1810F7FF) when false.
/// </summary>
public sealed class WarningBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xAA, 0x00));
    private static readonly SolidColorBrush NormalBrush = new(Windows.UI.Color.FromArgb(0x18, 0x10, 0xF7, 0xFF));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? WarningBrush : NormalBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
